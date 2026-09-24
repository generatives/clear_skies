using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// On every block entity (see <see cref="BlockDef.Components"/>): the voxel it belongs to, so any system can get
/// from the entity back to its block. Set by <see cref="ChunkVolume"/>, which creates and destroys block entities
/// with their voxels; the voxel is the source of truth, so these fields never change after creation — a block
/// replaced by a different type or orientation gets a new entity.
/// </summary>
public struct BlockRef
{
    public ChunkVolume Volume;

    /// <summary>The block's cell in <see cref="Volume"/>'s own space (world space for the static world,
    /// grid-local space for a dynamic grid).</summary>
    public Vector3D<int> Position;

    public BlockOrientation Orientation;
    public BlockId          Id;
}

/// <summary>Marks a Fan block entity: a thruster that <see cref="AirshipFlightSystem"/> allocates its ship's
/// desired force and torque across, pushing opposite the way the block's top faces. Thrust limits are the flight system's
/// tuning for now.</summary>
public struct Fan
{
}

/// <summary>Marks a Buoyant block entity: constant passive lift, applied at the block by
/// <see cref="AirshipFlightSystem"/> (strength is the flight system's tuning for now).</summary>
public struct Buoyant
{
}

/// <summary>Marks a block entity the player can use: clicking it publishes <see cref="BlockInteraction"/>s for it
/// (see <see cref="PlayerInputSystem"/>) instead of placing a block against it.</summary>
public struct Interactive
{
}

/// <summary>A lever: an arm the player drags across its range (see <see cref="LeverControlSystem"/>).</summary>
public struct Lever
{
    /// <summary>Where the arm is set, from -1 (fully to one side) through 0 (upright) to 1 (fully to the other).
    /// Towards -1 the arm leans to the block's own +X; towards 1, to its -X.</summary>
    public float Value;
}
