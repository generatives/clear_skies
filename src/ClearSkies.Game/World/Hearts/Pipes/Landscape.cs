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
        }
        pipes.Rain *= settings.SettleRain;
        pipes.FillHollows(); // lakes start at their new levels: outlets were cut, and the rain changed
        double until = pipes.Time + settings.SettleMinutes * 60;
        while (pipes.Time < until) pipes.Step();
        Console.WriteLine($"Landscape: landmass {landmass} worn and watered in {sw.Elapsed.TotalSeconds:0.0} s ({pipes.Steps:N0} steps)");
        // Each node's stream: how much rain runs through it (in columns' worth) and the node it sends most to.
        var flow = new float[land.Nodes];
        var down = new int[land.Nodes];
        float column = pipes.Rain * settings.Size * settings.Size;
        for (int n = 0; n < land.Nodes; n++)
        {
            flow[n] = pipes.Through(n) / column;
            down[n] = -1;
            float most = 0;
            for (int k = land.LinkStart[n]; k < land.LinkStart[n + 1]; k++)
                if (land.LinkTo[k] >= 0 && pipes.Flow[k] > most) { most = pipes.Flow[k]; down[n] = land.LinkTo[k]; }
        }
        // A stream shows only down the middle of the water running beside it: where it carries more than the columns on
        // either side across its way (a sheet down a hillside shows one line, not one per column).
        var shown = new float[land.Nodes];
        var beside = new (int A, int B)[land.Nodes]; // the top nodes either side of each one's way, or -1
        int TopAt(int ci, int cj) => ci < 0 || cj < 0 || ci >= land.Nx || cj >= land.Nz ? -1 : land.TopNode(ci + land.Nx * cj);
        for (int c = 0; c < land.Columns; c++)
        {
            int n = land.TopNode(c);
            if (n < 0) continue;
            beside[n] = (-1, -1);
            if (down[n] < 0) continue;
            int d = land.ColumnOf[down[n]], i = c % land.Nx, j = c / land.Nx;
            int di = d % land.Nx - i, dj = d / land.Nx - j;
            beside[n] = (TopAt(i - dj, j + di), TopAt(i + dj, j - di));
        }
        float FlowOf(int m) => m < 0 ? 0f : flow[m];
        for (int n = 0; n < land.Nodes; n++)
        {
            var (a, b) = beside[n];
            // The middle of the water running beside it, it carries that water too (a river spread over a few columns
            // shows as one, as wide as all of them).
            if (down[n] >= 0 && flow[n] > FlowOf(a) && flow[n] >= FlowOf(b)) shown[n] = flow[n] + FlowOf(a) + FlowOf(b);
        }
        // ...and only where it runs on into water or another shown stream (lowest first, so what it runs to is
        // settled): a stream that shows ends in water, not in a dash on a slope.
        var lowest = Enumerable.Range(0, land.Nodes).Where(n => shown[n] > 0).OrderBy(n => land.Floor[n]).ToArray();
        float least = settings.StreamFlow;
        bool Shown(int m) => m >= 0 && shown[m] >= least;
        foreach (int n in lowest)
        {
            int to = down[n];
            // (A stream half a column wide or more spreads over the columns beside its way too: running on into one of
            // those does.)
            bool wide = MathF.Sqrt(shown[n] / least) >= settings.Size / 2f;
            if (!Shown(to) && !(wide && (Shown(beside[to].A) || Shown(beside[to].B))) && pipes.Depth[to] <= settings.Damp + 0.5f) shown[n] = 0;
        }
        return new Shaped(land, start, pipes.Depth, shown, down, settings);
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

/// <summary>One landmass as the model left it: each column's top floor before and after, and the water over it. A
/// column's water goes into its own lowest blocks first, lowest under the model's sloping surface (see
/// HeartWorldGenerator.PlaceWater): a little (a creek) fills only the deepest few, a lot (a lake) its whole footprint. The first Damp blocks of it only dampen
/// the ground: thin water running over a slope shows as none.</summary>
public sealed class Shaped(Land land, float[] startFloor, float[] depth, float[] flow, int[] down, LandscapeSettings settings)
{
    /// <summary>The stream of the column with its middle at (x, z): how wide it runs (sqrt(flow / StreamFlow) blocks;
    /// 0 for none), the middle of the column it runs to, and the ground at both ends.</summary>
    public (float Width, int ToX, int ToZ, float From, float To) Stream(int x, int z)
    {
        int t = TopNodeAt(x, z);
        if (t < 0 || settings.StreamFlow <= 0 || down[t] < 0) return default;
        int to = down[t], c = land.ColumnOf[to];
        return (MathF.Sqrt(flow[t] / settings.StreamFlow), land.X0 + c % land.Nx * land.Size, land.Z0 + c / land.Nx * land.Size,
                land.Floor[t], land.Floor[to]);
    }

    /// <summary>A column's width, in blocks.</summary>
    public int Size => land.Size;

    /// <summary>How far the ground at lattice corner (x, z) (on the 8-block lines, the middle of a column) moved, if
    /// the column's top stood within Near blocks of <paramref name="top"/> (the corner's own top before); else 0.</summary>
    public float Shift(int x, int z, float top)
    {
        const float Near = 16f;
        int t = TopNodeAt(x, z);
        return t < 0 || MathF.Abs(startFloor[t] - top) > Near ? 0f : land.Floor[t] - startFloor[t];
    }

    /// <summary>The middle of the column holding block column (x, z) (the column round a lattice corner, Size/2 blocks
    /// either way: half a block more one way than the other).</summary>
    public (int X, int Z) Middle(int x, int z)
    {
        int size = land.Size;
        return (land.X0 + (int)MathF.Floor((x - land.X0 + size / 2f) / size) * size,
                land.Z0 + (int)MathF.Floor((z - land.Z0 + size / 2f) / size) * size);
    }

    /// <summary>How much water (blocks³) the column with its middle at (x, z) has; 0 where there's none.</summary>
    public float Volume(int x, int z)
    {
        int t = TopNodeAt(x, z);
        return t < 0 ? 0f : Math.Max(0f, depth[t] - settings.Damp) * land.Size * land.Size;
    }

    /// <summary>The water's surface over the column holding block column (x, z) (blocks), or NaN where it's dry.</summary>
    public float Surface(int x, int z)
    {
        var (mx, mz) = Middle(x, z);
        int t = TopNodeAt(mx, mz);
        return t < 0 || depth[t] <= 0f ? float.NaN : land.Floor[t] + depth[t];
    }

    /// <summary>The model's water surface (its ground where dry) blended between the four column middles round block
    /// column (x, z): a surface as smooth as the ground's, sloping with the water. NaN off the landmass.</summary>
    public float Plane(int x, int z)
    {
        int size = land.Size;
        int i = (int)MathF.Floor((x - land.X0) / (float)size), k = (int)MathF.Floor((z - land.Z0) / (float)size);
        float fx = (x - land.X0 - i * size) / (float)size, fz = (z - land.Z0 - k * size) / (float)size;
        float sum = 0, weights = 0;
        for (int c = 0; c < 4; c++)
        {
            int t = TopNodeAt(land.X0 + (i + (c & 1)) * size, land.Z0 + (k + (c >> 1)) * size);
            if (t < 0) continue;
            float w = ((c & 1) == 0 ? 1 - fx : fx) * ((c >> 1) == 0 ? 1 - fz : fz);
            sum += w * (land.Floor[t] + depth[t]);
            weights += w;
        }
        return weights > 1e-4f ? sum / weights : float.NaN;
    }

    /// <summary>The top node of the column with its middle at (x, z), or -1.</summary>
    private int TopNodeAt(int x, int z)
    {
        int size = land.Size, i = (x - land.X0) / size, k = (z - land.Z0) / size;
        if (x < land.X0 || z < land.Z0 || i >= land.Nx || k >= land.Nz) return -1;
        return land.TopNode(i + land.Nx * k);
    }
}

/// <summary>What the landscape model does (see <see cref="Landscape"/>), from --terrain: comma-separated name=value
/// pairs. off turns it off; erode-hours, settle-minutes, settle-rain (times the rain while settling), groundwater (true/false), damp (how deep water only dampens the ground) and size (column width) are the landscape's own; any other name is one of Pipes' numbers (e.g.
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
