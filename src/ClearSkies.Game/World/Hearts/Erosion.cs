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
/// water goes, and each moves towards the one it drains to, the faster the more land drains through it (stream power).
/// Each piece drains to its own edges: its rivers notch down into its rim where they leave it. Then the bigger rivers
/// cut channels with sloping banks, winding where the land is flat, and here and there a sill across a valley dams a
/// lake. Some of the detail is put back, none in the channels.
///
/// What's kept is how far each sample's top moved, less its detail; HeartWorldGenerator moves its pieces' tops by that
/// (see <see cref="Field.Shift"/>), with the detail worked out per block, so only the valleys come from the samples.
/// </summary>
public sealed class Erosion
{
    /// <summary>Blocks on a side of a sample.</summary>
    public const int Cell = 8;

    private const int Rounds = 30;

    /// <summary>The steepest the wearing may cut into the land, blocks down per block across (see Build).</summary>
    private const float MaxCutSlope = 1f;

    /// <summary>How fast land wears towards where it drains, per square root of the samples draining through it.</summary>
    private const double Wear = 0.02;

    /// <summary>How much of the land's detail is put back, outside the channels.</summary>
    private const float DetailKept = 0.3f;

    // A piece's edge stays where it is, except where a stream leaves it: there it wears down towards a notch up to
    // NotchDepth deep, the deepest where NotchFrom times RiverFrom samples drain out. All the land keeps at least
    // KeepUnder of its ground under it, so no valley cuts through its piece.
    private const double NotchDepth = 16, NotchFrom = 1, KeepUnder = 48;

    /// <summary>How far under its piece's top (roughly) the land may be and still be its ground, not a hole (as Water).</summary>
    private const float HoleDepth = 40f;

    // Rivers: a channel is cut where RiverFrom samples drain through (FlatFactor times that where the land is flatter
    // than FlatSlope), up to RiverDepth blocks deep; routed over the land raised by up to Meander blocks of noise where it
    // is flat, so they wind. Banks rise BankGrade blocks per block for BankReach blocks.
    private const double RiverFrom = 200, FlatFactor = 5, FlatSlope = 0.01, RiverDepth = 10, Meander = 40;
    private const float BankReach = 24f, BankGrade = 0.15f;

    /// <summary>How many times a river's line is eased towards its neighbours' (see Field).</summary>
    private const int RiverEasing = 4;

    // Lakes: where a river of LakeFrom times RiverFrom runs down a valley (slope LakeSlopeMin to LakeSlopeMax), one in
    // LakeChance gets a sill across it two samples on, SillThickness samples thick and LakeDepth over the bed,
    // LakeSpacing blocks from any other.
    private const double SillThickness = 1.5;
    private const double LakeFrom = 4, LakeSlopeMin = 0.01, LakeSlopeMax = 0.1, LakeChance = 0.15, LakeDepth = 8, LakeSpacing = 160;

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
    /// detail (Worn), its top worn (Top, with the detail kept), and how much it is channel (Mask, 1 in a channel or lake,
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

        /// <summary>How long it took to work out, seconds; and how many river samples and lakes it has.</summary>
        public double Seconds { get; internal set; }
        public int RiverSamples { get; internal set; }
        public int Lakes { get; internal set; }

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
                float foot = Bed[c] + t * (bedTo - Bed[c]);
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
        var piece = new long[n];     // which top-row heart's piece it is
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
                piece[c] = ((long)hi << 32) ^ (uint)hk;
            }
        });
        samplers.Dispose();

        // The broad shape: the tops less their detail. Worn down from there.
        var b = new double[n];
        for (int c = 0; c < n; c++) if (has[c]) b[c] = h0[c] - detail[c];
        var h = (double[])b.Clone();
        var recv = new int[n];
        var area = new double[n];
        var level = new double[n];
        var edge = new bool[n];
        var order = new List<int>(n);
        for (int c = 0; c < n; c++)
        {
            if (!has[c]) continue;
            int i = c % nx, k = c / nx;
            for (int dk = -1; dk <= 1 && !edge[c]; dk++)
            for (int di = -1; di <= 1 && !edge[c]; di++)
                edge[c] = !Joined(c, i + di, k + dk);
        }

        for (int round = 0; round < Rounds; round++)
        {
            Route(h);
            // Downstream first: each sample moves towards where it drains (implicitly, so any Wear is stable). An edge
            // drains off its piece: where a stream leaves, it wears towards a notch.
            foreach (int c in order)
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
        }

        var mask = new double[n];
        Rivers(h, mask);

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
        field.Seconds = clock.Elapsed.TotalSeconds;
        return field;

        // Whether sample c and sample (i, k) are on the same piece's land, so water goes between them.
        bool Joined(int c, int i, int k)
        {
            if (i < 0 || k < 0 || i >= nx || k >= nz) return false;
            int e = i + nx * k;
            return has[e] && piece[e] == piece[c];
        }

        // Where each sample drains (a priority flood in from its piece's edges, so hollows drain by their spill points),
        // in order, downstream first, and the samples draining through each.
        void Route(double[] height)
        {
            Array.Fill(recv, -1); Array.Fill(level, double.NaN); order.Clear();
            var flood = new PriorityQueue<int, double>();
            for (int c = 0; c < n; c++)
                if (edge[c]) { level[c] = height[c]; flood.Enqueue(c, height[c]); }
            while (flood.TryDequeue(out int c, out double at))
            {
                order.Add(c);
                int i = c % nx, k = c / nx;
                for (int dk = -1; dk <= 1; dk++)
                for (int di = -1; di <= 1; di++)
                {
                    if ((di | dk) == 0 || !Joined(c, i + di, k + dk)) continue;
                    int d = i + di + nx * (k + dk);
                    if (!double.IsNaN(level[d])) continue;
                    level[d] = Math.Max(at, height[d]);
                    recv[d] = c;
                    flood.Enqueue(d, level[d]);
                }
            }
            Array.Clear(area);
            foreach (int c in order) area[c] = 1;
            for (int q = order.Count - 1; q >= 0; q--) if (recv[order[q]] >= 0) area[recv[order[q]]] += area[order[q]];
        }

        // Trunk rivers cut into the land: routed with noise on flat ground (so they wind), their beds never rising
        // downstream, channels wider and deeper the more drains through them with banks sloping up into the land, and
        // now and then a lake dammed where one runs down a valley. Marks where it cut (1 in channels, fading over the
        // banks).
        void Rivers(double[] height, double[] carved)
        {
            var noise = new FastNoiseLite(unchecked((int)_seed + 77));
            noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
            noise.SetFractalType(FastNoiseLite.FractalType.FBm);
            noise.SetFractalOctaves(2);
            noise.SetFrequency(1f / 300f);
            var slope = new double[n];
            var prio = new double[n];
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
                double flat = Math.Clamp(1 - drop / 0.05, 0, 1);
                prio[c] = height[c] + Meander * Math.Abs(noise.GetNoise(x0 + i * Cell, z0 + k * Cell)) * flat;
            }
            Route(prio);
            var bed = new double[n];
            Array.Fill(bed, double.NaN);
            int rivers = 0;
            foreach (int c in order)
            {
                if (area[c] < RiverFrom) continue;
                // (Too small to carve across flat ground, a stream runs over it, its bed the ground: so the channel above
                // doesn't end in a pit, nor do beds upstream dig under it.)
                double target = slope[c] < FlatSlope && area[c] < FlatFactor * RiverFrom ? height[c]
                    : Math.Max(height[c] - Math.Min(RiverDepth, 3 * Math.Sqrt(area[c] / RiverFrom)), under[c] + KeepUnder);
                int to = recv[c];
                bed[c] = to >= 0 && !double.IsNaN(bed[to]) ? Math.Max(bed[to], target) : target;
                rivers++;
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
            // down the river (up it, the one most drains from).
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
            var cut = (double[])height.Clone();
            // Lakes: now and then, where a bigger river runs down a valley, a sill across it a little way downstream
            // (a rockfall, a moraine) raised LakeDepth over the bed, so the river backs up behind it and fills the
            // valley to that level, its shore wherever the land meets it.
            var lakes = new List<int>();
            foreach (int c in order)
            {
                if (double.IsNaN(bed[c]) || area[c] < LakeFrom * RiverFrom || slope[c] < LakeSlopeMin || slope[c] > LakeSlopeMax) continue;
                uint hash = (uint)((uint)(c + x0 * 31 + z0 * 17) * 2654435761u ^ (uint)_seed);
                if ((hash >> 8) / (double)(1 << 24) >= LakeChance) continue;
                int sill = recv[c] >= 0 ? recv[recv[c]] : -1;
                if (sill < 0 || double.IsNaN(bed[sill])) continue;
                int si = sill % nx, sk = sill / nx;
                if (lakes.Any(l => Math.Abs(l % nx - si) + Math.Abs(l / nx - sk) < LakeSpacing / Cell)) continue;
                double lakeLevel = bed[c] + LakeDepth;
                int reach = (int)Math.Ceiling(Math.Clamp(0.6 * Math.Sqrt(area[sill] / RiverFrom), 0.5, 4) + 3);
                // (A band across the valley, SillThickness samples thick along the way the river runs, not a mound.)
                double ax = si - c % nx, az = sk - c / nx, len = Math.Sqrt(ax * ax + az * az);
                ax /= len; az /= len;
                // (Only where the valley's sides stand over the lake at both ends of the sill, or it would be a ridge
                // on open ground, damming nothing.)
                if (!Walled(-1) || !Walled(1)) continue;
                bool Walled(int side)
                {
                    int ei = si + (int)Math.Round(-az * side * reach), ek = sk + (int)Math.Round(ax * side * reach);
                    return !Joined(sill, ei, ek) || cut[ei + nx * ek] >= lakeLevel;
                }
                lakes.Add(sill);
                for (int dk = -reach; dk <= reach; dk++)
                for (int di = -reach; di <= reach; di++)
                {
                    if (di * di + dk * dk > reach * reach || Math.Abs(di * ax + dk * az) > SillThickness / 2
                        || !Joined(sill, si + di, sk + dk)) continue;
                    int e = si + di + nx * (sk + dk);
                    if (cut[e] < lakeLevel) { cut[e] = lakeLevel; carved[e] = 1; }
                    // (Nor does a channel cut through it: its bed is the sill.)
                    if (!float.IsNaN(field.Bed[e])) field.Bed[e] = MathF.Max(field.Bed[e], (float)lakeLevel);
                }
            }
            Array.Copy(cut, height, n);
            field.RiverSamples = rivers;
            field.Lakes = lakes.Count;
        }
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
