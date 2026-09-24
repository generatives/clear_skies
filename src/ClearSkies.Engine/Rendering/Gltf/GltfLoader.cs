using System.Numerics;
using SharpGLTF.Schema2;
using Silk.NET.Maths;
using StbImageSharp;

namespace ClearSkies.Engine.Rendering.Gltf;

/// <summary>
/// Loads a glTF 2.0 model (<c>.gltf</c> or <c>.glb</c>, via SharpGLTF) into a <see cref="ModelData"/>, keeping the
/// default scene's node tree so a node can be posed at draw time (see <c>AnimatedModel</c>): each mesh's triangles
/// become parts in its node's local space, one per node and material, with shared vertices de-duplicated.
///
/// A skinned mesh is bound rigidly: each vertex is moved into the space of the joint that weighs most on it and
/// its part hangs off that joint's node, so posing the joint moves it. That is exact for Blockbench's exports
/// (every vertex fully bound to one bone, which is how its groups come out) and approximate for blended skins.
///
/// glTF's conventions (right-handed, +Y up, counter-clockwise front faces, 1 unit = 1 metre) already match the
/// engine's (1 unit = 1 block), so nothing is converted. Only the base colour (factor, texture and its sampler) and
/// alpha mode are read from materials. Pure data (no WebGPU dependency); <c>Renderer.UploadModel</c> turns it into
/// GPU resources.
/// </summary>
public static class GltfLoader
{
    public static ModelData Load(string path)
    {
        var model = ModelRoot.Load(path);
        var scene = model.DefaultScene ?? model.LogicalScenes.FirstOrDefault()
                    ?? throw new InvalidDataException($"glTF '{path}' has no scene.");

        // Node tree, parents first.
        var nodes = new List<ModelNode>();
        var nodeIndex = new Dictionary<Node, int>();
        var order = new List<Node>();
        void Visit(Node n, int parent)
        {
            int i = nodes.Count;
            nodeIndex[n] = i;
            order.Add(n);
            Matrix4x4.Decompose(n.LocalMatrix, out var s, out var r, out var t);
            nodes.Add(new ModelNode(n.Name, parent, new(t.X, t.Y, t.Z), new(r.X, r.Y, r.Z, r.W), new(s.X, s.Y, s.Z)));
            foreach (var c in n.VisualChildren) Visit(c, i);
        }
        foreach (var root in scene.VisualChildren) Visit(root, -1);

        var materials = new Dictionary<Material, ModelMaterial>();
        var parts = new Dictionary<(int Node, ModelMaterial Material), PartBuilder>();
        foreach (var node in order)
        {
            if (node.Mesh is null) continue;
            foreach (var prim in node.Mesh.Primitives)
                AddPrimitive(prim, node, nodeIndex, materials, parts);
        }
        if (parts.Count == 0) throw new InvalidDataException($"glTF '{path}' has no triangle geometry in its scene.");

        var result = parts.Select(kv => new ModelPart(kv.Key.Node, kv.Value.Vertices.ToArray(), kv.Value.Indices.ToArray(),
                                                      kv.Key.Material)).ToList();
        var (min, max) = RestBounds(nodes, result);
        return new ModelData(nodes, result, min, max);
    }

    private static void AddPrimitive(MeshPrimitive prim, Node node, Dictionary<Node, int> nodeIndex,
                                     Dictionary<Material, ModelMaterial> materials,
                                     Dictionary<(int, ModelMaterial), PartBuilder> parts)
    {
        if (prim.DrawPrimitiveType is not (PrimitiveType.TRIANGLES or PrimitiveType.TRIANGLE_STRIP or PrimitiveType.TRIANGLE_FAN))
            return;
        var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
        if (positions is null) return;
        var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var uvs     = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        var colors  = prim.GetVertexAccessor("COLOR_0")?.AsColorArray();

        var material = prim.Material is null ? ModelMaterial.White
            : materials.TryGetValue(prim.Material, out var m) ? m
            : materials[prim.Material] = ConvertMaterial(prim.Material);

        // Where each vertex hangs: its own node, or for a skinned mesh the node of its heaviest joint, with the
        // position/normal moved into that joint's space by its inverse bind matrix.
        int[] vertexNode = new int[positions.Count];
        Matrix4x4[]? toNode = null;
        var skin = node.Skin;
        var joints  = skin is null ? null : prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
        var weights = skin is null ? null : prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
        if (skin is not null && joints is not null && weights is not null)
        {
            toNode = new Matrix4x4[positions.Count];
            for (int v = 0; v < positions.Count; v++)
            {
                var j = joints[v]; var w = weights[v];
                float jj = j.X, ww = w.X;
                if (w.Y > ww) { ww = w.Y; jj = j.Y; }
                if (w.Z > ww) { ww = w.Z; jj = j.Z; }
                if (w.W > ww) { jj = j.W; }
                var (jointNode, inverseBind) = skin.GetJoint((int)jj);
                vertexNode[v] = nodeIndex.TryGetValue(jointNode, out var ni) ? ni : -1;
                toNode[v] = inverseBind;
            }
        }
        else
        {
            Array.Fill(vertexNode, nodeIndex[node]);
        }

        foreach (var (a, b, c) in prim.GetTriangleIndices())
        {
            // A triangle goes with its first vertex's node (they agree for rigidly bound skins).
            int n = vertexNode[a];
            if (!parts.TryGetValue((n, material), out var part))
                parts[(n, material)] = part = new PartBuilder();

            Vector3 P(int i) => toNode is null ? positions[i] : Vector3.Transform(positions[i], toNode[i]);
            var pa = P(a); var pb = P(b); var pc = P(c);
            // Primitives exported without normals (or with zero ones): use the face normal instead.
            var faceNormal = Vector3.Normalize(Vector3.Cross(pb - pa, pc - pa));
            foreach (var (i, p) in new[] { (a, pa), (b, pb), (c, pc) })
            {
                var normal = normals is null ? Vector3.Zero
                    : toNode is null ? normals[i] : Vector3.TransformNormal(normals[i], toNode[i]);
                normal = normal == Vector3.Zero ? faceNormal : Vector3.Normalize(normal);
                var color = material.BaseColor * (colors is null ? Vector4.One : colors[i]);
                part.Add(p, normal, new Vector3(color.X, color.Y, color.Z), uvs is null ? Vector2.Zero : uvs[i]);
            }
        }
    }

    /// <summary>Model-space bounds of every part with every node at its rest pose.</summary>
    private static (Vector3D<float> Min, Vector3D<float> Max) RestBounds(List<ModelNode> nodes, List<ModelPart> parts)
    {
        var world = new Matrix4x4[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var local = Matrix4x4.CreateScale(n.Scale.X, n.Scale.Y, n.Scale.Z)
                      * Matrix4x4.CreateFromQuaternion(new Quaternion(n.Rotation.X, n.Rotation.Y, n.Rotation.Z, n.Rotation.W))
                      * Matrix4x4.CreateTranslation(n.Translation.X, n.Translation.Y, n.Translation.Z);
            world[i] = n.Parent < 0 ? local : local * world[n.Parent]; // System.Numerics: row vectors, child first
        }

        var min = new Vector3D<float>(float.MaxValue);
        var max = new Vector3D<float>(float.MinValue);
        foreach (var part in parts)
        foreach (var v in part.Vertices)
        {
            var p = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
            if (part.Node >= 0) p = Vector3.Transform(p, world[part.Node]);
            min = Vector3D.Min(min, new Vector3D<float>(p.X, p.Y, p.Z));
            max = Vector3D.Max(max, new Vector3D<float>(p.X, p.Y, p.Z));
        }
        return (min, max);
    }

    /// <summary>Collects one part's triangles, sharing identical vertices.</summary>
    private sealed class PartBuilder
    {
        public readonly List<Vertex> Vertices = new();
        public readonly List<uint> Indices = new();
        private readonly Dictionary<(Vector3, Vector3, Vector3, Vector2), uint> _index = new();

        public void Add(Vector3 position, Vector3 normal, Vector3 color, Vector2 uv)
        {
            var key = (position, normal, color, uv);
            if (!_index.TryGetValue(key, out uint i))
            {
                i = (uint)Vertices.Count;
                _index[key] = i;
                Vertices.Add(new Vertex
                {
                    Position = new Vector3D<float>(position.X, position.Y, position.Z),
                    Normal   = new Vector3D<float>(normal.X, normal.Y, normal.Z),
                    Color    = new Vector3D<float>(color.X, color.Y, color.Z),
                    Uv       = new Vector3D<float>(uv.X, uv.Y, 0f),
                });
            }
            Indices.Add(i);
        }
    }

    private static ModelMaterial ConvertMaterial(Material material)
    {
        var baseColor = Vector4.One;
        ModelTextureData? texture = null;
        if (material.FindChannel("BaseColor") is { } channel)
        {
            baseColor = channel.Color;
            if (channel.Texture?.PrimaryImage?.Content is { IsValid: true } image)
                texture = DecodeTexture(image.Content, channel.TextureSampler);
        }

        // BLEND is drawn as a cutout too: models go through the opaque pass with no sorting.
        float cutoff = material.Alpha switch
        {
            AlphaMode.MASK  => material.AlphaCutoff,
            AlphaMode.BLEND => 0.5f,
            _               => 0f,
        };
        return new ModelMaterial(baseColor, texture, cutoff);
    }

    private static ModelTextureData DecodeTexture(ReadOnlyMemory<byte> encoded, TextureSampler? sampler)
    {
        var image = ImageResult.FromMemory(encoded.ToArray(), ColorComponents.RedGreenBlueAlpha);

        // glTF sampler defaults: repeat wrap, filtering up to the implementation (we pick linear).
        bool nearest = sampler?.MagFilter == TextureInterpolationFilter.NEAREST;
        bool clampU  = sampler?.WrapS == TextureWrapMode.CLAMP_TO_EDGE;
        bool clampV  = sampler?.WrapT == TextureWrapMode.CLAMP_TO_EDGE;
        return new ModelTextureData(image.Width, image.Height, image.Data, nearest, clampU, clampV);
    }
}
