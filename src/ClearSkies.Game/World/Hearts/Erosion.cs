using System.Collections.Concurrent;
using System.Diagnostics;

namespace ClearSkies.Game.Generation;

/// <summary>
/// The land worn by its water, worked out a landmass at a time (see <see cref="HeartGrid.Landmass"/>) before any of its
/// chunks are made, and kept.
///
/// The hearts and their pieces come first, as ever (they go by the terrain as <see cref="ContinentTerrain"/> makes it,
/// never by this). Over the landmass's real piece tops, sampled every <see cref="Cell"/> blocks, the detail (bumps,
/// crags, dells) is taken off, and what's left is worn for <see cref="Rounds"/> rounds by the water as the flood settles
/// it (see <see cref="Flood"/>): each sample wears towards where its water goes, the faster the more runs through it
/// (stream power), and where much runs over gentle ground it scours its floor down, spread across the samples beside it.
/// Some of the detail is put back. Then the flood settles the water once more over the land as worn, for
/// <see cref="Water"/> to lay out.
///
/// What's kept is how far each sample's top moved, less its detail; HeartWorldGenerator moves its pieces' tops by that
/// (see <see cref="Field.Shift"/>), with the detail worked out per block.
/// </summary>
public sealed class Erosion
{
    /// <summary>Blocks on a side of a sample.</summary>
    public const int Cell = 8;

    // Rounds of wearing, and how many between the flood settling the water again over the land as worn so far.
    private const int Rounds = 6, FloodEvery = 3;

    /// <summary>How fast land wears towards where it drains, per square root of the rain draining through it (samples' worth).</summary>
    private const double Wear = 0.1;

    /// <summary>Where water runs: RiverFrom samples' rain (see Water too).</summary>
    internal const double RiverFrom = 40;

    // Scour: where RiverFrom samples drain over ground flatter than ScourSlope, its floor is cut down by up to Scour
    // blocks a round (twice that for the biggest), to at most ScourMost under where it spills, spread ScourAcross
    // samples either side.
    private const double ScourSlope = 0.02, Scour = 3, ScourMost = 24;
    private const int ScourAcross = 2;

    /// <summary>How much of the land's detail is put back.</summary>
    private const float DetailKept = 0.3f;

    /// <summary>How much of its ground all the land keeps under it, so no valley cuts through its piece.</summary>
    private const double KeepUnder = 48;

    /// <summary>How far under its piece's top (roughly) the land may be and still be its ground, not a hole.</summary>
    private const float HoleDepth = 40f;

    /// <summary>How far (blocks, at most about) a block column reads the samples from somewhere else, by noise, so
    /// what's worn and the water in it wander off the sample grid's lines.</summary>
    private const float WarpAcross = 6f;

    private static readonly Engine.Generation.FastNoiseLite WarpX = WarpNoise(71), WarpZ = WarpNoise(72);

    private static Engine.Generation.FastNoiseLite WarpNoise(int seed)
    {
        var noise = new Engine.Generation.FastNoiseLite(seed);
        noise.SetNoiseType(Engine.Generation.FastNoiseLite.NoiseType.OpenSimplex2);
        noise.SetFractalType(Engine.Generation.FastNoiseLite.FractalType.FBm);
        noise.SetFractalOctaves(2);
        noise.SetFrequency(1f / 40f);
        return noise;
    }

    /// <summary>Where block position (x, z) reads the samples (see <see cref="WarpAcross"/>): the land's wear and its
    /// water are read from the same place, so the water stays in what's worn.</summary>
    public static (float X, float Z) Warped(float x, float z)
        => (x + WarpAcross * WarpX.GetNoise(x, z), z + WarpAcross * WarpZ.GetNoise(x, z));

    private static readonly ConcurrentDictionary<ulong, Erosion> BySeed = new();

    /// <summary>The erosion of the world with this seed.</summary>
    public static Erosion For(ulong seed) => BySeed.GetOrAdd(seed, s => new Erosion(s));

    private readonly ulong _seed;
    private readonly ConcurrentDictionary<(int X, int Z), Lazy<Field>> _fields = new();

    private Erosion(ulong seed) => _seed = seed;

    /// <summary>The worn landmass (x, z) is on, worked out the first time it is asked for (all who ask wait for it).</summary>
    public Field Of(float x, float z) => Of(HeartGrid.Landmass(_seed, x, z));

    public Field Of((int X, int Z) landmass) =>
        _fields.GetOrAdd(landmass, l => new Lazy<Field>(() => Build(l), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>One landmass worn: per sample (from block (X0, Z0), Nx by Nz of them), how far its top moved less its
    /// detail (Worn). Samples off the land take the nearest land's, so tops by a crack read them smoothly.</summary>
    public sealed class Field
    {
        public readonly int X0, Z0, Nx, Nz;
        internal readonly float[] Worn;

        /// <summary>How long it took to work out, seconds.</summary>
        public double Seconds { get; internal set; }

        /// <summary>The water over the land as worn (see Hydrology).</summary>
        public Hydrology.Field Water { get; internal set; } = null!;

        internal Field(int x0, int z0, int nx, int nz)
        {
            X0 = x0; Z0 = z0; Nx = nx; Nz = nz;
            Worn = new float[nx * nz];
        }

        /// <summary>How far to move a piece's top at (x, z), where the terrain's detail (its height less its broad
        /// shape, see ContinentTerrain.Height) is <paramref name="detail"/>.</summary>
        public float Shift(float x, float z, float detail)
        {
            (x, z) = Warped(x, z);
            float fx = (x - X0) / Cell - 0.5f, fz = (z - Z0) / Cell - 0.5f;
            int i = (int)MathF.Floor(fx), k = (int)MathF.Floor(fz);
            if (i < 0 || k < 0 || i >= Nx - 1 || k >= Nz - 1) return 0f;
            float tx = fx - i, tz = fz - k;
            int c = i + Nx * k;
            float a = Worn[c] + (Worn[c + 1] - Worn[c]) * tx, b = Worn[c + Nx] + (Worn[c + Nx + 1] - Worn[c + Nx]) * tx;
            return a + (b - a) * tz - (1f - DetailKept) * detail;
        }
    }

    private Field Build((int X, int Z) landmass)
    {
        var clock = Stopwatch.StartNew();
        // The cell's bounds: where its own ground can be, found on a coarse grid around it.
        float spacing = HeartGrid.LandmassSpacing;
        int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
        for (float z = (landmass.Z - 0.7f) * spacing; z <= (landmass.Z + 1.7f) * spacing; z += 64f)
        for (float x = (landmass.X - 0.7f) * spacing; x <= (landmass.X + 1.7f) * spacing; x += 64f)
        {
            if (HeartGrid.Landmass(_seed, x, z) != landmass) continue;
            x0 = Math.Min(x0, (int)x); z0 = Math.Min(z0, (int)z); x1 = Math.Max(x1, (int)x); z1 = Math.Max(z1, (int)z);
        }
        x0 = FloorDiv(x0 - 64, 32) * 32; z0 = FloorDiv(z0 - 64, 32) * 32;
        x1 = FloorDiv(x1 + 96, 32) * 32; z1 = FloorDiv(z1 + 96, 32) * 32;
        int nx = (x1 - x0) / Cell, nz = (z1 - z0) / Cell, n = nx * nz;
        var field = new Field(x0, z0, nx, nz);

        // The real tops (as the generator makes them, before this moves them), a chunk's worth of samples at a time so
        // each sampler loads that chunk's hearts once; and each sample's piece.
        var has = new bool[n];
        var h0 = new double[n];      // the top
        var under = new double[n];   // the bottom of its top span
        var detail = new double[n];  // the terrain's detail there
        var rain = new double[n];    // the rain on it (see Hydrology.Rain)
        var piece = new long[n];     // which top-row heart's piece it is
        var columns = new (short Lo, short Hi)[n][]; // its solid spans
        Array.Fill(columns, Array.Empty<(short, short)>());
        var terrain = ContinentTerrain.For(_seed);
        var water = Water.For(_seed);
        const int PerChunk = 32 / Cell;
        int chunksX = (nx + PerChunk - 1) / PerChunk, chunksZ = (nz + PerChunk - 1) / PerChunk;
        var samplers = new ThreadLocal<(HeartWorldGenerator Gen, List<(short Lo, short Hi)> Spans)>(
            () => (new HeartWorldGenerator(_seed, eroded: false), new List<(short, short)>()));
        Parallel.For(0, chunksX * chunksZ, q =>
        {
            var (gen, spans) = samplers.Value;
            int cx = q % chunksX, cz = q / chunksX;
            for (int k = cz * PerChunk; k < Math.Min(nz, cz * PerChunk + PerChunk); k++)
            for (int i = cx * PerChunk; i < Math.Min(nx, cx * PerChunk + PerChunk); i++)
            {
                int c = i + nx * k;
                float x = x0 + i * Cell + Cell / 2, z = z0 + k * Cell + Cell / 2;
                if (HeartGrid.Landmass(_seed, x, z) != landmass) continue;
                if (water.Nearest(x, z, out int hi, out int hk) is not { Alive: true } heart) continue;
                gen.ColumnSpans((int)x, (int)z, spans);
                if (spans.Count == 0) continue;
                float height = terrain.Height(x, z, out float broad);
                int top = spans[^1].Hi;
                if (top <= HeartWorldGenerator.PieceTop(height, heart.Surface, heart.Drop) - HoleDepth) continue;
                has[c] = true;
                h0[c] = top + 1;
                under[c] = spans[^1].Lo;
                detail[c] = height - broad;
                rain[c] = Hydrology.Rain(terrain, x, z);
                piece[c] = ((long)hi << 32) ^ (uint)hk;
                columns[c] = spans.ToArray();
            }
        });
        samplers.Dispose();

        // The broad shape: the tops less their detail. Worn down from there, round by round, by the water as the flood
        // settles it over the land as worn so far (every FloodEvery rounds).
        var b = new double[n];
        for (int c = 0; c < n; c++) if (has[c]) b[c] = h0[c] - detail[c];
        var h = (double[])b.Clone();
        Hydrology.Field settled = null!;
        for (int round = 0; round < Rounds; round++)
        {
            if (round % FloodEvery == 0) settled = Settle(c => h[c] + DetailKept * detail[c]);
            var recv = settled.Down;
            var area = settled.Flow;
            // Towards where its water goes (off the land: the void, down to keeping its ground under it), downstream
            // first, implicitly (so any Wear is stable).
            foreach (int c in settled.Order)
            {
                int to = recv[c];
                double target = to < 0 ? under[c] + KeepUnder : h[to];
                double dist = to >= 0 && c % nx != to % nx && c / nx != to / nx ? 1.414 : 1;
                if (target >= h[c]) continue; // in a hollow
                double f = Wear * Math.Sqrt(area[c]) / dist;
                h[c] = Math.Max((h[c] + f * target) / (1 + f), Math.Min(h[c], under[c] + KeepUnder));
            }
            // And down into its floor where it runs gently, spread across the samples beside it.
            var cut = new double[n];
            foreach (int c in settled.Order)
            {
                int to = recv[c];
                if (to < 0 || area[c] < RiverFrom) continue;
                double slope = (h[c] - h[to]) / Cell;
                if (slope >= ScourSlope) continue;
                cut[c] = Scour * Math.Min(1 - Math.Max(slope, 0) / ScourSlope, 1) * Math.Min(Math.Sqrt(area[c] / RiverFrom), 2);
            }
            var spread = new double[n];
            for (int c = 0; c < n; c++)
            {
                if (!has[c]) continue;
                int ci = c % nx, ck = c / nx;
                for (int dk = -ScourAcross; dk <= ScourAcross; dk++)
                for (int di = -ScourAcross; di <= ScourAcross; di++)
                {
                    if (!Joined(c, ci + di, ck + dk)) continue;
                    double across = Math.Sqrt(di * di + dk * dk) / (ScourAcross + 1);
                    if (across < 1) spread[c] = Math.Max(spread[c], cut[ci + di + nx * (ck + dk)] * (1 - across * across));
                }
            }
            for (int c = 0; c < n; c++)
                if (spread[c] > 0)
                    h[c] = Math.Max(h[c] - spread[c], Math.Min(h[c], Math.Max(settled.Spill[c] - ScourMost, under[c] + KeepUnder)));
        }

        // What's kept: how far each top moved less its detail, spread out over the samples off the land from the
        // nearest on it.
        var worn = field.Worn;
        var queue = new Queue<int>();
        var done = new bool[n];
        for (int c = 0; c < n; c++)
        {
            if (!has[c]) continue;
            worn[c] = (float)(h[c] - b[c]);
            done[c] = true;
            queue.Enqueue(c);
        }
        while (queue.TryDequeue(out int c))
        {
            int i = c % nx, k = c / nx;
            for (int d = 0; d < 4; d++)
            {
                int ni = i + (d == 0 ? 1 : d == 1 ? -1 : 0), nk = k + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (ni < 0 || nk < 0 || ni >= nx || nk >= nz) continue;
                int e = ni + nx * nk;
                if (done[e]) continue;
                done[e] = true;
                worn[e] = worn[c];
                queue.Enqueue(e);
            }
        }

        // And the water once more, over the land as worn.
        field.Water = Settle(c => h[c] + DetailKept * detail[c]);
        field.Seconds = clock.Elapsed.TotalSeconds;
        return field;

        // The water the flood settles over the land with each sample's top at the height given.
        Hydrology.Field Settle(Func<int, double> height)
        {
            var spans = new (short Lo, short Hi)[n][];
            for (int c = 0; c < n; c++)
                spans[c] = has[c] ? CoarseLand.Lift(columns[c], (int)Math.Round(height(c) - h0[c])) : columns[c];
            var land = new CoarseLand(x0, z0, nx, nz, spans, piece, size: Cell);
            var flood = new Flood(land, unchecked((int)_seed)) { Rain = (x, z) => Hydrology.Rain(terrain, x, z) };
            flood.Run();
            return new Hydrology.Field(land, flood);
        }

        // Whether sample c and sample (i, k) are on the same piece's land.
        bool Joined(int c, int i, int k)
        {
            if (i < 0 || k < 0 || i >= nx || k >= nz) return false;
            int e = i + nx * k;
            return has[e] && piece[e] == piece[c];
        }
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
