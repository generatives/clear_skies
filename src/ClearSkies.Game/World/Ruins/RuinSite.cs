using System.Diagnostics;
using ClearSkies.Game.Generation;

namespace ClearSkies.Game.Ruins;

/// <summary>What a ruin is asked for: how much floor it needs, and the shape it grows in.</summary>
public sealed class RuinSettings
{
    /// <summary>Stand-in for the population stages: the ruin needs Population × AreaPerPerson blocks² of floor.</summary>
    public int Population = 150_000;
    public float AreaPerPerson = 40f;
    /// <summary>Deepest it goes, in floors below the top floor.</summary>
    public int MaxFloors = 40;
    /// <summary>Width over height: going down a block costs Aspect / 2 blocks across, so it grows to about Aspect
    /// times as wide as it is deep.</summary>
    public float Aspect = 2f;
    /// <summary>Each cell of gap crossed costs this many times a cell of ground, so it stays on its piece until going
    /// on is dearer than bridging.</summary>
    public float GapCost = 8f;
    /// <summary>Another piece gets its own entrance (and grows from it) once this much of its ground (blocks²) is in.</summary>
    public int PieceEntranceArea = 40_000;
    /// <summary>How much the cost of a step varies (0-1), in blobs of RuinSite.RoughScale cells: a ragged edge.</summary>
    public float Roughness = 0.7f;
}

/// <summary>
/// Where a ruin can go on one island of a landmass (see <see cref="LandmassSample"/>), on a grid of
/// <see cref="Cell"/>-block cells and floors <see cref="FloorPitch"/> blocks apart. It grows out from its entrance (the
/// island's most interior point thick enough; the top floor sits <see cref="Cover"/> under the ground there) one cell
/// or floor at a time, cheapest first: a step across costs its length, a step down costs
/// <see cref="RuinSettings.Aspect"/> / 2 times its height (through rock solid on both floors), a step across a gap
/// costs <see cref="RuinSettings.GapCost"/> times more (and gaps over <see cref="BridgeCells"/> aren't crossed). It
/// stops once it holds the floor it needs. A piece it bridges onto gets its own entrance once enough of it is in. For
/// each floor, what the ground is like in each cell (see <see cref="Ground"/>); the grid covers the ruin's bounds only.
/// </summary>
public sealed class RuinSite
{
    public const int Cell = 4;
    public const int FloorPitch = 10;
    /// <summary>Rock left over the top floor's ceiling at the entrance.</summary>
    public const int Cover = 12;
    /// <summary>Rock a cell needs under its floor and over its ceiling to hold a room.</summary>
    public const int Margin = 2;
    /// <summary>Widest gap (in cells) a bridge crosses, and so the widest crack the region grows across.</summary>
    public const int BridgeCells = 8;
    /// <summary>A drop (blocks) between neighbouring cells that parts pieces: a crack's edge, with land far below.</summary>
    public const int Cliff = 16;
    /// <summary>Cells across a blob of the growth's rough cost.</summary>
    public const int RoughScale = 24;

    public enum Ground : byte
    {
        /// <summary>Not in the region.</summary>
        Out,
        /// <summary>In the region, but no rock at this floor's height: a gap a hall must bridge.</summary>
        Open,
        /// <summary>Some rock at this floor's height, not enough to hold a room: a hall tunnels through.</summary>
        Partial,
        /// <summary>Rock all through the floor, with <see cref="Margin"/> to spare: rooms can go here.</summary>
        Solid,
    }

    public readonly ulong Seed;
    public readonly (int X, int Z) Landmass;
    /// <summary>The grid: cell (i, j) covers blocks X0 + i Cell .. + Cell - 1 (and Z likewise).</summary>
    public readonly int X0, Z0, Nx, Nz;
    public readonly int TopY, Floors;
    /// <summary>Per floor, per cell (i + Nx j).</summary>
    public readonly Ground[][] Grounds;
    /// <summary>Per cell: the top of its highest ground (short.MinValue for none).</summary>
    public readonly short[] Surface;
    /// <summary>Per cell: which piece of land it's on (-1 for none): land joined 4-way at the surface where it doesn't drop
    /// more than <see cref="Cliff"/>, so cracks part it.</summary>
    public readonly int[] PieceOf;
    /// <summary>The entrances, the main one first: cell, and the floor its stairs come down to.</summary>
    public readonly List<(int Cell, int Floor)> Entrances = [];
    /// <summary>The main entrance's cell.</summary>
    public int Centre => Entrances[0].Cell;
    public readonly int IslandLandCells;
    public readonly int RegionCells, RegionLandCells;
    public readonly long TargetArea;
    public readonly TimeSpan RegionTime;
    /// <summary>What the site was picked from: the landmass, which island of it, and (over the whole landmass, for
    /// maps) which cells are in the region.</summary>
    public readonly LandmassSample Sample;
    public readonly int Island;
    public readonly bool[] LandmassRegion;
    /// <summary>Per cell: in some floor's region.</summary>
    public readonly bool[] Region;

    public int FloorY(int floor) => TopY - floor * FloorPitch;
    public Ground At(int floor, int i, int j) =>
        i < 0 || j < 0 || i >= Nx || j >= Nz ? Ground.Out : Grounds[floor][i + Nx * j];

    private RuinSite(LandmassSample sample, int island, RuinSettings settings)
    {
        Seed = sample.Seed; Landmass = sample.Landmass; Sample = sample; Island = island;
        var sw = Stopwatch.StartNew();
        int fnx = sample.Nx, fnz = sample.Nz, fn = fnx * fnz;
        IslandLandCells = sample.IslandLand[island];

        // What the ground is like in landmass cell c for a floor at height y.
        Ground GroundAt(int c, int y)
        {
            var g = Ground.Open;
            for (int s = 0; s < 2; s++)
            {
                short lo = s == 0 ? sample.TopLo[c] : sample.LowLo[c], hi = s == 0 ? sample.TopHi[c] : sample.LowHi[c];
                if (hi == short.MinValue) continue;
                if (lo <= y - Margin && hi >= y + FloorPitch - 1 + Margin) return Ground.Solid;
                if (lo <= y + FloorPitch - 1 && hi >= y) g = Ground.Partial;
            }
            return g;
        }
        bool Thick(int c) => sample.Land[c] && sample.TopHi[c] - sample.TopLo[c] >= Cover + FloorPitch + 2 * Margin;

        // Pieces: the island's land, joined 4-way where the ground doesn't drop more than Cliff between cells (a crack with
        // land far below it parts pieces too). How far each cell is from such an edge, or the sky.
        bool Joined(int a, int b) => sample.Land[a] && sample.Land[b] && Math.Abs(sample.TopHi[a] - sample.TopHi[b]) <= Cliff;
        var inner = LandmassSample.Distances(fnx, fnz, c =>
        {
            if (!sample.Land[c]) return true;
            int i = c % fnx, j = c / fnx;
            return i == 0 || j == 0 || i == fnx - 1 || j == fnz - 1
                || !Joined(c, c - 1) || !Joined(c, c + 1) || !Joined(c, c - fnx) || !Joined(c, c + fnx);
        });
        var pieceOf = new int[fn]; Array.Fill(pieceOf, -1);
        var pieceDeep = new List<int>();
        {
            var q = new Queue<int>();
            for (int s = 0; s < fn; s++)
            {
                if (!sample.Land[s] || sample.IslandOf[s] != island || pieceOf[s] >= 0) continue;
                int id = pieceDeep.Count, deep = -1;
                pieceOf[s] = id; q.Enqueue(s);
                while (q.TryDequeue(out int c))
                {
                    if (Thick(c) && (deep < 0 || inner[c] > inner[deep])) deep = c;
                    int i = c % fnx, j = c / fnx;
                    void Visit(int e) { if (pieceOf[e] < 0 && Joined(c, e)) { pieceOf[e] = id; q.Enqueue(e); } }
                    if (i > 0) Visit(c - 1);
                    if (i < fnx - 1) Visit(c + 1);
                    if (j > 0) Visit(c - fnx);
                    if (j < fnz - 1) Visit(c + fnx);
                }
                pieceDeep.Add(deep);
            }
        }

        // The main entrance: the island's most interior cell (farthest from sky and cliffs) whose ground is thick enough
        // to hold the top floor under Cover.
        int entrance = -1;
        for (int c = 0; c < fn; c++)
            if (sample.IslandOf[c] == island && Thick(c) && (entrance < 0 || inner[c] > inner[entrance])) entrance = c;
        if (entrance < 0) entrance = sample.IslandCentre[island];
        TopY = Math.Max(sample.TopHi[entrance] - Cover - FloorPitch + 1, sample.TopLo[entrance] + Margin);
        int maxFloors = Math.Clamp(settings.MaxFloors, 1, 255);
        int YOf(int f) => TopY - f * FloorPitch;

        // Growth, cheapest first, over (floor, cell) nodes. Each node's best cost so far and the gap cells run into it.
        const float RoomShare = 0.45f;
        TargetArea = (long)(settings.Population * settings.AreaPerPerson);
        float down = FloorPitch * settings.Aspect / 2, diag = MathF.Sqrt(2);
        // Smooth noise on the cost of each step, so the edge wanders instead of tracing an octagon.
        float Rough(int c, int f)
        {
            float x = (c % fnx) / (float)RoughScale, z = (c / fnx) / (float)RoughScale, w = f / 3f;
            int x0 = (int)MathF.Floor(x), z0 = (int)MathF.Floor(z), w0 = (int)MathF.Floor(w);
            float fx = x - x0, fz = z - z0, fw = w - w0;
            fx = fx * fx * (3 - 2 * fx); fz = fz * fz * (3 - 2 * fz); fw = fw * fw * (3 - 2 * fw);
            float H(int a, int b, int d)
            {
                ulong h = Seed ^ (ulong)(uint)a * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)b * 0xC2B2AE3D27D4EB4FUL ^ (ulong)(uint)d * 0x165667B19E3779F9UL;
                h ^= h >> 31; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 29;
                return (h >> 40) / (float)(1 << 24);
            }
            float L(float a, float b, float t) => a + (b - a) * t;
            float v = L(L(L(H(x0, z0, w0), H(x0 + 1, z0, w0), fx), L(H(x0, z0 + 1, w0), H(x0 + 1, z0 + 1, w0), fx), fz),
                        L(L(H(x0, z0, w0 + 1), H(x0 + 1, z0, w0 + 1), fx), L(H(x0, z0 + 1, w0 + 1), H(x0 + 1, z0 + 1, w0 + 1), fx), fz), fw);
            return 1 + settings.Roughness * (2 * v - 1);
        }
        var best = new Dictionary<long, (float Cost, byte Run)>();
        var done = new HashSet<long>();
        var queue = new PriorityQueue<long, float>();
        long Key(int f, int c) => (long)f * fn + c;
        void Offer(int f, int c, float cost, int run)
        {
            long k = Key(f, c);
            if (done.Contains(k) || (best.TryGetValue(k, out var b) && b.Cost <= cost)) return;
            best[k] = (cost, (byte)run);
            queue.Enqueue(k, cost);
        }
        var entranceOf = new Dictionary<int, int> { [pieceOf[entrance]] = 0 };
        Entrances.Add((entrance, 0));
        var pieceSolid = new Dictionary<int, int>();
        int pieceEntranceCells = Math.Max(1, settings.PieceEntranceArea / (Cell * Cell));
        if (GroundAt(entrance, TopY) == Ground.Solid) Offer(0, entrance, 0, 0);
        double area = 0;
        int deepest = 0;
        while (area < TargetArea && queue.TryDequeue(out long k, out float d))
        {
            if (!done.Add(k)) continue;
            int f = (int)(k / fn), c = (int)(k % fn), run = best[k].Run;
            var g = GroundAt(c, YOf(f));
            deepest = Math.Max(deepest, f);
            if (g == Ground.Solid)
            {
                area += Cell * Cell * RoomShare;
                int p = pieceOf[c];
                if (p >= 0 && !entranceOf.ContainsKey(p) && (pieceSolid[p] = pieceSolid.GetValueOrDefault(p) + 1) >= pieceEntranceCells
                    && pieceDeep[p] >= 0)
                {
                    // Enough of this piece is in: it gets its own entrance, its top floor Cover under the ground there,
                    // and grows from it from now on.
                    int e = pieceDeep[p];
                    int ef = Math.Max(0, (int)Math.Ceiling((TopY - (sample.TopHi[e] - Cover - FloorPitch + 1)) / (double)FloorPitch));
                    while (ef < maxFloors && GroundAt(e, YOf(ef)) != Ground.Solid) ef++;
                    entranceOf[p] = Entrances.Count;
                    if (ef < maxFloors) { Entrances.Add((e, ef)); Offer(ef, e, d, 0); }
                }
                // Down and up a floor, through rock on both.
                if (f + 1 < maxFloors && GroundAt(c, YOf(f + 1)) == Ground.Solid) Offer(f + 1, c, d + down, 0);
                if (f > 0 && GroundAt(c, YOf(f - 1)) == Ground.Solid) Offer(f - 1, c, d + down, 0);
            }
            int i = c % fnx, j = c / fnx;
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                int ni = i + di, nj = j + dj;
                if ((di | dj) == 0 || ni < 0 || nj < 0 || ni >= fnx || nj >= fnz) continue;
                int e = ni + fnx * nj;
                if (sample.IslandOf[e] != island) continue;
                bool open = GroundAt(e, YOf(f)) == Ground.Open;
                int r = open ? run + 1 : 0;
                if (r > BridgeCells) continue;
                float step = Cell * (di != 0 && dj != 0 ? diag : 1) * (open ? settings.GapCost : 1) * Rough(e, f);
                Offer(f, e, d + step, r);
            }
        }
        Floors = deepest + 1;
        // (Gap cells it reached without getting across stay in: halls clip them away.)
        var regions = new bool[Floors][];
        for (int f = 0; f < Floors; f++) regions[f] = new bool[fn];
        foreach (long k in done) regions[k / fn][k % fn] = true;
        var union = new bool[fn];
        foreach (var r in regions) for (int c = 0; c < fn; c++) union[c] |= r[c];
        LandmassRegion = union;
        int ri0 = int.MaxValue, rj0 = int.MaxValue, ri1 = int.MinValue, rj1 = int.MinValue;
        for (int c = 0; c < fn; c++)
        {
            if (!union[c]) continue;
            RegionCells++;
            if (sample.Land[c]) RegionLandCells++;
            int i = c % fnx, j = c / fnx;
            ri0 = Math.Min(ri0, i); rj0 = Math.Min(rj0, j); ri1 = Math.Max(ri1, i); rj1 = Math.Max(rj1, j);
        }
        ri0 = Math.Max(0, ri0 - 1); rj0 = Math.Max(0, rj0 - 1); ri1 = Math.Min(fnx - 1, ri1 + 1); rj1 = Math.Min(fnz - 1, rj1 + 1);
        Nx = ri1 - ri0 + 1; Nz = rj1 - rj0 + 1;
        X0 = sample.X0 + ri0 * Cell; Z0 = sample.Z0 + rj0 * Cell;
        int n = Nx * Nz;
        Surface = new short[n];
        Region = new bool[n];
        Grounds = new Ground[Floors][];
        for (int f = 0; f < Floors; f++) Grounds[f] = new Ground[n];
        for (int j = 0; j < Nz; j++)
        for (int i = 0; i < Nx; i++)
        {
            int g = (i + ri0) + fnx * (j + rj0), c = i + Nx * j;
            Surface[c] = sample.TopHi[g];
            Region[c] = union[g];
            for (int f = 0; f < Floors; f++)
                if (regions[f][g]) Grounds[f][c] = GroundAt(g, FloorY(f));
        }
        PieceOf = new int[n];
        for (int j = 0; j < Nz; j++)
        for (int i = 0; i < Nx; i++) PieceOf[i + Nx * j] = pieceOf[(i + ri0) + fnx * (j + rj0)];
        for (int e = 0; e < Entrances.Count; e++)
        {
            var (c, f) = Entrances[e];
            Entrances[e] = ((c % fnx - ri0) + Nx * (c / fnx - rj0), f);
        }
        RegionTime = sw.Elapsed;
    }

    /// <summary>The ruin site on island <paramref name="island"/> of a sampled landmass (0 is the biggest).</summary>
    public static RuinSite Build(LandmassSample sample, int island, RuinSettings settings) => new(sample, island, settings);
}
