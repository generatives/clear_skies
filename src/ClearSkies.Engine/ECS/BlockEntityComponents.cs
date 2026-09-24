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

/// <summary>A lever: an arm the player drags across its range (see <see cref="LeverControlSystem"/>). On a ship it asks
/// for acceleration along the axis it levers on (see <see cref="AirshipFlightSystem"/>): at 1, the full lever
/// acceleration towards the lever's north face; at -1, towards its south face. A ship's levers on the same axis move together.</summary>
public struct Lever
{
    /// <summary>Where the arm is set, from -1 (fully to one side) through 0 (upright) to 1 (fully to the other).
    /// The arm levers north and south: towards 1 it leans to the block's north face (towards the player who placed
    /// it), towards -1 to its south face.</summary>
    public float Value;
}

/// <summary>A ship's wheel: the player grabs its rim and turns it (see <see cref="SteeringWheelControlSystem"/>), and
/// the ship's <see cref="Helm"/> heading turns with it, one for one: clockwise (as seen from its north face, where the
/// player who placed it stands) to starboard.</summary>
public struct SteeringWheel
{
    /// <summary>How far the wheel has been turned from where it was modelled, clockwise, in radians. Unbounded: it
    /// keeps counting past a full turn.</summary>
    public float Angle;
}
