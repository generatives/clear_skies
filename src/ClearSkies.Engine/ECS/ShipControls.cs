using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>One of a ship's three thrust axes, in its own frame.</summary>
public enum ThrustAxis : byte
{
    /// <summary>Towards the ship's north (-Z).</summary>
    Forward,
    /// <summary>Towards its east (+X).</summary>
    Right,
    /// <summary>Towards its top (+Y).</summary>
    Up,
}

/// <summary>
/// What a ship's own controls ask of it: the real state its <see cref="Lever"/>s, <see cref="SteeringWheel"/>s and
/// <see cref="Toggle"/>s show and set, like a UI over it. Set by the SetShipThrust, SetShipTurn and SetShipAnchored
/// commands, and read by <see cref="AirshipFlightSystem"/> and <see cref="AnchorSystem"/>. Lives on the volume's root entity (a grid, or the static world, where it does
/// nothing); a volume without one asks for nothing.
///
/// Every lever on one axis shows that axis' setting (see <see cref="LeverControlSystem"/>) and every wheel shows the
/// turn (see <see cref="SteeringWheelControlSystem"/>), so they always agree, a new lever or wheel shows the ship's
/// setting as soon as it's placed, and taking controls away or putting them back changes nothing about the ship.
/// </summary>
public struct ShipControls
{
    /// <summary>Thrust settings along the ship's own axes (see <see cref="ThrustAxis"/>), each from -1 to 1.</summary>
    public float Forward, Right, Up;

    /// <summary>Turn setting, -1 to 1: clockwise seen from above (to starboard) for positive.</summary>
    public float Turn;

    /// <summary>Whether the ship's toggles are on: its anchors hold it to what's beside them (see
    /// <see cref="AnchorSystem"/>, which turns this back off when nothing is in reach).</summary>
    public bool Anchored;

    public readonly float Thrust(ThrustAxis axis) => axis switch
    {
        ThrustAxis.Forward => Forward,
        ThrustAxis.Right => Right,
        _ => Up,
    };

    public void SetThrust(ThrustAxis axis, float value)
    {
        switch (axis)
        {
            case ThrustAxis.Forward: Forward = value; break;
            case ThrustAxis.Right: Right = value; break;
            default: Up = value; break;
        }
    }

    /// <summary>The controls of the ship <paramref name="volume"/> belongs to: nothing asked for if it has none.</summary>
    public static ShipControls Of(ChunkVolume volume) =>
        volume.Root.IsAlive && volume.Root.Has<ShipControls>() ? volume.Root.Get<ShipControls>() : default;

    /// <summary>Which of its ship's axes a lever levers along, and which way along it its north face points (+1 or -1):
    /// a lever at 1 (leaning to its north face) asks for <c>sign</c> × full thrust on that axis, so levers facing
    /// opposite ways on one axis show opposite settings.</summary>
    public static (ThrustAxis Axis, float Sign) LeverAxis(in BlockRef lever)
    {
        int north = (int)lever.Orientation.North; // directions come in opposite pairs: north/south, east/west, up/down
        return ((ThrustAxis)(north / 2), north % 2 == 0 ? 1f : -1f);
    }

    /// <summary>Where a lever's arm stands (-1 to 1) for its ship's current setting.</summary>
    public static float LeverValue(in BlockRef lever)
    {
        var (axis, sign) = LeverAxis(lever);
        return Of(lever.Volume).Thrust(axis) * sign;
    }
}
