namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// Builds texture mip levels on the CPU: each level halves the one above with a 2×2 box filter. On an sRGB texture the
/// average is taken in linear light (sRGB values averaged as they are come out too dark), and colour is weighted by
/// alpha so clear texels (glass, cut-out leaves) don't darken their neighbours' colour.
/// </summary>
internal static class MipChain
{
    private static readonly float[] ToLinear = BuildToLinear();

    private static float[] BuildToLinear()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            t[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return t;
    }

    private static byte ToSrgb(float l)
    {
        l = System.Math.Clamp(l, 0f, 1f);
        float c = l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f;
        return (byte)System.Math.Clamp((int)MathF.Round(c * 255f), 0, 255);
    }

    /// <summary>The level below <paramref name="rgba"/> (<paramref name="width"/>×<paramref name="height"/> RGBA8):
    /// half the size in each direction (at least 1), each texel the average of the up to 2×2 it covers.</summary>
    public static (byte[] Rgba, int Width, int Height) Halve(byte[] rgba, int width, int height, bool srgb)
    {
        int w = System.Math.Max(1, width / 2), h = System.Math.Max(1, height / 2);
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float r = 0, g = 0, b = 0, a = 0;
            int n = 0;
            for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                int sx = System.Math.Min(2 * x + dx, width - 1), sy = System.Math.Min(2 * y + dy, height - 1);
                int i = 4 * (sy * width + sx);
                float alpha = rgba[i + 3] / 255f;
                r += alpha * Channel(rgba[i], srgb);
                g += alpha * Channel(rgba[i + 1], srgb);
                b += alpha * Channel(rgba[i + 2], srgb);
                a += alpha;
                n++;
            }
            int o = 4 * (y * w + x);
            if (a > 0f)
            {
                dst[o]     = Encode(r / a, srgb);
                dst[o + 1] = Encode(g / a, srgb);
                dst[o + 2] = Encode(b / a, srgb);
            }
            dst[o + 3] = (byte)System.Math.Clamp((int)MathF.Round(a / n * 255f), 0, 255);
        }
        return (dst, w, h);
    }

    private static float Channel(byte v, bool srgb) => srgb ? ToLinear[v] : v / 255f;

    private static byte Encode(float v, bool srgb)
        => srgb ? ToSrgb(v) : (byte)System.Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
