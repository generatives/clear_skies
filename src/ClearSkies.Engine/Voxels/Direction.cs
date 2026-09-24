using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>One of the 6 axis directions in a volume's local space. A block's orientation (see
/// <see cref="BlockOrientation"/>) is a pair of these: where its top and its north face point.</summary>
public enum Direction : byte
{
    North = 0, // local -Z
    South = 1, // local +Z
    East  = 2, // local +X
    West  = 3, // local -X
    Up    = 4, // local +Y
    Down  = 5, // local -Y
}

public static class DirectionExtensions
{
    public static Vector3D<int> ToVector(this Direction direction) => direction switch
    {
        Direction.North => new(0, 0, -1),
        Direction.South => new(0, 0, 1),
        Direction.East  => new(1, 0, 0),
        Direction.West  => new(-1, 0, 0),
        Direction.Up    => new(0, 1, 0),
        Direction.Down  => new(0, -1, 0),
        _               => new(0, 0, -1),
    };

    /// <summary>Snaps an arbitrary direction to whichever of the 6 axis directions it is most aligned with.</summary>
    public static Direction FromVector(Vector3D<float> dir)
    {
        float ax = MathF.Abs(dir.X), ay = MathF.Abs(dir.Y), az = MathF.Abs(dir.Z);
        if (ax >= ay && ax >= az) return dir.X >= 0 ? Direction.East : Direction.West;
        if (ay >= ax && ay >= az) return dir.Y >= 0 ? Direction.Up : Direction.Down;
        return dir.Z >= 0 ? Direction.South : Direction.North;
    }

    /// <summary>Exact conversion for a normal that is already one of the 6 axis directions (e.g. a
    /// raycast hit normal) — no snapping/magnitude comparison needed.</summary>
    public static Direction FromNormal(Vector3D<int> normal) => normal switch
    {
        { X: 1 }  => Direction.East,
        { X: -1 } => Direction.West,
        { Y: 1 }  => Direction.Up,
        { Y: -1 } => Direction.Down,
        { Z: 1 }  => Direction.South,
        { Z: -1 } => Direction.North,
        _         => Direction.North,
    };
}
