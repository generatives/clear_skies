namespace ClearSkies.Game.Generation;

/// <summary>
/// The islands world's layout. The world is a stack of landscapes (<see cref="Layers"/>), each a continuous surface
/// floating at its own height, with hills (and on the landmasses, mountains) and land some way down under it. Each is
/// broken into pieces, as a 3D jigsaw: pieces hold the land nearest their point, the points jittered on a grid of
/// cells wide and flat, so pieces stack in layers, with cracks between them (see <see cref="IslandWorldGenerator"/>).
/// Whether a piece is there goes by its landscape's coverage where its point is: where coverage is full, every piece
/// is, and the landscape holds together, cracked; where it thins, more pieces are missing and the land breaks up into
/// islands, then islets, then open sky. Islands are never drawn as shapes: they are what is left of a landscape.
///
/// Low down, the landmasses: coverage in great blobs many kilometres across, intact at their cores. Under them, an
/// underworld of smaller pieces; above, landscapes whose coverage gathers into ever sparser clusters with height.
///
/// Thread-safe: everything is worked out from the seed alone.
/// </summary>
public static class IslandField
{
    /// <summary>A landscape: its surface at about <see cref="Base"/> (swelling up and down by up to
    /// <see cref="Swell"/>), with hills up to <see cref="Hills"/> high and mountains up to <see cref="Mountains"/>; land
    /// about <see cref="Thickness"/> deep under it; broken into pieces from cells <see cref="CellSize"/> across, with
    /// cracks about twice <see cref="Crack"/> wide between them.</summary>
    public readonly record struct Layer(
        float Base, float Swell, float Hills, float Mountains, float Thickness, float CellSize, float Crack)
    {
        /// <summary>Highest its surface reaches, and lowest its underside does.</summary>
        public float TopBound => Base + Swell + Hills + Mountains + 8f;
        public float BottomBound => Base - Swell - Thickness - Roots * (Hills + Mountains) - 8f;
    }

    /// <summary>The landscapes, bottom up: the underworld, the landmasses, the middle and the high.</summary>
    public static readonly Layer[] Layers =
    {
        new(Base: -110f, Swell: 25f,  Hills: 25f, Mountains: 0f,   Thickness: 60f,  CellSize: 180f, Crack: 9f),
        new(Base: 150f,  Swell: 50f,  Hills: 70f, Mountains: 650f, Thickness: 150f, CellSize: 320f, Crack: 11f),
        new(Base: 640f,  Swell: 90f,  Hills: 50f, Mountains: 0f,   Thickness: 110f, CellSize: 260f, Crack: 10f),
        new(Base: 1200f, Swell: 120f, Hills: 35f, Mountains: 0f,   Thickness: 70f,  CellSize: 200f, Crack: 9f),
    };

    public const int Underworld = 0, Landmass = 1;

    /// <summary>How much more a vertical offset counts than a horizontal one when finding a block's piece: pieces are
    /// this many times wider than tall, so they stack in layers.</summary>
    public const float VerticalScale = 1.6f;

    /// <summary>How far a landscape's underside hangs down under high ground on top, as a share of how high it is:
    /// roots under mountains.</summary>
    public const float Roots = 0.3f;

    /// <summary>How far a piece floats clear of the landscape under it, at least: closer, and it isn't there.</summary>
    public const float Clearance = 50f;

    /// <summary>The streamed world's vertical band, in blocks (chunk layers -8..55): nothing generates outside it.</summary>
    public const int WorldBottom = -256;
    public const int WorldTop    = 1792;

    /// <summary>The lowest any land may reach: above the cloud sea (<see cref="CloudSeaAltitude"/>).</summary>
    public const float LowestBottom = WorldBottom + 56f;

    /// <summary>The cloud sea (see SkySettings.CloudSeaAltitude): its layer ends a good way below the lowest land.</summary>
    public const float CloudSeaAltitude = -300f;

    // Landmasses: blobs of value noise at LandSpacing, roughened by a finer one; land from LandFrom, whole by LandFull,
    // and fully inside a core past CoreAt.
    private const float LandSpacing = 9000f, LandDetail = 2600f, LandFrom = 0.45f, LandFull = 0.6f, CoreAt = 0.68f;

    /// <summary>0-1: how much of landscape <paramref name="layer"/> is there at (x, z): the chance that a piece whose
    /// point is there is.</summary>
    public static float Coverage(ulong seed, int layer, float x, float z)
    {
        switch (layer)
        {
            case Landmass:
            {
                // Whole at their cores, breaking up towards their rims into islands, with islets scattered off them.
                float v = Blobs(seed, 0xA1, x, z, LandSpacing, LandDetail, 0.25f);
                float land = Smoothstep(LandFrom, LandFull, v);
                float islets = Smoothstep(0.62f, 0.8f, Blobs(seed, 0xA2, x, z, 900f, 300f, 0.3f)) * Smoothstep(0.3f, LandFrom, v);
                return MathF.Max(land, 0.7f * islets);
            }
            case Underworld:
                return 0.8f * Smoothstep(0.5f, 0.7f, Blobs(seed, 0xA3, x, z, 1100f, 350f, 0.3f));
            case 2:
                return 0.85f * Smoothstep(0.62f, 0.8f, Blobs(seed, 0xA4, x, z, 1600f, 450f, 0.3f));
            default:
                return 0.75f * Smoothstep(0.7f, 0.86f, Blobs(seed, 0xA5, x, z, 1200f, 320f, 0.3f));
        }
    }

    /// <summary>A point near (x, z) well inside a landmass's core, spiralling out on a coarse grid.</summary>
    public static (float X, float Z) NearestLandmassCore(ulong seed, float x, float z)
    {
        const float Step = 250f;
        for (int ring = 0; ; ring++)
        for (int j = -ring; j <= ring; j++)
        for (int i = -ring; i <= ring; i++)
        {
            if (Math.Max(Math.Abs(i), Math.Abs(j)) != ring) continue;
            float px = x + i * Step, pz = z + j * Step;
            if (Blobs(seed, 0xA1, px, pz, LandSpacing, LandDetail, 0.25f) > CoreAt) return (px, pz);
        }
    }

    /// <summary>Value noise at <paramref name="spacing"/>, roughened by <paramref name="detailShare"/> of a finer one at
    /// <paramref name="detail"/>: about 0-1.</summary>
    private static float Blobs(ulong seed, uint salt, float x, float z, float spacing, float detail, float detailShare)
        => (1f - detailShare) * ValueNoise((uint)seed ^ salt, x / spacing, z / spacing)
           + detailShare * ValueNoise((uint)seed ^ (salt + 0x100u), x / detail, z / detail);

    private static float ValueNoise(uint seed, float x, float z)
    {
        float fx = MathF.Floor(x), fz = MathF.Floor(z);
        int ix = (int)fx, iz = (int)fz;
        float tx = Smoothstep(0f, 1f, x - fx), tz = Smoothstep(0f, 1f, z - fz);
        float a = Hash01(seed, ix, iz), b = Hash01(seed, ix + 1, iz);
        float c = Hash01(seed, ix, iz + 1), d = Hash01(seed, ix + 1, iz + 1);
        return a + (b - a) * tx + (c - a) * tz + (a - b - c + d) * tx * tz;
    }

    private static float Hash01(uint seed, int x, int z) => (Hash(seed, 7, x, 0, z) >> 40) * (1f / (1 << 24));

    /// <summary>A well-mixed hash of a cell.</summary>
    public static ulong Hash(ulong seed, int salt, int x, int y, int z)
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

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
