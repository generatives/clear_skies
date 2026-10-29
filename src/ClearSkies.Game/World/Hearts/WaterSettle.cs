using System.Collections.Concurrent;
using ClearSkies.Engine.Core;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Water as it settles on the blocks: Water plans where lakes and rivers are (lakes over samples every few blocks,
/// rivers in the channels Erosion wore), and this pours that water onto the real top, a tile of the world at a time
/// (with a margin round it, so neighbouring tiles agree), and lets it settle. It never cuts the land. Water spreads
/// into small gaps beside it, stands no higher than the dry ground round it (so it never stands against air), and a
/// lake, or a stretch of river at one level, comes down whole to its lowest edge. Where a river would dip under the
/// water either side of it, or come into a lake under the lake's level, it rises as far as the ground round it lets it.
/// Where a river comes to a drop (its piece's edge, a hole, a cliff), it doesn't stop short: it runs to the lip and
/// spills over it (that water flows, see WaterFlow, falling from there).
/// </summary>
internal sealed class WaterSettle
{
    private const int Tile = 128, Margin = 16, N = Tile + 2 * Margin;

    /// <summary>How far under a river's level the ground may lie and still be its bed; past that, down to RiverFall, a
    /// hollow it falls into and pools in; further, a drop it ends at.</summary>
    private const int RiverGap = 8, RiverFall = 24;

    /// <summary>How far (blocks) a river's water may stand over the water beside it: more, and it ends there.</summary>
    private const int FallMost = 4;

    /// <summary>How far (blocks) from planned water it spreads into gaps lower than it.</summary>
    private const int FillReach = 2;

    /// <summary>How far (blocks) up a river from a lake it comes level with the lake.</summary>
    private const int MouthReach = 6;

    /// <summary>How many columns across water may dip under the water either side and still be filled up.</summary>
    private const int DipAcross = 3;

    private const int Dry = 0, Planned = 1, Filled = 2;

    /// <summary>At most this many rounds of coming down and rising (it settles in a few).</summary>
    private const int MaxRounds = 20;

    /// <summary>One tile's settled water: per column its level (int.MinValue for none), and whether it spills over a
    /// drop beside it.</summary>
    private sealed class Settled
    {
        public readonly short[] Level = new short[Tile * Tile];
        public readonly bool[] Spills = new bool[Tile * Tile];
    }

    private static readonly Settled Empty = new();

    private readonly Water _water;
    private readonly ulong _seed;
    private readonly ConcurrentDictionary<long, Settled> _tiles = new();
    private readonly ThreadLocal<HeartWorldGenerator> _generator;

    static WaterSettle()
    {
        Array.Fill(Empty.Level, short.MinValue);
    }

    public WaterSettle(Water water, ulong seed)
    {
        _water = water;
        _seed = seed;
        _generator = new ThreadLocal<HeartWorldGenerator>(() => new HeartWorldGenerator(_seed));
    }

    /// <summary>The water's level at column (x, z), or int.MinValue for none; and whether it spills over a drop beside
    /// it.</summary>
    public (int Level, bool Spills) At(int x, int z)
    {
        int tx = FloorDiv(x, Tile), tz = FloorDiv(z, Tile);
        var tile = _tiles.GetOrAdd(((long)tx << 32) ^ (uint)tz, static (_, a) => a.Settle.Build(a.X, a.Z), (Settle: this, X: tx, Z: tz));
        int i = x - tx * Tile + Tile * (z - tz * Tile);
        short level = tile.Level[i];
        return (level == short.MinValue ? int.MinValue : level, tile.Spills[i]);
    }

    // A tile's working arrays, a column each of the tile and its margin (on the large object heap, where dropping them
    // costs a full collection): rented for each tile settled, and cleared. Enough kept for a few settled at once.
    private static readonly FixedArrayPool<int> Ints = new(N * N, maxKept: 40);
    private static readonly FixedArrayPool<byte> Bytes = new(N * N, maxKept: 8);
    private static readonly FixedArrayPool<bool> Bools = new(N * N, maxKept: 8);

    private static T[] Rent<T>(FixedArrayPool<T> pool)
    {
        var a = pool.Rent();
        Array.Clear(a);
        return a;
    }

    private Settled Build(int tx, int tz)
    {
        int x0 = tx * Tile - Margin, z0 = tz * Tile - Margin;
        var plan = Rent(Ints);   // the planned level, int.MinValue for none
        var river = Rent(Bools); // whether it's a river's
        bool any = false;
        for (int c = 0; c < N * N; c++)
        {
            var w = _water.At(x0 + c % N, z0 + c / N);
            plan[c] = w.Level;
            river[c] = w.River;
            any |= w.Level != int.MinValue;
        }
        if (!any)
        {
            Ints.Return(plan); Bools.Return(river);
            return Empty;
        }

        // The real top, where it matters: under and near the planned water.
        var near = Distance(c => plan[c] != int.MinValue, FillReach + 1);
        var ground = Rent(Ints);
        var generator = _generator.Value!;
        for (int c = 0; c < N * N; c++)
            ground[c] = near[c] > FillReach + 1 ? int.MaxValue : generator.Top(x0 + c % N, z0 + c / N) ?? int.MinValue;

        // Where the ground is far under a river, a hollow, it pools at the bottom; a drop, it ends.
        var level = Rent(Ints);
        var kind = Rent(Bytes);
        for (int c = 0; c < N * N; c++)
        {
            int t = ground[c], p = plan[c];
            level[c] = int.MinValue;
            if (t == int.MinValue || t == int.MaxValue || p == int.MinValue) continue;
            if (river[c] && t < p - RiverGap) p = t >= p - RiverFall ? t + 1 : int.MinValue;
            if (p <= t) continue;
            level[c] = p;
            kind[c] = Planned;
        }

        // Water spreads into gaps beside it lower than it.
        var reach = Distance(c => kind[c] == Planned, FillReach);
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int c = 0; c < N * N; c++)
            {
                if (kind[c] != Dry || reach[c] > FillReach || ground[c] == int.MinValue || ground[c] == int.MaxValue) continue;
                int best = int.MinValue;
                foreach (int n in Neighbours(c)) if (kind[n] != Dry && level[n] > ground[c]) best = Math.Max(best, level[n]);
                if (best == int.MinValue) continue;
                level[c] = best;
                kind[c] = Filled;
                changed = true;
            }
        }

        var fromLake = Distance(c => kind[c] == Planned && !river[c], MouthReach);
        var raised = Rent(Bools); // (risen as below: no longer level with the rest of its stretch)
        var spills = Rent(Bools); // (a river at a drop: it spills over)
        for (int round = 0, settling = 1; settling == 1 && round < MaxRounds; round++)
        {
            settling = 0;
            // It stands no higher than the dry ground beside it: water lower than that comes down to it (and a lake,
            // or a stretch of river at about one level, comes down with it, no more than the block it was planned
            // over its neighbour); at a drop a river spills over, anything else ends; spread into a gap that doesn't
            // hold it, it drains back out.
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int c = 0; c < N * N; c++)
                {
                    if (kind[c] == Dry) continue;
                    int w = level[c];
                    bool ends = false, spill = false;
                    bool runs = river[c] && kind[c] == Planned;
                    foreach (int n in Neighbours(c))
                    {
                        if (kind[n] != Dry)
                        {
                            if (kind[c] == Planned && kind[n] == Planned && Math.Abs(plan[n] - plan[c]) <= 1 && !raised[c] && !raised[n])
                                w = Math.Min(w, level[n] + Math.Max(0, plan[c] - plan[n]));
                            // (A river standing more than FallMost over the water beside it spills down to it, as over
                            // a drop.)
                            if (runs && level[c] - level[n] > FallMost) { spill = true; continue; }
                            // (And water spread into a gap drains into lower water beside it.)
                            if ((river[c] && level[c] - level[n] > FallMost) || (kind[c] == Filled && level[n] < level[c])) { ends = true; break; }
                            continue;
                        }
                        int g = ground[n];
                        if (g == int.MaxValue || g >= w) continue;
                        if (runs && (g == int.MinValue || g < w - RiverGap)) { spill = true; continue; }
                        if (kind[c] == Filled || g == int.MinValue || g < w - RiverGap) { ends = true; break; }
                        w = g;
                    }
                    // (Water spread into a gap with no water left beside it as high, its source gone, drains too.)
                    if (!ends && kind[c] == Filled)
                    {
                        ends = true;
                        foreach (int n in Neighbours(c)) if (kind[n] != Dry && level[n] >= level[c]) ends = false;
                    }
                    if (spills[c] != spill) { spills[c] = spill; changed = true; }
                    if (!ends && w == level[c]) continue;
                    if (ends || w <= ground[c])
                    {
                        kind[c] = Dry;
                        level[c] = int.MinValue;
                        spills[c] = false;
                    }
                    else level[c] = w;
                    changed = true;
                }
            }

            // Water dipping under the water either side of it, or coming into a lake under its level, rises as far as
            // the dry ground beside it lets it.
            for (int c = 0; c < N * N; c++)
            {
                if (kind[c] == Dry) continue;
                int want = Math.Max(Math.Min(Higher(c, 1, 0), Higher(c, -1, 0)), Math.Min(Higher(c, 0, 1), Higher(c, 0, -1)));
                if (fromLake[c] <= MouthReach)
                    foreach (int n in Neighbours(c))
                        if (kind[n] != Dry && fromLake[n] < fromLake[c]) want = Math.Max(want, level[n]);
                if (want <= level[c]) continue;
                foreach (int n in Neighbours(c))
                    if (kind[n] == Dry && ground[n] != int.MinValue) want = Math.Min(want, ground[n]);
                if (want <= level[c]) continue;
                level[c] = want;
                raised[c] = true;
                settling = 1;
            }
        }

        var settled = new Settled();
        for (int k = 0; k < Tile; k++)
        for (int i = 0; i < Tile; i++)
        {
            int c = i + Margin + N * (k + Margin);
            settled.Level[i + Tile * k] = level[c] == int.MinValue ? short.MinValue : (short)level[c];
            settled.Spills[i + Tile * k] = spills[c];
        }
        foreach (var a in (Span<int[]>)[plan, near, ground, level, reach, fromLake]) Ints.Return(a);
        Bytes.Return(kind);
        Bools.Return(river);
        Bools.Return(raised);
        Bools.Return(spills);
        return settled;

        // The first water higher than column c's going (dx, dz) from it over water no higher, within DipAcross
        // columns; or int.MinValue.
        int Higher(int c, int dx, int dz)
        {
            int i = c % N, k = c / N;
            for (int s = 1; s <= DipAcross; s++)
            {
                int ni = i + s * dx, nk = k + s * dz;
                if (ni < 0 || nk < 0 || ni >= N || nk >= N) return int.MinValue;
                int n = ni + N * nk;
                if (kind[n] == Dry || level[n] > level[c]) return kind[n] == Dry ? int.MinValue : level[n];
            }
            return int.MinValue;
        }
    }

    /// <summary>The columns beside column c within the tile and its margin (a struct to go through with foreach, which
    /// makes nothing: it's asked for every column, many times over).</summary>
    private static NeighbourColumns Neighbours(int c) => new(c);

    private struct NeighbourColumns(int c)
    {
        private int _way = -1;
        public int Current { get; private set; }
        public readonly NeighbourColumns GetEnumerator() => this;

        public bool MoveNext()
        {
            int i = c % N, k = c / N;
            while (++_way < 4)
            {
                switch (_way)
                {
                    case 0 when i > 0: Current = c - 1; return true;
                    case 1 when i < N - 1: Current = c + 1; return true;
                    case 2 when k > 0: Current = c - N; return true;
                    case 3 when k < N - 1: Current = c + N; return true;
                }
            }
            return false;
        }
    }

    /// <summary>How many steps (sideways, up to most) each column is from the nearest one that is; past most, most + 1.</summary>
    private static int[] Distance(Func<int, bool> from, int most)
    {
        var d = Ints.Rent(); // (all of it set below)
        var queue = t_queue ??= new Queue<int>();
        for (int c = 0; c < N * N; c++)
        {
            d[c] = from(c) ? 0 : most + 1;
            if (d[c] == 0) queue.Enqueue(c);
        }
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            if (d[c] >= most) continue;
            foreach (int n in Neighbours(c))
                if (d[n] > d[c] + 1) { d[n] = d[c] + 1; queue.Enqueue(n); }
        }
        return d;
    }

    [ThreadStatic] private static Queue<int>? t_queue; // Distance's, kept as big as it grows

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
