using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Generates the "island hearts" world: a <see cref="ContinentTerrain"/> broken into pieces by the hearts of
/// <see cref="HeartGrid"/>. Each block of ground belongs to its nearest heart (counting vertical offsets
/// <see cref="HeartGrid.VerticalScale"/> times over, and with the block's position wobbled by noise so piece edges are
/// ragged rather than straight). A block is solid if it is under the terrain surface, above the world's rough bottom,
/// its heart is alive, and it isn't in the crack between its piece and a neighbouring live one. So neighbouring pieces
/// fit together, their tops continue the terrain, and where hearts are dead there are holes.
///
/// How far inside a live piece a point is (its <see cref="DistanceLattice"/>, negative outside) is worked out only at
/// the corners of cells <see cref="DistanceLattice.Cell"/> blocks on a side, and blocks between take it interpolated
/// from those: per chunk column, block column by block column, as solid spans (runs of solid blocks), then filled in
/// chunk by chunk.
/// </summary>
public sealed class HeartWorldGenerator : IWorldGenerator
{
    private const int S = ChunkData.Size;
    private const int MaxSpans = 32; // per block column

    /// <summary>More than the terrain can rise above the highest of a chunk column's corners and centre.</summary>
    private const float TerrainRiseMargin = 96f;

    // Wobble of a block's position before finding its nearest heart, in blocks: across, and up and down.
    private const float WarpAcross = 18f, WarpUp = 6f, WarpFrequency = 0.012f;

    // How far each piece is worn back from its faces, in blocks, so there are cracks twice that wide between
    // neighbouring pieces and small or thin pieces wear away altogether: across (from side faces) and up and down (from
    // top and bottom faces), in the floor and higher up, varied by noise.
    private const float FloorWear = 18f, UpperWear = 24f, FloorWearUp = 13f, UpperWearUp = 18f;

    /// <summary>How far round a piece's edges and corners are worn, in scaled space (see Inside).</summary>
    private const float Rounding = 14f;

    // Wear grows from the floor's to the upper over CrackOver blocks from CrackFrom above the floor's top.
    private const float CrackFrom = -20f, CrackOver = 220f;

    /// <summary>Solid runs thinner than this are worn away: slivers where the terrain surface just grazes a piece.</summary>
    private const int MinThickness = 6;

    // A piece's top is the terrain worn down: where the terrain there stands above the terrain at its heart, only
    // Flatten of the difference is left, and the whole top is lowered by up to MaxDrop (each piece its own amount).
    // So tops are flatter than the land they came from, and neighbours don't line up.
    private const float Flatten = 0.45f, MaxDrop = 30f;

    /// <summary>The top of a piece where the terrain surface is <paramref name="surface"/>, for a heart where it is
    /// <paramref name="heartSurface"/>, worn down by <paramref name="drop"/> (0-1, see <see cref="Heart.Drop"/>).</summary>
    internal static float PieceTop(float surface, float heartSurface, float drop) =>
        surface - MathF.Max(0f, surface - heartSurface) * (1f - Flatten) - MaxDrop * drop;

    // A top is bare (see ContinentTerrain.Block) to Shaded if land lies over it within ShadeReach blocks.
    private const float Shaded = 0.75f, ShadeReach = 250f;

    /// <summary>One lava block in this many glows.</summary>
    private const int LavaGlowOdds = 40;

    /// <summary>How far above <see cref="HeartGrid.LowestBottom"/> the world's rough bottom reaches.</summary>
    private const float BottomRoughness = 30f;

    private readonly ulong _seed;
    private readonly ContinentTerrain _terrain;
    private readonly FastNoiseLite _warpX, _warpY, _warpZ, _crack, _bottom;

    public HeartWorldGenerator(ulong seed)
    {
        _seed = seed;
        _terrain = ContinentTerrain.For(seed);
        _warpX = Noise(seed + 31, WarpFrequency);
        _warpY = Noise(seed + 32, WarpFrequency);
        _warpZ = Noise(seed + 33, WarpFrequency);
        _crack = Noise(seed + 34, 0.03f);
        _bottom = Noise(seed + 35, 0.02f);
    }

    private static FastNoiseLite Noise(ulong seed, float frequency)
    {
        var n = new FastNoiseLite(unchecked((int)seed));
        n.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        n.SetFractalType(FastNoiseLite.FractalType.FBm);
        n.SetFractalOctaves(2);
        n.SetFrequency(frequency);
        return n;
    }

    public ulong ColumnLayers(int chunkX, int chunkZ, int minChunkY)
    {
        // Ground is between the world's bottom and the terrain: bound the terrain over the column (its highest
        // sample, plus more than it can rise between samples).
        float terrainTop = float.MinValue;
        for (int j = 0; j <= 2; j++)
        for (int i = 0; i <= 2; i++)
            terrainTop = MathF.Max(terrainTop, _terrain.Height(chunkX * S + i * S * 0.5f, chunkZ * S + j * S * 0.5f));
        terrainTop = MathF.Min(terrainTop + TerrainRiseMargin, HeartGrid.WorldTop - 1);
        int lo = System.Math.Max((int)MathF.Floor(HeartGrid.LowestBottom / S) - minChunkY, 0);
        int hi = System.Math.Min((int)MathF.Floor(terrainTop / S) - minChunkY, 63);
        return lo > hi ? 0 : (ulong.MaxValue >> (63 - hi)) & (ulong.MaxValue << lo);
    }

    // ── Generation ──────────────────────────────────────────────────────────

    /// <summary>The chunk column last prepared, and the columns prepared lately (see <see cref="ProfileCache"/>): a
    /// generator is used on one thread, and chunks over the same ground are made on the same one.</summary>
    private ColumnProfile? _profile;
    private readonly ProfileCache _profiles = new();

    // While a column is worked out (see BuildProfile), per block column: its solid spans, before they're packed into
    // its ColumnProfile; and the column's lattice, and one block column's distances interpolated from it, per level.
    private readonly (short Lo, short Hi)[] _spans = new (short, short)[S * S * MaxSpans];
    private readonly byte[] _spanCount = new byte[S * S];
    private readonly DistanceLattice _lattice = new();
    private float[] _levels = Array.Empty<float>();

    // The hearts around the chunk column being profiled, per layer: cells from (_cx0, _cy0, _cz0), _nx by _ny by _nz of
    // them, stored from _first as scaled positions (y times VerticalScale), whether each exists and whether it is alive.
    private readonly int[] _cx0 = new int[HeartGrid.Layers.Length], _cy0 = new int[HeartGrid.Layers.Length],
                           _cz0 = new int[HeartGrid.Layers.Length], _nx = new int[HeartGrid.Layers.Length],
                           _ny = new int[HeartGrid.Layers.Length], _nz = new int[HeartGrid.Layers.Length],
                           _first = new int[HeartGrid.Layers.Length];
    private float[] _hx = Array.Empty<float>(), _hy = Array.Empty<float>(), _hz = Array.Empty<float>();
    private bool[] _alive = Array.Empty<bool>(), _exists = Array.Empty<bool>();
    private float[] _hs = Array.Empty<float>(), _drop = Array.Empty<float>(); // terrain height at the heart; its Drop

    public void Generate(ChunkData data, ChunkPosition pos)
    {
        Prepare(pos.X, pos.Z);
        var p = _profile!;

        int originY = pos.Y * S;
        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            int col = lx + S * lz;
            for (int s = 0; s < p.Count(col); s++)
            {
                var (lo, hi) = p.Spans[p.Start[col] + s];
                int y0 = System.Math.Max(lo, originY), y1 = System.Math.Min(hi, originY + S - 1);
                for (int y = y0; y <= y1; y++)
                    data.Set(lx, y - originY, lz, ContinentTerrain.Block(y, hi, p.Strata[col], p.Patch[col], p.Bare[p.Start[col] + s], p.Biome[p.Start[col] + s], p.Dry[p.Start[col] + s]));

                int x = pos.X * S + lx, z = pos.Z * S + lz;
                // Some of a lava pool glows: a lamp each, so only one block in LavaGlowOdds.
                if (hi >= originY && hi < originY + S && data.Get(lx, hi - originY, lz) == BlockId.Lava
                    && Trees.Hash(x, hi, z, _seed ^ 0x1A7A) % LavaGlowOdds == 0)
                    data.Set(lx, hi - originY, lz, BlockId.GlowingLava);

                // Crystals in caves: in a floor under other land, and in a ceiling over another span.
                var crystal = Crystals.For(p.Biome[p.Start[col] + s]);
                if (p.Bare[p.Start[col] + s] == Shaded && hi >= originY && hi < originY + S && Crystals.At(x, hi, z, _seed))
                    data.Set(lx, hi - originY, lz, crystal);
                if (s > 0 && lo - p.Spans[p.Start[col] + s - 1].Hi < ShadeReach && lo >= originY && lo < originY + S && Crystals.At(x, lo, z, _seed))
                    data.Set(lx, lo - originY, lz, crystal);

                // What grows on the span's top, in the open cell above it.
                int above = hi + 1;
                if (above < originY || above >= originY + S) continue;
                if (s + 1 < p.Count(col) && p.Spans[p.Start[col] + s + 1].Lo <= above) continue;
                var ground = ContinentTerrain.Block(hi, hi, p.Strata[col], p.Patch[col], p.Bare[p.Start[col] + s], p.Biome[p.Start[col] + s], p.Dry[p.Start[col] + s]);
                var plant = ContinentTerrain.Plant(ground, pos.X * S + lx, above, pos.Z * S + lz, p.Flora[col], p.Kind[col], _seed);
                if (plant != BlockId.Air) data.Set(lx, above - originY, lz, plant);
            }
        }

        // Trees and cacti, over the ground and plants, on tops with room above them (see Trees).
        for (int lz = Trees.Reach; lz < S - Trees.Reach; lz++)
        for (int lx = Trees.Reach; lx < S - Trees.Reach; lx++)
        {
            int col = lx + S * lz;
            for (int s = 0; s < p.Count(col); s++)
            {
                int hi = p.Spans[p.Start[col] + s].Hi;
                if (hi + Trees.MaxHeight < originY || hi >= originY + S) continue;
                var biome = p.Biome[p.Start[col] + s];
                var ground = ContinentTerrain.Block(hi, hi, p.Strata[col], p.Patch[col], p.Bare[p.Start[col] + s], biome, p.Dry[p.Start[col] + s]);
                int x = pos.X * S + lx, z = pos.Z * S + lz;
                var kind = Trees.At(ground, x, hi, z, p.Woods[col], _seed, biome);
                if (kind == Trees.Kind.None) continue;
                int reach = Trees.ReachOf(kind);
                if (lx < reach || lz < reach || lx >= S - reach || lz >= S - reach) continue;
                if (s + 1 < p.Count(col) && p.Spans[p.Start[col] + s + 1].Lo <= hi + Trees.HeightOf(kind) + 1) continue;
                Trees.Grow(data, originY, kind, lx, hi, lz, Trees.Hash(x, hi, z, _seed));
            }
        }
    }

    /// <summary>Takes chunk column (chunkX, chunkZ)'s solid spans from the profiles worked out lately (see
    /// <see cref="ProfileCache"/>), working them out if they aren't there.</summary>
    public void Prepare(int chunkX, int chunkZ)
    {
        if (_profiles.TryGet(chunkX, chunkZ, out _profile)) return;
        _profile = _profiles.Add(chunkX, chunkZ);
        BuildProfile(_profile, chunkX, chunkZ);
    }

    /// <summary>The solid spans (bottom up) of block column (lx, lz) of the chunk column last prepared.</summary>
    public ReadOnlySpan<(short Lo, short Hi)> Spans(int lx, int lz)
    {
        int col = lx + S * lz;
        return _profile!.Spans.AsSpan(_profile.Start[col], _profile.Count(col));
    }

    /// <summary>Works out a chunk column's solid spans and what fills them.</summary>
    private void BuildProfile(ColumnProfile p, int chunkX, int chunkZ)
    {
        float x0 = chunkX * S, z0 = chunkZ * S;
        p.Clear();
        if (!FillLattice(chunkX, chunkZ))
        {
            Array.Clear(_spanCount);
            p.Pack(_spanCount, _spans, MaxSpans);
            return;
        }

        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            int col = lx + S * lz;
            _spanCount[col] = (byte)ColumnSpans(lx, lz, _spans.AsSpan(col * MaxSpans, MaxSpans));
        }

        // Packed (only the spans there are, so profiles are small enough to share), then what's on each span's top.
        p.Pack(_spanCount, _spans, MaxSpans);
        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            int col = lx + S * lz, spans = p.Count(col), at = p.Start[col];
            if (spans == 0) continue;
            float wx = x0 + lx, wz = z0 + lz;
            p.Strata[col] = _terrain.Strata(wx, wz);
            p.Patch[col] = _terrain.Patch(wx, wz);
            p.Flora[col] = _terrain.Flora(wx, wz);
            p.Woods[col] = _terrain.Woods(wx, wz);
            p.Kind[col] = _terrain.Kind(wx, wz);
            float heat = _terrain.Heat(wx, wz), wet = _terrain.Wet(wx, wz);
            for (int sp = 0; sp < spans; sp++)
            {
                int hi = p.Spans[at + sp].Hi;
                p.Bare[at + sp] = sp + 1 < spans && p.Spans[at + sp + 1].Lo - hi < ShadeReach ? Shaded : 0f;
                float h = Biomes.Heat(hi, heat);
                p.Biome[at + sp] = Biomes.Pick(h, wet);
                p.Dry[at + sp] = Biomes.Dry(h, wet, Biomes.SandBlend);
            }
        }
    }

    /// <summary>Works out chunk column (chunkX, chunkZ)'s <see cref="DistanceLattice"/>: false (and nothing worked out)
    /// where no live heart is near it, as between islands.</summary>
    private bool FillLattice(int chunkX, int chunkZ)
    {
        const int N = DistanceLattice.Corners, Cell = DistanceLattice.Cell;
        float x0 = chunkX * S, z0 = chunkZ * S;
        var lattice = _lattice;

        // The corners' columns: the terrain's height, the world's rough bottom and the wobble there.
        Span<float> height = stackalloc float[N * N], bottom = stackalloc float[N * N];
        float top = float.MinValue, low = float.MaxValue;
        for (int k = 0; k < N; k++)
        for (int i = 0; i < N; i++)
        {
            float wx = x0 + i * Cell, wz = z0 + k * Cell;
            height[i + N * k] = MathF.Min(_terrain.Height(wx, wz), HeartGrid.WorldTop - 1);
            bottom[i + N * k] = HeartGrid.LowestBottom + BottomRoughness * (0.5f + 0.5f * _bottom.GetNoise(wx, wz));
            top = MathF.Max(top, height[i + N * k]);
            low = MathF.Min(low, bottom[i + N * k]);
        }
        // (Hearts are placed by depth under the surface: heights from here on are relative to it.)
        LoadHearts(x0 - WarpAcross, z0 - WarpAcross, x0 + S + WarpAcross, z0 + S + WarpAcross,
                   HeartGrid.LowestBottom - top - TerrainRiseMargin - WarpUp, WarpUp);
        bool anyAlive = false;
        foreach (bool a in _alive.AsSpan(0, _heartCount)) if (a) { anyAlive = true; break; }
        if (!anyAlive) return false;

        // From a level under the lowest bottom to one over the highest top, so the ends are outside the ground.
        int first = (int)MathF.Floor(low / Cell) - 1, last = (int)MathF.Ceiling(top / Cell) + 1;
        lattice.Reset(chunkX, chunkZ, first, last - first + 1);
        if (_levels.Length < lattice.Levels) _levels = new float[lattice.Levels];

        // Up each corner's column. Going up a level, a distance changes by no more than Steepest (see Inside), so where
        // it's further than that past Reach it's still past Reach a level up: as far as that goes, no need to work it out.
        const float Steepest = Cell * (HeartGrid.VerticalScale + 0.5f);
        for (int k = 0; k < N; k++)
        for (int i = 0; i < N; i++)
        {
            int c = i + N * k;
            float wx = x0 + i * Cell, wz = z0 + k * Cell;
            float px = wx + WarpAcross * _warpX.GetNoise(wx, wz);
            float pz = wz + WarpAcross * _warpZ.GetNoise(wx, wz);
            float lift = WarpUp * _warpY.GetNoise(wx, wz);
            float wearNoise = 0.6f + 0.4f * (_crack.GetNoise(wx, wz) + 1f);
            _nearValid = false; // a new column: its hearts' horizontal distances differ
            var column = lattice.Column(c);
            for (int j = 0; j < lattice.Levels;)
            {
                float y = (first + j) * Cell;
                float d = MathF.Min(InsideAt(px, pz, lift, wearNoise, height[c], y), (y - bottom[c]) * HeartGrid.VerticalScale);
                float clamped = Math.Clamp(d, -DistanceLattice.Reach, DistanceLattice.Reach);
                int same = 1 + System.Math.Max(0, (int)((MathF.Abs(d) - DistanceLattice.Reach) / Steepest));
                for (int n = 0; n < same && j < lattice.Levels; n++) column[j++] = clamped;
            }
        }
        return true;
    }

    /// <summary>The solid spans (bottom up) of block column (lx, lz) of the chunk column whose lattice was last worked
    /// out (see FillLattice): where its distances, interpolated between the lattice's corners, are positive. Runs thinner
    /// than MinThickness are left out. Returns how many.</summary>
    private int ColumnSpans(int lx, int lz, Span<(short Lo, short Hi)> into)
    {
        var lattice = _lattice;
        int levels = lattice.Levels, cell = DistanceLattice.Cell;
        lattice.Interpolate(lx, lz, _levels.AsSpan(0, levels));
        var d = _levels;

        // Between levels a distance is linear: a run starts at the first block over where it rises through zero and
        // ends at the last under where it falls through it.
        int count = 0, lo = int.MinValue;
        for (int j = 0; j + 1 < levels; j++)
        {
            float a = d[j], b = d[j + 1];
            if ((a > 0f) == (b > 0f)) continue;
            float y = (lattice.First + j) * cell + cell * a / (a - b); // where it crosses zero
            if (b > 0f) lo = (int)MathF.Floor(y) + 1;
            else
            {
                int hi = System.Math.Min((int)MathF.Ceiling(y) - 1, HeartGrid.WorldTop - 1);
                if (lo != int.MinValue && hi - lo + 1 >= MinThickness && count < into.Length) into[count++] = ((short)lo, (short)hi);
                lo = int.MinValue;
            }
        }
        return count;
    }

    /// <summary>How far inside a live piece block y of a column (wobbled to (px, pz), lifted by lift) is, in scaled
    /// space (see Inside).</summary>
    private float InsideAt(float px, float pz, float lift, float wearNoise, float height, float y)
    {
        float t = WearBlend(y);
        float across = (FloorWear + (UpperWear - FloorWear) * t) * wearNoise;
        float up = (FloorWearUp + (UpperWearUp - FloorWearUp) * t) * wearNoise * HeartGrid.VerticalScale;
        return Inside(px, (y - height + lift) * HeartGrid.VerticalScale, pz, y, height, across, up);
    }

    /// <summary>0 in the floor to 1 in the broken ground above: how far wear goes from the floor's to the upper.</summary>
    private static float WearBlend(float y)
    {
        float t = Math.Clamp((y - HeartGrid.FloorTop - CrackFrom) / CrackOver, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // The hearts near the current test (indices into the heart arrays) and their horizontal distances squared from the
    // column; the cells they came from (per layer, int.MinValue for none); this test's distances squared; and the
    // nearest one's faces (_facesFor: which near heart they are for, -1 for none): per other near heart, 1 over twice
    // the distance between the two, how fast the face between them nears per block up, and its normal's squared upward part.
    private int[] _near = new int[27 * 4];
    private float[] _nearXZ = new float[27 * 4], _nearD = new float[27 * 4];
    private float[] _faceInv2Len = new float[27 * 4], _faceRate = new float[27 * 4], _faceUpness = new float[27 * 4];
    private readonly int[] _nearKey = new int[HeartGrid.Layers.Length];
    private int _nearCount, _facesFor = -1;
    private bool _nearValid;

    private void NearSet(float sx, float sz, ReadOnlySpan<int> key)
    {
        key.CopyTo(_nearKey);
        _nearValid = true;
        _facesFor = -1;
        int count = 0;
        for (int l = 0; l < HeartGrid.Layers.Length; l++)
        {
            if (key[l] == int.MinValue) continue;
            var layer = HeartGrid.Layers[l];
            int cx = (int)MathF.Floor(sx / layer.CellSize) - _cx0[l];
            int cz = (int)MathF.Floor(sz / layer.CellSize) - _cz0[l];
            int cy = key[l];
            for (int k = cz - 1; k <= cz + 1; k++)
            for (int j = cy - 1; j <= cy + 1; j++)
            for (int i = cx - 1; i <= cx + 1; i++)
            {
                if ((uint)i >= (uint)_nx[l] || (uint)j >= (uint)_ny[l] || (uint)k >= (uint)_nz[l]) continue;
                int h = _first[l] + i + _nx[l] * (j + _ny[l] * k);
                if (!_exists[h]) continue;
                float dx = _hx[h] - sx, dz = _hz[h] - sz;
                _near[count] = h;
                _nearXZ[count++] = dx * dx + dz * dz;
            }
        }
        _nearCount = count;
    }

    /// <summary>The faces of near heart <paramref name="bn"/>'s piece, against each other near heart.</summary>
    private void FaceConstants(int bn)
    {
        _facesFor = bn;
        int bi = _near[bn];
        for (int n = 0; n < _nearCount; n++)
        {
            if (n == bn) continue;
            int h = _near[n];
            float ex = _hx[h] - _hx[bi], ey = _hy[h] - _hy[bi], ez = _hz[h] - _hz[bi];
            float len2 = ex * ex + ey * ey + ez * ez, len = MathF.Sqrt(len2);
            _faceInv2Len[n] = 1f / (2f * len);
            _faceRate[n] = ey / len * HeartGrid.VerticalScale;
            _faceUpness[n] = ey * ey / len2;
        }
    }

    /// <summary>How far the point at scaled position (sx, sy, sz) is inside a live piece, worn back from its faces by
    /// <paramref name="wearAcross"/> and <paramref name="wearUp"/>, in scaled space: positive inside, negative outside.
    /// Going up a block it changes by at most about VerticalScale (each face's distance does, and a smooth minimum of
    /// them changes no faster than the fastest).</summary>
    private float Inside(float sx, float sy, float sz, float y, float surface, float wearAcross, float wearUp)
    {
        // The hearts to search: those in the cells around the point in each layer whose band is within a cell of it. A
        // walk up a column keeps sx and sz, so the set (with each heart's horizontal distance) is kept from test to test
        // until one of those cells changes (see NearSet).
        float ly = sy / HeartGrid.VerticalScale;
        Span<int> key = stackalloc int[HeartGrid.Layers.Length];
        for (int l = 0; l < HeartGrid.Layers.Length; l++)
        {
            var layer = HeartGrid.Layers[l];
            key[l] = int.MinValue;
            if (ly < layer.YMin - layer.CellHeight || ly > layer.YMax + layer.CellHeight || _nx[l] == 0) continue;
            key[l] = (int)MathF.Floor(sy / layer.CellSize) - _cy0[l];
        }
        if (!_nearValid || !key.SequenceEqual(_nearKey)) NearSet(sx, sz, key);
        int count = _nearCount;

        float best = float.MaxValue;
        int bn = -1;
        for (int n = 0; n < count; n++)
        {
            float dy = _hy[_near[n]] - sy;
            float d = _nearD[n] = _nearXZ[n] + dy * dy;
            if (d < best) { best = d; bn = n; }
        }
        if (bn < 0) return -DistanceLattice.Reach;
        int bi = _near[bn];
        if (bn != _facesFor) FaceConstants(bn);

        // Distances to the planes halfway between the nearest heart and each other: its piece's faces. In a dead piece
        // the point is outside by the distance to the nearest live neighbour's ground, past its face and that one's wear.
        if (!_alive[bi])
        {
            float toLive = float.MaxValue;
            for (int n = 0; n < count; n++)
                if (n != bn && _alive[_near[n]]) toLive = MathF.Min(toLive, (_nearD[n] - best) * _faceInv2Len[n] + wearAcross);
            return toLive == float.MaxValue ? -DistanceLattice.Reach : -toLive;
        }

        // In a live piece, each face less the wear there (across for a side face, up and down for a top or bottom one,
        // blended between for a slanted one), and the piece's top; inside by a smooth minimum of those: worn back from
        // every face, and further at edges and corners, where faces meet, so they're rounded.
        Span<float> worn = stackalloc float[27 * 4 + 1];
        int faces = 0;
        float least = float.MaxValue;
        for (int n = 0; n < count; n++)
        {
            if (n == bn) continue;
            float plane = (_nearD[n] - best) * _faceInv2Len[n];
            // Against a dead heart above, the piece is the top of the land there: its face is the land's surface
            // lowered (parallel to it in this space, the hearts being placed by depth), so it isn't worn like a
            // crack, only rounded where it meets the sides, like the terrain top below.
            bool landTop = _faceRate[n] > 0f && _faceUpness[n] > 0.5f && !_alive[_near[n]];
            float f = landTop ? plane : plane - (wearAcross + (wearUp - wearAcross) * _faceUpness[n]);
            worn[faces++] = f;
            least = MathF.Min(least, f);
        }
        // The piece's top, worn down from the terrain: a face like the others, so the rim where it meets the sides is
        // rounded too (but not worn back: the top is where it is).
        float top = (PieceTop(surface, _hs[bi], _drop[bi]) - y) * HeartGrid.VerticalScale;
        worn[faces++] = top;
        least = MathF.Min(least, top);

        // Smooth minimum: least - Rounding * ln(sum of e^-(f - least) / Rounding).
        float sum = 0f;
        for (int i = 0; i < faces; i++)
        {
            float e = (worn[i] - least) / Rounding;
            if (e < 12f) sum += MathF.Exp(-e);
        }
        return least - Rounding * MathF.Log(sum);
    }

    /// <summary>Loads the hearts of every cell a point in the box (world blocks) can search, in each layer.</summary>
    private int _heartCount; // hearts loaded by LoadHearts

    private void LoadHearts(float minX, float minZ, float maxX, float maxZ, float minY, float maxY)
    {
        int total = 0;
        for (int l = 0; l < HeartGrid.Layers.Length; l++)
        {
            var layer = HeartGrid.Layers[l];
            float lo = MathF.Max(minY, layer.YMin - layer.CellHeight), hi = MathF.Min(maxY, layer.YMax + layer.CellHeight);
            _first[l] = total;
            if (lo > hi) { _nx[l] = _ny[l] = _nz[l] = 0; continue; }
            _cx0[l] = layer.CellX(minX) - 1; _cz0[l] = layer.CellX(minZ) - 1; _cy0[l] = layer.CellY(lo) - 1;
            _nx[l] = layer.CellX(maxX) + 2 - _cx0[l];
            _nz[l] = layer.CellX(maxZ) + 2 - _cz0[l];
            _ny[l] = layer.CellY(hi) + 2 - _cy0[l];
            total += _nx[l] * _ny[l] * _nz[l];
        }
        _heartCount = total;
        if (_hx.Length < total)
        {
            _hx = new float[total]; _hy = new float[total]; _hz = new float[total];
            _alive = new bool[total]; _exists = new bool[total]; _hs = new float[total]; _drop = new float[total];
        }
        for (int l = 0; l < HeartGrid.Layers.Length; l++)
        for (int k = 0; k < _nz[l]; k++)
        for (int j = 0; j < _ny[l]; j++)
        for (int i = 0; i < _nx[l]; i++)
        {
            int h = _first[l] + i + _nx[l] * (j + _ny[l] * k);
            var heart = HeartGrid.At(_seed, l, _cx0[l] + i, _cy0[l] + j, _cz0[l] + k);
            _hx[h] = heart.X; _hy[h] = -heart.Depth * HeartGrid.VerticalScale; _hz[h] = heart.Z;
            _alive[h] = heart.Alive; _exists[h] = heart.Exists; _drop[h] = heart.Drop; _hs[h] = heart.Surface;
        }
    }
}

/// <summary>One chunk column of the hearts world worked out: per block column, its solid spans (bottom up, from
/// <see cref="Start"/>) with each top's look, and what's on it. Not changed while it's kept (see
/// <see cref="ProfileCache"/>); once let go of, it's made again for another column, its arrays reused.</summary>
internal sealed class ColumnProfile
{
    private const int Columns = ChunkData.Size * ChunkData.Size;

    /// <summary>Takes the column's solid spans, <paramref name="counts"/> per block column at
    /// <paramref name="perColumn"/> apart in <paramref name="spans"/>, packed one after another.</summary>
    public void Pack(byte[] counts, (short Lo, short Hi)[] spans, int perColumn)
    {
        for (int col = 0; col < Columns; col++) Start[col + 1] = Start[col] + counts[col];
        int total = Start[Columns];
        if (Spans.Length < total)
        {
            int length = System.Math.Max(total, 2 * Spans.Length); // (only as much as it needs at first)
            Spans = new (short, short)[length];
            Bare = new float[length];
            Biome = new Biome[length];
            Dry = new float[length];
        }
        for (int col = 0; col < Columns; col++)
            Array.Copy(spans, col * perColumn, Spans, Start[col], counts[col]);
    }

    public readonly int[] Start = new int[Columns + 1];
    public (short Lo, short Hi)[] Spans = Array.Empty<(short, short)>();
    /// <summary>Per span top: how shaded it is, its biome, and how near a desert (Biomes.Dry).</summary>
    public float[] Bare = Array.Empty<float>(), Dry = Array.Empty<float>();
    public Biome[] Biome = Array.Empty<Biome>();
    public readonly float[] Strata = new float[Columns], Patch = new float[Columns], Flora = new float[Columns],
                            Woods = new float[Columns], Kind = new float[Columns];

    public int Count(int col) => Start[col + 1] - Start[col];

    /// <summary>Clears what's only set where there are spans, for a profile made again.</summary>
    public void Clear()
    {
        Array.Clear(Strata); Array.Clear(Patch); Array.Clear(Flora); Array.Clear(Woods); Array.Clear(Kind);
    }
}

/// <summary>The column profiles one generator built lately, the oldest let go first and made again for the next column:
/// they last long enough that the collector would carry each through its generations only to drop it.</summary>
internal sealed class ProfileCache
{
    /// <summary>Columns kept: a profile is about 30 KB (more where there's lots of ground), so a few MB a generator.
    /// Enough for the few level-2 columns (16 chunk columns each) a worker is making chunks over at once.</summary>
    private const int Capacity = 128;

    private readonly Dictionary<(int, int), ColumnProfile> _profiles = new();
    private readonly Queue<(int, int)> _order = new();

    public bool TryGet(int x, int z, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ColumnProfile? kept)
        => _profiles.TryGetValue((x, z), out kept);

    /// <summary>A profile to make for column (x, z), kept from now on: the oldest one, let go of, once it's full.</summary>
    public ColumnProfile Add(int x, int z)
    {
        var made = _order.Count < Capacity || !_profiles.Remove(_order.Dequeue(), out var oldest) ? new ColumnProfile() : oldest;
        _profiles[(x, z)] = made;
        _order.Enqueue((x, z));
        return made;
    }
}

/// <summary>Clouds lie over the hearts world's continents, thicker over its clusters, and clear over the gaps between
/// continents: other land shows from afar as a bank of cloud.</summary>
public sealed class HeartCloudDensity : ICloudDensityMap
{
    private const float Reach = 750f;

    private readonly ulong _seed;

    public HeartCloudDensity(ulong seed) => _seed = seed;

    public float Density(float x, float z)
    {
        float continent = HeartGrid.Continent(_seed, x, z);
        if (continent <= 0f) return 0f;
        float cluster = HeartGrid.ClusterField(_seed, x, z);
        for (int i = 0; i < 8 && cluster < 1f; i++)
        {
            float a = i * (MathF.Tau / 8f);
            cluster = MathF.Max(cluster, 0.8f * HeartGrid.ClusterField(_seed, x + Reach * MathF.Cos(a), z + Reach * MathF.Sin(a)));
        }
        return continent * (0.6f + 0.4f * cluster);
    }
}
