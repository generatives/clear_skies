using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Generation;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Generates a floating-island sky world: islands in four size classes, from rocks tens of blocks across to large
/// islands about 2 km across with real mountain ranges, placed at many heights by <see cref="IslandGrid"/>. Each is a
/// rounded lens (see <see cref="IslandDef"/>) with an irregular, non-circular footprint and a cragged underside,
/// carrying plains/mountains/beaches/lakes on top.
///
/// Generation is per-column (2.5D per island): for a given (x,z), each island reaching the column fills its own
/// span, a pure function of that island's shape parameters plus a handful of independent noise fields sampled at
/// world coordinates offset per-island for decorrelation. Islands never overlap, so the spans never collide.
/// </summary>
public sealed class SkyWorldGenerator : IWorldGenerator
{
    private const int S = ChunkData.Size;

    /// <summary>Room for every island a column can reach: the cells a column overlaps, at most 2 × 2 per class
    /// horizontally times each class's layers (1 + 2 + 4 + 16).</summary>
    private const int MaxIslands = 96;

    public ulong Seed => _seed;

    private readonly ulong _seed;

    private readonly FastNoiseLite _heightNoise; // rolling "plains" surface field
    private readonly FastNoiseLite _ridgeNoise;  // ridged "mountains" surface field
    private readonly FastNoiseLite _maskNoise;   // broad low-freq patches gating where mountains occur
    private readonly FastNoiseLite _lakeNoise;   // lake placement field
    private readonly FastNoiseLite _edgeNoise;   // coastline radius wobble
    private readonly FastNoiseLite _warpNoise;   // domain warp for non-circular footprints
    private readonly FastNoiseLite _bumpNoise;   // small-scale underside crags

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

        // Higher frequency than the original 0.008 (shorter wavelength) keeps any single "above
        // threshold" patch of this field small — the old low frequency could stay elevated across huge
        // contiguous stretches, producing lakes that just kept going. 0.014 (down from 0.02, ~1.4x
        // longer wavelength) roughly doubles typical lake area (area scales with wavelength squared)
        // while keeping that same bound in place.
        _lakeNoise = new FastNoiseLite(unchecked((int)(seed + 4)));
        _lakeNoise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        _lakeNoise.SetFractalType(FastNoiseLite.FractalType.FBm);
        _lakeNoise.SetFractalOctaves(4);
        _lakeNoise.SetFrequency(0.014f);

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
        int originY = (int)origin.Y;
        var profile = Profile(pos.X, pos.Z);

        // Islands never overlap (see IslandGrid), so a column can hold several at different heights: fill them all.
        for (int i = 0; i < profile.Count; i++)
        {
            ref readonly var island = ref profile.Islands[i];
            if (originY + S < island.YMin || originY > island.YMax) continue;
            var spans = profile.Spans(i, this);
            for (int lz = 0; lz < S; lz++)
            for (int lx = 0; lx < S; lx++)
                FillSpan(data, lx, lz, originY, in spans[lx + S * lz]);
        }
    }

    /// <summary>What one island puts in one block column: nothing, land from Bottom to Top, or a lake (land up to
    /// Floor, water up to Top).</summary>
    private struct ColumnSpan
    {
        public byte Kind; // 0 none, 1 land, 2 lake
        public BlockId TopBlock;
        public float Bottom, Top, Floor;
    }

    /// <summary>The islands reaching one chunk column and, computed on first use, each one's spans over its 32 × 32 block
    /// columns. Streaming generates a column's chunks one after another on one thread, so the noise behind an island's
    /// shape is evaluated once per column rather than once per chunk of it.</summary>
    private sealed class ColumnProfile
    {
        public int X = int.MinValue, Z = int.MinValue;
        public int Count;
        public readonly IslandDef[] Islands = new IslandDef[MaxIslands];
        private readonly ColumnSpan[]?[] _spans = new ColumnSpan[MaxIslands][];
        private readonly bool[] _computed = new bool[MaxIslands];

        public void Reset(int x, int z, int count)
        {
            X = x; Z = z; Count = count;
            Array.Clear(_computed);
        }

        public ColumnSpan[] Spans(int i, SkyWorldGenerator gen)
        {
            var spans = _spans[i] ??= new ColumnSpan[S * S];
            if (_computed[i]) return spans;
            _computed[i] = true;
            for (int lz = 0; lz < S; lz++)
            for (int lx = 0; lx < S; lx++)
                spans[lx + S * lz] = gen.ShapeColumn(in Islands[i], X * S + lx, Z * S + lz);
            return spans;
        }
    }

    private readonly ColumnProfile _profile = new();

    private ColumnProfile Profile(int chunkX, int chunkZ)
    {
        if (_profile.X == chunkX && _profile.Z == chunkZ) return _profile;
        int originX = chunkX * S, originZ = chunkZ * S;
        int count = CollectIslands(originX, IslandGrid.WorldBottom, originZ, originX + S, IslandGrid.WorldTop - 1, originZ + S,
                                   _profile.Islands);
        int kept = 0;
        for (int i = 0; i < count; i++)
            if (ReachesColumn(_profile.Islands[i], originX, originZ)) _profile.Islands[kept++] = _profile.Islands[i];
        _profile.Reset(chunkX, chunkZ, kept);
        return _profile;
    }

    private static void FillSpan(ChunkData data, int lx, int lz, int originY, in ColumnSpan span)
    {
        if (span.Kind == 1) FillLandColumn(data, lx, lz, originY, span.Bottom, span.Top, span.TopBlock);
        else if (span.Kind == 2) FillLakeColumn(data, lx, lz, originY, span.Bottom, span.Floor, span.Top);
    }

    public ulong ColumnLayers(int chunkX, int chunkZ, int minChunkY)
    {
        int originX = chunkX * S, originZ = chunkZ * S;
        Span<IslandDef> islands = stackalloc IslandDef[MaxIslands];
        int count = CollectIslands(originX, IslandGrid.WorldBottom, originZ, originX + S, IslandGrid.WorldTop - 1, originZ + S, islands);
        ulong bits = 0;
        for (int i = 0; i < count; i++)
        {
            if (!ReachesColumn(islands[i], originX, originZ)) continue;
            int lo = System.Math.Max((int)MathF.Floor(islands[i].YMin / S) - minChunkY, 0);
            int hi = System.Math.Min((int)MathF.Floor(islands[i].YMax / S) - minChunkY, 63);
            if (lo > hi) continue;
            bits |= (ulong.MaxValue >> (63 - hi)) & (ulong.MaxValue << lo);
        }
        return bits;
    }

    /// <summary>Every class's islands in the grid cells overlapping the box.</summary>
    private int CollectIslands(float minX, float minY, float minZ, float maxX, float maxY, float maxZ, Span<IslandDef> output)
    {
        int count = 0;
        for (int c = 0; c < IslandGrid.ClassCount; c++)
            count = IslandGrid.Collect(_seed, (IslandClass)c, minX, minY, minZ, maxX, maxY, maxZ, output, count);
        return count;
    }

    /// <summary>Whether <paramref name="island"/> can reach the chunk column at (originX, originZ). Bounds only — no
    /// noise — so it's cheap enough to test every island against every chunk before doing per-column work.</summary>
    private static bool ReachesColumn(in IslandDef island, int originX, int originZ)
    {
        float reach = island.Reach;
        float dxMin = MathF.Max(0f, MathF.Abs(island.CenterX - (originX + S * 0.5f)) - S * 0.5f);
        float dzMin = MathF.Max(0f, MathF.Abs(island.CenterZ - (originZ + S * 0.5f)) - S * 0.5f);
        return dxMin * dxMin + dzMin * dzMin <= reach * reach;
    }

    // ── Per-column shaping ──────────────────────────────────────────────────────

    private ColumnSpan ShapeColumn(in IslandDef island, float wx, float wz)
    {
        // Cheap raw-distance pre-check before paying for a domain-warp noise sample: even with warp
        // and anisotropic stretch, nothing beyond ~2x the nominal radius can end up inside.
        float rawDx = wx - island.CenterX;
        float rawDz = wz - island.CenterZ;
        float rejectR = island.Radius * 2f;
        if (rawDx * rawDx + rawDz * rawDz > rejectR * rejectR) return default;

        float offX = island.NoiseOffsetX;
        float offZ = island.NoiseOffsetZ;

        // The footprint's warp and coastline wobble are sampled at coordinates scaled down on big islands, so their
        // features grow with the island instead of turning a big coast into fine zig-zags.
        float fs = MathF.Min(1f, 300f / island.Radius);

        // Domain-warp the sampling position (not the geometric one) so the displacement itself is
        // decorrelated per island, then apply that displacement to the true world position — this
        // keeps the warp a pure perturbation of geometry rather than an accidental extra offset.
        float sampleX = (wx + offX) * fs;
        float sampleZ = (wz + offZ) * fs;
        float warpedX = sampleX;
        float warpedZ = sampleZ;
        _warpNoise.SetDomainWarpAmp(island.Radius * 0.25f * fs);
        _warpNoise.DomainWarp(ref warpedX, ref warpedZ);

        float px = wx + (warpedX - sampleX) / fs;
        float pz = wz + (warpedZ - sampleZ) / fs;

        // Rotate + anisotropically stretch around the island center so footprints read as elongated,
        // rotated blobs rather than circles.
        float dx = px - island.CenterX;
        float dz = pz - island.CenterZ;
        float cosR = MathF.Cos(-island.RotationRad);
        float sinR = MathF.Sin(-island.RotationRad);
        float rx = (dx * cosR - dz * sinR) / island.StretchMajor;
        float rz = (dx * sinR + dz * cosR) / island.StretchMinor;

        float edge = _edgeNoise.GetNoise(sampleX, sampleZ); // [-1,1] coastline wobble
        float effectiveR = island.Radius * (0.80f + 0.20f * edge);
        if (effectiveR <= 1f) return default;

        float dist = MathF.Sqrt(rx * rx + rz * rz);
        float t = dist / effectiveR;
        if (t >= 1f) return default;

        // ── The lens. e is blocks in from the rim. The top of the rim is a rounded lip (a quarter circle of radius
        //    Lip rising from BaseY); the underside a bowl Lip + Depth deep, (1 - t²)^0.75: steep enough right at the
        //    rim to round it off, then tapering in like an inverted hill rather than dropping as a sheer wall. Small
        //    cragged bumps keep the underside from looking mathematical. ──
        float e = (1f - t) * effectiveR;
        float lip = island.Lip;
        float bowl = MathF.Pow(MathF.Max(0f, 1f - t * t), 0.75f);
        float bump = _bumpNoise.GetNoise(wx + offX, wz + offZ); // [-1,1]
        float bottomY = island.BaseY - (lip + island.Depth) * bowl + bump * island.Bump * bowl;

        float lipTop = e < lip ? MathF.Sqrt(MathF.Max(0f, lip * lip - (lip - e) * (lip - e))) : lip;
        float inland = Saturate((e - lip) / MathF.Max(effectiveR - lip, 1f)); // 0 at the lip, 1 at the centre
        float crown = island.Crown * MathF.Sin(MathF.Min(inland * 2f, 1f) * MathF.PI * 0.5f);
        float groundY = island.BaseY + lipTop + crown; // the smooth, noise-free ground

        // ── Top: mountains mostly inside the footprint, gated by a broad patch mask; plains roll gently everywhere
        //    else, fading in over the first blocks past the lip so the rim stays round. ──
        float mountainReach = MathF.Max(0f, (0.72f - t) / 0.72f);
        float maskField = _maskNoise.GetNoise(wx + offX, wz + offZ) * 0.5f + 0.5f;
        float mountainFactor = Smoothstep(0.30f, 0.55f, maskField);
        float mtn = island.MountainMax > 0f ? mountainReach * mountainFactor : 0f;

        float landFade = Smoothstep(0f, 24f, e - lip);
        float rolling = _heightNoise.GetNoise(wx + offX, wz + offZ);
        float ridged = _ridgeNoise.GetNoise(wx + offX, wz + offZ);
        float terrain = Lerp(rolling, ridged, mtn);
        float amplitude = Lerp(IslandDef.PlainsAmplitude, MathF.Max(island.MountainMax, IslandDef.PlainsAmplitude), mtn);

        float topY = groundY + terrain * amplitude * landFade;
        if (topY - bottomY < 2f) return default; // a sliver at the very rim

        // ── Lakes: independent low-frequency field, interior only, away from rims and mountains. The
        // water surface is flat (not following the noisy topY — an undulating lake just reads as sloped
        // ground with a blue tint), but its level tracks the LOCAL smooth ground (groundY) minus a fixed margin,
        // rather than one constant per island — a single global level looked flush with the ground wherever the
        // local plains happened to already sit near it. The margin just needs to clear the ordinary noise wobble so
        // a lake still reads as recessed at typical low dips, not a full basin.
        //
        // lakeBlend is continuous (not a hard in/out boolean): as lakeField rises through the shore
        // band, the land height eases down toward lakeLevel — a gentle bank leading down to the water —
        // rather than jumping straight from full terrain height to flat water at a single threshold
        // (which read as a sheer cliff around every lake). Once the blended height actually reaches
        // lakeLevel the column is committed to being water, with a carved floor beneath it.
        float lakeField = _lakeNoise.GetNoise(wx + offX, wz + offZ) * 0.5f + 0.5f;
        float lakeLevel = groundY - 3f;

        float lakeBlend = Smoothstep(0.60f, 0.78f, lakeField);
        lakeBlend *= Smoothstep(lip + 20f, lip + 60f, e);   // fade out near the rim
        lakeBlend *= 1f - Smoothstep(0.25f, 0.35f, mtn);     // fade out approaching mountains

        if (lakeBlend > 0f && topY > lakeLevel)
        {
            float blendedTopY = Lerp(topY, lakeLevel, lakeBlend);
            if (blendedTopY <= lakeLevel + 0.5f)
            {
                float lakeCarve = Lerp(2f, 14f, Saturate((lakeField - 0.78f) / 0.22f));
                float lakeFloorY = MathF.Max(bottomY + 1f, lakeLevel - lakeCarve);
                return new ColumnSpan { Kind = 2, Bottom = bottomY, Floor = lakeFloorY, Top = lakeLevel };
            }

            return new ColumnSpan { Kind = 1, Bottom = bottomY, Top = blendedTopY,
                                    TopBlock = PickTopBlock(e, blendedTopY - groundY, island, mtn) };
        }

        return new ColumnSpan { Kind = 1, Bottom = bottomY, Top = topY, TopBlock = PickTopBlock(e, topY - groundY, island, mtn) };
    }

    /// <summary>The top block: a sand beach just in from the rim (not on tiny islands), rock and then snow up the
    /// mountains, by <paramref name="height"/> above the smooth ground as a fraction of the island's mountains, else
    /// grass.</summary>
    private static BlockId PickTopBlock(float e, float height, in IslandDef island, float mtn)
    {
        float beachMask = island.Class == IslandClass.Tiny ? 0f : 1f - Smoothstep(island.Lip + 4f, island.Lip + 14f, e);
        float m = MathF.Max(island.MountainMax, 1f);
        float snowMask = Smoothstep(0.28f * m, 0.45f * m, height) * mtn;
        float rockMask = Smoothstep(0.12f * m, 0.24f * m, height) * mtn;
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
