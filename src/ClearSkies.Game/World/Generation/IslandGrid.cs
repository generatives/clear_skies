using System;

namespace ClearSkies.Game.Generation;

/// <summary>The island size classes, largest first; each has its own grid (see <see cref="IslandGrid"/>).</summary>
public enum IslandClass { Large, Medium, Small, Tiny }

/// <summary>
/// Parameters for one floating island, resolved deterministically from its grid cell. Positions and heights are
/// world blocks; rotation and stretch are consumed by <see cref="SkyWorldGenerator"/>'s per-column shape math.
///
/// The body is a rounded lens: a rim of half-thickness <see cref="Lip"/> rounded like a semicircle, a gentle
/// <see cref="Crown"/> on top, and an underside bowl <see cref="Depth"/> deep below the rim, all about
/// <see cref="BaseY"/>. Terrain (plains, and mountains up to <see cref="MountainMax"/>) sits on the top.
/// </summary>
public struct IslandDef
{
    public IslandClass Class;
    public float CenterX;
    public float CenterZ;
    public float BaseY;
    public float Radius;

    public float Lip;
    public float Crown;
    public float Depth;
    public float Bump;        // underside crag amplitude
    public float MountainMax; // peak height of mountains above the crown

    /// Per-island rotation + anisotropic stretch, applied to the (warped) sample offset before computing
    /// radial distance, so footprints read as elongated/rotated blobs rather than circles.
    public float RotationRad;
    public float StretchMajor;
    public float StretchMinor;

    /// Decorrelates this island's noise sampling from every other island sharing the same noise fields.
    public float NoiseOffsetX;
    public float NoiseOffsetZ;

    /// <summary>Plains roll this far above and below the crown.</summary>
    public const float PlainsAmplitude = 4f;

    /// <summary>How far from the centre, horizontally, the island (after warp, stretch and coastline wobble) can
    /// reach.</summary>
    public readonly float Reach => Radius * 1.4f * MathF.Max(StretchMajor, StretchMinor);

    /// <summary>The lowest and highest block the island can fill.</summary>
    public readonly float YMin => BaseY - Lip - Depth - Bump - 1f;
    public readonly float YMax => BaseY + Lip + Crown + MathF.Max(PlainsAmplitude, MountainMax) + 2f;
}

/// <summary>
/// Deterministic island placement: one grid of 3D cells per <see cref="IslandClass"/>, each cell (reproducibly, given
/// the world seed) holding at most one island of its class. Big classes have big cells; the large islands' cells are
/// much wider than they are tall (one cell spans the whole world height), the smaller classes stack several cells up
/// the <see cref="WorldBottom"/>..<see cref="WorldTop"/> band, so islands sit at many heights.
///
/// Where islands go isn't uniform, so the world reads as places rather than an even sprinkle:
/// <list type="bullet">
/// <item>an <see cref="Archipelago"/> field, varying over tens of km, sets how likely large (and a few stray medium)
/// islands are: archipelagos, and wide stretches of open sky;</item>
/// <item>smaller islands gather around bigger ones (<see cref="Near"/>): medium ones around large islands, small ones
/// around those, and tiny rocks around all three, each close to its parent and around its height. Away from any parent,
/// a smaller island is rare, so groups read as groups;</item>
/// <item>heights follow a slowly undulating <see cref="Stratum"/>, so islands form a loose layer you look across rather
/// than filling the sky above and below: each island sits within its class's <c>HeightSpread</c> of the stratum, or of
/// its parent.</item>
/// </list>
///
/// Every island stays inside its own cell (horizontally and vertically), so a chunk's islands are found by looking
/// only at the cells it overlaps. An island that would overlap one of a bigger class is dropped, so islands never
/// merge.
/// </summary>
public static class IslandGrid
{
    /// <summary>The height band islands live in: the 64 chunk layers streaming covers (see ChunkLoadSystem, with
    /// Program's MinChunkY of 0).</summary>
    public const int WorldBottom = -256;
    public const int WorldTop    = 1792;

    /// <summary>Gap kept between islands of different classes, in blocks.</summary>
    private const float Clearance = 16f;

    /// <summary>One class's grid, odds and sizes. A cell holds an island with a chance from <see cref="ChanceOpen"/>
    /// (open sky) to <see cref="ChanceArchipelago"/> by the <see cref="Archipelago"/> field, or up to
    /// <see cref="ChanceNear"/> close to a parent (<see cref="Near"/>), whichever is higher. Radii run from
    /// <see cref="MinRadius"/> to <see cref="MaxRadius"/>, mostly towards the small end (bigger near a parent); the
    /// class ranges overlap, so sizes run on from one class to the next. An island sits within
    /// <see cref="HeightSpread"/> of the stratum, or of its parent's height.</summary>
    private readonly record struct ClassDef(int CellSize, int CellHeight, float ChanceOpen, float ChanceArchipelago,
                                            float ChanceNear, float MinRadius, float MaxRadius, float HeightSpread);

    private static readonly ClassDef[] Classes =
    {
        //   cell   height  open     archi.  near    radius        height spread
        new(10240, 2048,   0.05f,   0.80f,  0f,     700f, 1200f,  150f), // Large: 1.5-2.5 km across
        new( 2560, 1024,   0.004f,  0.12f,  1.00f,  120f,  450f,  250f), // Medium
        new(  768,  512,   0.0005f, 0.01f,  0.80f,   30f,  140f,  200f), // Small
        new(  192,  128,   0.0001f, 0.001f, 0.50f,    6f,   35f,  160f), // Tiny
    };

    // Archipelago field: value noise at these two spacings (blocks), mapped through a smoothstep for contrast.
    private const float ArchipelagoSpacing = 24000f, ArchipelagoDetail = 9000f;

    // The stratum: the height islands gather around, undulating between these over these spacings (blocks).
    private const float StratumLow = 550f, StratumHigh = 1250f, StratumSpacing = 16000f, StratumDetail = 5000f;

    // A parent's pull: full out to NearRim × its radius past its rim, gone by FarRim × its radius (+ FarRimExtra).
    private const float NearRim = 0.2f, FarRim = 3.5f, FarRimExtra = 100f;

    /// <summary>Base Y range of large islands (their cell is the whole band, so this keeps them off its ends).</summary>
    private const float LargeMinBaseY = 650f, LargeMaxBaseY = 1150f;

    public const int ClassCount = 4;

    public static int CellSize(IslandClass c)   => Classes[(int)c].CellSize;
    public static int CellHeight(IslandClass c) => Classes[(int)c].CellHeight;

    /// <summary>The number of vertical cells in class <paramref name="c"/>'s grid.</summary>
    public static int Layers(IslandClass c) => (WorldTop - WorldBottom) / Classes[(int)c].CellHeight;

    /// <summary>Writes the islands of class <paramref name="c"/> in the cells overlapping the box into
    /// <paramref name="output"/> starting at <paramref name="count"/>, and returns the new count (stopping when the span
    /// is full).</summary>
    public static int Collect(ulong seed, IslandClass c, float minX, float minY, float minZ, float maxX, float maxY,
                              float maxZ, Span<IslandDef> output, int count)
    {
        var def = Classes[(int)c];
        int x0 = FloorDiv(minX, def.CellSize), x1 = FloorDiv(maxX, def.CellSize);
        int z0 = FloorDiv(minZ, def.CellSize), z1 = FloorDiv(maxZ, def.CellSize);
        int y0 = Math.Max(FloorDiv(minY - WorldBottom, def.CellHeight), 0);
        int y1 = Math.Min(FloorDiv(maxY - WorldBottom, def.CellHeight), Layers(c) - 1);
        for (int cz = z0; cz <= z1; cz++)
        for (int cy = y0; cy <= y1; cy++)
        for (int cx = x0; cx <= x1; cx++)
        {
            if (count == output.Length) return count;
            if (TryResolve(seed, c, cx, cy, cz, out output[count])) count++;
        }
        return count;
    }

    /// <summary>The island in cell (cx, cy, cz) of class <paramref name="c"/>'s grid, if it has one that doesn't
    /// overlap a bigger class's island.</summary>
    public static bool TryResolve(ulong seed, IslandClass c, int cx, int cy, int cz, out IslandDef island)
    {
        if (!TryPlace(seed, c, cx, cy, cz, out island)) return false;

        // Checked against the bigger classes' placements before their own overlap checks, so this never recurses.
        for (int b = 0; b < (int)c; b++)
        {
            var def = Classes[b];
            float r = island.Reach + Clearance;
            int x0 = FloorDiv(island.CenterX - r, def.CellSize), x1 = FloorDiv(island.CenterX + r, def.CellSize);
            int z0 = FloorDiv(island.CenterZ - r, def.CellSize), z1 = FloorDiv(island.CenterZ + r, def.CellSize);
            int y0 = Math.Max(FloorDiv(island.YMin - Clearance - WorldBottom, def.CellHeight), 0);
            int y1 = Math.Min(FloorDiv(island.YMax + Clearance - WorldBottom, def.CellHeight), Layers((IslandClass)b) - 1);
            for (int bz = z0; bz <= z1; bz++)
            for (int by = y0; by <= y1; by++)
            for (int bx = x0; bx <= x1; bx++)
                if (Placed(seed, (IslandClass)b, bx, by, bz, out var big) && Overlaps(island, big))
                    return false;
        }
        return true;
    }

    private static bool Overlaps(in IslandDef a, in IslandDef b)
    {
        float r = a.Reach + b.Reach + Clearance;
        float dx = a.CenterX - b.CenterX, dz = a.CenterZ - b.CenterZ;
        return dx * dx + dz * dz < r * r
            && a.YMin - Clearance < b.YMax && b.YMin - Clearance < a.YMax;
    }

    /// <summary>The island cell (cx, cy, cz) would hold, before checking it against bigger classes.</summary>
    private static bool TryPlace(ulong seed, IslandClass c, int cx, int cy, int cz, out IslandDef island)
    {
        island = default;
        var def = Classes[(int)c];
        var rng = new SplitMix64Rng(HashCell(seed, (int)c, cx, cy, cz));
        float roll = rng.NextFloat01();
        if (roll >= MathF.Max(def.ChanceArchipelago, def.ChanceNear)) return false; // cheap reject: no odds reach it

        float midX = (cx + 0.5f) * def.CellSize, midZ = (cz + 0.5f) * def.CellSize;
        float chance = Lerp(def.ChanceOpen, def.ChanceArchipelago, Archipelago(seed, midX, midZ));
        var (near, parentY) = c == IslandClass.Large ? (0f, 0f) : Near(seed, c, cx, cz);
        chance = MathF.Max(chance, def.ChanceNear * near);
        if (roll >= chance) return false;

        // Mostly small for the class; bigger near a parent.
        float radius   = Lerp(def.MinRadius, def.MaxRadius, MathF.Pow(rng.NextFloat01(), Lerp(2f, 1f, near)));
        float stretchA = rng.NextRange(0.75f, 1.35f);
        float stretchB = rng.NextRange(0.75f, 1.35f);
        island = new IslandDef
        {
            Class        = c,
            Radius       = radius,
            RotationRad  = rng.NextFloat01() * MathF.Tau,
            StretchMajor = stretchA,
            StretchMinor = stretchB,
            NoiseOffsetX = rng.NextFloat01() * 100000f,
            NoiseOffsetZ = rng.NextFloat01() * 100000f,
        };
        Shape(ref island, rng.NextFloat01());

        // Anywhere in the cell that keeps the whole island inside it.
        float reach = island.Reach;
        island.CenterX = cx * (float)def.CellSize + rng.NextRange(reach, def.CellSize - reach);
        island.CenterZ = cz * (float)def.CellSize + rng.NextRange(reach, def.CellSize - reach);

        // Within the class's spread of the stratum, or of the parent's height. Every vertical cell of a column rolls its
        // own height and keeps the island only if that falls inside it, so a column's islands spread evenly over the
        // span and never fill the band.
        float target = Lerp(Stratum(seed, midX, midZ), parentY, near);
        float y = target + rng.NextRange(-def.HeightSpread, def.HeightSpread);
        if (c == IslandClass.Large) y = Math.Clamp(y, LargeMinBaseY, LargeMaxBaseY);
        float cellBottom = WorldBottom + cy * (float)def.CellHeight;
        float below = island.Lip + island.Depth + island.Bump + 1f;
        float above = island.YMax - island.BaseY;
        if (y < cellBottom + below || y > cellBottom + def.CellHeight - above) return false;
        island.BaseY = y;
        return true;
    }

    /// <summary>0 in open sky up to 1 in an archipelago, varying over tens of km.</summary>
    private static float Archipelago(ulong seed, float x, float z)
    {
        float v = 0.7f * ValueNoise(seed ^ 0xA5C1u, x / ArchipelagoSpacing, z / ArchipelagoSpacing)
                + 0.3f * ValueNoise(seed ^ 0x5A1Cu, x / ArchipelagoDetail, z / ArchipelagoDetail);
        return Smoothstep(0.35f, 0.65f, v);
    }

    /// <summary>The height islands gather around at (x, z), away from any parent.</summary>
    private static float Stratum(ulong seed, float x, float z)
    {
        float v = 0.75f * ValueNoise(seed ^ 0x57A7u, x / StratumSpacing, z / StratumSpacing)
                + 0.25f * ValueNoise(seed ^ 0x7A75u, x / StratumDetail, z / StratumDetail);
        return Lerp(StratumLow, StratumHigh, v);
    }

    /// <summary>How strongly a bigger island draws class <paramref name="c"/>'s islands into column (cx, cz) of its grid
    /// (0-1, by the cell centre's distance past the parent's rim, relative to the parent's size), and that parent's
    /// height. The strongest of every bigger class's islands around; cached per column, since each of its vertical
    /// cells asks.</summary>
    private static (float Near, float Y) Near(ulong seed, IslandClass c, int cx, int cz)
    {
        var key = (seed, (int)c, cx, cz);
        if (NearCache.TryGetValue(key, out var cached)) return cached;

        var def = Classes[(int)c];
        float x = (cx + 0.5f) * def.CellSize, z = (cz + 0.5f) * def.CellSize;
        float best = 0f, bestY = 0f;
        for (int p = 0; p < (int)c; p++)
        {
            var pd = Classes[p];
            float search = pd.MaxRadius * (1.4f * 1.35f + FarRim) + FarRimExtra; // a parent's reach plus its pull
            int x0 = FloorDiv(x - search, pd.CellSize), x1 = FloorDiv(x + search, pd.CellSize);
            int z0 = FloorDiv(z - search, pd.CellSize), z1 = FloorDiv(z + search, pd.CellSize);
            for (int pz = z0; pz <= z1; pz++)
            for (int py = 0; py < Layers((IslandClass)p); py++)
            for (int px = x0; px <= x1; px++)
            {
                if (!Placed(seed, (IslandClass)p, px, py, pz, out var parent)) continue;
                float r = parent.Radius * 0.5f * (parent.StretchMajor + parent.StretchMinor);
                float dx = x - parent.CenterX, dz = z - parent.CenterZ;
                float past = MathF.Sqrt(dx * dx + dz * dz) - r;
                float near = 1f - Smoothstep(NearRim * parent.Radius, FarRim * parent.Radius + FarRimExtra, past);
                if (near > best) { best = near; bestY = parent.BaseY; }
            }
        }
        if (NearCache.Count > CacheLimit) NearCache.Clear();
        NearCache[key] = (best, bestY);
        return (best, bestY);
    }

    // Placements of the classes that are parents (every class but tiny), and each grid column's pull: every smaller cell
    // that might hold an island asks for the few around it. Cleared when they grow past CacheLimit (a long flight).
    private const int CacheLimit = 1 << 20;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, int, int, int, int), (bool, IslandDef)> PlacedCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, int, int, int), (float, float)> NearCache = new();

    /// <summary><see cref="TryPlace"/>, cached for the parent classes.</summary>
    private static bool Placed(ulong seed, IslandClass c, int cx, int cy, int cz, out IslandDef island)
    {
        if (c == IslandClass.Tiny) return TryPlace(seed, c, cx, cy, cz, out island);
        var key = (seed, (int)c, cx, cy, cz);
        if (!PlacedCache.TryGetValue(key, out var v))
        {
            v = (TryPlace(seed, c, cx, cy, cz, out var d), d);
            if (PlacedCache.Count > CacheLimit) PlacedCache.Clear();
            PlacedCache[key] = v;
        }
        island = v.Item2;
        return v.Item1;
    }

    private static float ValueNoise(ulong seed, float x, float z)
    {
        float fx = MathF.Floor(x), fz = MathF.Floor(z);
        int ix = (int)fx, iz = (int)fz;
        float tx = Smoothstep(0f, 1f, x - fx), tz = Smoothstep(0f, 1f, z - fz);
        float a = Hash01(seed, ix, iz), b = Hash01(seed, ix + 1, iz);
        float c = Hash01(seed, ix, iz + 1), d = Hash01(seed, ix + 1, iz + 1);
        return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), tz);
    }

    private static float Hash01(ulong seed, int x, int z) => (HashCell(seed, 7, x, 0, z) >> 40) * (1f / (1 << 24));

    /// <summary>The lens: about 5:1 across to thick for medium and large islands, stubbier (towards 2:1) for small
    /// and tiny ones so they read as rocks rather than flakes. Mountains grow with the island: real ranges on the
    /// large ones, none on the tiny ones.</summary>
    private static void Shape(ref IslandDef island, float roll)
    {
        float r = island.Radius;
        float ratio = Lerp(2f, 5f, Smoothstep(10f, 150f, r));
        float thickness = 2f * r / ratio;
        island.Lip    = Math.Clamp(thickness * 0.12f, 1.5f, 30f);
        island.Crown  = thickness * 0.08f;
        island.Depth  = thickness - 2f * island.Lip - island.Crown;
        island.Bump   = Math.Clamp(thickness * 0.04f, 0.5f, 8f);
        island.MountainMax = island.Class == IslandClass.Tiny ? 0f : Math.Clamp(r * 0.22f, 0f, 240f) * (0.6f + 0.4f * roll);
    }

    private static int FloorDiv(float v, int size) => (int)MathF.Floor(v / size);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static ulong HashCell(ulong worldSeed, int cls, int cellX, int cellY, int cellZ)
    {
        ulong h = worldSeed ^ ((ulong)(uint)cls * 0xD6E8FEB86659FD93UL);
        h ^= (ulong)(uint)cellX * 0x9E3779B97F4A7C15UL;
        h = Mix(h);
        h ^= (ulong)(uint)cellY * 0x94D049BB133111EBUL;
        h = Mix(h);
        h ^= (ulong)(uint)cellZ * 0xC2B2AE3D27D4EB4FUL;
        h = Mix(h);
        return h;
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

/// <summary>Minimal SplitMix64 PRNG for deterministic discrete draws (counts, positions, sizes).</summary>
public struct SplitMix64Rng
{
    private ulong _state;

    public SplitMix64Rng(ulong seed) => _state = seed;

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// Uniform float in [0, 1) with 24 bits of precision.
    public float NextFloat01() => (NextUInt64() >> 40) * (1f / (1 << 24));

    public float NextRange(float min, float max) => min + (max - min) * NextFloat01();
}
