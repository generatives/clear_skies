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
        var top = new int[width * width]; var water = new int[width * width]; var fall = new (int Top, int Bottom)[width * width]; var before = new int[width * width];
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
                water[c] = n == 0 ? int.MinValue : gen.WaterTopAt(x, z);
                fall[c] = gen.FallAt(x, z);
            }
        });
        var rgb = new byte[width * width * 3];
        int wet = 0, land = 0;
        for (int j = 0; j < width; j++)
        for (int i = 0; i < width; i++)
        {
            int c = i + width * j, o = c * 3;
            if (fall[c].Top != int.MinValue && fall[c].Top >= fall[c].Bottom) { rgb[o] = 255; rgb[o + 1] = 60; rgb[o + 2] = 200; continue; }
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
        // Water standing over a dry neighbour's ground: water that would run off.
        int hanging = 0, thin = 0, walls = 0;
        for (int j = 1; j < width - 1; j++)
        for (int i = 1; i < width - 1; i++)
        {
            int c = i + width * j;
            if (top[c] == int.MinValue || water[c] <= top[c]) continue;
            bool hangs = false;
            foreach (int d in new[] { c - 1, c + 1, c - width, c + width })
                if (top[d] != int.MinValue && water[d] <= top[d] && top[d] < water[c]) hangs = true;
            if (hangs) { hanging++; if (water[c] - top[c] == 1) thin++; else walls++; }
        }
        // Distant terrain's water (see HeartWorldGenerator.GenerateCoarse): each column's water over its own top.
        int farWet = 0, farLand = 0, lone = 0, fw = width / 4;
        var far = generators.Value!;
        var farWater = new bool[fw * fw];
        for (int j = 0; j < fw; j++)
        for (int i = 0; i < fw; i++)
        {
            int c = 4 * i + width * 4 * j;
            if (top[c] == int.MinValue) continue;
            farLand++;
            if (HeartWorldGenerator.WaterTop(far.WaterAt(x0 + 4 * i, z0 + 4 * j), top[c]) > top[c]) { farWet++; farWater[i + fw * j] = true; }
        }
        for (int j = 1; j < fw - 1; j++)
        for (int i = 1; i < fw - 1; i++)
            if (farWater[i + fw * j] && !farWater[i - 1 + fw * j] && !farWater[i + 1 + fw * j] && !farWater[i + fw * (j - 1)] && !farWater[i + fw * (j + 1)]) lone++;
        Console.WriteLine($"Distant terrain (4-block voxels) water over {100.0 * farWet / Math.Max(1, farLand):0.0}% of land, {100.0 * lone / Math.Max(1, farWet):0.0}% of it lone voxels");
        {
            int falls = 0; long fallBlocks = 0; var heights = new List<int>();
            foreach (var f in fall) if (f.Top != int.MinValue && f.Top >= f.Bottom) { falls++; fallBlocks += f.Top - f.Bottom + 1; heights.Add(f.Top - f.Bottom + 1); }
            heights.Sort();
            Console.WriteLine($"Waterfalls: {falls:N0} block columns, {fallBlocks:N0} water blocks" + (falls > 0 ? $", height median {heights[falls / 2]}, most {heights[^1]}" : ""));
        }
        {
            // Steps in the water's surface between neighbouring wet columns, by where they fall against the 8-block
            // lines of the landscape's columns (even spread: 12.5% each).
            var phase = new int[8]; int steps = 0, big = 0;
            for (int j = 0; j < width - 1; j++)
            for (int i = 0; i < width - 1; i++)
            {
                int c = i + width * j;
                if (top[c] == int.MinValue || water[c] <= top[c]) continue;
                foreach (var (d, ph) in new[] { (c + 1, (x0 + i) & 7), (c + width, (z0 + j) & 7) })
                {
                    if (top[d] == int.MinValue || water[d] <= top[d] || water[d] == water[c]) continue;
                    steps++; phase[ph]++; if (Math.Abs(water[d] - water[c]) >= 2) big++;
                }
            }
            Console.WriteLine($"Water surface steps: {steps:N0} ({big:N0} of 2 or more); by 8-block phase: " + string.Join(" ", phase.Select(v => $"{100.0 * v / Math.Max(1, steps):0.0}")));
        }
        Console.WriteLine($"Hanging water columns: {hanging:N0} ({thin:N0} of them 1 block deep, {walls:N0} deeper)");
        var depths = new List<int>();
        for (int c = 0; c < top.Length; c++) if (top[c] != int.MinValue && water[c] > top[c]) depths.Add(water[c] - top[c]);
        depths.Sort();
        if (depths.Count > 0)
            Console.WriteLine($"Water blocks deep: median {depths[depths.Count / 2]}, 90% {depths[depths.Count * 9 / 10]}, most {depths[^1]}; " +
                              $"1 deep {100.0 * depths.Count(d => d == 1) / depths.Count:0}%, 2 deep {100.0 * depths.Count(d => d == 2) / depths.Count:0}%");
        {
            int shallow = 0, deep = 0; double shallowMoved = 0, deepMoved = 0;
            for (int c = 0; c < top.Length; c++)
            {
                if (top[c] == int.MinValue || before[c] == int.MinValue || water[c] <= top[c]) continue;
                if (water[c] - top[c] <= 2) { shallow++; shallowMoved += top[c] - before[c]; }
                else { deep++; deepMoved += top[c] - before[c]; }
            }
            Console.WriteLine($"Ground under shallow water (1-2 deep) moved {shallowMoved / Math.Max(1, shallow):+0.0;-0.0} blocks on average, under deeper {deepMoved / Math.Max(1, deep):+0.0;-0.0}");
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
        // Relief, before and after: mean slope (blocks a block, to the next column along X and Z), share steeper than
        // 1, and local relief (highest less lowest top in each 64-block square, averaged).
        (double Slope, double Steep, double Relief) Relief(int[] h)
        {
            double slope = 0, steep = 0, relief = 0; int count = 0, squares = 0;
            for (int j = 0; j + 1 < width; j++)
            for (int i = 0; i + 1 < width; i++)
            {
                int c = i + width * j;
                if (h[c] == int.MinValue || h[c + 1] == int.MinValue || h[c + width] == int.MinValue) continue;
                double s = (Math.Abs(h[c + 1] - h[c]) + Math.Abs(h[c + width] - h[c])) / 2.0;
                if (s > 8) continue; // a cliff between pieces
                slope += s; if (s > 1) steep++; count++;
            }
            for (int j = 0; j + 64 <= width; j += 64)
            for (int i = 0; i + 64 <= width; i += 64)
            {
                int lo = int.MaxValue, hi = int.MinValue, n = 0;
                for (int b = j; b < j + 64; b++)
                for (int a = i; a < i + 64; a++)
                {
                    int v = h[a + width * b];
                    if (v == int.MinValue) continue;
                    lo = Math.Min(lo, v); hi = Math.Max(hi, v); n++;
                }
                if (n < 64 * 64) continue;
                relief += hi - lo; squares++;
            }
            return (slope / Math.Max(1, count), 100.0 * steep / Math.Max(1, count), relief / Math.Max(1, squares));
        }
        var terrain = ContinentTerrain.For(seed);
        var surface = new int[width * width];
        Parallel.For(0, width, j => { for (int i = 0; i < width; i++) surface[i + width * j] = top[i + width * j] == int.MinValue ? int.MinValue : (int)MathF.Floor(terrain.Height(x0 + i, z0 + j)); });
        var (sT, stT, rT) = Relief(surface);
        Console.WriteLine($"Terrain surface (no pieces), where there's land: slope {sT:0.00}, steeper than 1: {stT:0.0}%, relief per 64 blocks {rT:0.0}");
        var (s0, st0, r0) = Relief(before);
        var (s1, st1, r1) = Relief(top);
        Console.WriteLine($"Relief before: slope {s0:0.00}, steeper than 1: {st0:0.0}%, relief per 64 blocks {r0:0.0}; after: {s1:0.00}, {st1:0.0}%, {r1:0.0}");
        using (var f = File.Create(path))
            new ImageWriter().WritePng(rgb, width, width, ColorComponents.RedGreenBlue, f);
        Console.WriteLine($"Blocks {width}x{width} from ({x0}, {z0}) in {sw.Elapsed.TotalSeconds:0.0} s: water over {100.0 * wet / Math.Max(1, land):0.0}% of land");
    }
}
