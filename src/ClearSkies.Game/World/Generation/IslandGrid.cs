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
/// Deterministic island placement: one grid of 3D cells per <see cref="IslandClass"/>, each cell independently (and
/// reproducibly, given the world seed) holding at most one island of its class. Big classes have big cells; the
/// large islands' cells are much wider than they are tall (one cell spans the whole world height), the smaller
/// classes stack several cells up the <see cref="WorldBottom"/>..<see cref="WorldTop"/> band, so islands sit at
/// many heights.
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

    private readonly record struct ClassDef(int CellSize, int CellHeight, float Chance, float MinRadius, float MaxRadius,
                                            float MinBaseY, float MaxBaseY);

    private static readonly ClassDef[] Classes =
    {
        //   cell   height  chance  radius        base Y (clamped to the cell)
        new(10240, 2048,   0.50f,  800f, 1100f,   650f, 1150f), // Large: about 2 km across, in a middle band
        new( 2560, 1024,   0.30f,  150f,  350f,   float.MinValue, float.MaxValue), // Medium
        new(  768,  512,   0.15f,   40f,  100f,   float.MinValue, float.MaxValue), // Small
        new(  192,  128,   0.05f,   10f,   25f,   float.MinValue, float.MaxValue), // Tiny
    };

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
                if (TryPlace(seed, (IslandClass)b, bx, by, bz, out var big) && Overlaps(island, big))
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
        if (rng.NextFloat01() >= def.Chance) return false;

        float radius   = rng.NextRange(def.MinRadius, def.MaxRadius);
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

        float cellBottom = WorldBottom + cy * (float)def.CellHeight;
        float below = island.Lip + island.Depth + island.Bump + 1f;
        float above = island.YMax - island.BaseY;
        float lo = Math.Max(cellBottom + below, def.MinBaseY), hi = Math.Min(cellBottom + def.CellHeight - above, def.MaxBaseY);
        island.BaseY = lo <= hi ? rng.NextRange(lo, hi) : cellBottom + below;
        return true;
    }

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
