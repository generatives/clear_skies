using System.Diagnostics;
using ClearSkies.Game.Generation;

namespace ClearSkies.Game.Ruins;

/// <summary>What a ruin is asked for: how much of its landmass it spreads over, and how much floor it needs.</summary>
public sealed class RuinSettings
{
    /// <summary>Share of its island's land the ruin's region covers (0-1).</summary>
    public float Share = 0.3f;

    /// <summary>Stand-in for the population stages: the ruin needs Population × AreaPerPerson blocks² of floor, and
    /// stacks floors until it has that much (or the ground runs out, or <see cref="MaxFloors"/>).</summary>
    public int Population = 150_000;
    public float AreaPerPerson = 40f;
    public int MaxFloors = 12;
}

/// <summary>
/// Where a ruin can go on one island of a landmass (see <see cref="LandmassSample"/>), on a grid of
/// <see cref="Cell"/>-block cells. It starts from its entrance (see <see cref="Centre"/>): the top floor sits
/// <see cref="Cover"/> under the ground there, and floors go down from it, flat, <see cref="FloorPitch"/> blocks apart.
/// Each floor's region grows out through whatever ground there is at its height (crossing gaps up to
/// <see cref="BridgeCells"/>), from the entrance for the top floor and from the floor above's for the rest, so the ruin
/// follows the rock. For each floor, what the ground is like in each cell (see <see cref="Ground"/>); the grid covers
/// the floors' bounds only.
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
    /// <summary>The main entrance's cell, which the floors grew from: the island's most interior point thick enough.</summary>
    public readonly int Centre;
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

        // The entrance: the island's most interior cell whose ground is thick enough to hold the top floor under Cover.
        int entrance = -1;
        for (int c = 0; c < fn; c++)
        {
            if (sample.IslandOf[c] != island || !sample.Land[c] || sample.TopHi[c] - sample.TopLo[c] < Cover + FloorPitch + 2 * Margin) continue;
            if (entrance < 0 || sample.FromSky[c] > sample.FromSky[entrance]) entrance = c;
        }
        if (entrance < 0) entrance = sample.IslandCentre[island];
        TopY = Math.Max(sample.TopHi[entrance] - Cover - FloorPitch + 1, sample.TopLo[entrance] + Margin);

        // Floors, top down, each grown out from the entrance (the top floor) or from the floor above wherever it has
        // solid ground under it: nearest first, through any ground at its height and across gaps up to BridgeCells, until
        // it holds the asked share of the island's land in solid cells or runs out. They stop once they hold the floor the
        // ruin needs (rooms take roughly RoomShare of solid ground) or one comes out too small.
        const float RoomShare = 0.45f;
        TargetArea = (long)(settings.Population * settings.AreaPerPerson);
        int want = (int)(settings.Share * IslandLandCells), least = Math.Max(500, IslandLandCells / 100);
        int ei = entrance % fnx, ej = entrance / fnx;
        var regions = new List<bool[]>();
        var run = new byte[fn];
        var queue = new PriorityQueue<int, float>();
        double area = 0;
        while (regions.Count < settings.MaxFloors && area < TargetArea)
        {
            int y = TopY - regions.Count * FloorPitch, solid = 0;
            var inFloor = new bool[fn];
            queue.Clear();
            void Seed(int c) { inFloor[c] = true; run[c] = 0; float dx = c % fnx - ei, dz = c / fnx - ej; queue.Enqueue(c, dx * dx + dz * dz); }
            if (regions.Count == 0) { if (GroundAt(entrance, y) == Ground.Solid) Seed(entrance); }
            else
            {
                var above = regions[^1];
                for (int c = 0; c < fn; c++) if (above[c] && GroundAt(c, y) == Ground.Solid) Seed(c);
            }
            while (solid < want && queue.TryDequeue(out int c, out _))
            {
                if (GroundAt(c, y) == Ground.Solid) solid++;
                int i = c % fnx, j = c / fnx;
                for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    int ni = i + di, nj = j + dj;
                    if (ni < 0 || nj < 0 || ni >= fnx || nj >= fnz) continue;
                    int d = ni + fnx * nj;
                    if (inFloor[d] || sample.IslandOf[d] != island) continue;
                    int r = GroundAt(d, y) == Ground.Open ? run[c] + 1 : 0;
                    if (r > BridgeCells) continue;
                    inFloor[d] = true; run[d] = (byte)r;
                    float dx = ni - ei, dz = nj - ej;
                    queue.Enqueue(d, dx * dx + dz * dz);
                }
            }
            // Cells queued but never reached aren't in it.
            while (queue.TryDequeue(out int c, out _)) inFloor[c] = false;
            if (solid < least) break;
            regions.Add(inFloor);
            area += solid * (double)(Cell * Cell) * RoomShare;
        }
        Floors = regions.Count;

        // The region is every floor's together; crop to its bounds (a cell of margin).
        var union = new bool[fn];
        foreach (var r in regions) for (int c = 0; c < fn; c++) union[c] |= r[c];
        union[entrance] = true;
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
        Centre = (ei - ri0) + Nx * (ej - rj0);
        RegionTime = sw.Elapsed;
    }

    /// <summary>The ruin site on island <paramref name="island"/> of a sampled landmass (0 is the biggest).</summary>
    public static RuinSite Build(LandmassSample sample, int island, RuinSettings settings) => new(sample, island, settings);
}
