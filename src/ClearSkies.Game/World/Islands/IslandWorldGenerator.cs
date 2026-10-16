using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Generates the islands world: the islands <see cref="IslandField"/> places, each shaped on its own.
///
/// An island is a lump of land floating at its base height: its outline is a ragged blob around its centre; its top
/// rises from a low rim at the edge to its relief inland (<see cref="IslandSurface.Hills"/>, and mountains on the
/// landmasses); its underside hangs down deepest under its middle. Cracks run all through it, top to bottom: it is
/// split into shards, like a jigsaw, with a gap between each and the next. Shards near the middle stay level with each
/// other, so the land reads as one across the cracks; towards the edge they sink and tilt away, and a few at the rim
/// are gone, so the edge is broken.
///
/// Per chunk column this is worked out once, block column by block column, as solid spans (runs of solid blocks), then
/// filled in chunk by chunk.
/// </summary>
public sealed class IslandWorldGenerator : IWorldGenerator
{
    private const int S = ChunkData.Size;
    private const int MaxSpans = 32; // per block column

    // Wobble of a column's position before finding its shard, in blocks, so cracks are ragged rather than straight.
    private const float CrackWarp = 18f, CrackWarpFrequency = 0.012f;

    /// <summary>How far a shard's top and bottom are rounded off at a crack, in blocks.</summary>
    private const float Rounding = 6f;

    /// <summary>How far in from the edge (as a share of the radius) an island's top rises to its full relief.</summary>
    private const float RimWidth = 0.3f;

    // Shards sink: up to InnerSink in an island's middle, up to EdgeSink at its edge; past MissingFrom of the way out
    // to the edge, MissingChance of them are gone.
    private const float InnerSink = 3f, EdgeSink = 40f, MissingFrom = 0.12f, MissingChance = 0.35f;

    /// <summary>Solid runs thinner than this are worn away: slivers at an island's rim.</summary>
    private const int MinThickness = 4;

    // A top is bare (see IslandSurface.Block) if land lies over it within ShadeReach blocks.
    private const float Shaded = 0.75f, ShadeReach = 250f;

    private readonly ulong _seed;
    private readonly IslandSurface _surface;
    private readonly FastNoiseLite _edge, _coast, _under, _warpX, _warpZ, _crack;

    public IslandWorldGenerator(ulong seed)
    {
        _seed = seed;
        _surface = IslandSurface.For(seed);
        _edge = Noise(seed + 41, 3, 1f);       // sampled in radii, per island: the outline's big bays and headlands
        _coast = Noise(seed + 42, 3, 0.02f);   // the outline's small wiggles
        _under = Noise(seed + 43, 3, 0.015f);  // lumps on the underside
        _warpX = Noise(seed + 31, 2, CrackWarpFrequency);
        _warpZ = Noise(seed + 33, 2, CrackWarpFrequency);
        _crack = Noise(seed + 34, 2, 0.03f);   // how wide the cracks are
    }

    private static FastNoiseLite Noise(ulong seed, int octaves, float frequency)
    {
        var n = new FastNoiseLite(unchecked((int)seed));
        n.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        n.SetFractalType(FastNoiseLite.FractalType.FBm);
        n.SetFractalOctaves(octaves);
        n.SetFrequency(frequency);
        return n;
    }

    private readonly List<Island> _near = new();

    public ulong ColumnLayers(int chunkX, int chunkZ, int minChunkY)
    {
        _near.Clear();
        IslandField.Near(_seed, chunkX * S, chunkZ * S, chunkX * S + S, chunkZ * S + S, _near);
        if (_near.Count == 0) return 0;
        float top = float.MinValue, bottom = float.MaxValue;
        foreach (var island in _near)
        {
            top = MathF.Max(top, island.TopBound);
            bottom = MathF.Min(bottom, island.BottomBound);
        }
        int lo = System.Math.Max((int)MathF.Floor(MathF.Max(bottom, IslandField.LowestBottom) / S) - minChunkY, 0);
        int hi = System.Math.Min((int)MathF.Floor(MathF.Min(top, IslandField.WorldTop - 1) / S) - minChunkY, 63);
        return lo > hi ? 0 : (ulong.MaxValue >> (63 - hi)) & (ulong.MaxValue << lo);
    }

    // ── Generation ──────────────────────────────────────────────────────────

    /// <summary>One chunk column's solid spans, per block column: computed once and reused for each of the column's
    /// chunks (streaming generates a column's chunks one after another on one thread).</summary>
    private int _profileX = int.MinValue, _profileZ = int.MinValue;
    private readonly (short Lo, short Hi)[] _spans = new (short, short)[S * S * MaxSpans];
    private readonly byte[] _spanCount = new byte[S * S];
    private readonly float[] _strata = new float[S * S];
    private readonly float[] _patch = new float[S * S];
    private readonly float[] _flora = new float[S * S], _kind = new float[S * S], _woods = new float[S * S];
    private readonly float[] _bare = new float[S * S * MaxSpans];

    public void Generate(ChunkData data, ChunkPosition pos)
    {
        Prepare(pos.X, pos.Z);

        int originY = pos.Y * S;
        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            int col = lx + S * lz;
            for (int s = 0; s < _spanCount[col]; s++)
            {
                var (lo, hi) = _spans[col * MaxSpans + s];
                int y0 = System.Math.Max(lo, originY), y1 = System.Math.Min(hi, originY + S - 1);
                for (int y = y0; y <= y1; y++)
                    data.Set(lx, y - originY, lz, IslandSurface.Block(y, hi, _strata[col], _patch[col], _bare[col * MaxSpans + s]));

                // What grows on the span's top, in the open cell above it.
                int above = hi + 1;
                if (above < originY || above >= originY + S) continue;
                if (s + 1 < _spanCount[col] && _spans[col * MaxSpans + s + 1].Lo <= above) continue;
                var ground = IslandSurface.Block(hi, hi, _strata[col], _patch[col], _bare[col * MaxSpans + s]);
                var plant = IslandSurface.Plant(ground, pos.X * S + lx, above, pos.Z * S + lz, _flora[col], _kind[col], _seed);
                if (plant != BlockId.Air) data.Set(lx, above - originY, lz, plant);
            }
        }

        // Trees and cacti, over the ground and plants, on tops with room above them (see Trees).
        for (int lz = Trees.Reach; lz < S - Trees.Reach; lz++)
        for (int lx = Trees.Reach; lx < S - Trees.Reach; lx++)
        {
            int col = lx + S * lz;
            for (int s = 0; s < _spanCount[col]; s++)
            {
                int hi = _spans[col * MaxSpans + s].Hi;
                if (hi + Trees.MaxHeight < originY || hi >= originY + S) continue;
                if (s + 1 < _spanCount[col] && _spans[col * MaxSpans + s + 1].Lo <= hi + Trees.MaxHeight + 1) continue;
                var ground = IslandSurface.Block(hi, hi, _strata[col], _patch[col], _bare[col * MaxSpans + s]);
                int x = pos.X * S + lx, z = pos.Z * S + lz;
                var kind = Trees.At(ground, x, hi, z, _woods[col], _seed);
                if (kind != Trees.Kind.None) Trees.Grow(data, originY, kind, lx, hi, lz, Trees.Hash(x, hi, z, _seed));
            }
        }
    }

    /// <summary>Works out chunk column (chunkX, chunkZ)'s solid spans, if it isn't the one last worked out.</summary>
    public void Prepare(int chunkX, int chunkZ)
    {
        if (chunkX != _profileX || chunkZ != _profileZ) BuildProfile(chunkX, chunkZ);
    }

    /// <summary>The solid spans (bottom up) of block column (lx, lz) of the chunk column last prepared.</summary>
    public ReadOnlySpan<(short Lo, short Hi)> Spans(int lx, int lz)
    {
        int col = lx + S * lz;
        return _spans.AsSpan(col * MaxSpans, _spanCount[col]);
    }

    /// <summary>The highest solid block of block column (x, z), if it has any.</summary>
    public bool TryTopAt(int x, int z, out int top)
    {
        int cx = (int)MathF.Floor(x / (float)S), cz = (int)MathF.Floor(z / (float)S);
        Prepare(cx, cz);
        var spans = Spans(x - cx * S, z - cz * S);
        top = spans.Length > 0 ? spans[^1].Hi : 0;
        return spans.Length > 0;
    }

    /// <summary>A point on solid ground near the middle of the landmass nearest (x, z): world X and Z, and its top.</summary>
    public (int X, int Top, int Z) GroundNear(float x, float z)
    {
        var land = IslandField.NearestLandmass(_seed, x, z);
        for (int ring = 0; ; ring++)
        for (int k = 0; k < System.Math.Max(1, ring * 8); k++)
        {
            float a = k * MathF.Tau / System.Math.Max(1, ring * 8);
            int px = (int)(land.X + ring * 16f * MathF.Cos(a)), pz = (int)(land.Z + ring * 16f * MathF.Sin(a));
            if (TryTopAt(px, pz, out int top)) return (px, top, pz);
        }
    }

    private readonly List<Island> _islands = new();
    private readonly Dictionary<(int Island, int I, int J), float> _shardSink = new();

    private void BuildProfile(int chunkX, int chunkZ)
    {
        _profileX = chunkX; _profileZ = chunkZ;
        float x0 = chunkX * S, z0 = chunkZ * S;
        _islands.Clear();
        _shardSink.Clear();
        IslandField.Near(_seed, x0, z0, x0 + S, z0 + S, _islands);
        Array.Clear(_spanCount);
        if (_islands.Count == 0) return;

        Span<(short Lo, short Hi)> found = stackalloc (short, short)[MaxSpans];
        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            int col = lx + S * lz;
            float x = x0 + lx, z = z0 + lz;
            int count = 0;
            for (int n = 0; n < _islands.Count && count < MaxSpans; n++)
                if (Column(n, x, z, out int lo, out int hi)) found[count++] = ((short)lo, (short)hi);
            if (count == 0) continue;

            // Bottom up, overlapping ones merged.
            found[..count].Sort((a, b) => a.Lo.CompareTo(b.Lo));
            int spans = 0;
            for (int i = 0; i < count; i++)
            {
                if (spans > 0 && found[i].Lo <= _spans[col * MaxSpans + spans - 1].Hi + 1)
                {
                    ref var last = ref _spans[col * MaxSpans + spans - 1];
                    last.Hi = System.Math.Max(last.Hi, found[i].Hi);
                }
                else _spans[col * MaxSpans + spans++] = found[i];
            }
            _spanCount[col] = (byte)spans;

            _strata[col] = _surface.Strata(x, z);
            _patch[col] = _surface.Patch(x, z);
            _flora[col] = _surface.Flora(x, z);
            _woods[col] = _surface.Woods(x, z);
            _kind[col] = _surface.Kind(x, z);
            for (int sp = 0; sp < spans; sp++)
            {
                int hi = _spans[col * MaxSpans + sp].Hi;
                bool shaded = sp + 1 < spans && _spans[col * MaxSpans + sp + 1].Lo - hi < ShadeReach;
                _bare[col * MaxSpans + sp] = shaded ? Shaded : 0f;
            }
        }
    }

    /// <summary>0 at the edge of <paramref name="island"/>'s outline at (x, z), rising to 1 at its middle; under 0
    /// outside it.</summary>
    private float Inland(in Island island, float x, float z)
    {
        float inv = 1f / island.Radius;
        float ox = (island.Id & 0xFFFF) * 0.37f, oz = ((island.Id >> 16) & 0xFFFF) * 0.37f;
        float ux = (x - island.X) * inv * 1.2f, uz = (z - island.Z) * inv * 1.2f;
        float wx = x + IslandField.EdgeWarp * island.Radius * _edge.GetNoise(ux + ox, uz + oz);
        float wz = z + IslandField.EdgeWarp * island.Radius * _edge.GetNoise(ux + oz + 100f, uz + ox);
        float dx = wx - island.X, dz = wz - island.Z;
        return 1f - MathF.Sqrt(dx * dx + dz * dz) * inv + IslandField.CoastWiggle * _coast.GetNoise(x, z) * inv;
    }

    /// <summary>The solid run of island <paramref name="n"/> (of those near the chunk column) in block column (x, z), if
    /// any: inside its outline and not in a crack.</summary>
    private bool Column(int n, float x, float z, out int lo, out int hi)
    {
        lo = hi = 0;
        var island = _islands[n];
        float dx = x - island.X, dz = z - island.Z;
        if (dx * dx + dz * dz > island.Reach * island.Reach) return false;
        float inland = Inland(island, x, z);
        if (inland <= 0f) return false;
        var b = island.Info;

        // Its shard: the nearest of the band's shard points, jittered on a grid, to the column (wobbled), and how far
        // the column is from the nearest crack (halfway to another point).
        float px = x + CrackWarp * _warpX.GetNoise(x, z), pz = z + CrackWarp * _warpZ.GetNoise(x, z);
        int gx = (int)MathF.Floor(px / b.ShardSize), gz = (int)MathF.Floor(pz / b.ShardSize);
        Span<float> sx = stackalloc float[9], sz = stackalloc float[9];
        int best = 0;
        float bestD = float.MaxValue;
        for (int k = 0; k < 9; k++)
        {
            int i = gx + k % 3 - 1, j = gz + k / 3 - 1;
            ShardPoint(island.Band, i, j, b.ShardSize, out sx[k], out sz[k]);
            float d = (sx[k] - px) * (sx[k] - px) + (sz[k] - pz) * (sz[k] - pz);
            if (d < bestD) { bestD = d; best = k; }
        }
        float toCrack = float.MaxValue;
        for (int k = 0; k < 9; k++)
        {
            if (k == best) continue;
            float ex = sx[k] - sx[best], ez = sz[k] - sz[best];
            float d = (sx[k] - px) * (sx[k] - px) + (sz[k] - pz) * (sz[k] - pz);
            toCrack = MathF.Min(toCrack, (d - bestD) / (2f * MathF.Sqrt(ex * ex + ez * ez)));
        }
        float crack = b.Crack * (0.6f + 0.4f * (_crack.GetNoise(x, z) + 1f));
        float fromCrack = toCrack - crack;
        if (fromCrack <= 0f) return false;

        // The shard sinks the further out it is, and near the edge may be gone.
        int si = gx + best % 3 - 1, sj = gz + best / 3 - 1;
        if (!_shardSink.TryGetValue((n, si, sj), out float sink))
        {
            ulong h = IslandField.Mix(island.Id ^ ((ulong)(uint)si * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)sj * 0xC2B2AE3D27D4EB4FUL));
            float r = (h & 0xFFFFFF) / 16777216f, gone = ((h >> 24) & 0xFFFFFF) / 16777216f;
            float at = Inland(island, sx[best], sz[best]); // how far out the shard is
            float edge = 1f - Smoothstep(0.05f, 0.45f, at);
            sink = at < MissingFrom && gone < MissingChance ? float.NaN : r * (InnerSink + EdgeSink * edge);
            _shardSink[(n, si, sj)] = sink;
        }
        if (float.IsNaN(sink)) return false;

        // Top: a low rim rising inland to hills, and to mountains on a landmass's inland parts.
        float interior = Smoothstep(0f, RimWidth, inland);
        float top = island.Base + 3f + b.Hills * (0.5f + 0.5f * _surface.Hills(x, z)) * interior;
        if (b.Mountains > 0f) top += b.Mountains * _surface.Mountains(x, z) * Smoothstep(0.15f, 0.6f, inland);
        // Underside: deepest under the middle, lumpy, and hanging further down under high ground, as roots under
        // mountains.
        float bottom = island.Base - 4f - b.Thickness * MathF.Sqrt(MathF.Min(inland, 1f)) * (0.7f + 0.3f * _under.GetNoise(x, z))
                     - IslandField.Roots * (top - island.Base);
        // Rounded off along the cracks.
        if (fromCrack < Rounding)
        {
            float r = Rounding - fromCrack;
            top -= r * r / (2f * Rounding);
            bottom += r * r / Rounding;
        }
        top -= sink;
        bottom -= sink * 0.8f;
        bottom = MathF.Max(bottom, IslandField.LowestBottom);
        top = MathF.Min(top, IslandField.WorldTop - 1);

        lo = (int)MathF.Ceiling(bottom);
        hi = (int)MathF.Floor(top);
        return hi - lo + 1 >= MinThickness;
    }

    private void ShardPoint(int band, int i, int j, float size, out float x, out float z)
    {
        ulong h = _seed ^ ((ulong)(uint)(band + 500) * 0xD6E8FEB86659FD93UL);
        h ^= (ulong)(uint)i * 0x9E3779B97F4A7C15UL; h = IslandField.Mix(h);
        h ^= (ulong)(uint)j * 0xC2B2AE3D27D4EB4FUL; h = IslandField.Mix(h);
        x = (i + 0.1f + 0.8f * ((h & 0xFFFFFF) / 16777216f)) * size;
        z = (j + 0.1f + 0.8f * (((h >> 24) & 0xFFFFFF) / 16777216f)) * size;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>Clouds lie over the landmasses, thinner over the smaller islands, and clear over the open sky between: land
/// shows from afar as a bank of cloud. Thread-safe: cloud tiles are built on worker threads.</summary>
public sealed class IslandCloudDensity : ICloudDensityMap
{
    private readonly ulong _seed;

    public IslandCloudDensity(ulong seed) => _seed = seed;

    public float Density(float x, float z)
    {
        var near = new List<Island>();
        IslandField.Near(_seed, x, z, x, z, near);
        float density = 0f;
        foreach (var island in near)
        {
            if (island.Band == IslandField.Underworld) continue;
            float d = MathF.Sqrt((x - island.X) * (x - island.X) + (z - island.Z) * (z - island.Z));
            float cover = Math.Clamp((island.Reach - d) / (0.4f * island.Radius), 0f, 1f);
            density = MathF.Max(density, (island.Band == IslandField.Landmass ? 1f : 0.6f) * cover);
        }
        return density;
    }
}
