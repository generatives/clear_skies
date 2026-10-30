using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace ClearSkies.Game.Generation;

/// <summary>
/// How the land is worn and where water stands on it, one landmass (see <see cref="HeartGrid.Landmass"/>) at a time:
/// the landmass is sampled in 8-block columns (see <see cref="Land"/>), rained on and worn by the pipes model (see
/// <see cref="Pipes"/>) for <see cref="LandscapeSettings.ErodeHours"/>, its hollows filled to where they spill, and its
/// water let run <see cref="LandscapeSettings.SettleMinutes"/> more. Worked out whole the first time any of it is
/// asked for (tens of seconds) and kept, so every chunk of it agrees. HeartWorldGenerator moves its ground by the
/// wear (see <see cref="Shift"/>) and fills water to the level the model left (see <see cref="WaterLevel"/>).
/// </summary>
public sealed class Landscape(ulong seed, LandscapeSettings settings)
{
    public ulong Seed => seed;
    public LandscapeSettings Settings => settings;

    private readonly ConcurrentDictionary<(int X, int Z), Lazy<Shaped>> _landmasses = new();

    /// <summary>The landmass (x, z) is on, worked out if it hasn't been.</summary>
    public Shaped At(float x, float z)
    {
        var key = HeartGrid.Landmass(seed, x, z);
        return _landmasses.GetOrAdd(key, k => new Lazy<Shaped>(() => Shape(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private Shaped Shape((int X, int Z) landmass)
    {
        var sw = Stopwatch.StartNew();
        var (x0, z0, x1, z1) = Bounds(seed, landmass);
        var land = new Land(seed, landmass, x0, z0, x1, z1, settings.Size);
        var start = (float[])land.Floor.Clone();
        var pipes = new Pipes(land);
        foreach (var (name, value) in settings.Pipes) Pipes.Set(pipes, name, value);
        if (settings.Groundwater) pipes.UseGroundwater();
        pipes.FillHollows();
        if (settings.ErodeHours > 0)
        {
            pipes.StartEroding(settings.ErodeHours * 3600);
            while (pipes.Time < settings.ErodeHours * 3600) pipes.Step();
            pipes.Eroding = false;
            pipes.FillHollows(); // lakes whose outlets were cut start at their new spill level
        }
        double until = pipes.Time + settings.SettleMinutes * 60;
        while (pipes.Time < until) pipes.Step();
        Console.WriteLine($"Landscape: landmass {landmass} worn and watered in {sw.Elapsed.TotalSeconds:0.0} s ({pipes.Steps:N0} steps)");
        return new Shaped(land, start, pipes.Depth, settings.MinDepth);
    }

    /// <summary>A landmass's bounds (blocks, on 32-block lines): where its own ground can be, found on a coarse grid
    /// round it, with a margin.</summary>
    public static (int X0, int Z0, int X1, int Z1) Bounds(ulong seed, (int X, int Z) landmass)
    {
        float spacing = HeartGrid.LandmassSpacing;
        int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
        for (float z = (landmass.Z - 0.7f) * spacing; z <= (landmass.Z + 1.7f) * spacing; z += 64f)
        for (float x = (landmass.X - 0.7f) * spacing; x <= (landmass.X + 1.7f) * spacing; x += 64f)
        {
            if (HeartGrid.Landmass(seed, x, z) != landmass) continue;
            x0 = Math.Min(x0, (int)x); z0 = Math.Min(z0, (int)z); x1 = Math.Max(x1, (int)x); z1 = Math.Max(z1, (int)z);
        }
        static int FloorTo(int v, int step) => (int)Math.Floor(v / (double)step) * step;
        return (FloorTo(x0 - 64, 32), FloorTo(z0 - 64, 32), FloorTo(x1 + 96, 32), FloorTo(z1 + 96, 32));
    }
}

/// <summary>One landmass as the model left it: each column's top floor before and after, and the water over it.</summary>
public sealed class Shaped(Land land, float[] startFloor, float[] depth, float minDepth)
{
    /// <summary>How far the ground near world corner (x, z) (on the 8-block lines between columns) moved, from the
    /// columns round it whose top stood within Near blocks of <paramref name="top"/> (the corner's own top before):
    /// their moves averaged. 0 where none did.</summary>
    public float Shift(int x, int z, float top)
    {
        const float Near = 16f;
        int size = land.Size, i = (x - land.X0) / size, k = (z - land.Z0) / size;
        float sum = 0; int count = 0;
        for (int dk = -1; dk <= 0; dk++)
        for (int di = -1; di <= 0; di++)
        {
            int ci = i + di, ck = k + dk;
            if (ci < 0 || ck < 0 || ci >= land.Nx || ck >= land.Nz) continue;
            int t = land.TopNode(ci + land.Nx * ck);
            if (t < 0 || MathF.Abs(startFloor[t] - top) > Near) continue;
            sum += land.Floor[t] - startFloor[t];
            count++;
        }
        return count == 0 ? 0f : sum / count;
    }

    /// <summary>The water's surface (blocks) over block column (x, z): the levels of the wet ones of the four columns
    /// round it, blended by nearness; none (NaN) where it's nearer dry ones.</summary>
    public float WaterLevel(int x, int z)
    {
        int size = land.Size;
        float fx = (x + 0.5f - land.X0) / size - 0.5f, fz = (z + 0.5f - land.Z0) / size - 0.5f;
        int i = (int)MathF.Floor(fx), k = (int)MathF.Floor(fz);
        float tx = fx - i, tz = fz - k, weight = 0, level = 0;
        for (int dk = 0; dk <= 1; dk++)
        for (int di = 0; di <= 1; di++)
        {
            int ci = i + di, ck = k + dk;
            if (ci < 0 || ck < 0 || ci >= land.Nx || ck >= land.Nz) continue;
            int t = land.TopNode(ci + land.Nx * ck);
            if (t < 0 || depth[t] < minDepth) continue;
            float w = (di == 0 ? 1 - tx : tx) * (dk == 0 ? 1 - tz : tz);
            weight += w;
            level += w * (land.Floor[t] + depth[t]);
        }
        return weight < 0.5f ? float.NaN : level / weight;
    }
}

/// <summary>What the landscape model does (see <see cref="Landscape"/>), from --terrain: comma-separated name=value
/// pairs. off turns it off; erode-hours, settle-minutes, groundwater (true/false), min-depth (the least water that
/// shows, in blocks) and size (column width) are the landscape's own; any other name is one of Pipes' numbers (e.g.
/// creep=1e-5, speedup=900, rain=3e-4, manning=0.035, carry=0.05; dashes ignored, any case).</summary>
public sealed class LandscapeSettings
{
    public bool Enabled = true;
    public double ErodeHours = 1, SettleMinutes = 20;
    public bool Groundwater = true;
    public float MinDepth = 0.3f;
    public int Size = 8;
    public readonly List<(string Name, string Value)> Pipes = new();

    public static LandscapeSettings Parse(string? text)
    {
        var s = new LandscapeSettings();
        if (string.IsNullOrWhiteSpace(text)) return s;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            string name = (eq < 0 ? part : part[..eq]).ToLowerInvariant(), value = eq < 0 ? "true" : part[(eq + 1)..];
            var c = CultureInfo.InvariantCulture;
            switch (name)
            {
                case "off": s.Enabled = false; break;
                case "erode-hours": s.ErodeHours = double.Parse(value, c); break;
                case "settle-minutes": s.SettleMinutes = double.Parse(value, c); break;
                case "groundwater": s.Groundwater = bool.Parse(value); break;
                case "min-depth": s.MinDepth = float.Parse(value, c); break;
                case "size": s.Size = int.Parse(value, c); break;
                default:
                    if (!Generation.Pipes.Set(null, name, value)) throw new ArgumentException($"--terrain: no setting called {name}");
                    s.Pipes.Add((name, value));
                    break;
            }
        }
        return s;
    }
}
