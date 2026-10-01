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
    /// <summary>The chunk's opaque cube faces, or null when it has none.</summary>
    public GpuMesh? Mesh;

    /// <summary>The chunk's <see cref="RenderLayer.Cutout"/> cube faces (glass), drawn with the opaque world minus their
    /// clear texels; null when it has none.</summary>
    public GpuMesh? CutoutMesh;

    /// <summary>The chunk's <see cref="RenderLayer.Translucent"/> cube faces (water), drawn alpha-blended after the
    /// opaque world; null when it has none.</summary>
    public GpuMesh? TransparentMesh;

    /// <summary>Each of the chunk's meshes that it has.</summary>
    public readonly IEnumerable<GpuMesh> Meshes()
    {
        if (Mesh != null) yield return Mesh;
        if (CutoutMesh != null) yield return CutoutMesh;
        if (TransparentMesh != null) yield return TransparentMesh;
    }

    /// <summary>True when it has nothing to draw.</summary>
    public readonly bool IsEmpty => Mesh == null && CutoutMesh == null && TransparentMesh == null && Models.Length == 0;

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
/// the node poses are this entity's alone, so entities sharing a model animate independently.
///
/// Animation systems pose nodes by name — <c>rm.SetRotationFromRest("arm_group", swing)</c>, or
/// <c>SetTranslationFromRest</c> to slide one — and the renderer turns the poses into the matrices it draws with. A name
/// refers to the first node with that name (see <see cref="GpuModel.FindNode"/>); a name the model doesn't have is
/// ignored and the setter returns false. The component holds references to its arrays, so the setters work on the copy
/// <c>Entity.Get</c> hands back and need no <c>Set</c> afterwards. Create it with the constructor; it starts at the
/// model's rest pose.
///
/// Nodes posed by ticks are drawn between their last two ticked poses, like Transforms (see
/// <see cref="ClearSkies.Engine.ECS.TickInterpolationSystem"/>, which records each tick's poses with
/// <see cref="EndTick"/>): the poses set here stay the true pose, and <see cref="ComputeDrawnPose"/> blends. A node set
/// outside the ticks, so that it no longer matches the last tick's pose, is drawn as set.
/// </summary>
public struct RenderedModel
{
    private readonly GpuModel _model;
    private readonly NodePose[] _nodes;   // each node's local pose, indexed like _model.Nodes
    private readonly Mat4[] _matrices;    // each node's model-space matrix, as of the last ComputeDrawnPose
    private readonly Ticked _ticked;

    // Each node's pose as of the last two ticks, and the poses last drawn with.
    private sealed class Ticked
    {
        public NodePose[] Previous, Current;
        public readonly NodePose[] Drawn;
        public bool Started;

        public Ticked(int nodes) { Previous = new NodePose[nodes]; Current = new NodePose[nodes]; Drawn = new NodePose[nodes]; }
    }

    public RenderedModel(GpuModel model)
    {
        _model    = model;
        _nodes    = model.Nodes.Select(n => n.Rest).ToArray();
        _matrices = (Mat4[])model.RestPose.Clone();
        _ticked   = new Ticked(_nodes.Length);
    }

    public readonly GpuModel Model => _model;

    public readonly bool HasNode(string node) => _model.FindNode(node) >= 0;

    /// <summary>Sets <paramref name="node"/>'s local pose outright.</summary>
    public readonly bool SetPose(string node, NodePose pose)
    {
        int i = _model.FindNode(node);
        if (i < 0) return false;
        _nodes[i] = pose;
        return true;
    }

    /// <summary>Sets <paramref name="node"/>'s local rotation outright, keeping its translation and scale.</summary>
    public readonly bool SetRotation(string node, Quaternion<float> rotation)
    {
        int i = _model.FindNode(node);
        if (i < 0) return false;
        _nodes[i] = _nodes[i] with { Rotation = rotation };
        return true;
    }

    /// <summary>Sets <paramref name="node"/>'s local rotation to its rest rotation turned further by
    /// <paramref name="offset"/> (in the node's own frame), keeping its translation and scale — e.g. an arm swung by an
    /// angle from where it was modelled.</summary>
    public readonly bool SetRotationFromRest(string node, Quaternion<float> offset)
    {
        int i = _model.FindNode(node);
        if (i < 0) return false;
        _nodes[i] = _nodes[i] with { Rotation = _model.Nodes[i].Rotation * offset };
        return true;
    }

    /// <summary>Sets <paramref name="node"/>'s local translation outright, keeping its rotation and scale.</summary>
    public readonly bool SetTranslation(string node, Vector3D<float> translation)
    {
        int i = _model.FindNode(node);
        if (i < 0) return false;
        _nodes[i] = _nodes[i] with { Translation = translation };
        return true;
    }

    /// <summary>Sets <paramref name="node"/>'s local translation to its rest translation moved by
    /// <paramref name="offset"/> (in its parent's frame), keeping its rotation and scale — e.g. a slider pushed along
    /// its track.</summary>
    public readonly bool SetTranslationFromRest(string node, Vector3D<float> offset)
    {
        int i = _model.FindNode(node);
        if (i < 0) return false;
        _nodes[i] = _nodes[i] with { Translation = _model.Nodes[i].Translation + offset };
        return true;
    }

    /// <summary>Puts <paramref name="node"/> back at its rest pose.</summary>
    public readonly bool ResetPose(string node)
    {
        int i = _model.FindNode(node);
        if (i < 0) return false;
        _nodes[i] = _model.Nodes[i].Rest;
        return true;
    }

    /// <summary>Puts every node back at its rest pose.</summary>
    public readonly void ResetAll()
    {
        for (int i = 0; i < _nodes.Length; i++) _nodes[i] = _model.Nodes[i].Rest;
    }

    /// <summary>End of a tick: records the tick's poses. The first tick recorded starts there, with nothing to blend
    /// from.</summary>
    public readonly void EndTick()
    {
        var t = _ticked;
        if (t is null) return; // default-constructed: nothing to record
        if (!t.Started)
        {
            _nodes.CopyTo(t.Previous, 0);
            _nodes.CopyTo(t.Current, 0);
            t.Started = true;
            return;
        }
        (t.Previous, t.Current) = (t.Current, t.Previous);
        _nodes.CopyTo(t.Current, 0);
    }

    /// <summary>Computes the pose to draw and returns it: one model-space matrix per node, for
    /// <c>Renderer.DrawModel</c>, with each node <paramref name="alpha"/> of the way from its previous tick's pose to its
    /// latest (see <see cref="EndTick"/>). Called by the renderer for visible entities only.</summary>
    public readonly ReadOnlySpan<Mat4> ComputeDrawnPose(float alpha)
    {
        var t = _ticked;
        for (int i = 0; i < _nodes.Length; i++)
        {
            var pose = _nodes[i];
            t.Drawn[i] = t.Started && pose == t.Current[i] && t.Previous[i] != pose
                ? NodePose.Lerp(t.Previous[i], pose, alpha)
                : pose;
        }
        _model.ComputePose(_matrices, t.Drawn);
        return _matrices;
    }
}
