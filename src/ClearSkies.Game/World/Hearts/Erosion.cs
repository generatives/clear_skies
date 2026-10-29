using System.Collections.Concurrent;
using System.Diagnostics;
using ClearSkies.Engine.Generation;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Valleys worn into the land by its rivers, worked out a landmass at a time (see <see cref="HeartGrid.Landmass"/>)
/// before any of its chunks are made, and kept: so its pieces' tops are shaped for the water that runs off them.
///
/// The hearts and their pieces come first, as ever (they go by the terrain as <see cref="ContinentTerrain"/> makes it,
/// never by this). Over the landmass's real piece tops, sampled every <see cref="Cell"/> blocks with their worn edges,
/// cracks and holes, the detail (bumps, crags, dells) is taken off, and what's left is worn down by rain for
/// <see cref="Rounds"/> rounds: each round a priority flood from each piece's edges gives every sample the way its
/// water goes, and each moves towards the one it drains to, the faster the more rain drains through it (stream power;
/// rain as the climate's wetness gives it, see Hydrology.Rain, so dry land hardly wears).
/// Each piece drains to its own edges: its rivers notch down into its rim where they leave it. Where much drains over
/// gentle ground, its floor is also scoured down under where it spills, so basins open behind steeper rims for lakes.
/// Then the bigger rivers cut channels with sloping banks, winding where the land is flat. Some of the detail is put
/// back, none in the channels.
///
/// What's kept is how far each sample's top moved, less its detail; HeartWorldGenerator moves its pieces' tops by that
/// (see <see cref="Field.Shift"/>), with the detail worked out per block, so only the valleys come from the samples.
/// </summary>
public sealed class Erosion
{
    /// <summary>Blocks on a side of a sample.</summary>
    public const int Cell = 8;

    // Rounds of wearing, and how many between the flood settling the water again over the land as worn so far.
    private const int Rounds = 6, FloodEvery = 3;

    /// <summary>The steepest the wearing may cut into the land, blocks down per block across (see Build).</summary>
    private const float MaxCutSlope = 1f;

    /// <summary>How fast land wears towards where it drains, per square root of the rain draining through it (samples' worth).</summary>
    private const double Wear = 0.1;

    // Scour: where ScourFrom times RiverFrom samples drain over ground flatter than ScourSlope, its floor is cut down by
    // up to Scour blocks a round (twice that for the biggest), to at most ScourMost under where it spills.
    private const double ScourFrom = 1, ScourSlope = 0.02, Scour = 3, ScourMost = 24;

    // A lake's bed scours fully LakeScourDepth blocks under its level (less towards its shores), and every scour is
    // spread ScourAcross samples either side.
    private const double LakeScourDepth = 8;
    private const int ScourAcross = 2;

    /// <summary>How much of the land's detail is put back, outside the channels.</summary>
    private const float DetailKept = 0.3f;

    // A piece's edge stays where it is, except where a stream leaves it: there it wears down towards a notch up to
    // NotchDepth deep, the deepest where NotchFrom times RiverFrom samples drain out. All the land keeps at least
    // KeepUnder of its ground under it, so no valley cuts through its piece.
    private const double NotchDepth = 16, NotchFrom = 1, KeepUnder = 48;

    /// <summary>How far under its piece's top (roughly) the land may be and still be its ground, not a hole (as Water).</summary>
    private const float HoleDepth = 40f;

    // Rivers: a channel is cut where RiverFrom columns' rain runs (FlatFactor times that where the land is flatter than
    // FlatSlope), up to RiverDepth blocks deep. Banks rise BankGrade blocks per block for BankReach blocks.
    internal const double RiverFrom = 40, FlatFactor = 5, FlatSlope = 0.01, RiverDepth = 10;
    private const float BankReach = 24f, BankGrade = 0.15f;

    /// <summary>How far a channel's bed rises from its middle to its edges, per block of its half-width.</summary>
    private const float EdgeRise = 0.5f;

    /// <summary>How many times a river's line is eased towards its neighbours' (see Field).</summary>
    private const int RiverEasing = 4;


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
    /// detail (Worn), its top worn (Top, with the detail kept), and how much it is channel (Mask, 1 in a channel,
    /// fading over the banks). Samples off the land take the nearest land's, so tops by a crack read them smoothly.
    /// The rivers' channels are cut per block, not by sample: each river sample has its bed (Bed, NaN for none), its
    /// half-width in blocks, the sample it runs on to (Down, -1 for none) and where its line runs through (RiverX,
    /// RiverZ: the samples' middles smoothed along the river, so it bends rather than zigzagging from sample to
    /// sample), and a channel is cut round the line between them, its banks sloping up from it.</summary>
    public sealed class Field
    {
        public readonly int X0, Z0, Nx, Nz;
        internal readonly float[] Worn, Top, Mask, Bed, Half, RiverX, RiverZ;
        internal readonly int[] Down;
        internal readonly bool[] NearRiver; // a river sample within Search samples
        private readonly FastNoiseLite _bendX, _bendZ;

        /// <summary>How long it took to work out, seconds; and how many river samples it has.</summary>
        public double Seconds { get; internal set; }
        public int RiverSamples { get; internal set; }

        /// <summary>The water over the land as worn (see Hydrology).</summary>
        public Hydrology.Field Water { get; internal set; } = null!;

        internal Field(ulong seed, int x0, int z0, int nx, int nz)
        {
            X0 = x0; Z0 = z0; Nx = nx; Nz = nz;
            int n = nx * nz;
            Worn = new float[n]; Top = new float[n]; Mask = new float[n];
            Bed = new float[n]; Half = new float[n]; Down = new int[n]; NearRiver = new bool[n];
            RiverX = new float[n]; RiverZ = new float[n];
            Array.Fill(Bed, float.NaN);
            Array.Fill(Down, -1);
            _bendX = BendNoise(seed + 78);
            _bendZ = BendNoise(seed + 79);
        }

        /// <summary>How far to move a piece's top at (x, z), where the terrain's detail (its height less its broad
        /// shape, see ContinentTerrain.Height) is <paramref name="detail"/>.</summary>
        public float Shift(float x, float z, float detail)
        {
            float fx = (x - X0) / Cell - 0.5f, fz = (z - Z0) / Cell - 0.5f;
            int i = (int)MathF.Floor(fx), k = (int)MathF.Floor(fz);
            if (i < 0 || k < 0 || i >= Nx - 1 || k >= Nz - 1) return 0f;
            float tx = fx - i, tz = fz - k;
            int c = i + Nx * k;
            float mask = Bilinear(Mask, c, tx, tz);
            float kept = DetailKept * (1f - mask) * detail;
            float shift = Bilinear(Worn, c, tx, tz) - detail + kept;
            int near = c + (tx >= 0.5f ? 1 : 0) + (tz >= 0.5f ? Nx : 0);
            if (!NearRiver[near]) return shift;
            float ground = Bilinear(Top, c, tx, tz) + kept;
            float channel = Channel(x, z, out _, ground);
            if (channel == float.MaxValue) return shift;
            return shift + MathF.Min(0f, channel - ground);
        }

        /// <summary>The bed of a channel or its bank at (x, z) (world Y), or float.MaxValue where there is none; and the
        /// channel's width there (blocks), 0 on a bank. Given the <paramref name="ground"/> there, a bank rises steeper
        /// where the ground stands high over the channel, so it meets the ground within its reach rather than leaving a
        /// wall at the end.</summary>
        public float Channel(float x, float z, out float width, float ground = float.NaN)
        {
            width = 0f;
            // (The line bent a little by noise, so a channel doesn't run dead straight between samples.)
            float qx = x + Bend * _bendX.GetNoise(x, z), qz = z + Bend * _bendZ.GetNoise(x, z);
            int ci = (int)MathF.Floor((qx - X0) / Cell), ck = (int)MathF.Floor((qz - Z0) / Cell);
            float best = float.MaxValue;
            for (int k = Math.Max(0, ck - Search); k <= Math.Min(Nz - 1, ck + Search); k++)
            for (int i = Math.Max(0, ci - Search); i <= Math.Min(Nx - 1, ci + Search); i++)
            {
                int c = i + Nx * k;
                if (float.IsNaN(Bed[c])) continue;
                float ax = RiverX[c], az = RiverZ[c], bx = ax, bz = az;
                int d = Down[c];
                float bedTo = Bed[c], halfTo = Half[c];
                if (d >= 0) { bx = RiverX[d]; bz = RiverZ[d]; bedTo = Bed[d]; halfTo = Half[d]; }
                float dist = SegmentDistance(qx, qz, ax, az, bx, bz, out float t);
                float half = Half[c] + t * (halfTo - Half[c]);
                float past = MathF.Max(0f, dist - half);
                if (past > BankReach) continue;
                // (Deepest down its middle line, rising across it to EdgeRise times its half-width at its edges, where
                // the banks start: a rounded bed, not a flat one.)
                float across = MathF.Min(dist / MathF.Max(half, 1f), 1f);
                float foot = Bed[c] + t * (bedTo - Bed[c]) + EdgeRise * half * across * across;
                float bed = foot + past * (float.IsNaN(ground) ? BankGrade : MathF.Max(BankGrade, (ground - foot) / BankReach));
                if (bed < best) { best = bed; width = past > 0f ? 0f : 2f * half; }
            }
            return best;
        }

        private float Bilinear(float[] v, int c, float tx, float tz)
        {
            float a = v[c] + (v[c + 1] - v[c]) * tx, b = v[c + Nx] + (v[c + Nx + 1] - v[c + Nx]) * tx;
            return a + (b - a) * tz;
        }
    }

    // How far (blocks) noise bends a channel off the line between its samples, and how many samples round a column to
    // look for one: as far as the widest channel and its banks reach, and the bend.
    private const float Bend = 6f;
    private const int Search = (int)((4 * Cell + BankReach + Bend) / Cell) + 2;

    private static FastNoiseLite BendNoise(ulong seed)
    {
        var n = new FastNoiseLite(unchecked((int)seed));
        n.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        n.SetFractalType(FastNoiseLite.FractalType.FBm);
        n.SetFractalOctaves(2);
        n.SetFrequency(0.02f);
        return n;
    }

    private static float SegmentDistance(float px, float pz, float ax, float az, float bx, float bz, out float t)
    {
        float vx = bx - ax, vz = bz - az, len2 = vx * vx + vz * vz;
        t = len2 > 0f ? Math.Clamp(((px - ax) * vx + (pz - az) * vz) / len2, 0f, 1f) : 0f;
        float dx = px - (ax + t * vx), dz = pz - (az + t * vz);
        return MathF.Sqrt(dx * dx + dz * dz);
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
        var field = new Field(_seed, x0, z0, nx, nz);

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
        // settles it over the land as worn so far (every FloodEvery rounds): each sample moves towards the one its water
        // goes on to, the faster the more runs through it (stream power); where much runs over gentle ground, its floor
        // is scoured down, even under where it spills, so basins open behind steeper rims.
        var b = new double[n];
        for (int c = 0; c < n; c++) if (has[c]) b[c] = h0[c] - detail[c];
        var h = (double[])b.Clone();
        Hydrology.Field settled = null!;
        for (int round = 0; round < Rounds; round++)
        {
            if (round % FloodEvery == 0) settled = Settle(c => h[c] + DetailKept * detail[c]);
            var recv = settled.Down;
            var area = settled.Flow;
            // Downstream first, implicitly (so any Wear is stable). An edge drains off its piece: where a stream
            // leaves, it wears towards a notch.
            foreach (int c in settled.Order)
            {
                int to = recv[c];
                double target, dist = 1;
                if (to < 0)
                {
                    double notch = NotchDepth * Math.Clamp(area[c] / (NotchFrom * RiverFrom), 0, 1);
                    target = Math.Max(b[c] - notch, under[c] + KeepUnder);
                }
                else
                {
                    target = h[to];
                    if (c % nx != to % nx && c / nx != to / nx) dist = 1.414;
                }
                if (target >= h[c]) continue; // in a hollow
                double f = Wear * Math.Sqrt(area[c]) / dist;
                h[c] = Math.Max((h[c] + f * target) / (1 + f), Math.Min(h[c], under[c] + KeepUnder));
            }
            // Scour: along the streams where they run gentle, and over a lake's whole bed by what runs out of it (its
            // water isn't in a line through it), the more the deeper, so the bowl deepens without cutting its shores;
            // each cut spread across ScourAcross samples, so a stream wears a trough, not a notch.
            var cut = new double[n];
            var outflow = new Dictionary<float, double>();
            foreach (int c in settled.Order)
            {
                int to = recv[c];
                float lake = settled.Lake[c];
                if (!float.IsNaN(lake) && (to < 0 || settled.Lake[to] != lake))
                    outflow[lake] = Math.Max(outflow.GetValueOrDefault(lake), area[c]);
            }
            foreach (int c in settled.Order)
            {
                int to = recv[c];
                float lake = settled.Lake[c];
                if (!float.IsNaN(lake))
                {
                    double runs = outflow.GetValueOrDefault(lake);
                    if (runs >= ScourFrom * RiverFrom)
                        cut[c] = Scour * Math.Min(Math.Sqrt(runs / (ScourFrom * RiverFrom)), 2) * Math.Clamp((lake - h[c]) / LakeScourDepth, 0, 1);
                    continue;
                }
                if (to < 0 || area[c] < ScourFrom * RiverFrom) continue;
                double slope = (h[c] - h[to]) / Cell;
                if (slope >= ScourSlope) continue;
                cut[c] = Scour * Math.Min(1 - Math.Max(slope, 0) / ScourSlope, 1) * Math.Min(Math.Sqrt(area[c] / (ScourFrom * RiverFrom)), 2);
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

        // The rivers' channels, where the water runs over the land as worn.
        var mask = new double[n];
        Rivers(h, mask, Settle(c => h[c] + DetailKept * detail[c]));

        // What's kept: how far each top moved less its detail, its top worn, and the channels; spread out over the
        // samples off the land from the nearest on it.
        var worn = field.Worn;
        var top = field.Top;
        var m = field.Mask;
        var queue = new Queue<int>();
        var done = new bool[n];
        for (int c = 0; c < n; c++)
        {
            if (!has[c]) continue;
            worn[c] = (float)(h[c] - b[c]);
            top[c] = (float)h[c];
            m[c] = (float)mask[c];
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
                worn[e] = worn[c]; top[e] = top[c]; m[e] = m[c];
                queue.Enqueue(e);
            }
        }
        // No steeper a cut than MaxCutSlope blocks down per block across, so where the land is worn far down it falls
        // away in slopes, not walls: a sample is raised to within that of its neighbours' (two sweeps each way spread
        // it across the field).
        for (int pass = 0; pass < 2; pass++)
        for (int dir = 0; dir < 2; dir++)
        for (int q = 0; q < n; q++)
        {
            int c = dir == 0 ? q : n - 1 - q, i = c % nx, k = c / nx, step = dir == 0 ? -1 : 1;
            float least = worn[c];
            for (int di = -1; di <= 1; di++)
            {
                int ni = i + di, nk = k + step;
                if (ni >= 0 && ni < nx && nk >= 0 && nk < nz)
                    least = MathF.Max(least, worn[ni + nx * nk] - MaxCutSlope * Cell * (di == 0 ? 1f : 1.414f));
            }
            if (i + step >= 0 && i + step < nx) least = MathF.Max(least, worn[c + step] - MaxCutSlope * Cell);
            float raised = least - worn[c];
            worn[c] = least; top[c] += raised;
        }
        // Which samples a channel's search can find one from.
        for (int c = 0; c < n; c++)
        {
            if (float.IsNaN(field.Bed[c])) continue;
            int i = c % nx, k = c / nx;
            for (int dk = -Search; dk <= Search; dk++)
            for (int di = -Search; di <= Search; di++)
                if (i + di >= 0 && k + dk >= 0 && i + di < nx && k + dk < nz) field.NearRiver[i + di + nx * (k + dk)] = true;
        }

        // And the water once more, over the land as the generator makes it now (its tops moved, as at each sample's
        // middle, channels and all), for Water to lay out.
        field.Water = Settle(c =>
        {
            float x = x0 + c % nx * Cell + Cell / 2, z = z0 + c / nx * Cell + Cell / 2;
            return h0[c] + field.Shift(x, z, (float)detail[c]);
        });
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

        // Channels cut into the land where the rivers run: their beds never rising downstream, wider and deeper the more
        // runs through them, with banks sloping up into the land. Marks where it cut (1 in channels, fading over the
        // banks).
        void Rivers(double[] height, double[] carved, Hydrology.Field rivers)
        {
            var recv = rivers.Down;
            var area = rivers.Flow;
            var order = rivers.Order;
            var slope = new double[n];
            for (int c = 0; c < n; c++)
            {
                if (!has[c]) continue;
                int i = c % nx, k = c / nx;
                double drop = 0;
                for (int dk = -1; dk <= 1; dk++)
                for (int di = -1; di <= 1; di++)
                {
                    if ((di | dk) == 0 || !Joined(c, i + di, k + dk)) continue;
                    drop = Math.Max(drop, (height[c] - height[i + di + nx * (k + dk)]) / (Cell * Math.Sqrt(di * di + dk * dk)));
                }
                slope[c] = drop;
            }
            var bed = new double[n];
            Array.Fill(bed, double.NaN);
            int count = 0;
            foreach (int c in order)
            {
                // (None in a lake: its water stands over its bed. Too small to carve across flat ground, a stream runs
                // over it, its bed the ground: so the channel above doesn't end in a pit, nor do beds upstream dig
                // under it.)
                if (area[c] < RiverFrom || !float.IsNaN(rivers.Lake[c])) continue;
                double target = slope[c] < FlatSlope && area[c] < FlatFactor * RiverFrom ? height[c]
                    : Math.Max(height[c] - Math.Min(RiverDepth, 3 * Math.Sqrt(area[c] / RiverFrom)), under[c] + KeepUnder);
                int to = recv[c];
                bed[c] = to >= 0 && !double.IsNaN(bed[to]) ? Math.Max(bed[to], target) : target;
                count++;
            }
            // The channels themselves are cut per block (see Field.Channel): here, only where they are, so less of the
            // detail comes back there.
            foreach (int c in order)
            {
                if (double.IsNaN(bed[c])) continue;
                double width = Math.Clamp(0.6 * Math.Sqrt(area[c] / RiverFrom), 0.5, 4); // samples either side
                field.Bed[c] = (float)bed[c];
                field.Half[c] = (float)(width * Cell);
                if (recv[c] >= 0 && !double.IsNaN(bed[recv[c]])) field.Down[c] = recv[c];
                int reach = (int)Math.Ceiling(width + BankReach / Cell), ci = c % nx, ck = c / nx;
                for (int dk = -reach; dk <= reach; dk++)
                for (int di = -reach; di <= reach; di++)
                {
                    if (!Joined(c, ci + di, ck + dk)) continue;
                    double past = Math.Max(0, Math.Sqrt(di * di + dk * dk) - width) * Cell;
                    if (past > BankReach) continue;
                    int e = ci + di + nx * (ck + dk);
                    carved[e] = Math.Max(carved[e], 1 - past / BankReach);
                }
            }
            // The rivers' lines: each sample's middle, eased a few times towards the middle of its neighbours up and
            // down the river (up it, the one most runs from): the flood's ways go only straight across, so they'd
            // go in steps.
            var up = new int[n];
            Array.Fill(up, -1);
            foreach (int c in order)
            {
                int d = field.Down[c];
                if (d >= 0 && (up[d] < 0 || area[c] > area[up[d]])) up[d] = c;
                field.RiverX[c] = x0 + (c % nx + 0.5f) * Cell;
                field.RiverZ[c] = z0 + (c / nx + 0.5f) * Cell;
            }
            var easedX = new float[n];
            var easedZ = new float[n];
            for (int pass = 0; pass < RiverEasing; pass++)
            {
                foreach (int c in order)
                {
                    int d = field.Down[c], u = up[c];
                    easedX[c] = field.RiverX[c]; easedZ[c] = field.RiverZ[c];
                    if (float.IsNaN(field.Bed[c]) || d < 0 || u < 0) continue;
                    easedX[c] = 0.5f * field.RiverX[c] + 0.25f * (field.RiverX[d] + field.RiverX[u]);
                    easedZ[c] = 0.5f * field.RiverZ[c] + 0.25f * (field.RiverZ[d] + field.RiverZ[u]);
                }
                foreach (int c in order) { field.RiverX[c] = easedX[c]; field.RiverZ[c] = easedZ[c]; }
            }
            field.RiverSamples = count;
        }
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
