using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Generation;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Generates a floating-island sky world: large islands (hundreds of blocks across, ~100 tall)
/// with irregular, non-circular footprints and cragged, non-hemispherical rounded undersides —
/// large islands get a flatter plateau in the middle rather than a pointed dome apex — carrying
/// plains/mountains/beaches/lakes on top. Islands are grouped into regions (see <see cref="RegionGrid"/>)
/// so clusters end up low-thousands of blocks apart, with genuinely empty sky between them.
///
/// Generation is per-column (2.5D): for a given (x,z), at most one island "owns" the column (fixed
/// priority order: main island, then satellites in draw order), and everything below is a pure
/// function of that island's shape parameters plus a handful of independent noise fields sampled at
/// world coordinates offset per-island for decorrelation.
/// </summary>
public sealed class SkyWorldGenerator : IWorldGenerator
{
    private const int MaxIslandsPerCell = 4;

    public ulong Seed => _seed;

    private readonly ulong _seed;

    private readonly FastNoiseLite _heightNoise; // rolling "plains" surface field
    private readonly FastNoiseLite _ridgeNoise;  // ridged "mountains" surface field
    private readonly FastNoiseLite _maskNoise;   // broad low-freq patches gating where mountains occur
    private readonly FastNoiseLite _lakeNoise;   // lake placement field
    private readonly FastNoiseLite _edgeNoise;   // coastline radius wobble
    private readonly FastNoiseLite _warpNoise;   // domain warp for non-circular footprints
    private readonly FastNoiseLite _bumpNoise;   // small-scale underside crags

    // Single-slot cache for RegionGrid cell resolution (see ResolveIslandsCached). Only valid because
    // Generate() is called from a single thread today; parallelizing generation later needs this made
    // thread-local (or dropped) rather than shared.
    private int _cachedCellX = int.MinValue, _cachedCellZ = int.MinValue;
    private int _cachedIslandCount;
    private readonly IslandDef[] _cachedIslands = new IslandDef[MaxIslandsPerCell];

    public SkyWorldGenerator(ulong seed = 1337)
    {
        _seed = seed;

        // Low frequency + few octaves + reduced gain everywhere below: keeps the fine-detail octaves
        // from contributing much, so terrain reads as broad, smooth landforms rather than small
        // 1-2 block pockmarks between adjacent columns.
        _heightNoise = new FastNoiseLite(unchecked((int)(seed + 1)));
        _heightNoise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        _heightNoise.SetFractalType(FastNoiseLite.FractalType.FBm);
        _heightNoise.SetFractalOctaves(3);
        _heightNoise.SetFractalGain(0.4f);
        _heightNoise.SetFrequency(0.005f);

        _ridgeNoise = new FastNoiseLite(unchecked((int)(seed + 2)));
        _ridgeNoise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        _ridgeNoise.SetFractalType(FastNoiseLite.FractalType.Ridged);
        _ridgeNoise.SetFractalOctaves(3);
        _ridgeNoise.SetFractalGain(0.4f);
        _ridgeNoise.SetFrequency(0.005f);

        _maskNoise = new FastNoiseLite(unchecked((int)(seed + 3)));
        _maskNoise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        _maskNoise.SetFractalType(FastNoiseLite.FractalType.FBm);
        _maskNoise.SetFractalOctaves(2);
        _maskNoise.SetFrequency(0.003f);

        _lakeNoise = new FastNoiseLite(unchecked((int)(seed + 4)));
        _lakeNoise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        _lakeNoise.SetFractalType(FastNoiseLite.FractalType.FBm);
        _lakeNoise.SetFractalOctaves(3);
        _lakeNoise.SetFrequency(0.008f);

        _edgeNoise = new FastNoiseLite(unchecked((int)(seed + 5)));
        _edgeNoise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        _edgeNoise.SetFractalType(FastNoiseLite.FractalType.FBm);
        _edgeNoise.SetFractalOctaves(2);
        _edgeNoise.SetFrequency(0.01f);

        _warpNoise = new FastNoiseLite(unchecked((int)(seed + 6)));
        _warpNoise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        _warpNoise.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
        _warpNoise.SetFrequency(0.0025f);

        _bumpNoise = new FastNoiseLite(unchecked((int)(seed + 7)));
        _bumpNoise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        _bumpNoise.SetFractalType(FastNoiseLite.FractalType.FBm);
        _bumpNoise.SetFractalOctaves(3);
        _bumpNoise.SetFrequency(0.05f);
    }

    public void Generate(ChunkData data, ChunkPosition pos)
    {
        var origin = pos.WorldOrigin;
        int originX = (int)origin.X;
        int originY = (int)origin.Y;
        int originZ = (int)origin.Z;

        // Cheap reject #1: resolve this chunk's region cell once. Most of the world has no island
        // cluster in its cell at all — that's the common case, and it costs one hash plus a few PRNG
        // draws, no noise evaluation. Region cells (4096 blocks) are far larger than a chunk (32 blocks),
        // so every chunk in a vertical stack (same X/Z, different Y) and most horizontal neighbours
        // resolve to the same cell — cached below so that repeated work isn't redone per chunk.
        Span<IslandDef> islandBuf = stackalloc IslandDef[MaxIslandsPerCell];
        int centerX = originX + ChunkData.Size / 2;
        int centerZ = originZ + ChunkData.Size / 2;
        int islandCount = ResolveIslandsCached(centerX, centerZ, islandBuf);
        if (islandCount == 0) return;

        // Cheap reject #2: bounding-volume check per island against this chunk's AABB before any
        // per-column work. Rejects chunks in a populated cell that are simply at the wrong altitude
        // or too far horizontally from every island in it.
        Span<int> candidates = stackalloc int[MaxIslandsPerCell];
        int candidateCount = 0;
        for (int i = 0; i < islandCount; i++)
        {
            ref readonly IslandDef island = ref islandBuf[i];
            float maxReach = island.Radius * 1.4f * MathF.Max(island.StretchMajor, island.StretchMinor);
            float islandYMin = island.BaseY - island.DomeDepth - 8f;
            float islandYMax = island.BaseY + 8f + 100f + 8f;

            if (originY + ChunkData.Size < islandYMin || originY > islandYMax) continue;

            float chunkCenterX = originX + ChunkData.Size * 0.5f;
            float chunkCenterZ = originZ + ChunkData.Size * 0.5f;
            float dxMin = MathF.Max(0f, MathF.Abs(island.CenterX - chunkCenterX) - ChunkData.Size * 0.5f);
            float dzMin = MathF.Max(0f, MathF.Abs(island.CenterZ - chunkCenterZ) - ChunkData.Size * 0.5f);
            if (dxMin * dxMin + dzMin * dzMin > maxReach * maxReach) continue;

            candidates[candidateCount++] = i;
        }
        if (candidateCount == 0) return;

        for (int lx = 0; lx < ChunkData.Size; lx++)
        for (int lz = 0; lz < ChunkData.Size; lz++)
        {
            float wx = originX + lx;
            float wz = originZ + lz;

            for (int ci = 0; ci < candidateCount; ci++)
            {
                ref readonly IslandDef island = ref islandBuf[candidates[ci]];
                if (TryFillColumn(data, lx, lz, originY, in island, wx, wz))
                    break; // fixed island priority order — first owner wins
            }
        }
    }

    /// <summary>Resolves the island cluster for the region cell containing world (wx, wz), reusing the
    /// last result when the query falls in the same cell as last time (see <see cref="_cachedCellX"/>).</summary>
    private int ResolveIslandsCached(int wx, int wz, Span<IslandDef> outIslands)
    {
        int cellX = wx >> RegionGrid.CellShift;
        int cellZ = wz >> RegionGrid.CellShift;

        if (cellX != _cachedCellX || cellZ != _cachedCellZ)
        {
            _cachedCellX = cellX;
            _cachedCellZ = cellZ;
            _cachedIslandCount = RegionGrid.ResolveIslandsForCell(_seed, cellX, cellZ, _cachedIslands);
        }

        for (int i = 0; i < _cachedIslandCount; i++)
            outIslands[i] = _cachedIslands[i];
        return _cachedIslandCount;
    }

    // ── Per-column shaping ──────────────────────────────────────────────────────

    private bool TryFillColumn(ChunkData data, int lx, int lz, int originY, in IslandDef island, float wx, float wz)
    {
        // Cheap raw-distance pre-check before paying for a domain-warp noise sample: even with warp
        // and anisotropic stretch, nothing beyond ~2x the nominal radius can end up inside.
        float rawDx = wx - island.CenterX;
        float rawDz = wz - island.CenterZ;
        float rejectR = island.Radius * 2f;
        if (rawDx * rawDx + rawDz * rawDz > rejectR * rejectR) return false;

        float offX = island.NoiseOffsetX;
        float offZ = island.NoiseOffsetZ;

        // Domain-warp the sampling position (not the geometric one) so the displacement itself is
        // decorrelated per island, then apply that displacement to the true world position — this
        // keeps the warp a pure perturbation of geometry rather than an accidental extra offset.
        float sampleX = wx + offX;
        float sampleZ = wz + offZ;
        float warpedX = sampleX;
        float warpedZ = sampleZ;
        _warpNoise.SetDomainWarpAmp(island.Radius * 0.25f);
        _warpNoise.DomainWarp(ref warpedX, ref warpedZ);

        float px = wx + (warpedX - sampleX);
        float pz = wz + (warpedZ - sampleZ);

        // Rotate + anisotropically stretch around the island center so footprints read as elongated,
        // rotated blobs rather than circles.
        float dx = px - island.CenterX;
        float dz = pz - island.CenterZ;
        float cosR = MathF.Cos(-island.RotationRad);
        float sinR = MathF.Sin(-island.RotationRad);
        float rx = (dx * cosR - dz * sinR) / island.StretchMajor;
        float rz = (dx * sinR + dz * cosR) / island.StretchMinor;

        float edge = _edgeNoise.GetNoise(wx + offX, wz + offZ); // [-1,1] coastline wobble
        float effectiveR = island.Radius * (0.80f + 0.20f * edge);
        if (effectiveR <= 1f) return false;

        float dist = MathF.Sqrt(rx * rx + rz * rz);
        float t = dist / effectiveR;
        if (t >= 1f) return false;

        // ── Bottom: plateau in the middle (flatter for larger islands), rounding to a thin rim,
        //    with small cragged bumps so it never reads as a smooth mathematical dome. ──
        float coreFalloff;
        if (t <= island.PlateauT)
        {
            coreFalloff = 1f;
        }
        else
        {
            float u = (t - island.PlateauT) / (1f - island.PlateauT);
            coreFalloff = MathF.Sqrt(MathF.Max(0f, 1f - u * u));
        }

        float bump = _bumpNoise.GetNoise(wx + offX, wz + offZ); // [-1,1]
        float bottomY = island.BaseY - island.DomeDepth * coreFalloff + bump * (6f * coreFalloff);

        // ── Top: mountains mostly inside the footprint, gated by a broad patch mask; rim always
        //    stays low/flat (reads as beach), regardless of the height/ridge fields. ──
        float mountainReach = MathF.Max(0f, (0.72f - t) / 0.72f);
        mountainReach = MathF.Pow(mountainReach, 1.0f);
        float maskField = _maskNoise.GetNoise(wx + offX, wz + offZ) * 0.5f + 0.5f;
        float mountainFactor = Smoothstep(0.30f, 0.55f, maskField);
        float mtn = mountainReach * mountainFactor;

        float surfaceBase = 8f * (1f - t);
        float rolling = _heightNoise.GetNoise(wx + offX, wz + offZ);
        float ridged = _ridgeNoise.GetNoise(wx + offX, wz + offZ);
        float terrain = Lerp(rolling, ridged, mtn);
        float amplitude = Lerp(4f, 100f, mtn);

        float topY = island.BaseY + surfaceBase + terrain * amplitude;
        if (topY <= bottomY) topY = bottomY + 1f; // guard against extreme parameter combinations

        // ── Lakes: independent low-frequency field, interior only, away from rims and mountains. ──
        float lakeField = _lakeNoise.GetNoise(wx + offX, wz + offZ) * 0.5f + 0.5f;
        bool isLake = lakeField > 0.55f && t < 0.75f && mtn < 0.3f;

        if (isLake)
        {
            // Water surface stays flush with the surrounding rim (topY), not recessed below it — a
            // recessed lip put a solid overhang around every shore, and the renderer's per-fragment
            // light sampler averages nearby *open* neighbour cells: a fragment whose whole local
            // neighbourhood reads solid gets zero light (pure black) regardless of texture.
            float lakeCarve = Lerp(2f, 14f, Saturate((lakeField - 0.62f) / 0.38f));
            float lakeFloorY = topY - lakeCarve;
            float waterSurfaceY = topY;
            FillLakeColumn(data, lx, lz, originY, bottomY, lakeFloorY, waterSurfaceY);
        }
        else
        {
            BlockId topBlock = PickTopBlock(t, topY, island, mtn);
            FillLandColumn(data, lx, lz, originY, bottomY, topY, topBlock);
        }

        return true;
    }

    private static BlockId PickTopBlock(float t, float topY, in IslandDef island, float mtn)
    {
        float beachMask = Smoothstep(0.80f, 0.92f, t);
        float snowMask = Smoothstep(island.BaseY + 28f, island.BaseY + 45f, topY) * mtn;
        float rockMask = Smoothstep(island.BaseY + 12f, island.BaseY + 24f, topY) * mtn;
        float grassMask = MathF.Max(0f, 1f - MathF.Max(beachMask, MathF.Max(snowMask, rockMask)));

        BlockId top = BlockId.Grass;
        float best = grassMask;
        if (beachMask > best) { top = BlockId.Sand; best = beachMask; }
        if (snowMask > best) { top = BlockId.Snow; best = snowMask; }
        if (rockMask > best) { top = BlockId.Rock; }

        return top;
    }

    private static void FillLandColumn(ChunkData data, int lx, int lz, int originY, float bottomY, float topY, BlockId topBlock)
    {
        // Depth is measured from the floored (integer) surface height, not the raw continuous topY:
        // using the continuous value here made the fractional part of topY — which drifts smoothly
        // across gently-sloped terrain — decide Grass vs. Dirt at the surface, producing coherent
        // banding wherever the slope was gentle (plains). Anchoring to floor(topY) makes the topmost
        // solid voxel always depth 0, eliminating that drift.
        float topFloor = MathF.Floor(topY);
        int yStart = Math.Max(0, (int)MathF.Floor(bottomY) - originY);
        int yEnd = Math.Min(ChunkData.Size - 1, (int)topFloor - originY);

        for (int ly = yStart; ly <= yEnd; ly++)
        {
            float depthFromTop = topFloor - (originY + ly);
            BlockId id = topBlock switch
            {
                BlockId.Grass => depthFromTop < 0.5f ? BlockId.Grass : depthFromTop < 2.5f ? BlockId.Dirt : BlockId.Stone,
                BlockId.Sand  => depthFromTop < 2.0f ? BlockId.Sand  : depthFromTop < 3.0f ? BlockId.Dirt : BlockId.Stone,
                BlockId.Snow  => depthFromTop < 3.0f ? BlockId.Snow  : BlockId.Stone,
                BlockId.Rock  => depthFromTop < 2.0f ? BlockId.Rock  : BlockId.Stone,
                _             => BlockId.Stone,
            };
            data.Set(lx, ly, lz, id);
        }
    }

    private static void FillLakeColumn(ChunkData data, int lx, int lz, int originY, float bottomY, float lakeFloorY, float waterSurfaceY)
    {
        float floorFloor = MathF.Floor(lakeFloorY);
        int yStart = Math.Max(0, (int)MathF.Floor(bottomY) - originY);
        int yEnd = Math.Min(ChunkData.Size - 1, (int)MathF.Floor(waterSurfaceY) - originY);

        for (int ly = yStart; ly <= yEnd; ly++)
        {
            float wy = originY + ly;
            BlockId id;
            if (wy > floorFloor)
            {
                id = BlockId.Water;
            }
            else
            {
                // Same floor-anchored depth as FillLandColumn, to avoid sand/dirt/stone banding on the lake bed.
                float depthFromFloor = floorFloor - wy;
                id = depthFromFloor < 1f ? BlockId.Sand : depthFromFloor < 2f ? BlockId.Dirt : BlockId.Stone;
            }
            data.Set(lx, ly, lz, id);
        }
    }

    // ── Small helpers ────────────────────────────────────────────────────────────

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Saturate((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }
}
