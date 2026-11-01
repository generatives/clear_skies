using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using ClearSkies.Engine.Generation;

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

    /// <summary>How far (blocks) a stream's line bends off a column's middle at most, and how quickly (per column) that
    /// changes from column to column: its meanders.</summary>
    private const float Meander = 2.5f, MeanderFrequency = 0.35f;

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
        // Each node's stream: how much rain runs through it (in columns' worth), the way it sends most (to a neighbouring
        // column), and the node there it sends it to, or -1 if that's a drain: over the land's edge.
        var flow = new float[land.Nodes];
        var down = new int[land.Nodes];
        var way = new (sbyte X, sbyte Z)[land.Nodes];
        float column = pipes.Rain * settings.Size * settings.Size;
        for (int n = 0; n < land.Nodes; n++)
        {
            flow[n] = pipes.Through(n) / column;
            down[n] = -1;
            float most = 0;
            for (int k = land.LinkStart[n]; k < land.LinkStart[n + 1]; k++)
                if (pipes.Flow[k] > most) { most = pipes.Flow[k]; down[n] = land.LinkTo[k]; way[n] = (land.LinkDx[k], land.LinkDz[k]); }
        }
        // What each column carries: what the model gains there (rain and springs: what it lets out past what comes in)
        // plus everything handed to it down the tree of main ways. A column hands on once everything handed to it has
        // arrived (up the tree first). Lakes carry too: water going round in one gains nothing, so only what runs in
        // and the rain on it reach the outlet.
        var carried = new float[land.Nodes];
        var waiting = new int[land.Nodes];
        for (int n = 0; n < land.Nodes; n++)
            if (down[n] >= 0) waiting[down[n]]++;
        void HandOn(int n)
        {
            float ins = 0, outs = 0;
            for (int k = land.LinkStart[n]; k < land.LinkStart[n + 1]; k++)
            {
                float q = pipes.Flow[k] / column;
                if (q > 0) outs += q; else ins -= q;
            }
            carried[n] += Math.Max(0f, outs - ins);
            if (down[n] >= 0) carried[down[n]] += carried[n];
        }
        var ready = new Stack<int>(Enumerable.Range(0, land.Nodes).Where(n => waiting[n] == 0));
        while (ready.Count > 0)
        {
            int n = ready.Pop();
            HandOn(n);
            if (down[n] >= 0 && --waiting[down[n]] == 0) ready.Push(down[n]);
        }
        // (Main ways going round in a ring, inside a lake: those hand on highest water first.)
        foreach (int n in Enumerable.Range(0, land.Nodes).Where(n => waiting[n] > 0).OrderByDescending(n => land.Floor[n] + pipes.Depth[n]))
            HandOn(n);
        // A stream shows where a column carries StreamFlow or more, and more than the columns either side of it across
        // its way (a sheet down a hillside shows one line, not one per column).
        var shown = new float[land.Nodes];
        var beside = new (int A, int B)[land.Nodes]; // the top nodes either side of each one's way, or -1
        int TopAt(int ci, int cj) => ci < 0 || cj < 0 || ci >= land.Nx || cj >= land.Nz ? -1 : land.TopNode(ci + land.Nx * cj);
        for (int c = 0; c < land.Columns; c++)
        {
            int n = land.TopNode(c);
            if (n < 0) continue;
            int i = c % land.Nx, j = c / land.Nx, di = way[n].X, dj = way[n].Z;
            beside[n] = way[n] == (0, 0) ? (-1, -1) : (TopAt(i - dj, j + di), TopAt(i + dj, j - di));
        }
        float least = settings.StreamFlow;
        float CarriedBy(int m) => m < 0 ? 0f : carried[m];
        for (int n = 0; n < land.Nodes; n++)
        {
            var (a, b) = beside[n];
            if (way[n] != (0, 0) && carried[n] >= least && carried[n] > CarriedBy(a) && carried[n] >= CarriedBy(b)) shown[n] = carried[n];
        }
        // Each column's meander: how far its stream's line bends off the column's middle, across its way.
        var bendNoise = new FastNoiseLite(unchecked((int)seed + 77));
        bendNoise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        bendNoise.SetFrequency(MeanderFrequency);
        var bend = new float[land.Nodes];
        for (int n = 0; n < land.Nodes; n++)
        {
            int c = land.ColumnOf[n];
            bend[n] = Meander * bendNoise.GetNoise(c % land.Nx, c / land.Nx);
        }
        // A stream's head: a shown stream out of no water, neither a shown stream nor standing water running into it (nor
        // standing in it), where it springs from the ground.
        bool Wet(int n) => shown[n] > 0 || pipes.Depth[n] > settings.Damp;
        var head = new bool[land.Nodes];
        for (int n = 0; n < land.Nodes; n++) head[n] = shown[n] > 0 && pipes.Depth[n] <= settings.Damp;
        for (int n = 0; n < land.Nodes; n++) if (Wet(n) && down[n] >= 0) head[down[n]] = false;
        return new Shaped(land, start, pipes.Depth, shown, down, way, bend, head, settings);
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

/// <summary>One landmass as the model left it: each column's top floor before and after, the water over it, and its
/// stream (see HeartWorldGenerator.PlaceWater). The first Damp blocks of water only dampen the ground:
/// thin water running over a slope shows as none.</summary>
public sealed class Shaped(Land land, float[] startFloor, float[] depth, float[] flow, int[] down, (sbyte X, sbyte Z)[] way,
                           float[] bend, bool[] head, LandscapeSettings settings)
{
    /// <summary>The stream of the column holding (x, z): how wide it runs (sqrt(flow / StreamFlow) blocks; 0 for
    /// none), its line, from the column's middle bent across its way by the column's meander to the same point of the
    /// column it runs to (in blocks, block centres at .5), and the ground at both ends. A stream that runs over the
    /// land's edge runs on level to the middle of the column past it. Head: no stream runs into it (it springs here).</summary>
    public (float Width, float FromX, float FromZ, float ToX, float ToZ, float From, float To, bool Head) Stream(int x, int z)
    {
        int t = TopNodeAt(x, z);
        if (t < 0 || settings.StreamFlow <= 0 || flow[t] <= 0f || way[t] == (0, 0)) return default;
        float width = MathF.Sqrt(flow[t] / settings.StreamFlow);
        var (fx, fz) = Bent(t);
        if (down[t] < 0)
        {
            var (mx, mz) = Middle(t);
            return (width, fx, fz, mx + way[t].X * land.Size, mz + way[t].Z * land.Size, land.Floor[t], land.Floor[t], head[t]);
        }
        var (tx, tz) = Bent(down[t]);
        return (width, fx, fz, tx, tz, land.Floor[t], land.Floor[down[t]], head[t]);
    }

    /// <summary>The middle of node <paramref name="n"/>'s column (a block centre).</summary>
    private (float X, float Z) Middle(int n)
    {
        int c = land.ColumnOf[n], size = land.Size;
        return (land.X0 + c % land.Nx * size + 0.5f, land.Z0 + c / land.Nx * size + 0.5f);
    }

    /// <summary>Where node <paramref name="n"/>'s stream passes through its column: the middle, bent across its way by
    /// its meander.</summary>
    private (float X, float Z) Bent(int n)
    {
        var (mx, mz) = Middle(n);
        var (wx, wz) = way[n];
        if (wx == 0 && wz == 0) return (mx, mz);
        float length = MathF.Sqrt(wx * wx + wz * wz);
        return (mx - wz / length * bend[n], mz + wx / length * bend[n]);
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

    /// <summary>The level of standing water (deeper than half a block past the damp) in the column with its middle at
    /// (x, z), or NaN.</summary>
    public float Level(int x, int z)
    {
        int t = TopNodeAt(x, z);
        return t < 0 || depth[t] <= settings.Damp + 0.5f ? float.NaN : land.Floor[t] + depth[t] - settings.Damp;
    }

    /// <summary>The floor (blocks) of the column with its middle at (x, z), or NaN.</summary>
    public float Floor(int x, int z)
    {
        int t = TopNodeAt(x, z);
        return t < 0 ? float.NaN : land.Floor[t];
    }

    /// <summary>The water's surface over the column holding block column (x, z) (blocks), or NaN where it's dry: its
    /// water past the damp that wets all ground.</summary>
    public float Surface(int x, int z)
    {
        var (mx, mz) = Middle(x, z);
        int t = TopNodeAt(mx, mz);
        return t < 0 || depth[t] <= settings.Damp ? float.NaN : land.Floor[t] + depth[t] - settings.Damp;
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
