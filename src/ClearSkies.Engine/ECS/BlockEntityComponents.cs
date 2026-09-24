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
/// Per-node pose overrides for an entity's model (a block entity's, drawn by <see cref="BlockEntityRenderSystem"/>):
/// each entry replaces the local rotation of the model node with that name, e.g. the lever's <c>arm_group</c>.
/// When several nodes share a name, the first in the model's node tree (parents first) is the one overridden —
/// for the lever, the parent of its arm's joint, which pivots the arm at the same point. Set the component again
/// (<c>Entity.Set</c>) after changing it, or share one dictionary and mutate it in place; the renderer reads it
/// every frame either way.
/// </summary>
public struct AnimatedModel
{
    public Dictionary<string, Quaternion<float>> NodeRotations;
}
