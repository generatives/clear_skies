namespace ClearSkies.Game.Generation;

/// <summary>One island: a lump of land floating at <paramref name="Base"/> (world Y of its rim), centred on
/// (<paramref name="X"/>, <paramref name="Z"/>), about <paramref name="Radius"/> blocks out to its ragged edge.
/// <paramref name="Band"/> is which of <see cref="IslandField.Bands"/> it is in; <paramref name="Id"/> sets its own shape
/// noise apart from its neighbours'.</summary>
public readonly record struct Island(int Band, float X, float Base, float Z, float Radius, ulong Id)
{
    public IslandField.BandInfo Info => IslandField.Bands[Band];

    /// <summary>Highest its top can reach and lowest its underside can, for bounding chunk columns.</summary>
    public float TopBound => Base + Info.Hills + Info.Mountains + 16f;
    public float BottomBound => Base - Info.Thickness - IslandField.Roots * (Info.Hills + Info.Mountains) - 16f;

    /// <summary>How far out from its centre any of it can reach, its edge wobble included.</summary>
    public float Reach => Radius * (1f + 1.42f * IslandField.EdgeWarp) + IslandField.CoastWiggle;
}

/// <summary>
/// Where islands are. The world is islands floating at every height, placed band by band (<see cref="Bands"/>), each
/// band a grid of cells with at most one island per cell: big landmasses kilometres across low down, with an underworld
/// of smaller islands under them; above, islands growing smaller and sparser with height. Each island is its own
/// shape at its own height, with its own relief on top (see <see cref="IslandWorldGenerator"/>), so islands are not
/// cut from one shared surface and can float anywhere.
///
/// Thread-safe: everything is worked out from the seed alone.
/// </summary>
public static class IslandField
{
    /// <summary>A band of islands: one chance in <see cref="Chance"/> of an island per <see cref="CellSize"/>-block cell,
    /// its rim at a height between <see cref="BaseMin"/> and <see cref="BaseMax"/>, its radius between
    /// <see cref="RadiusMin"/> and <see cref="RadiusMax"/>; up to <see cref="Thickness"/> deep at its middle; hills up to
    /// <see cref="Hills"/> high and mountains up to <see cref="Mountains"/> on top; cracked into shards about
    /// <see cref="ShardSize"/> across, the cracks between them about twice <see cref="Crack"/> wide.</summary>
    public readonly record struct BandInfo(
        float CellSize, float Chance, float BaseMin, float BaseMax, float RadiusMin, float RadiusMax,
        float Thickness, float Hills, float Mountains, float ShardSize, float Crack);

    /// <summary>The bands, bottom up: the underworld, the landmasses, the middle islands and the high ones.</summary>
    public static readonly BandInfo[] Bands =
    {
        new(CellSize: 900f,  Chance: 0.6f,  BaseMin: -110f, BaseMax: -40f,  RadiusMin: 120f,  RadiusMax: 380f,
            Thickness: 90f,  Hills: 22f,  Mountains: 0f,   ShardSize: 130f, Crack: 9f),
        new(CellSize: 7000f, Chance: 0.85f, BaseMin: 130f,  BaseMax: 190f,  RadiusMin: 1700f, RadiusMax: 3000f,
            Thickness: 160f, Hills: 70f,  Mountains: 650f, ShardSize: 230f, Crack: 10f),
        new(CellSize: 1500f, Chance: 0.55f, BaseMin: 380f,  BaseMax: 820f,  RadiusMin: 180f,  RadiusMax: 600f,
            Thickness: 140f, Hills: 45f,  Mountains: 0f,   ShardSize: 170f, Crack: 9f),
        new(CellSize: 1200f, Chance: 0.3f,  BaseMin: 950f,  BaseMax: 1450f, RadiusMin: 70f,   RadiusMax: 240f,
            Thickness: 80f,  Hills: 20f,  Mountains: 0f,   ShardSize: 110f, Crack: 8f),
    };

    public const int Underworld = 0, Landmass = 1;

    /// <summary>How far an island's outline is pushed about along each axis, as a share of its radius, and how far its
    /// small wiggles reach, in blocks.</summary>
    public const float EdgeWarp = 0.35f, CoastWiggle = 25f;

    /// <summary>How far an island's underside hangs down under high ground on top, as a share of how high it is: roots
    /// under mountains.</summary>
    public const float Roots = 0.3f;

    /// <summary>How far an island floats clear of the land under it, at least.</summary>
    private const float Clearance = 60f;

    /// <summary>The streamed world's vertical band, in blocks (chunk layers -8..55): nothing generates outside it.</summary>
    public const int WorldBottom = -256;
    public const int WorldTop    = 1792;

    /// <summary>The lowest an island may reach: above the cloud sea (<see cref="CloudSeaAltitude"/>).</summary>
    public const float LowestBottom = WorldBottom + 56f;

    /// <summary>The cloud sea (see SkySettings.CloudSeaAltitude): its layer ends a good way below the lowest islands.</summary>
    public const float CloudSeaAltitude = -300f;

    /// <summary>The island of cell (cx, cz) of band <paramref name="band"/>, if it has one. Islands over or under a
    /// landmass only exist where they float clear of it.</summary>
    public static bool TryGet(ulong seed, int band, int cx, int cz, out Island island)
    {
        var b = Bands[band];
        var rng = new SplitMix64Rng(Hash(seed, band, cx, cz));
        island = default;
        if (rng.NextFloat01() >= b.Chance) return false;
        float radius = rng.NextRange(b.RadiusMin, b.RadiusMax);
        // Anywhere in its cell, so neighbours may run together into one bigger, odder shape.
        float x = (cx + rng.NextFloat01()) * b.CellSize;
        float z = (cz + rng.NextFloat01()) * b.CellSize;
        float baseY = rng.NextRange(b.BaseMin, b.BaseMax);
        island = new Island(band, x, baseY, z, radius, rng.NextUInt64());
        if (band == Landmass) return true;

        // Over or under a landmass: clear of it, or not at all.
        int lx = (int)MathF.Floor(x / Bands[Landmass].CellSize), lz = (int)MathF.Floor(z / Bands[Landmass].CellSize);
        for (int j = lz - 1; j <= lz + 1; j++)
        for (int i = lx - 1; i <= lx + 1; i++)
        {
            if (!TryGet(seed, Landmass, i, j, out var land)) continue;
            float dx = x - land.X, dz = z - land.Z, both = land.Reach + island.Reach;
            if (dx * dx + dz * dz > both * both) continue;
            bool clear = band == Underworld
                ? island.Base + b.Hills + Clearance < land.BottomBound
                : island.BottomBound - Clearance > land.TopBound;
            if (!clear) return false;
        }
        return true;
    }

    /// <summary>Adds to <paramref name="into"/> every island that can reach into the box (world blocks, x and z).</summary>
    public static void Near(ulong seed, float minX, float minZ, float maxX, float maxZ, List<Island> into)
    {
        for (int band = 0; band < Bands.Length; band++)
        {
            var b = Bands[band];
            float pad = b.RadiusMax * (1f + 1.42f * EdgeWarp) + CoastWiggle;
            int x0 = (int)MathF.Floor((minX - pad) / b.CellSize), x1 = (int)MathF.Floor((maxX + pad) / b.CellSize);
            int z0 = (int)MathF.Floor((minZ - pad) / b.CellSize), z1 = (int)MathF.Floor((maxZ + pad) / b.CellSize);
            for (int cz = z0; cz <= z1; cz++)
            for (int cx = x0; cx <= x1; cx++)
            {
                if (!TryGet(seed, band, cx, cz, out var island)) continue;
                // Box to circle distance.
                float dx = MathF.Max(0f, MathF.Max(minX - island.X, island.X - maxX));
                float dz = MathF.Max(0f, MathF.Max(minZ - island.Z, island.Z - maxZ));
                if (dx * dx + dz * dz <= island.Reach * island.Reach) into.Add(island);
            }
        }
    }

    /// <summary>The landmass nearest (x, z), spiralling out cell by cell.</summary>
    public static Island NearestLandmass(ulong seed, float x, float z)
    {
        var b = Bands[Landmass];
        int cx0 = (int)MathF.Floor(x / b.CellSize), cz0 = (int)MathF.Floor(z / b.CellSize);
        for (int ring = 0; ; ring++)
        {
            bool found = false;
            Island best = default;
            float bestD = float.MaxValue;
            for (int j = -ring; j <= ring; j++)
            for (int i = -ring; i <= ring; i++)
            {
                if (Math.Max(Math.Abs(i), Math.Abs(j)) != ring) continue;
                if (!TryGet(seed, Landmass, cx0 + i, cz0 + j, out var land)) continue;
                float d = (land.X - x) * (land.X - x) + (land.Z - z) * (land.Z - z);
                if (d < bestD) { bestD = d; best = land; found = true; }
            }
            if (found) return best;
        }
    }

    private static ulong Hash(ulong seed, int band, int x, int z)
    {
        ulong h = seed ^ ((ulong)(uint)(band + 300) * 0xD6E8FEB86659FD93UL);
        h ^= (ulong)(uint)x * 0x9E3779B97F4A7C15UL; h = Mix(h);
        h ^= (ulong)(uint)z * 0xC2B2AE3D27D4EB4FUL; h = Mix(h);
        return h;
    }

    internal static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
