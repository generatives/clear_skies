using System.Numerics;

namespace ClearSkies.Engine.ECS;

/// <summary>One of a body's 6 drag entries: the area facing one body axis direction (m²) and where that area's centre is,
/// relative to the centre of mass, in body space.</summary>
public struct AirFace
{
    public float Area;
    public Vector3 Centroid;
}

/// <summary>
/// A body that feels the air (see <see cref="AirResistanceSystem"/>): 6 entries, one per body axis direction, in the order
/// +x, −x, +y, −y, +z, −z. Each is the area that faces that way and is first hit along it (a silhouette), and the
/// area-weighted centre of those faces relative to the centre of mass. Opposite directions share an area but not a
/// centre, since front and back faces sit at different depths. Data only: the system samples the wind.
/// </summary>
public struct ResistsAir
{
    public AirFace[] Faces;

    /// <summary>Multiplies the air constant for this body (1 for ships; players are tuned with it).</summary>
    public float DragScale;

    /// <summary>Which body shape <see cref="Faces"/> were worked out from (see AirshipResistanceSystem); -1 for none.</summary>
    internal int ShapeKey;

    public static int Index(int axis, bool positive) => axis * 2 + (positive ? 0 : 1);

    /// <summary>No area yet: a grid's, until AirshipResistanceSystem works its entries out from its blocks.</summary>
    public static ResistsAir Unshaped() => new() { Faces = new AirFace[6], DragScale = 1f, ShapeKey = -1 };

    /// <summary>A box-shaped body of <paramref name="size"/> (m) centred on its centre of mass.</summary>
    public static ResistsAir Box(Vector3 size, float dragScale = 1f)
    {
        var faces = new AirFace[6];
        for (int axis = 0; axis < 3; axis++)
        {
            float area = axis switch { 0 => size.Y * size.Z, 1 => size.X * size.Z, _ => size.X * size.Y };
            float half = axis switch { 0 => size.X, 1 => size.Y, _ => size.Z } / 2;
            var normal = axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
            faces[Index(axis, true)]  = new AirFace { Area = area, Centroid = normal * half };
            faces[Index(axis, false)] = new AirFace { Area = area, Centroid = -normal * half };
        }
        return new ResistsAir { Faces = faces, DragScale = dragScale, ShapeKey = -1 };
    }

    /// <summary>A player's capsule (radius 0.3 m, length 1 m: 0.6 m wide, 1.6 m tall), drawn as its box: sides 0.96 m²,
    /// top and bottom 0.36 m². Players don't turn, so where the faces are doesn't matter; they sit on the box.</summary>
    public static ResistsAir Player(float radius, float length) =>
        Box(new Vector3(2 * radius, length + 2 * radius, 2 * radius));

    /// <summary>A player gliding (see PlayerCharacter.Gliding): a wing <paramref name="span"/> wide and
    /// <paramref name="chord"/> deep, <paramref name="thickness"/> thick, the player lying along it. Its broad underside
    /// (span × chord, about 50 times the capsule's) falls slowly; tilted, it pushes the player along the way it tilts, as a
    /// flat panel does in a crosswind. Its edge-on front is small, so a dive picks up speed, and a pull-up spends it.</summary>
    public static ResistsAir Glider(float span = 5f, float chord = 3.6f, float thickness = 0.02f) =>
        Box(new Vector3(span, thickness, chord));
}
