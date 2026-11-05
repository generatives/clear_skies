using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using ClearSkies.Engine.Generation;

namespace ClearSkies.Game.Generation;

/// <summary>
/// How the land is worn and where water stands on it, one landmass (see <see cref="HeartGrid.Landmass"/>) at a time:
/// the landmass is sampled in 8-block columns (see <see cref="CoarseLandmass"/>), rained on and worn by the pipes model (see
/// <see cref="Pipes"/>) for <see cref="LandscapeSettings.ErodeHours"/>, its hollows filled to where they spill, and its
/// water let run <see cref="LandscapeSettings.SettleMinutes"/> more. Worked out whole the first time any of it is
/// asked for (tens of seconds) and kept, so every chunk of it agrees. HeartWorldGenerator moves its ground by the
/// wear (see <see cref="CoarseLandmass.Shift"/>) and places water as the model left it (see <see cref="CoarseLandmass.Water"/>).
/// </summary>
public sealed class Landscape(ulong seed, LandscapeSettings settings)
{
    public ulong Seed => seed;
    public LandscapeSettings Settings => settings;

    private readonly ConcurrentDictionary<(int X, int Z), Lazy<CoarseLandmass>> _landmasses = new();
    private readonly ConcurrentDictionary<(int X, int Z), CoarseLandmass> _done = new();
    private int _finished;

    /// <summary>How many landmasses have been worked out so far.</summary>
    public int Finished => Volatile.Read(ref _finished);

    /// <summary>The landmass (x, z) is on if it's been worked out, else null (without working it out).</summary>
    public CoarseLandmass? Done(float x, float z) => _done.TryGetValue(HeartGrid.Landmass(seed, x, z), out var coarse) ? coarse : null;

    /// <summary>The landmass (x, z) is on, worked out if it hasn't been.</summary>
    public CoarseLandmass At(float x, float z)
    {
        var key = HeartGrid.Landmass(seed, x, z);
        var coarse = _landmasses.GetOrAdd(key, k => new Lazy<CoarseLandmass>(() => Shape(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (_done.TryAdd(key, coarse)) Interlocked.Increment(ref _finished);
        return coarse;
    }

    private CoarseLandmass Shape((int X, int Z) landmass)
    {
        var sw = Stopwatch.StartNew();
        var (x0, z0, x1, z1) = Bounds(seed, landmass);
        var coarse = CoarseLandmass.Build(seed, landmass, x0, z0, x1, z1, settings);
        var pipes = new Pipes(coarse);
        foreach (var (name, value) in settings.Pipes) Pipes.Set(pipes, name, value);
        if (settings.Groundwater) pipes.UseGroundwater();
        pipes.FillHollows();
        if (settings.ErodeHours > 0)
        {
            pipes.StartEroding(settings.ErodeHours * 3600);
            while (pipes.Time < settings.ErodeHours * 3600) pipes.Step();
            pipes.Eroding = false;
        }
        pipes.Rain *= settings.SettleRain;
        pipes.FillHollows(); // lakes start at their new levels: outlets were cut, and the rain changed
        double until = pipes.Time + settings.SettleMinutes * 60;
        while (pipes.Time < until) pipes.Step();
        FlowTree.Build(coarse, pipes.Rain, seed);
        Streams.Build(coarse, seed);
        Watering.Build(coarse);
        Console.WriteLine($"Landscape: landmass {landmass} worn and watered in {sw.Elapsed.TotalSeconds:0.0} s ({pipes.Steps:N0} steps)");
        return coarse;
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

/// <summary>What the landscape model does (see <see cref="Landscape"/>), from --terrain: comma-separated name=value
/// pairs. off turns it off; erode-hours, settle-minutes, settle-rain (times the rain while settling), groundwater (true/false), damp (how deep water only dampens the ground), water-reach and water-climb (see WaterReach) and size (column width) are the landscape's own; any other name is one of Pipes' numbers (e.g.
/// creep=1e-5, speedup=900, rain=3e-4, manning=0.035, carry=0.05; dashes ignored, any case).</summary>
public sealed class LandscapeSettings
{
    public bool Enabled = true;
    public double ErodeHours = 1, SettleMinutes = 20;
    /// <summary>How many times as much rain falls while the water settles, after the erosion.</summary>
    public float SettleRain = 0.35f;
    public bool Groundwater = true;
    /// <summary>How deep water over a column only dampens its ground, in blocks: the rest goes into its blocks.</summary>
    public float Damp = 0.1f;
    /// <summary>How much rain (in columns' worth) runs through a column whose stream shows a block wide: each column's
    /// stream shows as a block-deep line from its middle to the column it runs to, sqrt(flow / StreamFlow) blocks wide
    /// (narrower than a block shows nothing). 0 for no streams.</summary>
    public float StreamFlow = 10f;
    public int Size = 8;
    /// <summary>How far water wets the land round it for the biomes, in blocks (see Landscape.Watered), and how many
    /// blocks across each block climbed out of it counts as.</summary>
    public float WaterReach = 64f, WaterClimb = 3f;
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
                case "settle-rain": s.SettleRain = float.Parse(value, c); break;
                case "groundwater": s.Groundwater = bool.Parse(value); break;
                case "damp": s.Damp = float.Parse(value, c); break;
                case "stream-flow": s.StreamFlow = float.Parse(value, c); break;
                case "water-reach": s.WaterReach = float.Parse(value, c); break;
                case "water-climb": s.WaterClimb = float.Parse(value, c); break;
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
