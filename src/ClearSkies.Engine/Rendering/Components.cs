using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;

/// <summary>
/// Everything drawn for one loaded chunk (see ChunkRenderSystem): its greedy-meshed cubes and its model blocks.
/// Set by ChunkMeshSystem whenever the chunk remeshes; absent while the chunk has nothing to draw.
/// </summary>
public struct ChunkRenderData
{
    /// <summary>The chunk's cube faces, or null when it holds only model blocks.</summary>
    public GpuMesh? Mesh;

    /// <summary>Every static model block (<see cref="BlockDef.Model"/>, not an entity block) in the chunk; empty
    /// when there are none.</summary>
    public ModelBlock[] Models;

    /// <summary>Owning volume's registration in the shared voxel storage; the fragment shader looks light up
    /// through it.</summary>
    public GridHandle? Grid;

    /// <summary>This chunk's position in its volume (grid-local chunk coordinates).</summary>
    public ChunkPosition ChunkPos;
}

/// <summary>One placed model block: its shared model, cell in the chunk (chunk-local voxel coordinates) and the
/// voxel's stored orientation, which the model is turned to.</summary>
public readonly record struct ModelBlock(GpuModel Model, BlockId Block, byte X, byte Y, byte Z, BlockOrientation Orientation);

/// <summary>Always renders the mesh as a wireframe overlay regardless of the global WireframeMode.</summary>
public struct WireframeRenderer
{
    public GpuMesh Mesh;
}

/// <summary>Renders the mesh in screen space (HUD pipeline: depth always passes, no depth write). Vertices are in NDC.</summary>
public struct HudRenderer
{
    public GpuMesh Mesh;
}
/// <summary>Lights a <see cref="RenderedModel"/> entity from one voxel's stored light instead of just sun and
/// ambient: its volume's registration, and the cell's chunk and chunk-local position. Block entities get it (see
/// <c>BlockModelSystem</c>), so they are lit like the static model blocks around them.</summary>
public struct VoxelLit
{
    public GridHandle Grid;
    public ChunkPosition Chunk;
    public Vector3D<int> Cell;
}

/// <summary>
/// Draws a 3D model (e.g. a glTF prop loaded via <see cref="ClearSkies.Engine.Rendering.Gltf.GltfLoader"/> and
/// uploaded with <c>Renderer.UploadModel</c>, or a block entity's model) at the entity's <c>Transform</c>, by
/// <c>ModelRenderSystem</c>, in this entity's own pose. The <see cref="GpuModel"/> is shared by every entity using it;
/// the node rotations are this entity's alone, so entities sharing a model animate independently.
///
/// Animation systems pose nodes by name — <c>rm.SetRotationFromRest("arm_group", swing)</c> — and the renderer turns
/// the rotations into the pose it draws with. A name refers to the first node with that name (see
/// <see cref="GpuModel.FindNode"/>); a name the model doesn't have is ignored and the setter returns false. The
/// component holds references to its arrays, so the setters work on the copy <c>Entity.Get</c> hands back and need no
/// <c>Set</c> afterwards. Create it with the constructor; it starts at the model's rest pose.
/// </summary>
public struct RenderedModel
{
    private readonly GpuModel _model;
    private readonly Quaternion<float>[] _rotations; // each node's local rotation, indexed like _model.Nodes
    private readonly Mat4[] _pose;                   // each node's model-space matrix, as of the last ComputePose

    public RenderedModel(GpuModel model)
    {
        _model     = model;
        _rotations = model.Nodes.Select(n => n.Rotation).ToArray();
        _pose      = (Mat4[])model.RestPose.Clone();
    }

    public readonly GpuModel Model => _model;

    public readonly bool HasNode(string node) => _model.FindNode(node) >= 0;

    /// <summary>Sets <paramref name="node"/>'s local rotation outright.</summary>
    public readonly bool SetRotation(string node, Quaternion<float> rotation)
    {
        int i = _model.FindNode(node);
        if (i < 0) return false;
        _rotations[i] = rotation;
        return true;
    }

    /// <summary>Sets <paramref name="node"/>'s local rotation to its rest rotation turned further by
    /// <paramref name="offset"/> (in the node's own frame) — e.g. an arm swung by an angle from where it was
    /// modelled.</summary>
    public readonly bool SetRotationFromRest(string node, Quaternion<float> offset)
    {
        int i = _model.FindNode(node);
        if (i < 0) return false;
        _rotations[i] = _model.Nodes[i].Rotation * offset;
        return true;
    }

    /// <summary>Puts <paramref name="node"/> back at its rest rotation.</summary>
    public readonly bool ResetRotation(string node) => SetRotationFromRest(node, Quaternion<float>.Identity);

    /// <summary>Puts every node back at its rest rotation.</summary>
    public readonly void ResetAll()
    {
        for (int i = 0; i < _rotations.Length; i++) _rotations[i] = _model.Nodes[i].Rotation;
    }

    /// <summary>Recomputes the pose from the current node rotations and returns it: one model-space matrix per
    /// node, for <c>Renderer.DrawModel</c>. Called by the renderer for visible entities only.</summary>
    public readonly ReadOnlySpan<Mat4> ComputePose()
    {
        _model.ComputePose(_pose, _rotations);
        return _pose;
    }
}
