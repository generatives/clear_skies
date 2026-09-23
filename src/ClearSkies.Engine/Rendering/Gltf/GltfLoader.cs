using System.Numerics;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Schema2;
using Silk.NET.Maths;
using StbImageSharp;

namespace ClearSkies.Engine.Rendering.Gltf;

/// <summary>
/// Loads a glTF 2.0 model (<c>.gltf</c> or <c>.glb</c>, via SharpGLTF) into a <see cref="ModelData"/>: every triangle
/// in the default scene, evaluated into model space by SharpGLTF (node transforms applied, skinned meshes at their
/// rest pose), merged into one part per material with shared vertices de-duplicated.
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

        var materials = new Dictionary<Material, ModelMaterial>();
        var parts = new Dictionary<ModelMaterial, PartBuilder>();
        var min = new Vector3D<float>(float.MaxValue);
        var max = new Vector3D<float>(float.MinValue);

        foreach (var tri in scene.EvaluateTriangles<VertexPositionNormal, VertexColor1Texture1>())
        {
            var material = tri.Material is null ? ModelMaterial.White
                : materials.TryGetValue(tri.Material, out var m) ? m
                : materials[tri.Material] = ConvertMaterial(tri.Material);
            if (!parts.TryGetValue(material, out var part))
                parts[material] = part = new PartBuilder();

            // Primitives exported without normals come back with zero ones: use the face normal instead.
            var faceNormal = Vector3.Normalize(Vector3.Cross(tri.B.Position - tri.A.Position, tri.C.Position - tri.A.Position));
            foreach (var v in new[] { tri.A, tri.B, tri.C })
            {
                var n = v.Geometry.Normal == Vector3.Zero ? faceNormal : Vector3.Normalize(v.Geometry.Normal);
                var color = material.BaseColor * v.Material.Color;
                part.Add(v.Position, n, new Vector3(color.X, color.Y, color.Z), v.Material.TexCoord);

                var p = new Vector3D<float>(v.Position.X, v.Position.Y, v.Position.Z);
                min = Vector3D.Min(min, p);
                max = Vector3D.Max(max, p);
            }
        }

        if (parts.Count == 0) throw new InvalidDataException($"glTF '{path}' has no triangle geometry in its scene.");
        var result = parts.Select(kv => new ModelPart(kv.Value.Vertices.ToArray(), kv.Value.Indices.ToArray(), kv.Key))
                          .ToList();
        return new ModelData(result, min, max);
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
