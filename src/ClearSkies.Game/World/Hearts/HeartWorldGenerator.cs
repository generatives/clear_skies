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

    // How far each piece is worn back from its faces, in blocks, so there are cracks twice that wide between
    // neighbouring pieces and small or thin pieces wear away altogether: across (from side faces) and up and down (from
    // top and bottom faces), in the floor and higher up, varied by noise.
    private const float FloorWear = 12f, UpperWear = 16f, FloorWearUp = 9f, UpperWearUp = 12f;

    /// <summary>How far round a piece's edges and corners are worn, in scaled space (see Solid).</summary>
    private const float Rounding = 6f;

    // Wear grows from the floor's to the upper over CrackOver blocks from CrackFrom above the floor's top: at most
    // (UpperWearUp - FloorWearUp) * VerticalScale * 1.5 / CrackOver per block (a smoothstep's steepest), times 1.4
    // for noise, so by under CrackSlack over CrackStepMax blocks.
    private const float CrackFrom = -20f, CrackOver = 220f;
    private const int CrackStepMax = 18;

    /// <summary>Solid runs thinner than this are worn away: slivers where the terrain surface just grazes a piece.</summary>
    private const int MinThickness = 6;

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

    // The hearts around the chunk column being profiled, per layer: cells from (_cx0, _cy0, _cz0), _nx by _ny by _nz of
    // them, stored from _first as scaled positions (y times VerticalScale), whether each exists and whether it is alive.
    private readonly int[] _cx0 = new int[HeartGrid.Layers.Length], _cy0 = new int[HeartGrid.Layers.Length],
                           _cz0 = new int[HeartGrid.Layers.Length], _nx = new int[HeartGrid.Layers.Length],
                           _ny = new int[HeartGrid.Layers.Length], _nz = new int[HeartGrid.Layers.Length],
                           _first = new int[HeartGrid.Layers.Length];
    private float[] _hx = Array.Empty<float>(), _hy = Array.Empty<float>(), _hz = Array.Empty<float>();
    private bool[] _alive = Array.Empty<bool>(), _exists = Array.Empty<bool>();

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
            float wearNoise = 0.6f + 0.4f * (_crack.GetNoise(wx, wz) + 1f);

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
                    float t = WearBlend(y);
                    float across = (FloorWear + (UpperWear - FloorWear) * t) * wearNoise + CrackSlack;
                    float up = (FloorWearUp + (UpperWearUp - FloorWearUp) * t) * wearNoise * HeartGrid.VerticalScale + CrackSlack;
                    solid = Solid(px, (y + lift) * HeartGrid.VerticalScale, pz, across, up, out float clear);
                    step = System.Math.Max(1, (int)clear);
                    // Where wear grows with height, not so far that it grows by more than CrackSlack.
                    if (y > HeartGrid.FloorTop + CrackFrom && y < HeartGrid.FloorTop + CrackFrom + CrackOver)
                        step = System.Math.Min(step, CrackStepMax);
                }
                if (solid && runLo == int.MinValue) runLo = y;
                else if (!solid && runLo != int.MinValue)
                {
                    if (spans < MaxSpans && y - runLo >= MinThickness) _spans[col * MaxSpans + spans++] = ((short)runLo, (short)(y - 1));
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

    /// <summary>0 in the floor to 1 in the broken ground above: how far wear goes from the floor's to the upper.</summary>
    private static float WearBlend(float y)
    {
        float t = Math.Clamp((y - HeartGrid.FloorTop - CrackFrom) / CrackOver, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>How much wear can grow between a test and the blocks it skips (see WearBlend), in scaled space: tests
    /// are made with this much more.</summary>
    private const float CrackSlack = 1f;

    /// <summary>Whether the point at scaled position (sx, sy, sz) is in a live piece, worn back from its faces by
    /// <paramref name="wearAcross"/> and <paramref name="wearUp"/> (scaled space), and how many blocks straight up it
    /// stays so (<paramref name="clear"/>).</summary>
    private bool Solid(float sx, float sy, float sz, float wearAcross, float wearUp, out float clear)
    {
        // The hearts to search: those in the cells around the point in each layer whose band is within a cell of it.
        // Going up, that set changes at the next cell boundary of a searched layer, or where another layer comes
        // within reach: no skipping past either.
        Span<int> near = stackalloc int[27 * 3];
        int count = 0;
        float y = sy / HeartGrid.VerticalScale, setChange = float.MaxValue;
        for (int l = 0; l < HeartGrid.Layers.Length; l++)
        {
            var layer = HeartGrid.Layers[l];
            if (y < layer.YMin - layer.CellHeight) { setChange = MathF.Min(setChange, layer.YMin - layer.CellHeight - y); continue; }
            if (y > layer.YMax + layer.CellHeight) continue;
            if (_nx[l] == 0) continue;
            int cx = (int)MathF.Floor(sx / layer.CellSize) - _cx0[l];
            int cy = (int)MathF.Floor(sy / layer.CellSize) - _cy0[l];
            int cz = (int)MathF.Floor(sz / layer.CellSize) - _cz0[l];
            setChange = MathF.Min(setChange, ((cy + _cy0[l] + 1) * layer.CellSize - sy) / HeartGrid.VerticalScale);
            if (y > layer.YMax) setChange = MathF.Min(setChange, layer.YMax + layer.CellHeight - y);
            for (int k = cz - 1; k <= cz + 1; k++)
            for (int j = cy - 1; j <= cy + 1; j++)
            for (int i = cx - 1; i <= cx + 1; i++)
            {
                if ((uint)i >= (uint)_nx[l] || (uint)j >= (uint)_ny[l] || (uint)k >= (uint)_nz[l]) continue;
                int h = _first[l] + i + _nx[l] * (j + _ny[l] * k);
                if (_exists[h]) near[count++] = h;
            }
        }

        float best = float.MaxValue;
        int bi = -1;
        foreach (int h in near[..count])
        {
            float dx = _hx[h] - sx, dy = _hy[h] - sy, dz = _hz[h] - sz;
            float d = dx * dx + dy * dy + dz * dz;
            if (d < best) { best = d; bi = h; }
        }
        clear = 0f;
        if (bi < 0) return false;

        // Distances to the planes halfway between the nearest heart and each other: its piece's faces, each less the
        // wear there (across for a side face, up and down for a top or bottom one, blended between for a slanted
        // one). In a live piece the point is solid if a smooth minimum of those is positive: worn back from every
        // face, and further at edges and corners, where faces meet, so they're rounded. In a dead piece it is empty.
        //
        // Going up, a face's distance changes linearly, at a rate of its normal's upward part (times VerticalScale per
        // block): it shrinks for a face above and grows for one below. Two bounds each on how long a point stays as it
        // is, the better of which counts:
        // - The smooth minimum changes no faster than its fastest face: a solid point stays solid for at least its
        //   value over the fastest shrinking rate, an empty one for minus its value over the fastest growing rate.
        // - The smooth minimum is at most Rounding * ln(faces) under the plain one and never over it: a solid point
        //   stays solid until some face comes within that of zero, an empty one while any face stays at or under zero.
        // Either stays in the same piece until it reaches a face above.
        bool alive = _alive[bi];
        float least = float.MaxValue, shrink = 0f, grow = 0f, toFace = float.MaxValue;
        Span<float> worn = stackalloc float[27 * 3], rates = stackalloc float[27 * 3];
        int faces = 0;
        foreach (int h in near[..count])
        {
            if (h == bi) continue;
            float dx = _hx[h] - sx, dy = _hy[h] - sy, dz = _hz[h] - sz;
            float ex = _hx[h] - _hx[bi], ey = _hy[h] - _hy[bi], ez = _hz[h] - _hz[bi];
            float len = MathF.Sqrt(ex * ex + ey * ey + ez * ez);
            float plane = (dx * dx + dy * dy + dz * dz - best) / (2f * len);
            float rate = ey / len * HeartGrid.VerticalScale; // how fast the face nears, per block up
            if (rate > 0f) toFace = MathF.Min(toFace, plane / rate);
            if (!alive) continue;
            float upness = ey * ey / (len * len);
            float f = plane - (wearAcross + (wearUp - wearAcross) * upness);
            rates[faces] = rate;
            worn[faces++] = f;
            least = MathF.Min(least, f);
            if (rate > 0f) shrink = MathF.Max(shrink, rate); else grow = MathF.Max(grow, -rate);
        }
        if (!alive)
        {
            clear = MathF.Max(MathF.Min(toFace, setChange), 0f);
            return false;
        }

        // Smooth minimum: least - Rounding * ln(sum of e^-(f - least) / Rounding). It is between least and gap under
        // it, so only worked out where that straddles zero, near a piece's surface; elsewhere its lower bound serves.
        float gap = faces > 1 ? Rounding * MathF.Log(faces) : 0f;
        float soft;
        if (faces == 0) soft = float.MaxValue;
        else if (least <= 0f) soft = least;
        else if (least > gap) soft = least - gap;
        else
        {
            float sum = 0f;
            for (int i = 0; i < faces; i++)
            {
                float e = (worn[i] - least) / Rounding;
                if (e < 12f) sum += MathF.Exp(-e);
            }
            soft = least - Rounding * MathF.Log(sum);
        }
        bool solid = soft > 0f || (faces > 0 && least > gap);
        float until;
        if (solid)
        {
            float byFace = float.MaxValue;
            for (int i = 0; i < faces; i++)
                if (rates[i] > 0f) byFace = MathF.Min(byFace, (worn[i] - gap) / rates[i]);
            until = MathF.Max(shrink > 0f ? soft / shrink : float.MaxValue, byFace);
        }
        else
        {
            float byFace = 0f;
            for (int i = 0; i < faces; i++)
                if (worn[i] <= 0f) byFace = MathF.Max(byFace, rates[i] >= 0f ? float.MaxValue : worn[i] / rates[i]);
            until = MathF.Max(grow > 0f ? -soft / grow : float.MaxValue, byFace);
        }
        clear = MathF.Max(MathF.Min(MathF.Min(until, toFace), setChange), 0f);
        return solid;
    }

    /// <summary>Loads the hearts of every cell a point in the box (world blocks) can search, in each layer.</summary>
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
        if (_hx.Length < total)
        {
            _hx = new float[total]; _hy = new float[total]; _hz = new float[total];
            _alive = new bool[total]; _exists = new bool[total];
        }
        for (int l = 0; l < HeartGrid.Layers.Length; l++)
        for (int k = 0; k < _nz[l]; k++)
        for (int j = 0; j < _ny[l]; j++)
        for (int i = 0; i < _nx[l]; i++)
        {
            int h = _first[l] + i + _nx[l] * (j + _ny[l] * k);
            var heart = HeartGrid.At(_seed, l, _cx0[l] + i, _cy0[l] + j, _cz0[l] + k);
            _hx[h] = heart.X; _hy[h] = heart.Y * HeartGrid.VerticalScale; _hz[h] = heart.Z;
            _alive[h] = heart.Alive; _exists[h] = heart.Exists;
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
