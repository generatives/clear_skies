using System.Numerics;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering.Gltf;

/// <summary>
/// A loaded 3D model on the CPU (see <see cref="GltfLoader"/>): its node tree, the parts hanging off those nodes,
/// and the model-space bounds of the rest pose. <see cref="Nodes"/> is ordered parents first.
/// </summary>
public sealed record ModelData(IReadOnlyList<ModelNode> Nodes, IReadOnlyList<ModelPart> Parts,
                               Vector3D<float> BoundsMin, Vector3D<float> BoundsMax);

/// <summary>One node of a model's tree: its name (null if unnamed), parent index (-1 for a root, always lower than
/// this node's own index) and rest-pose local transform.</summary>
public sealed record ModelNode(string? Name, int Parent, Vector3D<float> Translation, Quaternion<float> Rotation,
                               Vector3D<float> Scale);

/// <summary>
/// One draw's worth of a model: triangles sharing a node and a material, in that node's local space (<see cref="Node"/>
/// is an index into <see cref="ModelData.Nodes"/>). Vertices use the engine's <see cref="Vertex"/> layout with
/// <see cref="Vertex.Color"/> the material's base colour (times any vertex colour) and <see cref="Vertex.Uv"/> the
/// normalized texture coordinate (z unused, 0).
/// </summary>
public sealed record ModelPart(int Node, Vertex[] Vertices, uint[] Indices, ModelMaterial Material);

/// <summary>Base colour factor, optional base-colour texture, and alpha-test cutoff (0 = opaque).</summary>
public sealed record ModelMaterial(Vector4 BaseColor, ModelTextureData? Texture, float AlphaCutoff)
{
    public static readonly ModelMaterial White = new(Vector4.One, null, 0f);
}

/// <summary>Decoded RGBA8 texture pixels plus how to sample them.</summary>
public sealed record ModelTextureData(int Width, int Height, byte[] Rgba, bool Nearest, bool ClampU, bool ClampV);
