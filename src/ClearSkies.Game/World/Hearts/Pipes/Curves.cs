namespace ClearSkies.Game.Generation;

/// <summary>A point on a stream's line: where it is, and the ground, the model's water depth and the width factor
/// there.</summary>
internal readonly record struct CurvePoint(float X, float Z, float Floor, float Depth, float Wide);

/// <summary>Streams' lines as quadratic Bézier curves of three <see cref="CurvePoint"/>s.</summary>
internal static class Curves
{
    /// <summary>Halfway between a and b, every value blended.</summary>
    public static CurvePoint Half(CurvePoint a, CurvePoint b) =>
        new((a.X + b.X) / 2, (a.Z + b.Z) / 2, (a.Floor + b.Floor) / 2, (a.Depth + b.Depth) / 2, (a.Wide + b.Wide) / 2);

    /// <summary>The point <paramref name="s"/> (0-1) along curve <paramref name="c"/>.</summary>
    public static CurvePoint At(ReadOnlySpan<CurvePoint> c, float s)
    {
        float a = (1 - s) * (1 - s), b = 2 * s * (1 - s), e = s * s;
        return new CurvePoint(a * c[0].X + b * c[1].X + e * c[2].X, a * c[0].Z + b * c[1].Z + e * c[2].Z,
                              a * c[0].Floor + b * c[1].Floor + e * c[2].Floor, a * c[0].Depth + b * c[1].Depth + e * c[2].Depth,
                              a * c[0].Wide + b * c[1].Wide + e * c[2].Wide);
    }
}
