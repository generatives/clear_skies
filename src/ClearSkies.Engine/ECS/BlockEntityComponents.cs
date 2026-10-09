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
    /// <summary>How hard it pushed last tick, as a fraction (0-1) of its max: 0 when off. Set by the flight system on
    /// the machine flying its ship, and synced to everyone else (see <see cref="Entities.SyncedState"/>).</summary>
    public float Thrust;
}

/// <summary>Marks a Buoyant block entity: constant passive lift, applied at the block by
/// <see cref="AirshipFlightSystem"/> (strength is the flight system's tuning for now).</summary>
public struct Buoyant
{
}

/// <summary>Marks a block entity the player can use: clicking it publishes <see cref="BlockInteraction"/>s for it
/// (see <see cref="BlockActionSystem"/>) instead of placing a block against it.</summary>
public struct Interactive
{
}

/// <summary>A lever: an arm the player drags across its range (see <see cref="LeverControlSystem"/>), showing and setting
/// its ship's thrust along the axis it levers on (see <see cref="ShipControls"/>): leaning fully to its north face asks
/// for full thrust towards that face, fully to its south face full thrust the other way, upright none. It holds no
/// setting of its own.</summary>
public struct Lever
{
}

/// <summary>A ship's wheel: the player grabs its rim and turns it (see <see cref="SteeringWheelControlSystem"/>),
/// showing and setting its ship's turn (see <see cref="ShipControls.Turn"/>): turned fully clockwise (as seen from its
/// north face, where the player who placed it stands), full turn to starboard. It holds no setting of its own.</summary>
public struct SteeringWheel
{
    /// <summary>How far the wheel turns either way from centred (radians), at full turn: half a turn.</summary>
    public const float MaxAngle = MathF.PI;

    /// <summary>How far a wheel is turned (clockwise, radians) for its ship's current turn setting.</summary>
    public static float Angle(in BlockRef wheel) => ShipControls.Of(wheel.Volume).Turn * MaxAngle;
}
