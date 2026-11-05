using System.Runtime.Intrinsics;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Cube roots and powers from + - × ÷ and square roots alone, which IEEE fixes to the bit, so every machine works a
/// landmass out the same: MathF.Cbrt and Pow come from each platform's own maths library, whose last bits differ
/// between Windows, Linux and macOS. The vector cube root does exactly what the scalar one does, lane by lane.
/// </summary>
internal static class ExactMath
{
    // (A first guess at a cube root from the bits: a third of the exponent, about.)
    private const int CbrtBias = 0x2a5137a0;

    /// <summary>The cube root of <paramref name="x"/> (over 0): a guess from its bits, then Newton's steps.</summary>
    public static float Cbrt(float x)
    {
        float y = BitConverter.Int32BitsToSingle((int)(BitConverter.SingleToInt32Bits(x) * (1f / 3f)) + CbrtBias);
        for (int i = 0; i < 3; i++) y = (y + y + x / (y * y)) * (1f / 3f);
        return y;
    }

    /// <summary><see cref="Cbrt(float)"/>, lane by lane, to the same bits.</summary>
    public static Vector256<float> Cbrt(Vector256<float> x)
    {
        var guess = Vector256.ConvertToInt32(Vector256.ConvertToSingle(x.AsInt32()) * Vector256.Create(1f / 3f)) + Vector256.Create(CbrtBias);
        var y = guess.AsSingle();
        var third = Vector256.Create(1f / 3f);
        for (int i = 0; i < 3; i++) y = (y + y + x / (y * y)) * third;
        return y;
    }

    /// <summary><paramref name="a"/> (over 0) to the power <paramref name="b"/>.</summary>
    public static float Pow(float a, float b) => (float)Exp(b * Log(a));

    private const double Ln2 = 0.6931471805599453;

    /// <summary>The natural log of <paramref name="x"/> (over 0): its mantissa's by the atanh series, plus its exponent's.</summary>
    private static double Log(double x)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exponent = (int)((bits >> 52) & 0x7FF) - 1023;
        double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L); // 1 to 2
        if (m > 1.4142135623730951) { m /= 2; exponent++; }
        double s = (m - 1) / (m + 1), s2 = s * s, term = s, sum = 0;
        for (int k = 1; k <= 25; k += 2) { sum += term / k; term *= s2; }
        return 2 * sum + exponent * Ln2;
    }

    /// <summary>e to the <paramref name="y"/>: 2^k for the whole number of ln 2s in it, the series for the rest.</summary>
    private static double Exp(double y)
    {
        double k = Math.Round(y / Ln2);
        double r = y - k * Ln2, term = 1, sum = 1;
        for (int n = 1; n <= 16; n++) { term *= r / n; sum += term; }
        return sum * BitConverter.Int64BitsToDouble((long)(k + 1023) << 52);
    }
}
