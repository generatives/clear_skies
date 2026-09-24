using ClearSkies.Engine.Math;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// One of a block's 24 axis-aligned orientations, stored densely per voxel in <see cref="ChunkData"/> alongside its
/// <see cref="BlockId"/>. Given by two perpendicular directions in the owning volume's local space: <see cref="Up"/>,
/// where the block's own +Y (its top) points, and <see cref="North"/>, where its own -Z (its north face) points.
///
/// Stored as one byte, <c>up + 6 × spin</c>, where spin (0-3) is quarter turns about <see cref="Up"/>. Spin 0 is the
/// orientation a block got before blocks had a spin, so bytes 0-5 (all that older saves contain) still read as the
/// same orientation.
/// </summary>
public readonly struct BlockOrientation : IEquatable<BlockOrientation>
{
    /// <summary>How many orientations there are; every stored byte is below this.</summary>
    public const int Count = 24;

    // Always < Count: only FromByte (which checks), From and Placed make one. default is 0, up North at spin 0.
    private readonly byte _value;

    private BlockOrientation(byte value) => _value = value;

    /// <summary>Standing upright (+Y up) with the north face towards -Z: the identity orientation.</summary>
    public static readonly BlockOrientation Upright = new((byte)Direction.Up);

    public Direction Up    => (Direction)(_value % 6);
    public Direction North => Norths[_value];

    /// <summary>The rotation from the block's own space (+Y up, -Z north) to its volume's local space.</summary>
    public Quaternion<float> Rotation => Rotations[_value];

    public byte ToByte() => _value;

    /// <summary>Reads a stored byte; anything out of range (a corrupt save) becomes <see cref="Upright"/>.</summary>
    public static BlockOrientation FromByte(byte value) => value < Count ? new(value) : Upright;

    /// <summary>The orientation with its top along <paramref name="up"/> and its north face along
    /// <paramref name="north"/>. When <paramref name="north"/> isn't perpendicular to <paramref name="up"/>, the north
    /// face is left where spin 0 puts it.</summary>
    public static BlockOrientation From(Direction up, Direction north)
    {
        for (int spin = 0; spin < 4; spin++)
        {
            int value = (int)up + 6 * spin;
            if (Norths[value] == north) return new((byte)value);
        }
        return new((byte)up);
    }

    /// <summary>The orientation for a block placed against a surface: its bottom on the surface (its top along the
    /// surface's outward <paramref name="up"/>), turned so its north face points as nearly as it can along
    /// <paramref name="towards"/> — typically from the block towards the player. Only the part of
    /// <paramref name="towards"/> across the surface counts, since the north face must stay perpendicular to up; when
    /// there is none (looking straight along up), spin 0.</summary>
    public static BlockOrientation Placed(Direction up, Vector3D<float> towards)
    {
        var u = up.ToVector();
        var across = towards - Vector3D.Dot(towards, new Vector3D<float>(u.X, u.Y, u.Z)) * new Vector3D<float>(u.X, u.Y, u.Z);
        if (across.LengthSquared < 1e-8f) return new((byte)up);
        return From(up, DirectionExtensions.FromVector(across));
    }

    private static readonly Quaternion<float>[] Rotations = new Quaternion<float>[Count];
    private static readonly Direction[]            Norths    = new Direction[Count];

    static BlockOrientation()
    {
        var y = Vector3D<float>.UnitY;
        for (int spin = 0; spin < 4; spin++)
        {
            // Spin about the block's own +Y first, then tip +Y over to face `up`.
            var turn = Quaternion<float>.CreateFromAxisAngle(y, spin * MathF.PI / 2);
            for (int up = 0; up < 6; up++)
            {
                int i = up + 6 * spin;
                Rotations[i] = Multiply(UpRotation((Direction)up), turn);
                Norths[i]    = DirectionExtensions.FromVector(Vec.Rotate(Rotations[i], -Vector3D<float>.UnitZ));
            }
        }
    }

    /// <summary>Turns +Y to point along <paramref name="up"/>: about X, +Y turns towards +Z; about Z, towards -X.
    /// (Spin 0's rotations, which set where each spin-0 north face ends up.)</summary>
    private static Quaternion<float> UpRotation(Direction up)
    {
        var x = Vector3D<float>.UnitX;
        var z = Vector3D<float>.UnitZ;
        const float Quarter = MathF.PI / 2;
        return up switch
        {
            Direction.North => Quaternion<float>.CreateFromAxisAngle(x, -Quarter),
            Direction.South => Quaternion<float>.CreateFromAxisAngle(x,  Quarter),
            Direction.East  => Quaternion<float>.CreateFromAxisAngle(z, -Quarter),
            Direction.West  => Quaternion<float>.CreateFromAxisAngle(z,  Quarter),
            Direction.Down  => Quaternion<float>.CreateFromAxisAngle(x, MathF.PI),
            _            => Quaternion<float>.Identity,
        };
    }

    /// <summary>Hamilton product: rotating by the result rotates by <paramref name="b"/>, then <paramref name="a"/>.</summary>
    private static Quaternion<float> Multiply(Quaternion<float> a, Quaternion<float> b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    public bool Equals(BlockOrientation other) => _value == other._value;
    public override bool Equals(object? obj) => obj is BlockOrientation o && Equals(o);
    public override int GetHashCode() => _value;
    public static bool operator ==(BlockOrientation a, BlockOrientation b) => a._value == b._value;
    public static bool operator !=(BlockOrientation a, BlockOrientation b) => a._value != b._value;

    public override string ToString() => $"up {Up}, north {North}";
}
