using System.Numerics;
using System.Text.Json;
using Silk.NET.Maths;
using StbImageSharp;

namespace ClearSkies.Engine.Rendering.Gltf;

/// <summary>
/// Loads a glTF 2.0 file (<c>.gltf</c> JSON with embedded data-URI or external buffers/images) into a
/// <see cref="ModelData"/>: every triangle primitive in the default scene, baked into model space (node
/// transforms applied, skinned meshes posed at their bind pose), merged into one part per material.
///
/// Scope is deliberately small — enough for Blockbench exports and other simple static props: no animations,
/// morph targets, sparse accessors, <c>.glb</c> binaries or texture transforms. glTF's conventions (right-handed,
/// +Y up, counter-clockwise front faces, 1 unit = 1 metre) already match the engine's (1 unit = 1 block), so
/// nothing is converted. Pure data (no WebGPU dependency); <c>Renderer.UploadModel</c> turns it into GPU resources.
/// </summary>
public static class GltfLoader
{
    private const int ModeTriangles = 4;

    public static ModelData Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";

        var buffers   = LoadBuffers(root, baseDir);
        var materials = LoadMaterials(root, baseDir, buffers);
        var nodeWorld = ComputeNodeWorldMatrices(root);

        // One part per material; primitives without a material share a default (white, untextured) one.
        var parts = new Dictionary<int, (List<Vertex> Verts, List<uint> Idx)>();
        int defaultMaterial = -1;

        foreach (int node in SceneNodes(root))
        {
            var n = root.GetProperty("nodes")[node];
            if (!n.TryGetProperty("mesh", out var meshIndex)) continue;
            var mesh = root.GetProperty("meshes")[meshIndex.GetInt32()];

            // A skinned mesh ignores its own node's transform: its joints place it (glTF 2.0 §3.7.3.3).
            Matrix4x4[]? joints = n.TryGetProperty("skin", out var skin)
                ? JointMatrices(root, skin.GetInt32(), nodeWorld, buffers)
                : null;

            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                int mode = prim.TryGetProperty("mode", out var m) ? m.GetInt32() : ModeTriangles;
                if (mode != ModeTriangles) continue;

                int mat = prim.TryGetProperty("material", out var mi) ? mi.GetInt32() : -1;
                if (mat < 0)
                {
                    if (defaultMaterial < 0)
                    {
                        defaultMaterial = materials.Count;
                        materials.Add(ModelMaterial.White);
                    }
                    mat = defaultMaterial;
                }
                if (!parts.TryGetValue(mat, out var part))
                    parts[mat] = part = (new List<Vertex>(), new List<uint>());

                AppendPrimitive(root, prim, buffers, nodeWorld[node], joints, materials[mat].BaseColor, part.Verts, part.Idx);
            }
        }

        var result = new List<ModelPart>();
        var min = new Vector3D<float>(float.MaxValue);
        var max = new Vector3D<float>(float.MinValue);
        foreach (var (mat, (verts, idx)) in parts)
        {
            if (idx.Count == 0) continue;
            foreach (var v in verts)
            {
                min = Vector3D.Min(min, v.Position);
                max = Vector3D.Max(max, v.Position);
            }
            result.Add(new ModelPart(verts.ToArray(), idx.ToArray(), materials[mat]));
        }
        if (result.Count == 0) throw new InvalidDataException($"glTF '{path}' has no triangle geometry in its scene.");
        return new ModelData(result, min, max);
    }

    // ── scene graph ──────────────────────────────────────────────────────────

    /// <summary>Every node reachable from the default scene (or scene 0), parents before children.</summary>
    private static IEnumerable<int> SceneNodes(JsonElement root)
    {
        var stack = new Stack<int>(RootNodes(root).Reverse());
        while (stack.Count > 0)
        {
            int node = stack.Pop();
            yield return node;
            if (root.GetProperty("nodes")[node].TryGetProperty("children", out var children))
                foreach (var c in children.EnumerateArray().Reverse()) stack.Push(c.GetInt32());
        }
    }

    private static IEnumerable<int> RootNodes(JsonElement root)
    {
        if (root.TryGetProperty("scenes", out var scenes) && scenes.GetArrayLength() > 0)
        {
            int scene = root.TryGetProperty("scene", out var s) ? s.GetInt32() : 0;
            if (scenes[scene].TryGetProperty("nodes", out var nodes))
                return nodes.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            return Array.Empty<int>();
        }

        // No scenes: every node that isn't somebody's child is a root.
        if (!root.TryGetProperty("nodes", out var all)) return Array.Empty<int>();
        var isChild = new bool[all.GetArrayLength()];
        foreach (var n in all.EnumerateArray())
            if (n.TryGetProperty("children", out var children))
                foreach (var c in children.EnumerateArray()) isChild[c.GetInt32()] = true;
        return Enumerable.Range(0, isChild.Length).Where(i => !isChild[i]).ToArray();
    }

    /// <summary>World (model-space) matrix of every node, System.Numerics row-vector convention (v * M).</summary>
    private static Matrix4x4[] ComputeNodeWorldMatrices(JsonElement root)
    {
        if (!root.TryGetProperty("nodes", out var nodes)) return Array.Empty<Matrix4x4>();
        var world = new Matrix4x4[nodes.GetArrayLength()];
        for (int i = 0; i < world.Length; i++) world[i] = Matrix4x4.Identity;

        var stack = new Stack<(int Node, Matrix4x4 Parent)>();
        foreach (int r in RootNodes(root)) stack.Push((r, Matrix4x4.Identity));
        while (stack.Count > 0)
        {
            var (node, parent) = stack.Pop();
            var n = nodes[node];
            world[node] = LocalMatrix(n) * parent;
            if (n.TryGetProperty("children", out var children))
                foreach (var c in children.EnumerateArray()) stack.Push((c.GetInt32(), world[node]));
        }
        return world;
    }

    private static Matrix4x4 LocalMatrix(JsonElement node)
    {
        // glTF matrices are column-major column-vector; read sequentially into System.Numerics' row-major
        // fields that is exactly the transpose, i.e. the same transform in its row-vector convention.
        if (node.TryGetProperty("matrix", out var m))
        {
            var f = Floats(m);
            return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7],
                                 f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
        }

        var t = node.TryGetProperty("translation", out var te) ? Floats(te) : new[] { 0f, 0f, 0f };
        var r = node.TryGetProperty("rotation",    out var re) ? Floats(re) : new[] { 0f, 0f, 0f, 1f };
        var s = node.TryGetProperty("scale",       out var se) ? Floats(se) : new[] { 1f, 1f, 1f };
        return Matrix4x4.CreateScale(s[0], s[1], s[2])
             * Matrix4x4.CreateFromQuaternion(new Quaternion(r[0], r[1], r[2], r[3]))
             * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
    }

    /// <summary>Per-joint skinning matrices (inverse bind matrix, then the joint node's model-space pose).</summary>
    private static Matrix4x4[] JointMatrices(JsonElement root, int skinIndex, Matrix4x4[] nodeWorld, byte[][] buffers)
    {
        var skin = root.GetProperty("skins")[skinIndex];
        var jointNodes = skin.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
        float[]? ibm = skin.TryGetProperty("inverseBindMatrices", out var ibmAcc)
            ? ReadAccessor(root, ibmAcc.GetInt32(), buffers, out _)
            : null;

        var result = new Matrix4x4[jointNodes.Length];
        for (int j = 0; j < jointNodes.Length; j++)
        {
            var inverseBind = Matrix4x4.Identity;
            if (ibm != null)
            {
                int o = j * 16;
                inverseBind = new Matrix4x4(ibm[o], ibm[o + 1], ibm[o + 2], ibm[o + 3], ibm[o + 4], ibm[o + 5], ibm[o + 6], ibm[o + 7],
                                            ibm[o + 8], ibm[o + 9], ibm[o + 10], ibm[o + 11], ibm[o + 12], ibm[o + 13], ibm[o + 14], ibm[o + 15]);
            }
            result[j] = inverseBind * nodeWorld[jointNodes[j]];
        }
        return result;
    }

    // ── geometry ─────────────────────────────────────────────────────────────

    private static void AppendPrimitive(JsonElement root, JsonElement prim, byte[][] buffers, Matrix4x4 nodeMatrix,
                                        Matrix4x4[]? joints, Vector4 baseColor, List<Vertex> verts, List<uint> idx)
    {
        var attrs = prim.GetProperty("attributes");
        if (!attrs.TryGetProperty("POSITION", out var posAcc)) return;

        var pos = ReadAccessor(root, posAcc.GetInt32(), buffers, out _);
        int count = pos.Length / 3;
        float[]? nrm = attrs.TryGetProperty("NORMAL",     out var a) ? ReadAccessor(root, a.GetInt32(), buffers, out _) : null;
        float[]? uv  = attrs.TryGetProperty("TEXCOORD_0", out a)     ? ReadAccessor(root, a.GetInt32(), buffers, out _) : null;
        float[]? col = attrs.TryGetProperty("COLOR_0",    out a)     ? ReadAccessor(root, a.GetInt32(), buffers, out _) : null;
        int colWidth = col == null ? 0 : col.Length / count;

        float[]? jnt = null, wgt = null;
        if (joints != null && attrs.TryGetProperty("JOINTS_0", out var ja) && attrs.TryGetProperty("WEIGHTS_0", out var wa))
        {
            jnt = ReadAccessor(root, ja.GetInt32(), buffers, out _);
            wgt = ReadAccessor(root, wa.GetInt32(), buffers, out _);
        }

        uint[] indices;
        if (prim.TryGetProperty("indices", out var ia))
            indices = Array.ConvertAll(ReadAccessor(root, ia.GetInt32(), buffers, out _), f => (uint)f);
        else
            indices = Enumerable.Range(0, count).Select(i => (uint)i).ToArray();

        nrm ??= ComputeNormals(pos, indices);

        uint baseIndex = (uint)verts.Count;
        for (int i = 0; i < count; i++)
        {
            var matrix = nodeMatrix;
            if (jnt != null && wgt != null)
            {
                // Linear blend skinning, baked once at the bind pose.
                matrix = default;
                float total = 0f;
                for (int k = 0; k < 4; k++)
                {
                    float w = wgt[i * 4 + k];
                    if (w <= 0f) continue;
                    matrix += joints![(int)jnt[i * 4 + k]] * w;
                    total += w;
                }
                if (total <= 0f) matrix = nodeMatrix;
            }

            var p = Vector3.Transform(new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]), matrix);
            var n = Vector3.Normalize(TransformNormal(new Vector3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]), matrix));

            var c = baseColor;
            if (colWidth >= 3) c *= new Vector4(col![i * colWidth], col[i * colWidth + 1], col[i * colWidth + 2], 1f);

            verts.Add(new Vertex
            {
                Position = new Vector3D<float>(p.X, p.Y, p.Z),
                Normal   = new Vector3D<float>(n.X, n.Y, n.Z),
                Color    = new Vector3D<float>(c.X, c.Y, c.Z),
                Uv       = uv == null ? default : new Vector3D<float>(uv[i * 2], uv[i * 2 + 1], 0f),
            });
        }

        // A mirroring transform (negative determinant) flips winding; swap two indices to keep fronts CCW.
        bool flip = nodeMatrix.GetDeterminant() < 0f && jnt == null;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            idx.Add(baseIndex + indices[t]);
            idx.Add(baseIndex + indices[flip ? t + 2 : t + 1]);
            idx.Add(baseIndex + indices[flip ? t + 1 : t + 2]);
        }
    }

    private static Vector3 TransformNormal(Vector3 n, Matrix4x4 m)
    {
        // Inverse-transpose keeps normals perpendicular under non-uniform scale.
        return Matrix4x4.Invert(m, out var inv)
            ? Vector3.TransformNormal(n, Matrix4x4.Transpose(inv))
            : Vector3.TransformNormal(n, m);
    }

    /// <summary>Smooth per-vertex normals for primitives exported without any.</summary>
    private static float[] ComputeNormals(float[] pos, uint[] indices)
    {
        var acc = new Vector3[pos.Length / 3];
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            uint i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
            var p0 = new Vector3(pos[i0 * 3], pos[i0 * 3 + 1], pos[i0 * 3 + 2]);
            var p1 = new Vector3(pos[i1 * 3], pos[i1 * 3 + 1], pos[i1 * 3 + 2]);
            var p2 = new Vector3(pos[i2 * 3], pos[i2 * 3 + 1], pos[i2 * 3 + 2]);
            var fn = Vector3.Cross(p1 - p0, p2 - p0);
            acc[i0] += fn; acc[i1] += fn; acc[i2] += fn;
        }
        var result = new float[pos.Length];
        for (int i = 0; i < acc.Length; i++)
        {
            var n = acc[i].LengthSquared() > 0f ? Vector3.Normalize(acc[i]) : Vector3.UnitY;
            result[i * 3] = n.X; result[i * 3 + 1] = n.Y; result[i * 3 + 2] = n.Z;
        }
        return result;
    }

    // ── accessors & buffers ──────────────────────────────────────────────────

    /// <summary>Reads an accessor as flat floats (component count per element in <paramref name="width"/>).
    /// Normalized integer components are mapped to [0,1]/[-1,1]; plain integers keep their value.</summary>
    private static float[] ReadAccessor(JsonElement root, int accessorIndex, byte[][] buffers, out int width)
    {
        var acc = root.GetProperty("accessors")[accessorIndex];
        int count = acc.GetProperty("count").GetInt32();
        int componentType = acc.GetProperty("componentType").GetInt32();
        bool normalized = acc.TryGetProperty("normalized", out var nz) && nz.GetBoolean();
        width = acc.GetProperty("type").GetString() switch
        {
            "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT2" => 4, "MAT3" => 9, "MAT4" => 16,
            var t => throw new InvalidDataException($"Unknown glTF accessor type '{t}'."),
        };
        if (acc.TryGetProperty("sparse", out _))
            throw new NotSupportedException("Sparse glTF accessors are not supported.");

        var result = new float[count * width];
        if (!acc.TryGetProperty("bufferView", out var bvIndex)) return result; // all zeros per spec

        var view = root.GetProperty("bufferViews")[bvIndex.GetInt32()];
        var data = buffers[view.GetProperty("buffer").GetInt32()];
        int compSize = componentType switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4,
            _ => throw new InvalidDataException($"Unknown glTF component type {componentType}.") };
        int stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : compSize * width;
        int offset = (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
                   + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);

        var span = data.AsSpan();
        for (int e = 0; e < count; e++)
        for (int c = 0; c < width; c++)
        {
            int at = offset + e * stride + c * compSize;
            result[e * width + c] = componentType switch
            {
                5126 => BitConverter.ToSingle(span.Slice(at, 4)),
                5125 => BitConverter.ToUInt32(span.Slice(at, 4)),
                5123 => normalized ? BitConverter.ToUInt16(span.Slice(at, 2)) / 65535f : BitConverter.ToUInt16(span.Slice(at, 2)),
                5122 => normalized ? System.Math.Max(BitConverter.ToInt16(span.Slice(at, 2)) / 32767f, -1f) : BitConverter.ToInt16(span.Slice(at, 2)),
                5121 => normalized ? span[at] / 255f : span[at],
                _    => normalized ? System.Math.Max((sbyte)span[at] / 127f, -1f) : (sbyte)span[at],
            };
        }
        return result;
    }

    private static byte[][] LoadBuffers(JsonElement root, string baseDir)
    {
        if (!root.TryGetProperty("buffers", out var buffers)) return Array.Empty<byte[]>();
        return buffers.EnumerateArray().Select(b =>
        {
            if (!b.TryGetProperty("uri", out var uri))
                throw new NotSupportedException("glTF buffers without a uri (.glb binary chunks) are not supported.");
            return ReadUri(uri.GetString()!, baseDir);
        }).ToArray();
    }

    private static byte[] ReadUri(string uri, string baseDir)
    {
        if (uri.StartsWith("data:", StringComparison.Ordinal))
        {
            int comma = uri.IndexOf(',');
            if (comma < 0 || !uri.AsSpan(0, comma).EndsWith(";base64"))
                throw new InvalidDataException("Only base64 data URIs are supported in glTF files.");
            return Convert.FromBase64String(uri[(comma + 1)..]);
        }
        return File.ReadAllBytes(Path.Combine(baseDir, Uri.UnescapeDataString(uri)));
    }

    // ── materials ────────────────────────────────────────────────────────────

    private static List<ModelMaterial> LoadMaterials(JsonElement root, string baseDir, byte[][] buffers)
    {
        var list = new List<ModelMaterial>();
        if (!root.TryGetProperty("materials", out var materials)) return list;

        foreach (var m in materials.EnumerateArray())
        {
            var baseColor = Vector4.One;
            ModelTextureData? texture = null;
            if (m.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                if (pbr.TryGetProperty("baseColorFactor", out var bcf))
                {
                    var f = Floats(bcf);
                    baseColor = new Vector4(f[0], f[1], f[2], f[3]);
                }
                if (pbr.TryGetProperty("baseColorTexture", out var bct))
                    texture = LoadTexture(root, bct.GetProperty("index").GetInt32(), baseDir, buffers);
            }

            string alphaMode = m.TryGetProperty("alphaMode", out var am) ? am.GetString()! : "OPAQUE";
            // BLEND is drawn as a cutout too: models go through the opaque pass with no sorting.
            float cutoff = alphaMode switch
            {
                "MASK"  => m.TryGetProperty("alphaCutoff", out var ac) ? ac.GetSingle() : 0.5f,
                "BLEND" => 0.5f,
                _       => 0f,
            };
            list.Add(new ModelMaterial(baseColor, texture, cutoff));
        }
        return list;
    }

    private static ModelTextureData LoadTexture(JsonElement root, int textureIndex, string baseDir, byte[][] buffers)
    {
        var tex = root.GetProperty("textures")[textureIndex];
        var img = root.GetProperty("images")[tex.GetProperty("source").GetInt32()];

        byte[] encoded;
        if (img.TryGetProperty("uri", out var uri))
        {
            encoded = ReadUri(uri.GetString()!, baseDir);
        }
        else
        {
            var view = root.GetProperty("bufferViews")[img.GetProperty("bufferView").GetInt32()];
            int offset = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0;
            encoded = buffers[view.GetProperty("buffer").GetInt32()]
                .AsSpan(offset, view.GetProperty("byteLength").GetInt32()).ToArray();
        }
        var image = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);

        // glTF sampler defaults: repeat wrap, filtering up to the implementation (we pick linear).
        bool nearest = false, clampU = false, clampV = false;
        if (tex.TryGetProperty("sampler", out var si))
        {
            var s = root.GetProperty("samplers")[si.GetInt32()];
            nearest = s.TryGetProperty("magFilter", out var mag) && mag.GetInt32() == 9728; // NEAREST
            clampU  = s.TryGetProperty("wrapS", out var ws) && ws.GetInt32() == 33071;       // CLAMP_TO_EDGE
            clampV  = s.TryGetProperty("wrapT", out var wt) && wt.GetInt32() == 33071;
        }
        return new ModelTextureData(image.Width, image.Height, image.Data, nearest, clampU, clampV);
    }

    private static float[] Floats(JsonElement array) => array.EnumerateArray().Select(e => e.GetSingle()).ToArray();
}
