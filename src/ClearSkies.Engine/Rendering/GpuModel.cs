using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering.Gltf;
using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// A 3D model uploaded to the GPU by <see cref="Renderer.UploadModel"/>: its node tree, one mesh + texture per node
/// and material in that node's local space, and the model-space bounds of the rest pose that renderers frustum-cull
/// with. Shareable between any number of entities; a pose (one model-space matrix per node, see
/// <see cref="ComputePose"/>) is supplied per draw, defaulting to <see cref="RestPose"/>.
/// </summary>
public sealed class GpuModel : IDisposable
{
    public IReadOnlyList<ModelNode> Nodes { get; }
    public IReadOnlyList<GpuModelPart> Parts { get; }
    public Vector3D<float> BoundsMin { get; }
    public Vector3D<float> BoundsMax { get; }

    /// <summary>Each node's model-space matrix with every node at its rest transform.</summary>
    public Mat4[] RestPose { get; }

    // For each node, whether it is the first node carrying its name (the one a name-keyed override poses).
    private readonly bool[] _firstOfName;

    internal GpuModel(IReadOnlyList<ModelNode> nodes, IReadOnlyList<GpuModelPart> parts,
                      Vector3D<float> boundsMin, Vector3D<float> boundsMax)
    {
        Nodes     = nodes;
        Parts     = parts;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;

        var seen = new HashSet<string>();
        _firstOfName = nodes.Select(n => n.Name != null && seen.Add(n.Name)).ToArray();

        RestPose = new Mat4[nodes.Count];
        ComputePose(RestPose, null);
    }

    /// <summary>Fills <paramref name="pose"/> (one entry per node) with each node's model-space matrix, replacing the
    /// local rotation of every node named in <paramref name="rotations"/> (the first node of that name in
    /// <see cref="Nodes"/>, which lists parents first).</summary>
    public void ComputePose(Span<Mat4> pose, IReadOnlyDictionary<string, Quaternion<float>>? rotations)
    {
        for (int i = 0; i < Nodes.Count; i++)
        {
            var n = Nodes[i];
            var rotation = n.Rotation;
            if (rotations != null && _firstOfName[i] && rotations.TryGetValue(n.Name!, out var r)) rotation = r;

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
