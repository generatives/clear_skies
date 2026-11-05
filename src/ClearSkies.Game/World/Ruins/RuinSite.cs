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
/// Where a ruin can go on one island of a landmass (see <see cref="LandmassSample"/>), on a grid of <see cref="Cell"/>-block cells: its region (a connected share of the
/// island grown out from its most interior point, crossing cracks between pieces up to <see cref="BridgeCells"/>
/// wide), and for each of its floors, what the ground is like in each cell (see <see cref="Ground"/>).
///
/// Floors are flat, <see cref="FloorPitch"/> blocks apart, going down from <see cref="TopY"/>, which sits
/// <see cref="Cover"/> blocks under most of the region's surface. The grid covers the region's bounds only.
/// </summary>
public sealed class RuinSite
{
    public const int Cell = 4;
    public const int FloorPitch = 10;
    /// <summary>Rock left over the top floor's ceiling across most (80%) of the region.</summary>
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
    /// <summary>The cell the region grew from: the island's most interior point.</summary>
    public readonly int Centre;
    public readonly int RegionCells, RegionLandCells, IslandLandCells;
    public readonly long TargetArea;
    public readonly TimeSpan RegionTime;
    /// <summary>What the site was picked from: the landmass, which island of it, and (over the whole landmass, for
    /// maps) which cells are in the region.</summary>
    public readonly LandmassSample Sample;
    public readonly int Island;
    public readonly bool[] LandmassRegion;

    public int FloorY(int floor) => TopY - floor * FloorPitch;
    public Ground At(int floor, int i, int j) =>
        i < 0 || j < 0 || i >= Nx || j >= Nz ? Ground.Out : Grounds[floor][i + Nx * j];

    private RuinSite(LandmassSample sample, int island, RuinSettings settings)
    {
        Seed = sample.Seed; Landmass = sample.Landmass; Sample = sample; Island = island;
        var sw = Stopwatch.StartNew();
        int fnx = sample.Nx, fnz = sample.Nz, fn = fnx * fnz;
        var land = sample.Land; var fromLand = sample.FromLand;
        var topLo = sample.TopLo; var topHi = sample.TopHi; var lowLo = sample.LowLo; var lowHi = sample.LowHi;
        int centre = sample.IslandCentre[island];
        IslandLandCells = sample.IslandLand[island];

        // The region: grown from the island's most interior point, nearest first, over land and cracks narrow enough to
        // bridge, until it holds the asked share of the island's land.
        var inRegion = new bool[fn];
        var queue = new PriorityQueue<int, float>();
        int ci = centre % fnx, cj = centre / fnx, want = (int)(settings.Share * IslandLandCells), got = 0, all = 0;
        queue.Enqueue(centre, 0); inRegion[centre] = true;
        while (queue.TryDequeue(out int c, out _) && got < want)
        {
            all++;
            if (land[c]) got++;
            int i = c % fnx, j = c / fnx;
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                int ni = i + di, nj = j + dj;
                if (ni < 0 || nj < 0 || ni >= fnx || nj >= fnz) continue;
                int d = ni + fnx * nj;
                if (inRegion[d] || fromLand[d] > BridgeCells / 2) continue;
                inRegion[d] = true;
                float dx = ni - ci, dz = nj - cj;
                queue.Enqueue(d, dx * dx + dz * dz);
            }
        }
        // Cells queued but never reached aren't in it.
        while (queue.TryDequeue(out int c, out _)) inRegion[c] = false;
        LandmassRegion = inRegion;
        RegionCells = all; RegionLandCells = got;

        // Crop to the region's bounds (a cell of margin).
        int ri0 = int.MaxValue, rj0 = int.MaxValue, ri1 = int.MinValue, rj1 = int.MinValue;
        for (int c = 0; c < fn; c++)
        {
            if (!inRegion[c]) continue;
            int i = c % fnx, j = c / fnx;
            ri0 = Math.Min(ri0, i); rj0 = Math.Min(rj0, j); ri1 = Math.Max(ri1, i); rj1 = Math.Max(rj1, j);
        }
        ri0 = Math.Max(0, ri0 - 1); rj0 = Math.Max(0, rj0 - 1); ri1 = Math.Min(fnx - 1, ri1 + 1); rj1 = Math.Min(fnz - 1, rj1 + 1);
        Nx = ri1 - ri0 + 1; Nz = rj1 - rj0 + 1;
        X0 = sample.X0 + ri0 * Cell; Z0 = sample.Z0 + rj0 * Cell;
        int n = Nx * Nz;
        Surface = new short[n];
        var region = new bool[n];
        var los = new short[n * 2]; var his = new short[n * 2];
        var surfaces = new List<short>();
        for (int j = 0; j < Nz; j++)
        for (int i = 0; i < Nx; i++)
        {
            int f = (i + ri0) + fnx * (j + rj0), c = i + Nx * j;
            region[c] = inRegion[f];
            Surface[c] = topHi[f];
            los[2 * c] = topLo[f]; his[2 * c] = topHi[f]; los[2 * c + 1] = lowLo[f]; his[2 * c + 1] = lowHi[f];
            if (region[c] && land[f]) surfaces.Add(topHi[f]);
        }
        Centre = (centre % fnx - ri0) + Nx * (centre / fnx - rj0);

        // The top floor sits under 80% of the region's surface with Cover to spare.
        surfaces.Sort();
        int low = surfaces.Count == 0 ? 0 : surfaces[surfaces.Count / 5];
        TopY = low - Cover - FloorPitch + 1;

        // Floors, down from the top, until they hold the floor the ruin needs: rooms take roughly RoomShare of what can
        // hold them.
        const float RoomShare = 0.45f;
        TargetArea = (long)(settings.Population * settings.AreaPerPerson);
        var grounds = new List<Ground[]>();
        double area = 0;
        while (grounds.Count < settings.MaxFloors && area < TargetArea)
        {
            int y = TopY - grounds.Count * FloorPitch, solid = 0;
            var g = new Ground[n];
            for (int c = 0; c < n; c++)
            {
                if (!region[c]) continue;
                g[c] = Ground.Open;
                for (int s = 0; s < 2; s++)
                {
                    short lo = los[2 * c + s], hi = his[2 * c + s];
                    if (hi == short.MinValue) continue;
                    if (lo <= y - Margin && hi >= y + FloorPitch - 1 + Margin) { g[c] = Ground.Solid; break; }
                    if (lo <= y + FloorPitch - 1 && hi >= y) g[c] = Ground.Partial;
                }
                if (g[c] == Ground.Solid) solid++;
            }
            if (solid < RegionLandCells / 20) break; // the ground has run out
            grounds.Add(g);
            area += solid * (double)(Cell * Cell) * RoomShare;
        }
        Grounds = grounds.ToArray();
        Floors = Grounds.Length;
        RegionTime = sw.Elapsed;
    }

    /// <summary>The ruin site on island <paramref name="island"/> of a sampled landmass (0 is the biggest).</summary>
    public static RuinSite Build(LandmassSample sample, int island, RuinSettings settings) => new(sample, island, settings);
}
