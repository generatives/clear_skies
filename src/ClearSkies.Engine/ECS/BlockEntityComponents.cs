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
/// One entity's own pose for its <see cref="ModelRenderer"/> model, so entities sharing a model animate
/// independently. Animation systems write <see cref="NodeRotations"/> (each node's local rotation, indexed like
/// <see cref="GpuModel.Nodes"/>; find a node with <see cref="GpuModel.FindNode"/>) and
/// <see cref="ModelRenderSystem"/> turns them into <see cref="Pose"/> when it draws the entity. Create it with
/// <see cref="For"/>, so both arrays match the model and start at its rest pose.
/// </summary>
public struct AnimatedModel
{
    public Quaternion<float>[] NodeRotations;

    /// <summary>Each node's model-space matrix, recomputed from <see cref="NodeRotations"/> on every draw.</summary>
    public Mat4[] Pose;

    public static AnimatedModel For(GpuModel model) => new()
    {
        NodeRotations = model.Nodes.Select(n => n.Rotation).ToArray(),
        Pose          = (Mat4[])model.RestPose.Clone(),
    };
}
