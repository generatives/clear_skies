namespace ClearSkies.Game.Generation;

/// <summary>One island heart: it holds up the ground nearer to it than to any other heart (its piece), if it is alive.
/// Positions are world blocks. A heart that doesn't <paramref name="Exists"/> (outside its layer's band) is no heart at
/// all: it holds nothing and leaves the ground to others.</summary>
public readonly record struct Heart(float X, float Y, float Z, bool Alive, bool Exists);

/// <summary>
/// Where island hearts are. The world is a <see cref="ContinentTerrain"/> broken into pieces: hearts sit all through
/// the ground, one per cell of a 3D grid, and each holds the ground nearest to it, so the pieces fit together like a
/// jigsaw, with a crack between neighbours (see HeartWorldGenerator). Distances count vertical offsets
/// <see cref="VerticalScale"/> times over, so pieces are wide and flat rather than cubes, and the ground splits into
/// layers as well as columns. Each height band (<see cref="Layers"/>) has its own grid, finer higher up, so pieces get
/// smaller with height.
///
/// A dead heart's piece doesn't exist: a hole. Whether a heart is alive goes by height: nearly all are low down, so the
/// plains are a floor of land cracked into pieces; above it they die off quickly, so the foothills and ranges are
/// broken into scattered pieces. Up there the <see cref="ClusterField"/> gathers them into clusters, with open sky
/// between.
/// </summary>
public static class HeartGrid
{
    /// <summary>A layer of hearts: a grid of cells <see cref="CellSize"/> blocks across (and that over
    /// <see cref="VerticalScale"/> tall), whose hearts exist only from <see cref="YMin"/> up to <see cref="YMax"/>.</summary>
    public readonly record struct Layer(float CellSize, float YMin, float YMax)
    {
        public float CellHeight => CellSize / VerticalScale;
        public int CellX(float x) => (int)MathF.Floor(x / CellSize);
        public int CellY(float y) => (int)MathF.Floor(y * VerticalScale / CellSize);
    }

    /// <summary>The layers, bottom up: big pieces in the floor, smaller in the foothills, smaller still in the ranges.</summary>
    public static readonly Layer[] Layers =
    {
        new(150f, float.MinValue, 0f),
        new(110f, 0f, 400f),
        new(85f, 400f, float.MaxValue),
    };

    /// <summary>How much more a vertical offset counts than a horizontal one when finding a block's nearest heart.</summary>
    public const float VerticalScale = 1.6f;

    /// <summary>How far a heart may sit from its cell's middle, as a share of the cell: under 1, so no two are
    /// too close together.</summary>
    private const float Jitter = 0.8f;

    /// <summary>The lowest an island may reach: above the hearts world's cloud sea (<see cref="CloudSeaAltitude"/>).</summary>
    internal const float LowestBottom = IslandGrid.WorldBottom + 56f;

    /// <summary>The hearts world's cloud sea (see SkySettings.CloudSeaAltitude): its layer ends a good way below the
    /// lowest islands, so none sits in it.</summary>
    public const float CloudSeaAltitude = -300f;

    // Alive by height: FloorChance up to FloorTop, easing over FloorFade to UpperChance (in the middle of a cluster),
    // and thinning further to half of that ThinOver blocks higher.
    private const float FloorChance = 0.96f, FloorFade = 160f;
    public const float FloorTop = ContinentTerrain.PlainsLevel - 30f;
    private const float UpperChance = 0.35f, ThinOver = 1200f;

    // Clumps: value noise at these spacings (blocks). Clusters: blobs of value noise at ClusterSpacing, their edges
    // roughened by a finer one at ClusterDetail, gathered where the clumps are.
    private const float ClumpSpacing = 9000f, ClumpDetail = 3000f, ClusterSpacing = 1400f, ClusterDetail = 450f;

    /// <summary>The heart of cell (cx, cy, cz) of layer <paramref name="layer"/>.</summary>
    public static Heart At(ulong seed, int layer, int cx, int cy, int cz)
    {
        var l = Layers[layer];
        var rng = new SplitMix64Rng(HashCell(seed, 200 + layer, cx, cy, cz));
        float x = (cx + 0.5f + Jitter * (rng.NextFloat01() - 0.5f)) * l.CellSize;
        float y = (cy + 0.5f + Jitter * (rng.NextFloat01() - 0.5f)) * l.CellHeight;
        float z = (cz + 0.5f + Jitter * (rng.NextFloat01() - 0.5f)) * l.CellSize;
        bool exists = y >= l.YMin && y < l.YMax;
        return new Heart(x, y, z, exists && rng.NextFloat01() < AliveChance(seed, x, y, z), exists);
    }

    /// <summary>How likely a heart at (x, y, z) is to be alive.</summary>
    public static float AliveChance(ulong seed, float x, float y, float z)
    {
        float up = Smoothstep(FloorTop, FloorTop + FloorFade, y);
        if (up <= 0f) return FloorChance;
        float thin = Lerp(1f, 0.5f, Math.Clamp((y - FloorTop - FloorFade) / ThinOver, 0f, 1f));
        float upper = MathF.Min(UpperChance * thin * Lerp(0.25f, 2f, ClusterField(seed, x, z)), 1f);
        return Lerp(FloorChance, upper, up);
    }

    /// <summary>0-1: how far inside a cluster (x, z) is, rising gradually from its fringe to its core. Clusters are a
    /// kilometre or two across, a few hundred blocks apart where the big clumps are, and rare outside them.</summary>
    public static float ClusterField(ulong seed, float x, float z)
    {
        float v = 0.75f * ValueNoise((uint)seed ^ 0xC5u, x / ClusterSpacing, z / ClusterSpacing)
                + 0.25f * ValueNoise((uint)seed ^ 0xC6u, x / ClusterDetail, z / ClusterDetail);
        float clumps = 0.7f * ValueNoise((uint)seed ^ 0xC1u, x / ClumpSpacing, z / ClumpSpacing)
                     + 0.3f * ValueNoise((uint)seed ^ 0xC2u, x / ClumpDetail, z / ClumpDetail);
        return Smoothstep(0.5f, 0.75f, v) * Lerp(0.2f, 1f, Smoothstep(0.4f, 0.6f, clumps));
    }

    /// <summary>Finds the middle of a cluster near (x, z): the nearest point, on a coarse grid spiralling out, well
    /// inside one.</summary>
    public static bool TryFindCluster(ulong seed, float x, float z, out float clusterX, out float clusterZ)
    {
        const float Step = 200f;
        for (int ring = 0; ring < 100; ring++)
        for (int j = -ring; j <= ring; j++)
        for (int i = -ring; i <= ring; i++)
        {
            if (System.Math.Max(System.Math.Abs(i), System.Math.Abs(j)) != ring) continue;
            clusterX = x + i * Step; clusterZ = z + j * Step;
            if (ClusterField(seed, clusterX, clusterZ) > 0.9f) return true;
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
