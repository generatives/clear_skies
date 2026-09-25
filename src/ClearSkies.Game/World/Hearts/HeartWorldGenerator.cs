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
/// Per chunk column this is worked out once, block column by block column, as solid spans (runs of solid blocks), then
/// filled in chunk by chunk.
/// </summary>
public sealed class HeartWorldGenerator : IWorldGenerator
{
    private const int S = ChunkData.Size;
    private const int MaxSpans = 32; // per block column

    /// <summary>More than the terrain can rise above the highest of a chunk column's corners and centre.</summary>
    private const float TerrainRiseMargin = 96f;

    // Wobble of a block's position before finding its nearest heart, in blocks: across, and up and down.
    private const float WarpAcross = 18f, WarpUp = 6f, WarpFrequency = 0.012f;

    // Crack half-widths, in the scaled space nearest hearts are found in (a horizontal crack is VerticalScale times
    // thinner): narrow in the floor, wider higher up, varied by noise.
    private const float FloorCrack = 1.6f, UpperCrack = 7f;

    // Cracks widen from FloorCrack to UpperCrack over CrackOver blocks from CrackFrom above the floor's top: at most
    // (UpperCrack - FloorCrack) * 1.5 / CrackOver per block (a smoothstep's steepest), times 1.4 for crack noise, so
    // by at most CrackSlack over CrackStepMax blocks.
    private const float CrackFrom = -20f, CrackOver = 220f;
    private const int CrackStepMax = 18;

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
        terrainTop = MathF.Min(terrainTop + TerrainRiseMargin, IslandGrid.WorldTop - 1);
        int lo = System.Math.Max((int)MathF.Floor(HeartGrid.LowestBottom / S) - minChunkY, 0);
        int hi = System.Math.Min((int)MathF.Floor(terrainTop / S) - minChunkY, 63);
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
    private readonly float[] _height = new float[S * S];
    private readonly float[] _bottomAt = new float[S * S];

    // The hearts around the chunk column being profiled: cells _cx0.., _cy0.., _cz0.., _nx by _ny by _nz of them, stored
    // as scaled positions (y times VerticalScale) and whether each is alive.
    private int _cx0, _cy0, _cz0, _nx, _ny, _nz;
    private float[] _hx = Array.Empty<float>(), _hy = Array.Empty<float>(), _hz = Array.Empty<float>();
    private bool[] _alive = Array.Empty<bool>();

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
                    data.Set(lx, y - originY, lz, ContinentTerrain.Block(y, hi, _strata[col], _patch[col]));
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

    private void BuildProfile(int chunkX, int chunkZ)
    {
        _profileX = chunkX; _profileZ = chunkZ;
        float x0 = chunkX * S, z0 = chunkZ * S;

        float top = float.MinValue;
        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            int col = lx + S * lz;
            float wx = x0 + lx, wz = z0 + lz;
            _height[col] = MathF.Min(_terrain.Height(wx, wz), IslandGrid.WorldTop - 1);
            _bottomAt[col] = HeartGrid.LowestBottom + BottomRoughness * (0.5f + 0.5f * _bottom.GetNoise(wx, wz));
            top = MathF.Max(top, _height[col]);
        }
        LoadHearts(x0 - WarpAcross, z0 - WarpAcross, x0 + S + WarpAcross, z0 + S + WarpAcross,
                   HeartGrid.LowestBottom - WarpUp, top + WarpUp);

        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            int col = lx + S * lz;
            float wx = x0 + lx, wz = z0 + lz;
            // The column's wobble: pieces' sides are vertical, with ragged outlines; their layers wave up and down.
            float px = wx + WarpAcross * _warpX.GetNoise(wx, wz);
            float pz = wz + WarpAcross * _warpZ.GetNoise(wx, wz);
            float lift = WarpUp * _warpY.GetNoise(wx, wz);
            float crackNoise = 0.6f + 0.4f * (_crack.GetNoise(wx, wz) + 1f);

            // Up the column, skipping as far as nothing can change: each test also says how many blocks further up it
            // could, at the nearest piece face or crack above.
            int spans = 0, runLo = int.MinValue;
            int yBottom = (int)MathF.Ceiling(_bottomAt[col]), yTop = (int)MathF.Floor(_height[col]);
            for (int y = yBottom; y <= yTop + 1;)
            {
                bool solid = false;
                int step = 1;
                if (y <= yTop)
                {
                    solid = Solid(px, (y + lift) * HeartGrid.VerticalScale, pz, Crack(y) * crackNoise + CrackSlack, out float clear);
                    step = System.Math.Max(1, (int)clear);
                    // Where cracks widen with height, not so far that they widen by more than CrackSlack.
                    if (y > HeartGrid.FloorTop + CrackFrom && y < HeartGrid.FloorTop + CrackFrom + CrackOver)
                        step = System.Math.Min(step, CrackStepMax);
                }
                if (solid && runLo == int.MinValue) runLo = y;
                else if (!solid && runLo != int.MinValue)
                {
                    if (spans < MaxSpans) _spans[col * MaxSpans + spans++] = ((short)runLo, (short)(y - 1));
                    runLo = int.MinValue;
                }
                y = System.Math.Min(y + step, System.Math.Max(y + 1, yTop + 1));
            }
            _spanCount[col] = (byte)spans;
            if (spans == 0) continue;
            _strata[col] = _terrain.Strata(wx, wz);
            _patch[col] = _terrain.Patch(wx, wz);
        }
    }

    /// <summary>A crack's half-width at height y: narrow in the floor, wider in the broken ground above.</summary>
    private static float Crack(float y)
    {
        float t = Math.Clamp((y - HeartGrid.FloorTop - CrackFrom) / CrackOver, 0f, 1f);
        return FloorCrack + (UpperCrack - FloorCrack) * t * t * (3f - 2f * t);
    }

    /// <summary>How much a crack can widen between a test and the blocks it skips (see Crack), in scaled space: tests
    /// are made with cracks this much wider.</summary>
    private const float CrackSlack = 1f;

    /// <summary>Whether the point at scaled position (sx, sy, sz) is in a live piece, clear of the cracks around it, and
    /// how many blocks straight up it stays so (<paramref name="clear"/>).</summary>
    private bool Solid(float sx, float sy, float sz, float halfCrack, out float clear)
    {
        int cx = (int)MathF.Floor(sx / HeartGrid.CellSize) - _cx0;
        int cy = (int)MathF.Floor(sy / HeartGrid.CellSize) - _cy0;
        int cz = (int)MathF.Floor(sz / HeartGrid.CellSize) - _cz0;
        float best = float.MaxValue;
        int bi = -1;
        for (int k = cz - 1; k <= cz + 1; k++)
        for (int j = cy - 1; j <= cy + 1; j++)
        for (int i = cx - 1; i <= cx + 1; i++)
        {
            if ((uint)i >= (uint)_nx || (uint)j >= (uint)_ny || (uint)k >= (uint)_nz) continue;
            int h = i + _nx * (j + _ny * k);
            float dx = _hx[h] - sx, dy = _hy[h] - sy, dz = _hz[h] - sz;
            float d = dx * dx + dy * dy + dz * dz;
            if (d < best) { best = d; bi = h; }
        }
        clear = 0f;
        if (bi < 0) return false;

        // Distances to the planes halfway between the nearest heart and each other: its piece's faces. In a live piece
        // the point is solid if it is at least halfCrack from every face shared with another live piece; it stays so
        // until it nears one of those, or crosses a face into a dead piece. In a dead piece it stays empty until it
        // crosses a face into a live one.
        // A face's distance changes linearly going up: it shrinks only for a face above, at a rate of its normal's
        // upward part (times VerticalScale per block).
        bool alive = _alive[bi];
        float nearestLive = float.MaxValue, change = float.MaxValue;
        for (int k = cz - 1; k <= cz + 1; k++)
        for (int j = cy - 1; j <= cy + 1; j++)
        for (int i = cx - 1; i <= cx + 1; i++)
        {
            if ((uint)i >= (uint)_nx || (uint)j >= (uint)_ny || (uint)k >= (uint)_nz) continue;
            int h = i + _nx * (j + _ny * k);
            if (h == bi) continue;
            float dx = _hx[h] - sx, dy = _hy[h] - sy, dz = _hz[h] - sz;
            float ex = _hx[h] - _hx[bi], ey = _hy[h] - _hy[bi], ez = _hz[h] - _hz[bi];
            float len = MathF.Sqrt(ex * ex + ey * ey + ez * ez);
            float plane = (dx * dx + dy * dy + dz * dz - best) / (2f * len);
            float until; // how far the face can come before this changes, in scaled space
            if (alive) until = _alive[h] ? plane - halfCrack : plane;
            else if (_alive[h]) until = plane; // (no crack between a live piece and a hole)
            else continue;
            if (alive && _alive[h]) nearestLive = MathF.Min(nearestLive, plane);
            if (ey > 0f) change = MathF.Min(change, until * len / (ey * HeartGrid.VerticalScale));
        }
        bool solid = alive && nearestLive > halfCrack;
        if (solid || !alive)
        {
            // Not past the top of this cell's layer, beyond which other hearts come into the search.
            float layerTop = ((cy + _cy0 + 1) * HeartGrid.CellSize - sy) / HeartGrid.VerticalScale;
            clear = MathF.Max(MathF.Min(change, layerTop), 0f);
        }
        return solid;
    }

    /// <summary>Loads the hearts of every cell a point in the box (world blocks) can find its nearest two in.</summary>
    private void LoadHearts(float minX, float minZ, float maxX, float maxZ, float minY, float maxY)
    {
        _cx0 = HeartGrid.CellX(minX) - 1; _cz0 = HeartGrid.CellX(minZ) - 1; _cy0 = HeartGrid.CellY(minY) - 1;
        _nx = HeartGrid.CellX(maxX) + 2 - _cx0;
        _nz = HeartGrid.CellX(maxZ) + 2 - _cz0;
        _ny = HeartGrid.CellY(maxY) + 2 - _cy0;
        int n = _nx * _ny * _nz;
        if (_hx.Length < n)
        {
            _hx = new float[n]; _hy = new float[n]; _hz = new float[n]; _alive = new bool[n];
        }
        for (int k = 0; k < _nz; k++)
        for (int j = 0; j < _ny; j++)
        for (int i = 0; i < _nx; i++)
        {
            int h = i + _nx * (j + _ny * k);
            var heart = HeartGrid.At(_seed, _cx0 + i, _cy0 + j, _cz0 + k);
            _hx[h] = heart.X; _hy[h] = heart.Y * HeartGrid.VerticalScale; _hz[h] = heart.Z; _alive[h] = heart.Alive;
        }
    }
}

/// <summary>Clouds bank up over the hearts world's clusters: the cluster field, widened.</summary>
public sealed class HeartCloudDensity : ICloudDensityMap
{
    private const float Reach = 300f;

    private readonly ulong _seed;

    public HeartCloudDensity(ulong seed) => _seed = seed;

    public float Density(float x, float z)
    {
        float best = HeartGrid.ClusterField(_seed, x, z);
        for (int i = 0; i < 8 && best < 1f; i++)
        {
            float a = i * (MathF.Tau / 8f);
            best = MathF.Max(best, 0.8f * HeartGrid.ClusterField(_seed, x + Reach * MathF.Cos(a), z + Reach * MathF.Sin(a)));
        }
        return best;
    }
}
