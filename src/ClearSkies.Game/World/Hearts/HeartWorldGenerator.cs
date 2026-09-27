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
    private const float FloorWear = 18f, UpperWear = 24f, FloorWearUp = 13f, UpperWearUp = 18f;

    /// <summary>How far round a piece's edges and corners are worn, in scaled space (see Solid).</summary>
    private const float Rounding = 8f;

    // Wear grows from the floor's to the upper over CrackOver blocks from CrackFrom above the floor's top: at most
    // (UpperWearUp - FloorWearUp) * VerticalScale * 1.5 / CrackOver per block (a smoothstep's steepest), times 1.4
    // for noise, so by under CrackSlack over CrackStepMax blocks.
    private const float CrackFrom = -20f, CrackOver = 220f;
    private const int CrackStepMax = 12;

    /// <summary>Solid runs thinner than this are worn away: slivers where the terrain surface just grazes a piece.</summary>
    private const int MinThickness = 6;

    // A piece's top is the terrain worn down: where the terrain there stands above the terrain at its heart, only
    // Flatten of the difference is left, and the whole top is lowered by up to MaxDrop (each piece its own amount).
    // So tops are flatter than the land they came from, and neighbours don't line up.
    private const float Flatten = 0.45f, MaxDrop = 30f;

    // A top is bare (see ContinentTerrain.Block) the further it is below the terrain surface, from BareFrom to
    // BareFull blocks, and at least Shaded if land lies over it within ShadeReach blocks.
    private const float BareFrom = 12f, BareFull = 60f, Shaded = 0.75f, ShadeReach = 250f;

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

    /// <summary>One chunk column's solid spans, per block column: computed once and reused for each of the column's
    /// chunks (streaming generates a column's chunks one after another on one thread).</summary>
    private int _profileX = int.MinValue, _profileZ = int.MinValue;
    private readonly (short Lo, short Hi)[] _spans = new (short, short)[S * S * MaxSpans];
    private readonly byte[] _spanCount = new byte[S * S];
    private readonly float[] _strata = new float[S * S];
    private readonly float[] _patch = new float[S * S];
    private readonly float[] _bare = new float[S * S * MaxSpans];
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
    private float[] _hs = Array.Empty<float>(), _drop = Array.Empty<float>(); // terrain height at the heart; its Drop

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
                    data.Set(lx, y - originY, lz, ContinentTerrain.Block(y, hi, _strata[col], _patch[col], _bare[col * MaxSpans + s]));
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
            _height[col] = MathF.Min(_terrain.Height(wx, wz), HeartGrid.WorldTop - 1);
            _bottomAt[col] = HeartGrid.LowestBottom + BottomRoughness * (0.5f + 0.5f * _bottom.GetNoise(wx, wz));
            top = MathF.Max(top, _height[col]);
        }
        LoadHearts(x0 - WarpAcross, z0 - WarpAcross, x0 + S + WarpAcross, z0 + S + WarpAcross,
                   HeartGrid.LowestBottom - WarpUp, top + WarpUp);

        // Between islands most columns have no live heart near them at all: nothing to walk.
        bool anyAlive = false;
        foreach (bool a in _alive.AsSpan(0, _heartCount)) if (a) { anyAlive = true; break; }
        if (!anyAlive)
        {
            Array.Clear(_spanCount);
            return;
        }

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
            _nearValid = false; // a new column: its hearts' horizontal distances differ

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
                    solid = Solid(px, (y + lift) * HeartGrid.VerticalScale, pz, y, _height[col], across, up, out float clear);
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
            for (int sp = 0; sp < spans; sp++)
            {
                int hi = _spans[col * MaxSpans + sp].Hi;
                float bare = Math.Clamp((_height[col] - hi - BareFrom) / (BareFull - BareFrom), 0f, 1f);
                if (sp + 1 < spans && _spans[col * MaxSpans + sp + 1].Lo - hi < ShadeReach) bare = MathF.Max(bare, Shaded);
                _bare[col * MaxSpans + sp] = bare;
            }
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

    /// <summary>Whether the point at scaled position (sx, sy, sz) is in a live piece, worn back from its faces by
    /// <paramref name="wearAcross"/> and <paramref name="wearUp"/> (scaled space), and how many blocks straight up it
    /// stays so (<paramref name="clear"/>).</summary>
    private bool Solid(float sx, float sy, float sz, float y, float surface, float wearAcross, float wearUp, out float clear)
    {
        // The hearts to search: those in the cells around the point in each layer whose band is within a cell of it.
        // Going up, that set changes at the next cell boundary of a searched layer, or where another layer comes
        // within reach: no skipping past either. A walk up a column keeps sx and sz, so the set (with each heart's
        // horizontal distance) is kept from test to test until one of those cells changes (see NearSet).
        float ly = sy / HeartGrid.VerticalScale, setChange = float.MaxValue;
        Span<int> key = stackalloc int[HeartGrid.Layers.Length];
        for (int l = 0; l < HeartGrid.Layers.Length; l++)
        {
            var layer = HeartGrid.Layers[l];
            key[l] = int.MinValue;
            if (ly < layer.YMin - layer.CellHeight) { setChange = MathF.Min(setChange, layer.YMin - layer.CellHeight - ly); continue; }
            if (ly > layer.YMax + layer.CellHeight) continue;
            if (_nx[l] == 0) continue;
            int cy = (int)MathF.Floor(sy / layer.CellSize) - _cy0[l];
            key[l] = cy;
            setChange = MathF.Min(setChange, ((cy + _cy0[l] + 1) * layer.CellSize - sy) / HeartGrid.VerticalScale);
            if (ly > layer.YMax) setChange = MathF.Min(setChange, layer.YMax + layer.CellHeight - ly);
        }
        if (!_nearValid || !key.SequenceEqual(_nearKey)) NearSet(sx, sz, key);
        int count = _nearCount;

        float best = float.MaxValue;
        int bn = -1;
        for (int n = 0; n < count; n++)
        {
            float dy = _hy[_near[n]] - sy;
            float d = _nearXZ[n] + dy * dy;
            _nearD[n] = d;
            if (d < best) { best = d; bn = n; }
        }
        clear = 0f;
        if (bn < 0) return false;
        int bi = _near[bn];
        if (bn != _facesFor) FaceConstants(bn);

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
        for (int n = 0; n < count; n++)
        {
            if (n == bn) continue;
            float plane = (_nearD[n] - best) * _faceInv2Len[n];
            float rate = _faceRate[n]; // how fast the face nears, per block up
            if (rate > 0f) toFace = MathF.Min(toFace, plane / rate);
            if (!alive) continue;
            float f = plane - (wearAcross + (wearUp - wearAcross) * _faceUpness[n]);
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

        // The piece's top, worn down from the terrain: a face like the others, so the rim where it meets the sides is
        // rounded too (but not worn back: the top is where it is).
        float top = surface - MathF.Max(0f, surface - _hs[bi]) * (1f - Flatten) - MaxDrop * _drop[bi];
        rates[faces] = HeartGrid.VerticalScale;
        worn[faces] = (top - y) * HeartGrid.VerticalScale;
        least = MathF.Min(least, worn[faces++]);
        shrink = MathF.Max(shrink, HeartGrid.VerticalScale);

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
            _hx[h] = heart.X; _hy[h] = heart.Y * HeartGrid.VerticalScale; _hz[h] = heart.Z;
            _alive[h] = heart.Alive; _exists[h] = heart.Exists; _drop[h] = heart.Drop;
            _hs[h] = heart.Alive ? _terrain.Height(heart.X, heart.Z) : 0f;
        }
    }
}

/// <summary>Clouds lie over the hearts world's continents, thicker over its clusters, and clear over the gaps between
/// continents: other land shows from afar as a bank of cloud.</summary>
public sealed class HeartCloudDensity : ICloudDensityMap
{
    private const float Reach = 300f;

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
