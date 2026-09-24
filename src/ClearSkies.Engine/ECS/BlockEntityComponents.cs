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

/// <summary>Marks a Fan block entity: a thruster that <see cref="AirshipFlightSystem"/> allocates its ship's
/// desired force and torque across, pushing against the block's facing. Thrust limits are the flight system's
/// tuning for now.</summary>
public struct Fan
{
}

/// <summary>Marks a Buoyant block entity: constant passive lift, applied at the block by
/// <see cref="AirshipFlightSystem"/> (strength is the flight system's tuning for now).</summary>
public struct Buoyant
{
}

/// <summary>A lever's state. Placeholder behaviour for now: the lever is the first entity block, used to exercise
/// the block entity lifecycle and rendering.</summary>
public struct Lever
{
    public bool On;
}
