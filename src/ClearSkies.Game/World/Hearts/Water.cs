using System.Collections.Concurrent;

namespace ClearSkies.Game.Generation;

/// <summary>The water planned over a block column (see <see cref="Water.At"/>): where it stands, whatever the ground
/// there does with it.</summary>
/// <param name="Level">World Y of the top water block, or int.MinValue for none: water fills the column up to it where
/// the ground is under it.</param>
/// <param name="Lake">Whether it's a lake's (standing water), not running water's.</param>
public readonly record struct WaterColumn(int Level, bool Lake)
{
    public static readonly WaterColumn None = new(int.MinValue, false);
}

/// <summary>
/// Where water stands, read off the landmass's cells (see <see cref="Hydrology"/>: the flood settles it per cell over
/// the land as <see cref="Erosion"/> wore it). Each cell's water stands at its lake's level, or, where enough runs
/// through it, its lowest ground plus a depth by how much runs (FlowDepth times the square root of how many times
/// RiverFrom, at most FlowDeepest). A block column takes its own cell's lake, else running water's level blended
/// between the cells round it (of its piece), so a river runs smoothly from cell to cell; the generator fills it with
/// water where the ground is under that, and lets it run on where it would stand over lower dry ground. Nothing here
/// cuts the land: the water only lies in what the erosion made.
/// </summary>
public sealed class Water
{
    private const int Cell = Hydrology.Cell;
    private const double RiverFrom = Erosion.RiverFrom, FlowDepth = 1.5, FlowDeepest = 6;

    /// <summary>A cell's lowest ground is the lowest of its columns' but this share of them (1 in LowestOf).</summary>
    private const int LowestOf = 8;

    private static readonly ConcurrentDictionary<ulong, Water> BySeed = new();

    /// <summary>The water of the world with this seed.</summary>
    public static Water For(ulong seed) => BySeed.GetOrAdd(seed, s => new Water(s));

    private readonly ulong _seed;
    private readonly ConcurrentDictionary<long, Heart> _hearts = new();
    private readonly ConcurrentDictionary<(Hydrology.Field, int), float> _lowest = new();
    private readonly ConcurrentDictionary<Hydrology.Field, float[]> _lakes = new();

    private Water(ulong seed) => _seed = seed;

    /// <summary>The water over column (x, z) of the landmass whose water is <paramref name="field"/>.</summary>
    public WaterColumn At(Hydrology.Field field, int x, int z)
    {
        int own = field.ColumnAt(x + 0.5f, z + 0.5f);
        if (own < 0) return WaterColumn.None;
        var lakes = Lakes(field);
        if (!float.IsNaN(lakes[own])) return new((int)MathF.Round(lakes[own]) - 1, true);
        int oi = own % field.Nx, ok = own / field.Nx;
        // A lake in a cell beside it (of its piece) reaches into it where its ground is under the lake (the cells are
        // coarser than the ground's dips and rises).
        double lake = double.NaN;
        for (int dk = -1; dk <= 1; dk++)
        for (int di = -1; di <= 1; di++)
        {
            int c = Beside(field, lakes, own, oi + di, ok + dk);
            if (c >= 0 && !(lake >= lakes[c])) lake = lakes[c];
        }
        // Running water: as low as any of the four cells round it with water stands (of its piece), so it lies level
        // across its river rather than banking up on the higher cells at its sides.
        float fx = (x + 0.5f - field.X0) / Cell - 0.5f, fz = (z + 0.5f - field.Z0) / Cell - 0.5f;
        int i0 = (int)MathF.Floor(fx), k0 = (int)MathF.Floor(fz);
        double runs = double.NaN;
        for (int dk = 0; dk <= 1; dk++)
        for (int di = 0; di <= 1; di++)
        {
            int i = i0 + di, k = k0 + dk;
            if (i < 0 || k < 0 || i >= field.Nx || k >= field.Nz) continue;
            int c = i + field.Nx * k;
            if (field.Piece[c] != field.Piece[own]) continue;
            double s = Surface(field, c);
            if (!double.IsNaN(s)) runs = double.IsNaN(runs) ? s : Math.Min(runs, s);
        }
        if (!double.IsNaN(lake) && !(runs > lake)) return new((int)Math.Round(lake) - 1, true);
        return double.IsNaN(runs) ? WaterColumn.None : new((int)Math.Round(runs) - 1, false);
    }

    /// <summary>Cell (i, k) if it holds a lake and is of cell own's piece, else -1.</summary>
    private static int Beside(Hydrology.Field field, float[] lakes, int own, int i, int k)
    {
        if (i < 0 || k < 0 || i >= field.Nx || k >= field.Nz) return -1;
        int c = i + field.Nx * k;
        return field.Piece[c] == field.Piece[own] && !float.IsNaN(lakes[c]) ? c : -1;
    }

    /// <summary>Each cell's lake level: lakes in cells side by side (of one piece) are one body of water, at the lowest
    /// of their levels (the water over that would run off into it).</summary>
    private float[] Lakes(Hydrology.Field field) => _lakes.GetOrAdd(field, static f =>
    {
        var lakes = (float[])f.Lake.Clone();
        var done = new bool[lakes.Length];
        var body = new List<int>();
        var queue = new Queue<int>();
        for (int c = 0; c < lakes.Length; c++)
        {
            if (float.IsNaN(lakes[c]) || done[c]) continue;
            body.Clear();
            done[c] = true; queue.Enqueue(c);
            float lowest = float.MaxValue;
            while (queue.TryDequeue(out int e))
            {
                body.Add(e);
                lowest = MathF.Min(lowest, lakes[e]);
                int i = e % f.Nx, k = e / f.Nx;
                foreach (var (di, dk) in Ways)
                {
                    int ni = i + di, nk = k + dk;
                    if (ni < 0 || nk < 0 || ni >= f.Nx || nk >= f.Nz) continue;
                    int n = ni + f.Nx * nk;
                    if (done[n] || float.IsNaN(lakes[n]) || f.Piece[n] != f.Piece[e]) continue;
                    done[n] = true; queue.Enqueue(n);
                }
            }
            foreach (int e in body) lakes[e] = lowest;
        }
        return lakes;
    });

    private static readonly (int, int)[] Ways = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    /// <summary>The water's surface over cell c (world Y, blocks' tops), or NaN where none stands or runs.</summary>
    private double Surface(Hydrology.Field field, int c)
    {
        var lakes = Lakes(field);
        if (!float.IsNaN(lakes[c])) return lakes[c];
        double runs = field.Flow[c];
        if (runs < RiverFrom) return double.NaN;
        return Lowest(field, c) + Math.Min(FlowDeepest, FlowDepth * Math.Sqrt(runs / RiverFrom));
    }

    // Each worker's generator for the cells' ground.
    [ThreadStatic] private static HeartWorldGenerator? t_generator;

    /// <summary>The lowest ground in cell c (world Y over its top block) of the cell's own piece, worked out once; NaN
    /// for none.</summary>
    private float Lowest(Hydrology.Field field, int c)
        => _lowest.GetOrAdd((field, c), static (key, water) =>
        {
            var (field, c) = key;
            var generator = t_generator is { } kept && kept.Seed == water._seed ? kept : t_generator = new HeartWorldGenerator(water._seed);
            int x0 = field.X0 + c % field.Nx * Cell, z0 = field.Z0 + c / field.Nx * Cell;
            var tops = new List<int>(Cell * Cell);
            for (int z = z0; z < z0 + Cell; z++)
            for (int x = x0; x < x0 + Cell; x++)
                if (water.Nearest(x + 0.5f, z + 0.5f, out int hi, out int hk) is { Alive: true }
                    && (((long)hi << 32) ^ (uint)hk) == field.Piece[c] && generator.Top(x, z) is int top)
                    tops.Add(top + 1);
            if (tops.Count == 0) return float.NaN;
            // (Low, but not down a hole into the caves under it a column or two may be.)
            tops.Sort();
            return tops[tops.Count / LowestOf];
        }, this);

    /// <summary>The top row's heart whose piece holds (x, z) at the surface (nearest, counting depth VerticalScale
    /// times over), and its cell (hi, hk); null if none.</summary>
    internal Heart? Nearest(float x, float z, out int hi, out int hk)
    {
        float size = HeartGrid.Layers[0].CellSize;
        int i0 = (int)MathF.Floor(x / size), k0 = (int)MathF.Floor(z / size);
        Heart? best = null;
        float bestD = float.MaxValue;
        hi = hk = 0;
        for (int k = k0 - 1; k <= k0 + 1; k++)
        for (int i = i0 - 1; i <= i0 + 1; i++)
        {
            var h = TopHeart(i, k);
            if (!h.Exists) continue;
            float hy = h.Depth * HeartGrid.VerticalScale;
            float d = (h.X - x) * (h.X - x) + hy * hy + (h.Z - z) * (h.Z - z);
            if (d < bestD) { bestD = d; best = h; hi = i; hk = k; }
        }
        return best;
    }

    // Asked per block column: the lambdas are static, given what they need, so a lookup allocates nothing (one capturing
    // hi and hk allocated a closure and a delegate every call, most of what generating terrain allocated).
    private Heart TopHeart(int i, int k)
        => _hearts.GetOrAdd(Key(i, k), static (_, a) => HeartGrid.At(a.Seed, 0, a.I, -1, a.K), (Seed: _seed, I: i, K: k));

    private static long Key(int i, int k) => ((long)i << 32) ^ (uint)k;
}
