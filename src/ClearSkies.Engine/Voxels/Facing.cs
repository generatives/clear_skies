using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>One of the 6 axis directions in a volume's local space. A block's orientation (see
/// <see cref="BlockOrientation"/>) is a pair of these: where its top and its north face point.</summary>
public enum Facing : byte
{
    North = 0, // local -Z
    South = 1, // local +Z
    East  = 2, // local +X
    West  = 3, // local -X
    Up    = 4, // local +Y
    Down  = 5, // local -Y
}

public static class FacingExtensions
{
    public static Vector3D<int> ToVector(this Facing facing) => facing switch
    {
        Facing.North => new(0, 0, -1),
        Facing.South => new(0, 0, 1),
        Facing.East  => new(1, 0, 0),
        Facing.West  => new(-1, 0, 0),
        Facing.Up    => new(0, 1, 0),
        Facing.Down  => new(0, -1, 0),
        _            => new(0, 0, -1),
    };

    /// <summary>Snaps an arbitrary direction to whichever of the 6 axis facings it is most aligned with.</summary>
    public static Facing FromVector(Vector3D<float> dir)
    {
        float ax = MathF.Abs(dir.X), ay = MathF.Abs(dir.Y), az = MathF.Abs(dir.Z);
        if (ax >= ay && ax >= az) return dir.X >= 0 ? Facing.East : Facing.West;
        if (ay >= ax && ay >= az) return dir.Y >= 0 ? Facing.Up : Facing.Down;
        return dir.Z >= 0 ? Facing.South : Facing.North;
    }

    /// <summary>Exact conversion for a normal that is already one of the 6 axis directions (e.g. a
    /// raycast hit normal) — no snapping/magnitude comparison needed.</summary>
    public static Facing FromNormal(Vector3D<int> normal) => normal switch
    {
        { X: 1 }  => Facing.East,
        { X: -1 } => Facing.West,
        { Y: 1 }  => Facing.Up,
        { Y: -1 } => Facing.Down,
        { Z: 1 }  => Facing.South,
        { Z: -1 } => Facing.North,
        _         => Facing.North,
    };
}
