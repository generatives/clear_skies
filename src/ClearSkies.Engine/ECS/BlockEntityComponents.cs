using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// On every block entity (see <see cref="BlockDef.Components"/>): the voxel it belongs to, so any system can get
/// from the entity back to its block. Set by <see cref="ChunkVolume"/>, which creates and destroys block entities
/// with their voxels; the voxel is the source of truth, so these fields never change after creation — a block
/// replaced by a different type or facing gets a new entity.
/// </summary>
public struct BlockRef
{
    public ChunkVolume Volume;

    /// <summary>The block's cell in <see cref="Volume"/>'s own space (world space for the static world,
    /// grid-local space for a dynamic grid).</summary>
    public Vector3D<int> Position;

    public Facing  Facing;
    public BlockId Id;
}

/// <summary>A lever's state. Placeholder behaviour for now: the lever is the first entity block, used to exercise
/// the block entity lifecycle and rendering.</summary>
public struct Lever
{
    public bool On;
}

/// <summary>
/// One entity's own pose for a model, so entities sharing a model animate independently. Animation systems pose
/// nodes by name — <c>anim.SetRotationFromRest("arm_group", swing)</c> — and <see cref="ModelRenderSystem"/> turns
/// the node rotations into the pose it draws with. Create it with <see cref="For"/>, for the same model as the
/// entity's <see cref="ModelRenderer"/>; it starts at the model's rest pose.
///
/// A name refers to the first node with that name (see <see cref="GpuModel.FindNode"/>); a name the model doesn't
/// have is ignored, and the setters return false. The component holds references to its arrays, so the setters
/// work on the copy <c>Entity.Get</c> hands back and need no <c>Set</c> afterwards.
/// </summary>
public struct AnimatedModel
{
    private GpuModel _model;
    private Quaternion<float>[] _rotations; // each node's local rotation, indexed like _model.Nodes
    private Mat4[] _pose;                   // each node's model-space matrix, as of the last ComputePose

    public static AnimatedModel For(GpuModel model) => new()
    {
        _model     = model,
        _rotations = model.Nodes.Select(n => n.Rotation).ToArray(),
        _pose      = (Mat4[])model.RestPose.Clone(),
    };

    /// <summary>The model this pose is for.</summary>
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
