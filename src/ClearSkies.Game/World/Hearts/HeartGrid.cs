namespace ClearSkies.Game.Generation;

/// <summary>One island heart: it holds up the ground nearer to it than to any other heart (its piece), if it is alive.
/// X and Z are world blocks; <paramref name="Depth"/> is how far below the terrain surface at (X, Z) it sits, and
/// <paramref name="Surface"/> that surface's height. A heart that doesn't <paramref name="Exists"/> (above the surface)
/// is no heart at all: it holds nothing and leaves the ground to others. <paramref name="Drop"/> is how far its piece's
/// top is worn down, 0-1 of the most (see HeartWorldGenerator).</summary>
public readonly record struct Heart(float X, float Depth, float Z, float Surface, bool Alive, bool Exists, float Drop);

/// <summary>
/// Where island hearts are. The world is a <see cref="ContinentTerrain"/> broken into pieces: hearts sit all through
/// the ground, one per cell of a 3D grid, and each holds the ground nearest to it, so the pieces fit together like a
/// jigsaw, with a crack between neighbours (see HeartWorldGenerator). The grid's rows go down from the terrain surface,
/// not up from a fixed height: the islands are built around their hearts, so the top row's pieces always hold the
/// surface, and the layers under them follow the land's shape (the cracks between them run along it, not across its
/// slopes). Distances count depth <see cref="VerticalScale"/> times over, so pieces are wide and flat rather than
/// cubes.
///
/// A dead heart's piece doesn't exist: a hole. Whether a heart is alive goes by height: nearly all are low down, so the
/// plains are a floor of land cracked into pieces (under it, the underworld is sparser); above it they die off gradually, so the highlands break up into
/// scattered pieces, gathered by the <see cref="ClusterField"/> into clusters with open sky between. In the mountain
/// ranges most are alive again, so each range holds together, cracked.
/// </summary>
public static class HeartGrid
{
    /// <summary>A layer of hearts: a grid of cells <see cref="CellSize"/> blocks across (and that over
    /// <see cref="VerticalScale"/> tall), whose hearts exist only from <see cref="YMin"/> up to <see cref="YMax"/>
    /// (heights relative to the terrain surface: minus depth).</summary>
    public readonly record struct Layer(float CellSize, float YMin, float YMax)
    {
        public float CellHeight => CellSize / VerticalScale;
        public int CellX(float x) => (int)MathF.Floor(x / CellSize);
        public int CellY(float y) => (int)MathF.Floor(y * VerticalScale / CellSize);
    }

    /// <summary>The layers: one grid, all through the ground under the surface.</summary>
    public static readonly Layer[] Layers =
    {
        new(500f, float.MinValue, 0f),
    };

    /// <summary>The top of the underworld: below it, under the plains' top layer of pieces, most are gone, leaving
    /// caves hundreds of blocks high with the remaining pieces standing and hanging in them.</summary>
    public const float DeepTop = ContinentTerrain.PlainsLevel - 150f;

    /// <summary>How much more a vertical offset counts than a horizontal one when finding a block's nearest heart.</summary>
    public const float VerticalScale = 1.6f;

    /// <summary>How far a heart may sit from its cell's middle, as a share of the cell: under 1, so no two are
    /// too close together.</summary>
    private const float Jitter = 0.8f;

    /// <summary>The streamed world's vertical band, in blocks (chunk layers -8..55): nothing generates outside it.</summary>
    public const int WorldBottom = -256;
    public const int WorldTop    = 1792;

    /// <summary>The lowest an island may reach: above the hearts world's cloud sea (<see cref="CloudSeaAltitude"/>).</summary>
    internal const float LowestBottom = WorldBottom + 56f;

    /// <summary>The hearts world's cloud sea (see SkySettings.CloudSeaAltitude): its layer ends a good way below the
    /// lowest islands, so none sits in it.</summary>
    public const float CloudSeaAltitude = -300f;

    // Alive by height: FloorChance up to FloorTop, easing over FloorFade to UpperChance (in the middle of a cluster),
    // and thinning further to half of that ThinOver blocks higher. Inside a mountain range, MountainChance instead,
    // so a mountain holds together as one cracked whole.
    private const float FloorChance = 0.96f, FloorFade = 350f, DeepChance = 0.3f, DeepFade = 80f;
    public const float FloorTop = ContinentTerrain.PlainsLevel - 30f;
    private const float UpperChance = 0.45f, ThinOver = 1200f, MountainChance = 0.85f;

    // Islands: the floor, the underworld and the mountains keep their chances only inside the clusters (see
    // ClusterField), easing from IslandFrom to IslandFull of it. A range breaks up like the islands around it: up to
    // MountainInside of MountainChance inside a cluster, so it reads as a line of snowy pieces with sky through it
    // rather than one cracked block, and nothing of it between clusters. Elsewhere between clusters the ground
    // survives only in islets (see IsletField), up to IsletChance of it: small groups of pieces, stepping stones
    // across the open sky, never a lone piece on its own. So a continent is a scatter of island clusters with open
    // sky between, each still carrying the continent's surface on top.
    private const float IslandFrom = 0.1f, IslandFull = 0.6f, MountainInside = 0.55f;
    private const float IsletChance = 0.6f, IsletSpacing = 1300f, IsletDetail = 420f, IsletFrom = 0.74f, IsletFull = 0.84f;

    /// <summary>How much of FloorChance the floor keeps inside a cluster's fringe, rising to all of it in the cluster's
    /// core (from CoreFrom to CoreFull of it): a landmass is whole in the middle and breaks up towards its edges. A
    /// range in a cluster's core holds together the same way, up to MountainCore of MountainChance.</summary>
    private const float IslandFloor = 0.8f, CoreFrom = 0.4f, CoreFull = 0.75f, MountainCore = 0.9f;

    // Continents: value noise at ContinentSpacing, roughened by a finer one at ContinentDetail; land where it is over
    // ContinentEdgeLow, fading in until ContinentEdgeHigh.
    private const float ContinentSpacing = 24000f, ContinentDetail = 7500f, ContinentEdgeLow = 0.46f, ContinentEdgeHigh = 0.56f;

    /// <summary>Landmasses: the world is cut into cells about this many blocks across, each one jittered grid point's
    /// nearest ground, with open sky along their borders, so no land is bigger than one cell (see LandmassBorder).</summary>
    public const float LandmassSpacing = 6500f;
    // How far a landmass's point may sit from its cell's middle (a share of the cell); hearts within LandmassGap of a
    // border are dead, coming back over LandmassFade, so the pieces either side (up to a cell's reach of their hearts)
    // never meet.
    private const float LandmassJitter = 0.6f, LandmassGap = 600f, LandmassFade = 600f;

    // Clumps: value noise at these spacings (blocks). Clusters: blobs of value noise at ClusterSpacing, their edges
    // roughened by a finer one at ClusterDetail, gathered where the clumps are.
    private const float ClumpSpacing = 16000f, ClumpDetail = 5000f, ClusterSpacing = 3500f, ClusterDetail = 1100f;

    /// <summary>The heart of cell (cx, cy, cz) of layer <paramref name="layer"/>. Row cy is minus its depth below the
    /// surface, in cells: -1 is the top row.</summary>
    public static Heart At(ulong seed, int layer, int cx, int cy, int cz)
    {
        var l = Layers[layer];
        var rng = new SplitMix64Rng(HashCell(seed, 200 + layer, cx, cy, cz));
        float x = (cx + 0.5f + Jitter * (rng.NextFloat01() - 0.5f)) * l.CellSize;
        float y = (cy + 0.5f + Jitter * (rng.NextFloat01() - 0.5f)) * l.CellHeight;
        float z = (cz + 0.5f + Jitter * (rng.NextFloat01() - 0.5f)) * l.CellSize;
        bool exists = y >= l.YMin && y < l.YMax;
        float surface = exists ? ContinentTerrain.For(seed).Height(x, z) : 0f;
        // Alive by the height of the top of its row: the top row by the surface's own height (plains floor,
        // highlands, ranges), the rows under it by how far down they reach (under the plains, the underworld).
        bool alive = exists && rng.NextFloat01() < AliveChance(seed, x, surface + (cy + 1) * l.CellHeight, z);
        return new Heart(x, -y, z, surface, alive, exists, rng.NextFloat01());
    }

    /// <summary>How likely a heart at (x, y, z) is to be alive: by height and the mountain ranges, within a continent,
    /// dying off towards its landmass's border.</summary>
    public static float AliveChance(ulong seed, float x, float y, float z)
    {
        float continent = Continent(seed, x, z);
        if (continent <= 0f) return 0f;
        float inside = Smoothstep(LandmassGap, LandmassGap + LandmassFade, LandmassBorder(seed, x, z, out _));
        if (inside <= 0f) return 0f;
        return continent * inside * LandChance(seed, x, y, z);
    }

    /// <summary>Which landmass (x, z) belongs to: the cell of the jittered <see cref="LandmassSpacing"/> grid whose
    /// point is nearest. No land crosses from one to another (see <see cref="LandmassBorder"/>).</summary>
    public static (int X, int Z) Landmass(ulong seed, float x, float z)
    {
        LandmassBorder(seed, x, z, out var cell);
        return cell;
    }

    /// <summary>How far (x, z) is inside its landmass: the distance to the nearest edge of its cell, the line halfway
    /// to a neighbouring cell's point. Hearts within LandmassGap of it are dead, so there is always open sky between
    /// two landmasses and none is wider than about <see cref="LandmassSpacing"/> times (1 + LandmassJitter).</summary>
    public static float LandmassBorder(ulong seed, float x, float z, out (int X, int Z) cell)
    {
        int ci = (int)MathF.Floor(x / LandmassSpacing), cj = (int)MathF.Floor(z / LandmassSpacing);
        cell = (ci, cj);
        float bestX = 0f, bestZ = 0f, best = float.MaxValue;
        for (int j = cj - 1; j <= cj + 1; j++)
        for (int i = ci - 1; i <= ci + 1; i++)
        {
            var (sx, sz) = LandmassPoint(seed, i, j);
            float d = (sx - x) * (sx - x) + (sz - z) * (sz - z);
            if (d < best) { best = d; bestX = sx; bestZ = sz; cell = (i, j); }
        }
        float border = float.MaxValue;
        for (int j = cell.Z - 2; j <= cell.Z + 2; j++)
        for (int i = cell.X - 2; i <= cell.X + 2; i++)
        {
            if (i == cell.X && j == cell.Z) continue;
            var (sx, sz) = LandmassPoint(seed, i, j);
            float nx = sx - bestX, nz = sz - bestZ, len = MathF.Sqrt(nx * nx + nz * nz);
            // How far short of the halfway line to that point (x, z) is, along the line between the two points.
            float d = ((0.5f * (sx + bestX) - x) * nx + (0.5f * (sz + bestZ) - z) * nz) / len;
            border = MathF.Min(border, d);
        }
        return border;
    }

    private static (float X, float Z) LandmassPoint(ulong seed, int i, int j)
    {
        var rng = new SplitMix64Rng(HashCell(seed, 306, i, 0, j));
        return ((i + 0.5f + LandmassJitter * (rng.NextFloat01() - 0.5f)) * LandmassSpacing,
                (j + 0.5f + LandmassJitter * (rng.NextFloat01() - 0.5f)) * LandmassSpacing);
    }

    /// <summary>0-1: how far inside a continent (x, z) is. Continents are land many kilometres across; between them are
    /// gaps of several kilometres with no land at all. Their edges fade over a few kilometres, the land thinning out
    /// before it ends.</summary>
    public static float Continent(ulong seed, float x, float z)
    {
        float v = 0.8f * ValueNoise((uint)seed ^ 0xC7u, x / ContinentSpacing, z / ContinentSpacing)
                + 0.2f * ValueNoise((uint)seed ^ 0xC8u, x / ContinentDetail, z / ContinentDetail);
        return Smoothstep(ContinentEdgeLow, ContinentEdgeHigh, v);
    }

    private static float LandChance(ulong seed, float x, float y, float z)
    {
        float cluster = ClusterField(seed, x, z);
        float inIsland = Smoothstep(IslandFrom, IslandFull, cluster);
        float islet = inIsland < 1f ? IsletField(seed, x, z) : 0f;
        float inCore = Smoothstep(CoreFrom, CoreFull, cluster);
        float island = Lerp(IsletChance * islet, Lerp(IslandFloor, 1f, inCore), inIsland);
        if (y < DeepTop) return island * Lerp(DeepChance, FloorChance, Smoothstep(DeepTop - DeepFade, DeepTop, y));
        float up = Smoothstep(FloorTop, FloorTop + FloorFade, y);
        float floor = island * FloorChance;
        float mountain = Smoothstep(0.1f, 0.6f, ContinentTerrain.For(seed).RangeCore(x, z));
        float mountainChance = MountainChance * Lerp(MountainInside, MountainCore, inCore) * inIsland;
        if (up <= 0f) return Lerp(floor, mountainChance, mountain);
        float thin = Lerp(1f, 0.5f, Math.Clamp((y - FloorTop - FloorFade) / ThinOver, 0f, 1f));
        float upper = MathF.Min(UpperChance * thin * Lerp(0.5f * islet, 1.8f, cluster), 1f);
        return Lerp(Lerp(floor, upper, up), mountainChance, mountain);
    }

    /// <summary>0-1: how far inside a cluster (x, z) is, rising gradually from its fringe to its core. Clusters are a
    /// few kilometres across, under a kilometre apart where the big clumps are, and rare outside them.</summary>
    public static float ClusterField(ulong seed, float x, float z)
    {
        float v = 0.75f * ValueNoise((uint)seed ^ 0xC5u, x / ClusterSpacing, z / ClusterSpacing)
                + 0.25f * ValueNoise((uint)seed ^ 0xC6u, x / ClusterDetail, z / ClusterDetail);
        float clumps = 0.7f * ValueNoise((uint)seed ^ 0xC1u, x / ClumpSpacing, z / ClumpSpacing)
                     + 0.3f * ValueNoise((uint)seed ^ 0xC2u, x / ClumpDetail, z / ClumpDetail);
        return Smoothstep(0.5f, 0.75f, v) * Lerp(0.2f, 1f, Smoothstep(0.4f, 0.6f, clumps));
    }

    /// <summary>0-1: how far inside an islet (x, z) is. Islets are blobs a few hundred blocks across, most of a kilometre
    /// apart, their edges roughened by a finer noise: where the ground between clusters survives.</summary>
    public static float IsletField(ulong seed, float x, float z)
    {
        float v = 0.8f * ValueNoise((uint)seed ^ 0xC9u, x / IsletSpacing, z / IsletSpacing)
                + 0.2f * ValueNoise((uint)seed ^ 0xCAu, x / IsletDetail, z / IsletDetail);
        return Smoothstep(IsletFrom, IsletFull, v);
    }

    /// <summary>Finds the middle of a cluster near (x, z), well inside a continent: the nearest point, on a coarse grid
    /// spiralling out.</summary>
    public static bool TryFindCluster(ulong seed, float x, float z, out float clusterX, out float clusterZ)
    {
        const float Step = 200f;
        for (int ring = 0; ring < 100; ring++)
        for (int j = -ring; j <= ring; j++)
        for (int i = -ring; i <= ring; i++)
        {
            if (System.Math.Max(System.Math.Abs(i), System.Math.Abs(j)) != ring) continue;
            clusterX = x + i * Step; clusterZ = z + j * Step;
            if (ClusterField(seed, clusterX, clusterZ) > 0.9f && Continent(seed, clusterX, clusterZ) > 0.95f) return true;
        }
        clusterX = clusterZ = 0f;
        return false;
    }

    internal static float ValueNoise(uint seed, float x, float z)
    {
        float fx = MathF.Floor(x), fz = MathF.Floor(z);
        int ix = (int)fx, iz = (int)fz;
        float tx = Smoothstep(0f, 1f, x - fx), tz = Smoothstep(0f, 1f, z - fz);
        float a = Hash01(seed, ix, iz), b = Hash01(seed, ix + 1, iz);
        float c = Hash01(seed, ix, iz + 1), d = Hash01(seed, ix + 1, iz + 1);
        return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), tz);
    }

    internal static float Hash01(uint seed, int x, int z) => (HashCell(seed, 7, x, 0, z) >> 40) * (1f / (1 << 24));

    private static ulong HashCell(ulong seed, int salt, int x, int y, int z)
    {
        ulong h = seed ^ ((ulong)(uint)salt * 0xD6E8FEB86659FD93UL);
        h ^= (ulong)(uint)x * 0x9E3779B97F4A7C15UL; h = Mix(h);
        h ^= (ulong)(uint)y * 0x94D049BB133111EBUL; h = Mix(h);
        h ^= (ulong)(uint)z * 0xC2B2AE3D27D4EB4FUL; h = Mix(h);
        return h;
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
