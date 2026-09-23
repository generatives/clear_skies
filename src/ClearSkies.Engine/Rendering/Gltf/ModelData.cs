using System.Numerics;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering.Gltf;

/// <summary>A loaded 3D model on the CPU (see <see cref="GltfLoader"/>): model-space parts plus their bounds.</summary>
public sealed record ModelData(IReadOnlyList<ModelPart> Parts, Vector3D<float> BoundsMin, Vector3D<float> BoundsMax);

/// <summary>
/// One draw's worth of a model: triangles sharing a material. Vertices use the engine's <see cref="Vertex"/>
/// layout with model-space positions, <see cref="Vertex.Color"/> the material's base colour (times any vertex
/// colour) and <see cref="Vertex.Uv"/> the normalized texture coordinate (z unused, 0).
/// </summary>
public sealed record ModelPart(Vertex[] Vertices, uint[] Indices, ModelMaterial Material);

/// <summary>Base colour factor, optional base-colour texture, and alpha-test cutoff (0 = opaque).</summary>
public sealed record ModelMaterial(Vector4 BaseColor, ModelTextureData? Texture, float AlphaCutoff)
{
    public static readonly ModelMaterial White = new(Vector4.One, null, 0f);
}

/// <summary>Decoded RGBA8 texture pixels plus how to sample them.</summary>
public sealed record ModelTextureData(int Width, int Height, byte[] Rgba, bool Nearest, bool ClampU, bool ClampV);
