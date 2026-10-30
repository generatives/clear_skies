using System.Diagnostics;
using ClearSkies.Game.Generation;
using StbImageWriteSharp;


/// <summary>A top-down map of the game's own blocks (the generator with the landscape): each block column's top
/// shaded by slope and height, water blue by depth, and how much the wear moved it.</summary>
public static class BlockMap
{
    public static void Write(ulong seed, LandscapeSettings settings, int x0, int z0, int width, string path)
    {
        var sw = Stopwatch.StartNew();
        var landscape = settings.Enabled ? new Landscape(seed, settings) : null;
        landscape?.At(x0 + width / 2, z0 + width / 2); // worked out up front, so the timing below is just the blocks
        Console.WriteLine($"Landscape ready in {sw.Elapsed.TotalSeconds:0.0} s");
        sw.Restart();
        var top = new int[width * width]; var water = new int[width * width]; var before = new int[width * width];
        var raw = new ThreadLocal<HeartWorldGenerator>(() => new HeartWorldGenerator(seed));
        var generators = new ThreadLocal<HeartWorldGenerator>(() => new HeartWorldGenerator(seed, null, landscape));
        Parallel.For(0, (width + 31) / 32, band =>
        {
            var gen = generators.Value!;
            Span<(short Lo, short Hi)> spans = stackalloc (short, short)[32];
            for (int j = band * 32; j < Math.Min(width, band * 32 + 32); j++)
            for (int i = 0; i < width; i++)
            {
                int x = x0 + i, z = z0 + j, n = gen.SpansAt(x, z, spans), c = i + width * j;
                top[c] = n == 0 ? int.MinValue : spans[n - 1].Hi;
                int m = raw.Value!.SpansAt(x, z, spans);
                before[c] = m == 0 ? int.MinValue : spans[m - 1].Hi;
                water[c] = n == 0 ? int.MinValue : HeartWorldGenerator.WaterTop(gen.WaterAt(x, z), top[c]);
            }
        });
        var rgb = new byte[width * width * 3];
        int wet = 0, land = 0;
        for (int j = 0; j < width; j++)
        for (int i = 0; i < width; i++)
        {
            int c = i + width * j, o = c * 3;
            if (top[c] == int.MinValue) { rgb[o] = 18; rgb[o + 1] = 22; rgb[o + 2] = 34; continue; }
            land++;
            int Hat(int di, int dj)
            {
                int ni = Math.Clamp(i + di, 0, width - 1), nj = Math.Clamp(j + dj, 0, width - 1), d = ni + width * nj;
                return top[d] == int.MinValue ? top[c] : top[d];
            }
            double light = Math.Clamp(0.6 + (Hat(-1, -1) - Hat(1, 1)) * 0.25, 0.15, 1.0);
            double t = Math.Clamp((top[c] - 300) / 900.0, 0, 1), g = (90 + 130 * t) * light;
            if (water[c] > top[c])
            {
                wet++;
                double d = Math.Clamp((water[c] - top[c]) / 12.0, 0, 1);
                rgb[o] = (byte)(60 - 40 * d); rgb[o + 1] = (byte)(130 - 60 * d); rgb[o + 2] = (byte)(230 - 50 * d);
            }
            else { rgb[o] = (byte)(g * 0.95); rgb[o + 1] = (byte)g; rgb[o + 2] = (byte)(g * 0.8); }
        }
        int both = 0, moved = 0, most = 0; double sum = 0;
        for (int c = 0; c < top.Length; c++)
        {
            if (top[c] == int.MinValue || before[c] == int.MinValue) continue;
            int d = top[c] - before[c];
            both++; sum += Math.Abs(d); most = Math.Max(most, Math.Abs(d));
            if (Math.Abs(d) > 2) moved++;
        }
        Console.WriteLine($"Ground moved: mean {sum / Math.Max(1, both):0.0} blocks, {100.0 * moved / Math.Max(1, both):0.0}% of columns over 2 blocks, most {most}");
        using (var f = File.Create(path))
            new ImageWriter().WritePng(rgb, width, width, ColorComponents.RedGreenBlue, f);
        Console.WriteLine($"Blocks {width}x{width} from ({x0}, {z0}) in {sw.Elapsed.TotalSeconds:0.0} s: water over {100.0 * wet / Math.Max(1, land):0.0}% of land");
    }
}
