using System.Collections.Concurrent;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Generation;

namespace ClearSkies.Game.Generation;

/// <summary>What water there is at a block column (see <see cref="Water.At"/>).</summary>
/// <param name="Level">World Y of the top water block, or int.MinValue for none.</param>
/// <param name="Off">For a lake, how far (blocks) the column is from the middle of the lake's nearest sample cell.</param>
/// <param name="River">Whether it is a river's (running in a channel Erosion wore), not a lake's.</param>
public readonly record struct WaterColumn(int Level, float Off, bool River)
{
    public static readonly WaterColumn None = new(int.MinValue, float.MaxValue, false);
}

/// <summary>
/// Still water, no flow: lakes and the rivers between them, worked out one piece of land at a time. A piece (the
/// ground held by one of the top row's hearts, see <see cref="HeartGrid"/>) is ringed by cracks, so whatever rains on
/// it drains off its own edges: it is a watershed of its own, worked out whole once and cached, so every chunk agrees.
///
/// The lakes come from <see cref="Hydrology"/>, which settles the water over the whole landmass; this lays them over
/// the piece's real top (as HeartWorldGenerator wears it down), sampled every <see cref="Step"/> blocks: a lake fills
/// the samples under its level. Rivers run in the channels <see cref="Erosion"/> wore for them, filling each to the
/// brims of its bed. Nothing here cuts the land: the water only lies in what the erosion made.
/// </summary>
public sealed class Water
{
    public const int Step = 4;

    /// <summary>How far past its heart's cell (blocks) a piece's ground can reach: the window worked out round it.</summary>
    private static int Reach => (int)(0.8f * HeartGrid.Layers[0].CellSize);
    private static int Window => ((int)HeartGrid.Layers[0].CellSize + 2 * Reach) / Step;

    /// <summary>How near a piece's side face (scaled space, see HeartWorldGenerator.Solid) counts as its crack, for
    /// rivers: more than the wear and the wobble of its outline together.</summary>
    private static float FaceMargin => 44f + 24f * (HeartWorldGenerator.CrackWidth - 1f);

    /// <summary>How far under its piece's top (roughly) the land may be and still be its ground, not a hole.</summary>
    private const float HoleDepth = 40f;

    /// <summary>The ground of a drain: lower than any land, so whatever reaches it spills out.</summary>
    private const float Drain = -1e6f;

    private static readonly ConcurrentDictionary<ulong, Water> BySeed = new();

    /// <summary>The water of the world with this seed.</summary>
    public static Water For(ulong seed) => BySeed.GetOrAdd(seed, s => new Water(s));

    private readonly ulong _seed;
    private readonly ContinentTerrain _terrain;
    private readonly ConcurrentDictionary<long, Heart> _hearts = new();
    private readonly ConcurrentDictionary<long, Piece> _pieces = new();

    private Water(ulong seed)
    {
        _seed = seed;
        _terrain = ContinentTerrain.For(seed);
        _settle = new WaterSettle(this, seed);
    }

    private readonly WaterSettle _settle;

    /// <summary>The water's level at column (x, z) once it has settled on the blocks (see <see cref="WaterSettle"/>), or
    /// int.MinValue for none.</summary>
    public int Settled(int x, int z) => _settle.At(x, z);

    /// <summary>One piece's water, per sample of its window (cells from (X0, Z0), world sample coordinates).</summary>
    private sealed class Piece
    {
        public int X0, Z0;
        public int[] Lake = Array.Empty<int>();    // level of the lake over the sample, or int.MinValue
        public float[] Ground = Array.Empty<float>(); // the land's real top over the sample, or Drain
        public Erosion.Field Worn = null!;          // its landmass worn, rivers' channels and all
    }

    /// <summary>The water at column (x, z): a lake over it or right beside it (water fills it from its ground up to
    /// the level, where the ground is under it), or a river through it.</summary>
    public WaterColumn At(int x, int z)
    {
        float px = x + 0.5f, pz = z + 0.5f;
        if (Nearest(px, pz, out int hi, out int hk) is not { Alive: true }) return WaterColumn.None;
        var piece = PieceOf(hi, hk);
        int ci = FloorDiv(x, Step) - piece.X0, ck = FloorDiv(z, Step) - piece.Z0;
        if (ci < 1 || ck < 1 || ci >= Window - 1 || ck >= Window - 1) return WaterColumn.None;
        int own = piece.Lake[ci + Window * ck];
        if (own != int.MinValue) return new(own, Off(ci, ck, px, pz, piece), false);
        // A river: in its channel, as high as the channel's bed is all across.
        float stream = piece.Worn.Stream(px, pz);
        if (!float.IsNaN(stream)) return new((int)MathF.Floor(stream), 0f, true);

        // Just past a lake's sampled cells the ground can still dip under its level between samples: fill those too.
        int level = int.MinValue;
        float best = float.MaxValue;
        for (int dk = -1; dk <= 1; dk++)
        for (int di = -1; di <= 1; di++)
        {
            int l = piece.Lake[ci + di + Window * (ck + dk)];
            if (l == int.MinValue) continue;
            float cx = (piece.X0 + ci + di + 0.5f) * Step - px, cz = (piece.Z0 + ck + dk + 0.5f) * Step - pz;
            float dd = cx * cx + cz * cz;
            if (dd < best) { best = dd; level = l; }
        }
        return level == int.MinValue ? WaterColumn.None : new(level, MathF.Sqrt(best), false);
    }

    /// <summary>Roughly the top of heart <paramref name="h"/>'s piece at (x, z): the generator's, before its edges are
    /// rounded and water cuts it.</summary>
    internal float RoughTop(float x, float z, Heart h)
    {
        float height = _terrain.Height(x, z, out float broad);
        return HeartWorldGenerator.PieceTop(height, h.Surface, h.Drop) + Erosion.For(_seed).Of(x, z).Shift(x, z, height - broad);
    }

    private static float Off(int ci, int ck, float px, float pz, Piece piece)
    {
        float cx = (piece.X0 + ci + 0.5f) * Step - px, cz = (piece.Z0 + ck + 0.5f) * Step - pz;
        return MathF.Sqrt(cx * cx + cz * cz);
    }

    // A piece's working array, a window's samples (on the large object heap, where dropping it costs a full
    // collection): rented for each piece worked out, and cleared. Enough kept for a few worked out at once.
    private static readonly FixedArrayPool<float> Floats = new(Window * Window, maxKept: 8);

    private static T[] Rent<T>(FixedArrayPool<T> pool)
    {
        var a = pool.Rent();
        Array.Clear(a);
        return a;
    }

    // Each worker's generator for the pieces' ground.
    [ThreadStatic] private static HeartWorldGenerator? t_generator;

    /// <summary>Works out the water of the piece of the top row's heart in cell (hi, hk).</summary>
    private Piece Build(int hi, int hk)
    {
        int N = Window * Window;
        float size = HeartGrid.Layers[0].CellSize;
        var piece = new Piece { X0 = FloorDiv(hi * (int)size - Reach, Step), Z0 = FloorDiv(hk * (int)size - Reach, Step) };
        var self = TopHeart(hi, hk);
        var hearts = new List<Heart>();
        for (int k = hk - 2; k <= hk + 2; k++)
        for (int i = hi - 2; i <= hi + 2; i++)
            hearts.Add(TopHeart(i, k));

        // The piece's ground, and how far each sample is from its faces; elsewhere drains.
        var ground = new float[N];
        var face = Rent(Floats);
        for (int k = 0; k < Window; k++)
        for (int i = 0; i < Window; i++)
        {
            int c = i + Window * k;
            float x = (piece.X0 + i + 0.5f) * Step, z = (piece.Z0 + k + 0.5f) * Step;
            face[c] = FaceDistance(hearts, self, x, z);
            if (i == 0 || k == 0 || i == Window - 1 || k == Window - 1 || face[c] < FaceMargin) { ground[c] = Drain; continue; }
            ground[c] = 0f; // (worked out below)
        }

        // The ground is the land's real top (as HeartWorldGenerator makes it, rounded off by its faces and all), a
        // chunk column at a time so the generator loads the hearts round each once. Where it is far under the piece's
        // top, a hole into the caves under it, it is a drain too.
        var generator = t_generator is { } kept && kept.Seed == _seed ? kept : t_generator = new HeartWorldGenerator(_seed);
        int cx0 = FloorDiv(piece.X0 * Step, 32), cz0 = FloorDiv(piece.Z0 * Step, 32);
        int cx1 = FloorDiv((piece.X0 + Window) * Step, 32), cz1 = FloorDiv((piece.Z0 + Window) * Step, 32);
        for (int cz = cz0; cz <= cz1; cz++)
        for (int cx = cx0; cx <= cx1; cx++)
        for (int k = Math.Max(0, CeilDiv(cz * 32 - Step / 2, Step) - piece.Z0); k < Window && (piece.Z0 + k) * Step + Step / 2 < (cz + 1) * 32; k++)
        for (int i = Math.Max(0, CeilDiv(cx * 32 - Step / 2, Step) - piece.X0); i < Window && (piece.X0 + i) * Step + Step / 2 < (cx + 1) * 32; i++)
        {
            int c = i + Window * k;
            if (ground[c] == Drain) continue;
            int x = (piece.X0 + i) * Step + Step / 2, z = (piece.Z0 + k) * Step + Step / 2;
            float roughly = RoughTop(x, z, self);
            ground[c] = generator.Top(x, z) is int top && top > roughly - HoleDepth ? top + 0.5f : Drain;
        }
        piece.Ground = ground;

        // The lakes and the water running over the land, as the flood settles them a cell at a time over its whole
        // landmass (see Hydrology), over the land as worn (see Erosion).
        var erosion = Erosion.For(_seed).Of(self.X, self.Z);
        var hydrology = erosion.Water;
        piece.Worn = erosion;
        piece.Lake = new int[N];
        Array.Fill(piece.Lake, int.MinValue);
        int ColumnOf(int c) => hydrology.ColumnAt((piece.X0 + c % Window + 0.5f) * Step, (piece.Z0 + c / Window + 0.5f) * Step);

        // A lake fills every sample its ground is under that is joined to its cells by others its ground is under: as
        // high as its level, or lower if it would reach a drain (a crack, a hole) at that, as its rim at the blocks
        // lets it.
        var seen = new HashSet<float>();
        var fill = new List<int>();
        var queue = new Queue<int>();
        for (int c = 0; c < N; c++)
        {
            if (ground[c] == Drain || piece.Lake[c] != int.MinValue) continue;
            int column = ColumnOf(c);
            if (column < 0 || float.IsNaN(hydrology.Lake[column]) || !seen.Add(hydrology.Lake[column])) continue;
            int level = (int)MathF.Floor(hydrology.Lake[column]) - 1;
            for (; level > ground[c]; level--)
            {
                fill.Clear(); queue.Clear();
                var reached = new HashSet<int> { c };
                queue.Enqueue(c);
                bool leaks = false;
                while (queue.TryDequeue(out int e) && !leaks)
                {
                    fill.Add(e);
                    int ei = e % Window, ek = e / Window;
                    for (int d = 0; d < 4 && !leaks; d++)
                    {
                        int ni = ei + (d == 0 ? 1 : d == 1 ? -1 : 0), nk = ek + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (ni < 0 || nk < 0 || ni >= Window || nk >= Window) { leaks = true; break; }
                        int ne = ni + Window * nk;
                        if (ground[ne] >= level || reached.Contains(ne)) continue;
                        if (ground[ne] == Drain) { leaks = true; break; }
                        reached.Add(ne); queue.Enqueue(ne);
                    }
                }
                if (leaks) continue;
                foreach (int e in fill) piece.Lake[e] = Math.Max(piece.Lake[e], level);
                break;
            }
        }
        // Lakes whose fills meet are one body of water, at one level: the lowest of theirs (the water over it would
        // run off into it).
        var body = new List<int>();
        var joined = new bool[N];
        for (int c = 0; c < N; c++)
        {
            if (piece.Lake[c] == int.MinValue || joined[c]) continue;
            body.Clear(); queue.Clear();
            joined[c] = true; queue.Enqueue(c);
            int lowest = int.MaxValue;
            while (queue.TryDequeue(out int e))
            {
                body.Add(e);
                lowest = Math.Min(lowest, piece.Lake[e]);
                int ei = e % Window, ek = e / Window;
                for (int d = 0; d < 4; d++)
                {
                    int ni = ei + (d == 0 ? 1 : d == 1 ? -1 : 0), nk = ek + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (ni < 0 || nk < 0 || ni >= Window || nk >= Window) continue;
                    int ne = ni + Window * nk;
                    if (piece.Lake[ne] == int.MinValue || joined[ne]) continue;
                    joined[ne] = true; queue.Enqueue(ne);
                }
            }
            foreach (int e in body) piece.Lake[e] = lowest;
        }

        Floats.Return(face);
        return piece;
    }

    /// <summary>How far (x, z) is inside the piece of heart <paramref name="self"/> (scaled space, see
    /// HeartWorldGenerator.Solid): less than 0 if it is another's, or 0 if that piece is gone.</summary>
    private static float FaceDistance(List<Heart> hearts, Heart self, float x, float z)
    {
        if (!self.Alive) return 0f;
        float by = self.Depth * HeartGrid.VerticalScale;
        float bestD = (self.X - x) * (self.X - x) + by * by + (self.Z - z) * (self.Z - z);
        float least = float.MaxValue;
        foreach (var h in hearts)
        {
            if (!h.Exists || h == self) continue;
            float hy = h.Depth * HeartGrid.VerticalScale;
            float d = (h.X - x) * (h.X - x) + hy * hy + (h.Z - z) * (h.Z - z);
            float ex = h.X - self.X, ey = hy - by, ez = h.Z - self.Z;
            least = MathF.Min(least, (d - bestD) / (2f * MathF.Sqrt(ex * ex + ey * ey + ez * ez)));
        }
        return least;
    }

    /// <summary>The top row's heart nearest (x, z) at the surface (counting depth VerticalScale times over), and its
    /// cell.</summary>
    /// <summary>The top row's heart whose piece holds (x, z) at the surface, and its cell (hi, hk); null if none.</summary>
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

    private Piece PieceOf(int hi, int hk)
        => _pieces.GetOrAdd(Key(hi, hk), static (_, a) => a.Water.Build(a.I, a.K), (Water: this, I: hi, K: hk));

    private static long Key(int i, int k) => ((long)i << 32) ^ (uint)k;

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

    private static int CeilDiv(int a, int b) => -FloorDiv(-a, b);
}
