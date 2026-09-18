using System;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Parameters for one floating island, resolved deterministically from its region cell.
/// All fields are in world space / world-Y except the shaping parameters (PlateauT, rotation,
/// stretch), which are dimensionless and consumed by <see cref="SkyWorldGenerator"/>'s per-column
/// shape math.
/// </summary>
public struct IslandDef
{
    public float CenterX;
    public float CenterZ;
    public float Radius;
    public float DomeDepth;
    public float BaseY;

    /// Normalized radial fraction (0..1) up to which the island's underside stays a flat/near-full-depth
    /// plateau before rounding down to the rim. Larger for bigger islands.
    public float PlateauT;

    /// Per-island rotation + anisotropic stretch, applied to the (warped) sample offset before computing
    /// radial distance, so footprints read as elongated/rotated blobs rather than circles.
    public float RotationRad;
    public float StretchMajor;
    public float StretchMinor;

    /// Decorrelates this island's noise sampling from every other island sharing the same noise fields.
    public float NoiseOffsetX;
    public float NoiseOffsetZ;
}

/// <summary>
/// Deterministic region-cell -> island-cluster placement. The world is partitioned into
/// <see cref="CellSize"/>-block cells; each cell independently (and reproducibly, given the world seed)
/// decides whether it holds an island cluster, and if so, a main island plus 0-3 smaller satellites.
///
/// Every island (and its noise-perturbed footprint) is kept within <see cref="PlacementMargin"/> of the
/// cell's interior, so a cluster can never cross into a neighbouring cell. That guarantee is what lets
/// <see cref="SkyWorldGenerator"/> resolve a chunk's islands by looking at its own cell alone, with no
/// neighbour-cell checks.
/// </summary>
public static class RegionGrid
{
    public const int CellShift = 12;
    public const int CellSize = 1 << CellShift; // 4096 blocks

    private const float IslandChance = 0.55f;
    private const float BaseIslandY = 150f;

    // Main island max reach (radius*edge-multiplier) union satellite max reach (dist + radius*edge-multiplier)
    // from the main island's center, plus a safety buffer. See plan for the derivation.
    private const float PlacementMargin = 1450f;

    /// <summary>
    /// Resolves the island cluster (if any) for the cell containing world position (wx, wz).
    /// Returns the number of islands written into <paramref name="outIslands"/> (0-4, main island first).
    /// </summary>
    public static int ResolveIslands(ulong worldSeed, int wx, int wz, Span<IslandDef> outIslands)
    {
        // Arithmetic right-shift is floor-division by a power of two, correct for negative
        // coordinates too (two's-complement sign extension), so no branching floor-div needed.
        int cellX = wx >> CellShift;
        int cellZ = wz >> CellShift;
        return ResolveIslandsForCell(worldSeed, cellX, cellZ, outIslands);
    }

    public static int ResolveIslandsForCell(ulong worldSeed, int cellX, int cellZ, Span<IslandDef> outIslands)
    {
        var rng = new SplitMix64Rng(HashCell(worldSeed, cellX, cellZ));

        // 1. Does this cell have an island cluster at all?
        if (rng.NextFloat01() >= IslandChance)
            return 0;

        float cellOriginX = cellX * (float)CellSize;
        float cellOriginZ = cellZ * (float)CellSize;

        // 2-6. Main island.
        float mainRadius  = rng.NextRange(350f, 650f);
        float mainDepth   = rng.NextRange(28f, 50f); // kept modest so terrain amplitude (mountains) dominates the island's height, not the core
        float mainYJitter = rng.NextRange(-30f, 30f);
        float mainLocalX  = rng.NextRange(PlacementMargin, CellSize - PlacementMargin);
        float mainLocalZ  = rng.NextRange(PlacementMargin, CellSize - PlacementMargin);
        float mainOffX    = rng.NextFloat01() * 100000f;
        float mainOffZ    = rng.NextFloat01() * 100000f;
        float mainPlateau = Lerp(0.05f, rng.NextRange(0.30f, 0.55f), SizeBias(mainRadius));
        float mainRotate  = rng.NextFloat01() * MathF.Tau;
        float mainStretchA = rng.NextRange(0.75f, 1.35f);
        float mainStretchB = rng.NextRange(0.75f, 1.35f);

        int count = 0;
        outIslands[count++] = new IslandDef
        {
            CenterX = cellOriginX + mainLocalX,
            CenterZ = cellOriginZ + mainLocalZ,
            Radius = mainRadius,
            DomeDepth = mainDepth,
            BaseY = BaseIslandY + mainYJitter,
            PlateauT = mainPlateau,
            RotationRad = mainRotate,
            StretchMajor = mainStretchA,
            StretchMinor = mainStretchB,
            NoiseOffsetX = mainOffX,
            NoiseOffsetZ = mainOffZ,
        };

        // 7. Satellite count: squared roll biases toward fewer (0 most common, 3 rare).
        float satRoll = rng.NextFloat01();
        int satelliteCount = (int)(satRoll * satRoll * 4f);

        // 8. Satellites, in draw order.
        for (int i = 0; i < satelliteCount && count < outIslands.Length; i++)
        {
            float angle     = rng.NextFloat01() * MathF.Tau;
            float dist      = rng.NextRange(mainRadius + 150f, mainRadius + 500f);
            float satRadius = rng.NextRange(40f, 160f);
            float satDepth  = rng.NextRange(8f, 20f);
            float satYJit   = rng.NextRange(-20f, 20f);
            float satOffX   = rng.NextFloat01() * 100000f;
            float satOffZ   = rng.NextFloat01() * 100000f;
            float satPlateau = Lerp(0.05f, rng.NextRange(0.30f, 0.55f), SizeBias(satRadius));
            float satRotate  = rng.NextFloat01() * MathF.Tau;
            float satStretchA = rng.NextRange(0.75f, 1.35f);
            float satStretchB = rng.NextRange(0.75f, 1.35f);

            float satLocalX = mainLocalX + MathF.Cos(angle) * dist;
            float satLocalZ = mainLocalZ + MathF.Sin(angle) * dist;

            // Safety belt: should rarely trigger given the margin math above, but keeps satellites
            // inside the cell interior even in edge cases.
            float satMargin = satRadius * 1.3f + 50f;
            satLocalX = Math.Clamp(satLocalX, satMargin, CellSize - satMargin);
            satLocalZ = Math.Clamp(satLocalZ, satMargin, CellSize - satMargin);

            outIslands[count++] = new IslandDef
            {
                CenterX = cellOriginX + satLocalX,
                CenterZ = cellOriginZ + satLocalZ,
                Radius = satRadius,
                DomeDepth = satDepth,
                BaseY = BaseIslandY + mainYJitter + satYJit,
                PlateauT = satPlateau,
                RotationRad = satRotate,
                StretchMajor = satStretchA,
                StretchMinor = satStretchB,
                NoiseOffsetX = satOffX,
                NoiseOffsetZ = satOffZ,
            };
        }

        return count;
    }

    /// 0 at/below a modest radius, ramping to 1 by the largest islands get — used so only genuinely
    /// large islands get a pronounced flat plateau; small ones stay closer to a rounded blob.
    private static float SizeBias(float radius) => Math.Clamp((radius - 200f) / 450f, 0f, 1f);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static ulong HashCell(ulong worldSeed, int cellX, int cellZ)
    {
        ulong h = worldSeed;
        h ^= (ulong)(uint)cellX * 0x9E3779B97F4A7C15UL;
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
