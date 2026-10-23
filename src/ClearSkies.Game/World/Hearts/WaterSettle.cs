using System.Collections.Concurrent;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Water as it settles on the blocks: Water plans where lakes and rivers are (over samples every few blocks), and
/// this pours that water onto the real top, a tile of the world at a time (with a margin round it, so neighbouring
/// tiles agree), and lets it settle. Rivers cut their beds first, and their banks slope down to them. Then water
/// spreads into small gaps beside it, stands no higher than the dry ground round it (so it never stands against
/// air), and a lake, or a stretch of river at one level, comes down whole to its lowest edge. Where a river would
/// dip under the water either side of it, or come into a lake under the lake's level, it rises as far as the ground
/// round it lets it.
/// </summary>
internal sealed class WaterSettle
{
    private const int Tile = 128, Margin = 16, N = Tile + 2 * Margin;

    /// <summary>How far under a river's level the ground may lie and still be its bed; past that, down to RiverFall, a
    /// hollow it falls into and pools in; further, a drop it ends at.</summary>
    private const int RiverGap = 8, RiverFall = 24;

    /// <summary>How far (blocks) a river's water may stand over the water beside it: more, and it ends there.</summary>
    private const int FallMost = 4;

    /// <summary>How wide (blocks) and high over a lake a strip of ground between it and a river may be and be cut
    /// through.</summary>
    private const int DamWidth = 2, DamHeight = 3, RimHeight = 1;

    /// <summary>How far (blocks) from planned water it spreads into gaps lower than it.</summary>
    private const int FillReach = 2;

    /// <summary>How far (blocks) up a river from a lake it comes level with the lake.</summary>
    private const int MouthReach = 6;

    /// <summary>How many columns across water may dip under the water either side and still be filled up.</summary>
    private const int DipAcross = 3;

    /// <summary>How far (blocks) out a river's banks reach at most (see Water).</summary>
    private const int BankFar = 18;

    private const int Dry = 0, Planned = 1, Filled = 2;

    /// <summary>At most this many rounds of coming down and rising (it settles in a few).</summary>
    private const int MaxRounds = 20;

    /// <summary>One tile's settled water: per column its level (int.MinValue for none) and the top of its ground once
    /// rivers have cut it (int.MaxValue where it isn't cut).</summary>
    private sealed class Settled
    {
        public readonly short[] Level = new short[Tile * Tile], Ground = new short[Tile * Tile];
    }

    private static readonly Settled Empty = new();

    private readonly Water _water;
    private readonly ulong _seed;
    private readonly ConcurrentDictionary<long, Settled> _tiles = new();
    private readonly ThreadLocal<HeartWorldGenerator> _generator;

    static WaterSettle()
    {
        Array.Fill(Empty.Level, short.MinValue);
        Array.Fill(Empty.Ground, short.MaxValue);
    }

    public WaterSettle(Water water, ulong seed)
    {
        _water = water;
        _seed = seed;
        _generator = new ThreadLocal<HeartWorldGenerator>(() => new HeartWorldGenerator(_seed));
    }

    /// <summary>The water's level at column (x, z), or int.MinValue for none; and the top of the ground there once a
    /// river has cut it, or int.MaxValue where none has.</summary>
    public (int Level, int Ground) At(int x, int z)
    {
        int tx = FloorDiv(x, Tile), tz = FloorDiv(z, Tile);
        var tile = _tiles.GetOrAdd(((long)tx << 32) ^ (uint)tz, _ => Build(tx, tz));
        int i = x - tx * Tile + Tile * (z - tz * Tile);
        short level = tile.Level[i], ground = tile.Ground[i];
        return (level == short.MinValue ? int.MinValue : level, ground == short.MaxValue ? int.MaxValue : ground);
    }

    private Settled Build(int tx, int tz)
    {
        int x0 = tx * Tile - Margin, z0 = tz * Tile - Margin;
        var plan = new int[N * N];      // the planned level, int.MinValue for none
        var depth = new int[N * N];     // a river's bed under its level; 0 for a lake
        var bank = new int[N * N];
        bool any = false;
        for (int c = 0; c < N * N; c++)
        {
            var w = _water.At(x0 + c % N, z0 + c / N);
            plan[c] = w.Level;
            depth[c] = w.RiverDepth;
            bank[c] = w.Bank;
            any |= w.Level != int.MinValue || w.Bank != int.MaxValue;
        }
        if (!any) return Empty;

        // The real top, where it matters: under and near the planned water and its banks.
        var near = Distance(c => plan[c] != int.MinValue || bank[c] != int.MaxValue, FillReach + 1);
        var top = new int[N * N];
        var generator = _generator.Value!;
        for (int c = 0; c < N * N; c++)
            top[c] = near[c] > FillReach + 1 ? int.MaxValue : generator.Top(x0 + c % N, z0 + c / N) ?? int.MinValue;

        // Rivers cut their beds and banks; where the ground is far under a river, a hollow, it pools at the bottom; a
        // drop, it ends.
        var ground = new int[N * N];
        var level = new int[N * N];
        var kind = new byte[N * N];
        for (int c = 0; c < N * N; c++)
        {
            int t = top[c], p = plan[c];
            ground[c] = t;
            level[c] = int.MinValue;
            if (t == int.MinValue || t == int.MaxValue) continue;
            if (p == int.MinValue)
            {
                ground[c] = Math.Min(t, bank[c]);
                continue;
            }
            if (depth[c] > 0)
            {
                if (t < p - RiverGap) p = t >= p - RiverFall ? t + depth[c] : int.MinValue;
                if (p == int.MinValue) continue;
                ground[c] = Math.Min(t, p - depth[c]);
            }
            if (p <= ground[c]) continue;
            level[c] = p;
            kind[c] = Planned;
        }

        // A thin strip of ground between a lake and a river beside it (a dam the samples were too coarse to see) is
        // cut through, so the river runs into the lake; so is a rim just breaking a lake's surface. (What is cut is the
        // lake's from then on, so a strip cut part way through is cut on through.)
        for (bool cut = true; cut;)
        {
        cut = false;
        for (int c = 0; c < N * N; c++)
        {
            if (kind[c] != Planned || depth[c] > 0) continue;
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int i = c % N, k = c / N, s = 1, n = c;
                for (; s <= DamWidth + 1; s++)
                {
                    int ni = i + s * dx, nk = k + s * dz;
                    if (ni < 0 || nk < 0 || ni >= N || nk >= N) { s = int.MaxValue; break; }
                    n = ni + N * nk;
                    if (kind[n] != Dry) break;
                    if (ground[n] == int.MinValue || ground[n] == int.MaxValue || ground[n] > level[c] + DamHeight) { s = int.MaxValue; break; }
                }
                if (s < 2 || s > DamWidth + 1 || kind[n] != Planned) continue;
                // (Between two stretches of one lake, only a rim at about its level: a strip just breaking its surface.)
                if (depth[n] == 0)
                {
                    bool rim = level[n] == level[c];
                    for (int t = 1; t < s && rim; t++) rim = ground[c + t * (dx + N * dz)] <= level[c] + RimHeight;
                    if (!rim) continue;
                }
                int w = Math.Min(level[c], level[n]);
                for (int t = 1; t < s; t++)
                {
                    int d = c + t * (dx + N * dz);
                    plan[d] = level[d] = w;
                    depth[d] = 0;
                    ground[d] = Math.Min(ground[d], w - 1);
                    kind[d] = Planned;
                    cut = true;
                }
            }
        }
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

        var fromLake = Distance(c => kind[c] == Planned && depth[c] == 0, MouthReach);
        var raised = new bool[N * N]; // (risen as below: no longer level with the rest of its stretch)
        for (int round = 0, settling = 1; settling == 1 && round < MaxRounds; round++)
        {
            settling = 0;
            // It stands no higher than the dry ground beside it: water lower than that comes down to it (and a lake,
            // or a stretch of river at about one level, comes down with it, no more than the block it was planned
            // over its neighbour); over a drop, it ends; spread into a gap that doesn't hold it, it drains back out.
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int c = 0; c < N * N; c++)
                {
                    if (kind[c] == Dry) continue;
                    int w = level[c];
                    bool ends = false;
                    foreach (int n in Neighbours(c))
                    {
                        if (kind[n] != Dry)
                        {
                            if (kind[c] == Planned && kind[n] == Planned && Math.Abs(plan[n] - plan[c]) <= 1 && !raised[c] && !raised[n])
                                w = Math.Min(w, level[n] + Math.Max(0, plan[c] - plan[n]));
                            // (A river standing more than FallMost over the water beside it would be a wall of water:
                            // it ends there, as over a drop.)
                            // (And water spread into a gap drains into lower water beside it.)
                            if ((depth[c] > 0 && level[c] - level[n] > FallMost) || (kind[c] == Filled && level[n] < level[c])) { ends = true; break; }
                            continue;
                        }
                        int g = ground[n];
                        if (g == int.MaxValue || g >= w) continue;
                        if (kind[c] == Filled || g == int.MinValue || g < w - RiverGap) { ends = true; break; }
                        w = g;
                    }
                    // (Water spread into a gap with no water left beside it as high, its source gone, drains too.)
                    if (!ends && kind[c] == Filled)
                    {
                        ends = true;
                        foreach (int n in Neighbours(c)) if (kind[n] != Dry && level[n] >= level[c]) ends = false;
                    }
                    if (!ends && w == level[c]) continue;
                    if (ends || w <= ground[c])
                    {
                        kind[c] = Dry;
                        level[c] = int.MinValue;
                        ground[c] = top[c]; // (a river's bed it no longer fills isn't cut)
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

        // Where a river settled lower than planned, its banks come down as far with it (so they still slope down to its
        // water, not stand over it), though never under the water beside them.
        var drop = new int[N * N];
        for (int c = 0; c < N * N; c++)
            if (kind[c] == Planned && depth[c] > 0) drop[c] = Math.Max(0, plan[c] - level[c]);
        for (int pass = 0; pass < BankFar; pass++)
            for (int c = 0; c < N * N; c++)
            {
                if (kind[c] != Dry || bank[c] == int.MaxValue || plan[c] != int.MinValue) continue;
                foreach (int n in Neighbours(c)) drop[c] = Math.Max(drop[c], kind[n] != Dry || bank[n] != int.MaxValue ? drop[n] : 0);
            }
        for (int c = 0; c < N * N; c++)
        {
            if (drop[c] == 0 || kind[c] != Dry || bank[c] == int.MaxValue || top[c] == int.MaxValue || top[c] == int.MinValue) continue;
            int g = Math.Min(ground[c], bank[c] - drop[c]);
            foreach (int n in Neighbours(c)) if (kind[n] != Dry) g = Math.Max(g, level[n]);
            ground[c] = Math.Min(ground[c], g);
        }

        var settled = new Settled();
        for (int k = 0; k < Tile; k++)
        for (int i = 0; i < Tile; i++)
        {
            int c = i + Margin + N * (k + Margin), o = i + Tile * k;
            settled.Level[o] = level[c] == int.MinValue ? short.MinValue : (short)level[c];
            settled.Ground[o] = ground[c] == int.MinValue || ground[c] >= top[c] ? short.MaxValue : (short)ground[c];
        }
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

    /// <summary>The columns beside column c within the tile and its margin.</summary>
    private static IEnumerable<int> Neighbours(int c)
    {
        int i = c % N, k = c / N;
        if (i > 0) yield return c - 1;
        if (i < N - 1) yield return c + 1;
        if (k > 0) yield return c - N;
        if (k < N - 1) yield return c + N;
    }

    /// <summary>How many steps (sideways, up to most) each column is from the nearest one that is; past most, most + 1.</summary>
    private static int[] Distance(Func<int, bool> from, int most)
    {
        var d = new int[N * N];
        var queue = new Queue<int>();
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

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
