using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering.Gltf;
using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// A 3D model uploaded to the GPU by <see cref="Renderer.UploadModel"/>: its node tree, one mesh + texture per node
/// and material in that node's local space, and the model-space bounds of the rest pose that renderers frustum-cull
/// with. Shareable between any number of entities: an entity that animates keeps its own node rotations and pose
/// (see <c>RenderedModel</c> and <see cref="ComputePose"/>); anything else is drawn at <see cref="RestPose"/>.
/// </summary>
public sealed class GpuModel : IDisposable
{
    public IReadOnlyList<ModelNode> Nodes { get; }
    public IReadOnlyList<GpuModelPart> Parts { get; }
    public Vector3D<float> BoundsMin { get; }
    public Vector3D<float> BoundsMax { get; }

    /// <summary>Each node's model-space matrix with every node at its rest transform.</summary>
    public Mat4[] RestPose { get; }

    internal GpuModel(IReadOnlyList<ModelNode> nodes, IReadOnlyList<GpuModelPart> parts,
                      Vector3D<float> boundsMin, Vector3D<float> boundsMax)
    {
        Nodes     = nodes;
        Parts     = parts;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;

        var index = new Dictionary<string, int>();
        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i].Name is { } name) index.TryAdd(name, i);
        _nodeIndex = index;

        RestPose = new Mat4[nodes.Count];
        ComputePose(RestPose, default);
    }

    // Node name -> index of the first node with that name in Nodes (parents first).
    private readonly Dictionary<string, int> _nodeIndex;

    /// <summary>Index of the first node named <paramref name="name"/> in <see cref="Nodes"/> (parents first), or -1.
    /// When several nodes share a name (Blockbench exports a skinned group's joint and its parent under the group's
    /// name), that is the outermost one.</summary>
    public int FindNode(string name) => _nodeIndex.TryGetValue(name, out int i) ? i : -1;

    /// <summary>Fills <paramref name="pose"/> (one entry per node) with each node's model-space matrix, using
    /// <paramref name="rotations"/> (one per node) as the nodes' local rotations in place of their rest rotations;
    /// empty uses the rest rotations. Translation and scale always stay at rest.</summary>
    public void ComputePose(Span<Mat4> pose, ReadOnlySpan<Quaternion<float>> rotations)
    {
        for (int i = 0; i < Nodes.Count; i++)
        {
            var n = Nodes[i];
            var rotation = rotations.IsEmpty ? n.Rotation : rotations[i];

            var local = Mat4.Multiply(Mat4.Translation(n.Translation),
                        Mat4.Multiply(Mat4.FromQuaternion(rotation), Mat4.Scale(n.Scale)));
            pose[i] = n.Parent < 0 ? local : Mat4.Multiply(pose[n.Parent], local);
        }
    }

    public void Dispose()
    {
        foreach (var p in Parts)
        {
            p.Mesh.Dispose();
            p.Texture.Dispose();
        }
    }
}

/// <summary>One node's triangles in one material: the node it hangs off (index into <see cref="GpuModel.Nodes"/>,
/// -1 for model space), the mesh, its base-colour texture and the alpha-test cutoff (0 = opaque).</summary>
public sealed record GpuModelPart(int Node, GpuMesh Mesh, ModelTexture Texture, float AlphaCutoff);
