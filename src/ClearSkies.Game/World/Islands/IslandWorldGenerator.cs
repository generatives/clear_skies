using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Generates the islands world: the landscapes of <see cref="IslandField"/>, each broken into pieces.
///
/// A landscape is a continuous surface at its own height: hills (<see cref="IslandSurface.Hills"/>), mountains on the
/// landmasses, gentle swells, and land under it down to a lumpy underside, hanging lower under high ground. Its pieces
/// fit together like a 3D jigsaw: each holds the land nearest its point, with distances counting vertical offsets
/// <see cref="IslandField.VerticalScale"/> times over, so pieces are wide and flat and stack in layers, and with a
/// crack between each and the next, across and up and down. A piece is there or not by its landscape's coverage (see
/// <see cref="IslandField.Coverage"/>). Where coverage is full, its pieces keep the landscape's surface, so the land
/// reads as one across the cracks; where it thins, their tops are worn down towards the height at their point and sink
/// a little, each its own amount, so broken land steps and tilts.
///
/// Per chunk column this is worked out once, block column by block column, as solid spans (runs of solid blocks), then
/// filled in chunk by chunk.
/// </summary>
public sealed class IslandWorldGenerator : IWorldGenerator
{
    private const int S = ChunkData.Size;
    private const int MaxSpans = 32; // per block column

    // Wobble of a column's position before finding its piece, in blocks, so cracks are ragged rather than straight:
    // across, and up and down, so layers wave.
    private const float CrackWarp = 18f, LayerWarp = 6f, CrackWarpFrequency = 0.012f;

    /// <summary>Cracks between stacked pieces are this much as wide as those between side-by-side ones.</summary>
    private const float CrackUp = 0.75f;

    // Pieces are worn at their edges, in blocks: tops slope down by up to BankDrop over the last BankWidth before a
    // crack (shut ones too, which then read as shallow valleys), so cracks open out of valleys rather than off flat
    // ground, and undersides are rounded off over UnderRound; where two side cracks meet (rounded over EdgeRound) or are closer than MinWidth (wedges and fins),
    // worn down from the top, and up from the underside, by Taper blocks for each block in; and slabs against a crack
    // worn back to a rounded nose, gone where thinner than Slab.
    private const float BankWidth = 36f, BankDrop = 22f, UnderRound = 18f, EdgeRound = 20f, MinWidth = 24f, Taper = 4f, Slab = 24f;

    /// <summary>A crack face counts as a side crack when it is at most this steep from upright (sine squared of
    /// its tilt).</summary>
    private const float Upright = 0.3f;

    /// <summary>Leaning and stacked cracks (fading in from half <see cref="Upright"/> to one and a half) narrow from
    /// CloseDepth blocks below the top of the land (banked down towards its side cracks) and are shut from CloseFrom
    /// up, so where one comes out on a slope there is no ledge.</summary>
    private const float CloseFrom = 24f, CloseDepth = 120f;

    // Where a landscape's coverage thins, its pieces' tops keep only (1 - Flatten) of how far the surface there stands
    // above the surface at their point, and they sink by up to Sink (each its own share); at full coverage, neither.
    private const float Flatten = 0.6f, Sink = 12f, CoreSink = 3f;

    /// <summary>Solid runs thinner than this (after joining those that touch) are worn away: slivers where a surface
    /// just grazes a piece.</summary>
    private const int MinThickness = 8;

    /// <summary>The most layers of piece points a column searches.</summary>
    private const int MaxRows = 16;

    // A top is bare (see IslandSurface.Block) if land lies over it within ShadeReach blocks.
    private const float Shaded = 0.75f, ShadeReach = 250f;

    private readonly ulong _seed;
    private readonly IslandSurface _surface;
    private readonly FastNoiseLite _swell, _under, _warpX, _warpY, _warpZ, _crack;

    public IslandWorldGenerator(ulong seed)
    {
        _seed = seed;
        _surface = IslandSurface.For(seed);
        _swell = Noise(seed + 41, 2, 0.0006f);  // each landscape's broad rises and dips (sampled offset per landscape)
        _under = Noise(seed + 43, 2, 0.007f);   // lumps on the undersides
        _warpX = Noise(seed + 31, 2, CrackWarpFrequency);
        _warpY = Noise(seed + 32, 2, CrackWarpFrequency);
        _warpZ = Noise(seed + 33, 2, CrackWarpFrequency);
        _crack = Noise(seed + 34, 2, 0.03f);    // how wide the cracks are
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

    /// <summary>Each landscape samples the shared noise this far over, so their surfaces differ.</summary>
    private static float Offset(int layer) => layer * 7919f;

    /// <summary>World Y of landscape <paramref name="layer"/>'s surface at (x, z), and the level its hills stand on.</summary>
    private float Surface(int layer, float x, float z, out float level)
    {
        var l = IslandField.Layers[layer];
        float o = Offset(layer);
        level = l.Base + l.Swell * _swell.GetNoise(x + o, z - o);
        float top = level + l.Hills * _surface.Relief(x + o, z - o) * (0.5f + 0.5f * _surface.Hills(x + o, z + o));
        if (l.Mountains > 0f) top += l.Mountains * _surface.Mountains(x, z);
        return top;
    }

    /// <summary>World Y of landscape <paramref name="layer"/>'s underside at (x, z), under its surface standing on
    /// <paramref name="level"/>.</summary>
    private float Underside(int layer, float x, float z, float level)
    {
        var l = IslandField.Layers[layer];
        float o = Offset(layer);
        // Roots hang under the mountains only: hanging under every hill too, they make a comb of spikes.
        float roots = l.Mountains > 0f ? IslandField.Roots * l.Mountains * _surface.Mountains(x, z) : 0f;
        return level - l.Thickness * (0.85f + 0.2f * _under.GetNoise(x + o, z + o)) - roots;
    }

    public ulong ColumnLayers(int chunkX, int chunkZ, int minChunkY)
    {
        float top = float.MinValue, bottom = float.MaxValue;
        for (int layer = 0; layer < IslandField.Layers.Length; layer++)
        {
            if (!AnyPiece(layer, chunkX, chunkZ)) continue;
            top = MathF.Max(top, IslandField.Layers[layer].TopBound);
            bottom = MathF.Min(bottom, IslandField.Layers[layer].BottomBound);
        }
        if (top < bottom) return 0;
        int lo = System.Math.Max((int)MathF.Floor(MathF.Max(bottom, IslandField.LowestBottom) / S) - minChunkY, 0);
        int hi = System.Math.Min((int)MathF.Floor(MathF.Min(top, IslandField.WorldTop - 1) / S) - minChunkY, 63);
        return lo > hi ? 0 : (ulong.MaxValue >> (63 - hi)) & (ulong.MaxValue << lo);
    }

    /// <summary>Whether any piece of landscape <paramref name="layer"/> that block columns of chunk column (chunkX,
    /// chunkZ) can fall in is there by its coverage (not yet counting whether it is clear of the landmasses).</summary>
    private bool AnyPiece(int layer, int chunkX, int chunkZ)
    {
        var (cx0, cz0, cy0, nx, nz, ny) = CellRange(layer, chunkX, chunkZ);
        for (int k = 0; k < ny; k++)
        for (int j = 0; j < nz; j++)
        for (int i = 0; i < nx; i++)
            if (Present(layer, cx0 + i, cy0 + k, cz0 + j)) return true;
        return false;
    }

    /// <summary>The cells of landscape <paramref name="layer"/> whose pieces block columns of chunk column (chunkX,
    /// chunkZ) can fall in: from (cx0, cy0, cz0), nx by ny by nz of them.</summary>
    private static (int Cx0, int Cz0, int Cy0, int Nx, int Nz, int Ny) CellRange(int layer, int chunkX, int chunkZ)
    {
        var l = IslandField.Layers[layer];
        int cx0 = (int)MathF.Floor((chunkX * S - CrackWarp) / l.CellSize) - 1;
        int cx1 = (int)MathF.Floor((chunkX * S + S + CrackWarp) / l.CellSize) + 1;
        int cz0 = (int)MathF.Floor((chunkZ * S - CrackWarp) / l.CellSize) - 1;
        int cz1 = (int)MathF.Floor((chunkZ * S + S + CrackWarp) / l.CellSize) + 1;
        int cy0 = Row(l, l.BottomBound - LayerWarp) - 1, cy1 = Row(l, l.TopBound + LayerWarp) + 1;
        return (cx0, cz0, cy0, cx1 - cx0 + 1, cz1 - cz0 + 1, cy1 - cy0 + 1);
    }

    private static int Row(in IslandField.Layer l, float y) => (int)MathF.Floor(y * IslandField.VerticalScale / l.CellSize);

    // ── Pieces ──────────────────────────────────────────────────────────────

    /// <summary>A piece: its point (x, scaled y, z); whether it is there (0 not worked out yet, 1 there, 2 not); how
    /// much of the surface's rise above the surface at its point its top keeps; that surface height; and how far it
    /// sinks.</summary>
    private struct Piece
    {
        public float X, Y, Z;
        public byte State;
        public float Keep, Level, Sink;
    }

    /// <summary>The pieces of each landscape around the chunk column being profiled (see CellRange), points worked out
    /// up front and the rest when first needed.</summary>
    private sealed class PieceGrid
    {
        public int Cx0, Cz0, Cy0, Nx, Nz, Ny;
        public Piece[] Pieces = Array.Empty<Piece>();
        public int Index(int i, int k, int j) => (i - Cx0) + Nx * ((j - Cz0) + Nz * (k - Cy0));
    }

    private readonly PieceGrid[] _grids = Enumerable.Range(0, IslandField.Layers.Length).Select(_ => new PieceGrid()).ToArray();

    /// <summary>Loads landscape <paramref name="layer"/>'s pieces around chunk column (chunkX, chunkZ); whether any of
    /// them is there.</summary>
    private bool LoadGrid(int layer, int chunkX, int chunkZ)
    {
        var g = _grids[layer];
        (g.Cx0, g.Cz0, g.Cy0, g.Nx, g.Nz, g.Ny) = CellRange(layer, chunkX, chunkZ);
        int n = g.Nx * g.Nz * g.Ny;
        if (g.Pieces.Length < n) g.Pieces = new Piece[n];
        for (int k = 0; k < g.Ny; k++)
        for (int j = 0; j < g.Nz; j++)
        for (int i = 0; i < g.Nx; i++)
        {
            ref var p = ref g.Pieces[i + g.Nx * (j + g.Nz * k)];
            PointOf(layer, g.Cx0 + i, g.Cy0 + k, g.Cz0 + j, out p.X, out p.Y, out p.Z, out _);
            p.State = 0;
        }
        bool any = false;
        for (int k = 0; k < g.Ny; k++)
        for (int j = 0; j < g.Nz; j++)
        for (int i = 0; i < g.Nx; i++)
            any |= PieceAt(layer, g.Cx0 + i, g.Cy0 + k, g.Cz0 + j).State == 1;
        return any;
    }

    private void PointOf(int layer, int i, int k, int j, out float x, out float y, out float z, out ulong h)
    {
        float size = IslandField.Layers[layer].CellSize;
        h = IslandField.Hash(_seed, 500 + layer, i, k, j);
        x = (i + 0.1f + 0.8f * ((h & 0xFFFFF) / 1048576f)) * size;
        y = (k + 0.1f + 0.8f * (((h >> 20) & 0xFFFFF) / 1048576f)) * size;
        z = (j + 0.1f + 0.8f * (((h >> 40) & 0xFFFFF) / 1048576f)) * size;
    }

    /// <summary>Whether piece (i, k, j) of landscape <paramref name="layer"/> is there by its coverage alone.</summary>
    private bool Present(int layer, int i, int k, int j)
    {
        PointOf(layer, i, k, j, out float x, out _, out float z, out ulong h);
        return IslandField.Present(_seed, layer, x, z, (IslandField.Hash(h, 1, 0, 0, 0) & 0xFFFFFF) / 16777216f);
    }

    /// <summary>Piece (i, k, j) of landscape <paramref name="layer"/>'s grid, worked out if it isn't yet.</summary>
    private ref Piece PieceAt(int layer, int i, int k, int j)
    {
        var g = _grids[layer];
        ref var p = ref g.Pieces[g.Index(i, k, j)];
        if (p.State != 0) return ref p;
        PointOf(layer, i, k, j, out _, out _, out _, out ulong h);
        ulong h2 = IslandField.Hash(h, 1, 0, 0, 0);
        float jitter = (h2 & 0xFFFFFF) / 16777216f, sinkShare = ((h2 >> 24) & 0xFFFFFF) / 16777216f;
        float coverage = IslandField.Coverage(_seed, layer, p.X, p.Z);
        bool present = IslandField.Present(_seed, layer, p.X, p.Z, jitter);
        float rim = 1f - Smoothstep(0.75f, 1f, coverage); // 0 where the landscape is whole
        if (present)
        {
            float surface = Surface(layer, p.X, p.Z, out float level);
            p.Level = surface;
            present = ClearOfLandmass(layer, p.X, p.Z, surface, Underside(layer, p.X, p.Z, level));
        }
        p.State = present ? (byte)1 : (byte)2;
        p.Keep = 1f - Flatten * rim;
        p.Sink = sinkShare * (CoreSink + Sink * rim);
        return ref p;
    }

    /// <summary>Whether land of landscape <paramref name="layer"/> at (x, z), from <paramref name="underside"/> up to
    /// <paramref name="top"/>, is clear of the landmasses there.</summary>
    private bool ClearOfLandmass(int layer, float x, float z, float top, float underside)
    {
        if (layer == IslandField.Landmass || IslandField.Coverage(_seed, IslandField.Landmass, x, z) <= 0f) return true;
        float landTop = Surface(IslandField.Landmass, x, z, out float level);
        return layer == IslandField.Underworld
            ? top + IslandField.Clearance < Underside(IslandField.Landmass, x, z, level)
            : underside - IslandField.Clearance > landTop;
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
    private readonly bool[] _scrap = new bool[S * S * MaxSpans];

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

    /// <summary>A point on solid ground in the core of the landmass nearest (x, z): world X and Z, and its top.</summary>
    public (int X, int Top, int Z) GroundNear(float x, float z)
    {
        var (cx, cz) = IslandField.NearestLandmassCore(_seed, x, z);
        for (int ring = 0; ; ring++)
        for (int k = 0; k < System.Math.Max(1, ring * 8); k++)
        {
            float a = k * MathF.Tau / System.Math.Max(1, ring * 8);
            int px = (int)(cx + ring * 16f * MathF.Cos(a)), pz = (int)(cz + ring * 16f * MathF.Sin(a));
            if (TryTopAt(px, pz, out int top)) return (px, top, pz);
        }
    }

    private readonly bool[] _layerLive = new bool[IslandField.Layers.Length];

    // The smooth samples (see BuildProfile): every Step blocks, G by G of them over the chunk column.
    private const int Step = 4, G = S / Step + 1;
    private readonly float[] _gWarpX = new float[G * G], _gWarpZ = new float[G * G], _gLift = new float[G * G], _gWear = new float[G * G];
    private readonly float[][] _gTop = Enumerable.Range(0, IslandField.Layers.Length).Select(_ => new float[G * G]).ToArray();
    private readonly float[][] _gBottom = Enumerable.Range(0, IslandField.Layers.Length).Select(_ => new float[G * G]).ToArray();

    /// <summary>Samples <paramref name="grid"/> at block column (lx, lz) of the chunk column, bilinearly.</summary>
    private static float Lerp(float[] grid, int lx, int lz)
    {
        int gx = lx / Step, gz = lz / Step;
        float tx = (lx - gx * Step) * (1f / Step), tz = (lz - gz * Step) * (1f / Step);
        int c = gx + G * gz;
        float a = grid[c] + (grid[c + 1] - grid[c]) * tx;
        float b = grid[c + G] + (grid[c + G + 1] - grid[c + G]) * tx;
        return a + (b - a) * tz;
    }

    private void BuildProfile(int chunkX, int chunkZ)
    {
        _profileX = chunkX; _profileZ = chunkZ;
        float x0 = chunkX * S, z0 = chunkZ * S;
        Array.Clear(_spanCount);
        bool any = false;
        for (int layer = 0; layer < _layerLive.Length; layer++)
        {
            _layerLive[layer] = AnyPiece(layer, chunkX, chunkZ) && LoadGrid(layer, chunkX, chunkZ);
            any |= _layerLive[layer];
        }
        if (!any) return;

        // What varies smoothly over the chunk column, sampled every Step blocks and interpolated between: each live
        // landscape's surface and underside, and the wobble and crack width.
        for (int gz = 0; gz < G; gz++)
        for (int gx = 0; gx < G; gx++)
        {
            int c = gx + G * gz;
            float x = x0 + gx * Step, z = z0 + gz * Step;
            _gWarpX[c] = CrackWarp * _warpX.GetNoise(x, z);
            _gWarpZ[c] = CrackWarp * _warpZ.GetNoise(x, z);
            _gLift[c] = LayerWarp * _warpY.GetNoise(x, z);
            _gWear[c] = 0.6f + 0.4f * (_crack.GetNoise(x, z) + 1f);
            for (int layer = 0; layer < _layerLive.Length; layer++)
            {
                if (!_layerLive[layer]) continue;
                float top = Surface(layer, x, z, out float level);
                _gTop[layer][c] = top;
                _gBottom[layer][c] = Underside(layer, x, z, level);
            }
        }

        Span<(short Lo, short Hi)> found = stackalloc (short, short)[MaxSpans];
        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            int col = lx + S * lz;
            float x = x0 + lx, z = z0 + lz;
            int count = 0;
            for (int layer = 0; layer < _layerLive.Length; layer++)
                if (_layerLive[layer]) Column(layer, lx, lz, x, z, found, ref count);
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
            // Thin ones (after merging, so a run resting on another counts with it) worn away.
            int kept = 0;
            for (int i = 0; i < spans; i++)
            {
                var span = _spans[col * MaxSpans + i];
                if (span.Hi - span.Lo + 1 >= MinThickness) _spans[col * MaxSpans + kept++] = span;
            }
            spans = kept;
            _spanCount[col] = (byte)spans;

            _strata[col] = _surface.Strata(x, z);
            _patch[col] = _surface.Patch(x, z);
            _flora[col] = _surface.Flora(x, z);
            _woods[col] = _surface.Woods(x, z);
            _kind[col] = _surface.Kind(x, z);
        }

        DropScraps();
        for (int col = 0; col < S * S; col++)
        for (int sp = 0; sp < _spanCount[col]; sp++)
        {
            int hi = _spans[col * MaxSpans + sp].Hi;
            bool shaded = sp + 1 < _spanCount[col] && _spans[col * MaxSpans + sp + 1].Lo - hi < ShadeReach;
            _bare[col * MaxSpans + sp] = shaded ? Shaded : 0f;
        }
    }

    /// <summary>Drops scraps a block wide, where a piece wears away to nothing: runs with no run beside them along x,
    /// or none along z. Beyond the chunk column counts as beside.</summary>
    private void DropScraps()
    {
        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            int col = lx + S * lz;
            for (int sp = 0; sp < _spanCount[col]; sp++)
            {
                var (lo, hi) = _spans[col * MaxSpans + sp];
                bool alongX = lx == 0 || lx == S - 1 || Beside(col - 1, lo, hi) || Beside(col + 1, lo, hi);
                bool alongZ = lz == 0 || lz == S - 1 || Beside(col - S, lo, hi) || Beside(col + S, lo, hi);
                _scrap[col * MaxSpans + sp] = !(alongX && alongZ);
            }
        }
        for (int col = 0; col < S * S; col++)
        {
            int kept = 0;
            for (int sp = 0; sp < _spanCount[col]; sp++)
                if (!_scrap[col * MaxSpans + sp]) _spans[col * MaxSpans + kept++] = _spans[col * MaxSpans + sp];
            _spanCount[col] = (byte)kept;
        }
    }

    /// <summary>Whether column <paramref name="col"/> has a run overlapping heights lo..hi.</summary>
    private bool Beside(int col, short lo, short hi)
    {
        for (int sp = 0; sp < _spanCount[col]; sp++)
        {
            var other = _spans[col * MaxSpans + sp];
            if (other.Lo <= hi && other.Hi >= lo) return true;
        }
        return false;
    }

    /// <summary>Adds landscape <paramref name="layer"/>'s solid runs in block column (x, z) to <paramref name="found"/>:
    /// one per piece there that the column passes through, between the landscape's underside and the piece's top, and
    /// clear of the cracks.</summary>
    private void Column(int layer, int lx, int lz, float x, float z, Span<(short Lo, short Hi)> found, ref int count)
    {
        var l = IslandField.Layers[layer];
        var g = _grids[layer];
        float top = Lerp(_gTop[layer], lx, lz), bottom = Lerp(_gBottom[layer], lx, lz);

        // The pieces around the column (wobbled), from just under the underside to just over the top.
        const float V = IslandField.VerticalScale;
        float px = x + Lerp(_gWarpX, lx, lz), pz = z + Lerp(_gWarpZ, lx, lz);
        float lift = Lerp(_gLift, lx, lz);
        float size = l.CellSize;
        int gx = (int)MathF.Floor(px / size), gz = (int)MathF.Floor(pz / size);
        float tBottom = (bottom + lift) * V, tTop = (top + lift) * V;
        int gy0 = System.Math.Max((int)MathF.Floor(tBottom / size) - 1, g.Cy0);
        int gy1 = System.Math.Min((int)MathF.Floor(tTop / size) + 1, g.Cy0 + g.Ny - 1);
        int sites = 9 * System.Math.Min(gy1 - gy0 + 1, MaxRows);
        if (sites <= 0) return;
        Span<float> sxz = stackalloc float[9 * MaxRows], sx = stackalloc float[9 * MaxRows];
        Span<float> sy = stackalloc float[9 * MaxRows], sz = stackalloc float[9 * MaxRows];
        Span<int> index = stackalloc int[9 * MaxRows];
        for (int k = 0; k < sites; k++)
        {
            int at = g.Index(gx + k % 3 - 1, gy0 + k / 9, gz + (k / 3) % 3 - 1);
            ref var p = ref g.Pieces[at];
            index[k] = at;
            sx[k] = p.X; sy[k] = p.Y; sz[k] = p.Z;
            sxz[k] = (p.X - px) * (p.X - px) + (p.Z - pz) * (p.Z - pz);
        }

        // Up the column, piece by piece: the nearest point at t, the run where its piece is solid (at least the wear
        // from every face between it and another: each point's squared distance less the nearest's is linear in t,
        // so that is one run, worked out exactly), then on from where another point becomes nearest.
        float wearNoise = Lerp(_gWear, lx, lz);
        float across = l.Crack * wearNoise, up = l.Crack * CrackUp * wearNoise * V;
        // The top is banked down towards the cracks around the piece at the surface (all of them, so the bank is the
        // same for every run in the column and doesn't step where one crack takes over from another).
        float bank;
        {
            int i0 = 0;
            float best = float.MaxValue;
            for (int k = 0; k < sites; k++)
            {
                float d = sxz[k] + (tTop - sy[k]) * (tTop - sy[k]);
                if (d < best) { best = d; i0 = k; }
            }
            bank = Bank(CrackDistance(i0, sites, sxz, sx, sy, sz, across, up, tTop));
        }
        float t = tBottom;
        while (t < tTop && count < found.Length)
        {
            int i = 0;
            float best = float.MaxValue;
            for (int k = 0; k < sites; k++)
            {
                float d = sxz[k] + (t - sy[k]) * (t - sy[k]);
                if (d < best) { best = d; i = k; }
            }
            float lo = tBottom, hi = tTop, exit = tTop, loStacked = 0f, loClosed = 0f, loOpen = 0f;
            float tCeiling = tTop - bank * V; // how deep a crack is is measured from the banked top
            for (int j = 0; j < sites; j++)
            {
                if (j == i) continue;
                float ax = sx[j] - sx[i], ay = sy[j] - sy[i], az = sz[j] - sz[i];
                float len2 = ax * ax + ay * ay + az * az;
                float tilt = ay * ay / len2;
                float wear = across + (up - across) * tilt;
                float a = sxz[j] - sxz[i] + sy[j] * sy[j] - sy[i] * sy[i], b = -2f * ay; // dj^2 - di^2 = a + b t
                // A stacked crack shuts as it nears the surface (see CloseDepth).
                float stacked = Smoothstep(0.5f * Upright, 1.5f * Upright, tilt);
                float closed = b != 0f ? (1f - Smoothstep(CloseFrom, CloseDepth, (tCeiling + a / b) / V)) * stacked : 0f;
                float gap = 2f * MathF.Sqrt(len2) * wear;
                float need = gap * (1f - closed) - a;                                // b t >= need
                if (b > 0f) { if (need / b > lo) { lo = need / b; loStacked = stacked; loClosed = closed; loOpen = gap / b / V; } }
                else if (b < 0f) { hi = MathF.Min(hi, need / b); exit = MathF.Min(exit, -a / b); }
                else if (need > 0f) hi = lo;
            }
            t = MathF.Max(exit, t) + 0.01f;
            if (lo >= hi) continue;

            ref var piece = ref PieceAt(layer, g.Cx0 + index[i] % g.Nx, g.Cy0 + index[i] / (g.Nx * g.Nz), g.Cz0 + (index[i] / g.Nx) % g.Nz);
            if (piece.State != 1) continue;

            // Its top: the surface, worn down towards the surface at its point where the landscape is broken.
            float pieceTop = top - MathF.Max(0f, top - piece.Level) * (1f - piece.Keep);
            float tPieceTop = (pieceTop + lift) * V;
            if (lo >= tPieceTop) continue;
            bool atTop = hi >= tPieceTop;
            hi = MathF.Min(hi, tPieceTop);
            // Worn down from the top (and up from the underside) where it is narrow between two side cracks, or outside
            // the rounding where they meet: wedges and fins between cracks taper away.
            SideCracks(i, sites, sxz, sx, sy, sz, across, up, 0.5f * (lo + hi), out float side1, out float side2);
            float narrow = MinWidth - side1 - side2;
            if (side2 < EdgeRound)
            {
                float a = EdgeRound - side1, b = EdgeRound - side2;
                narrow = MathF.Max(narrow, MathF.Sqrt(a * a + b * b) - EdgeRound);
            }
            float taper = Taper * MathF.Max(narrow, 0f);
            float yLo = lo / V - lift, yHi = hi / V - lift;
            // The landscape's top and underside rounded off towards the side cracks: under a ceiling and over a floor
            // that every run keeps to, so a run whose top a leaning crack cuts off short isn't left standing as a wall.
            bool atBottom = lo <= tBottom;
            yHi = MathF.Min(yHi, pieceTop - bank);
            // (Measured at the run's own bottom, which is inside its piece.)
            SideCracks(i, sites, sxz, sx, sy, sz, across, up, lo, out float fromCrack, out _);
            yLo = MathF.Max(yLo, bottom + Round(fromCrack, UnderRound));
            // A thin slab against a crack, worn back to a rounded nose: from its underside if that is the landscape's,
            // else from the top. (The same however it is bounded, so land doesn't step where a crack above it meets
            // the surface; not over a stacked crack, where the slab thins out into the slope as the crack shuts.)
            if (!(atTop && atBottom) && yHi - yLo < 2f * Slab)
            {
                float th = yHi - yLo, worn = MathF.Sqrt(MathF.Max(th * th - Slab * Slab, 0f));
                if (atBottom) yLo = yHi - worn; else yHi = yLo + worn + (th - worn) * loStacked;
            }
            yHi -= taper;
            if (atBottom) yLo += taper;
            // (Down over a shut crack by as far as the piece under it could have kept it open, by its own depth, and
            // sink, so neither opens it.)
            yLo = MathF.Max(yLo - piece.Sink * 0.8f - (loOpen + CoreSink + Sink) * loClosed, IslandField.LowestBottom);
            yHi = MathF.Min(yHi - piece.Sink, IslandField.WorldTop - 1);
            int bLo = (int)MathF.Ceiling(yLo), bHi = (int)MathF.Floor(yHi);
            if (bHi >= bLo) found[count++] = ((short)bLo, (short)bHi);
        }
    }

    /// <summary>How far, in blocks, point i's piece is from its two nearest side cracks (see <see cref="Upright"/>)
    /// at scaled height t, less the wear: <paramref name="first"/> the nearer.</summary>
    private static void SideCracks(int i, int sites, Span<float> sxz, Span<float> sx, Span<float> sy, Span<float> sz,
                                   float across, float up, float t, out float first, out float second)
    {
        first = second = float.MaxValue;
        for (int j = 0; j < sites; j++)
        {
            if (j == i) continue;
            float ax = sx[j] - sx[i], ay = sy[j] - sy[i], az = sz[j] - sz[i];
            float len2 = ax * ax + ay * ay + az * az;
            float tilt = ay * ay / len2;
            if (tilt > Upright) continue;
            float wear = across + (up - across) * tilt;
            float dj = sxz[j] + (t - sy[j]) * (t - sy[j]), di = sxz[i] + (t - sy[i]) * (t - sy[i]);
            float d = (dj - di) / (2f * MathF.Sqrt(len2)) - wear;
            if (d < first) { second = first; first = d; }
            else if (d < second) second = d;
        }
    }

    /// <summary>How far across, in blocks, a point at scaled height t in point i's piece is from its nearest crack of
    /// any tilt, less a side crack's wear. (Across, not straight-line: a stacked crack just under the surface is far
    /// from where it comes out, so it doesn't flatten the land over it. The same wear for every crack, so where the
    /// piece at the surface changes, which is on a crack, both sides agree.)</summary>
    private static float CrackDistance(int i, int sites, Span<float> sxz, Span<float> sx, Span<float> sy, Span<float> sz,
                                       float across, float up, float t)
    {
        float nearest = float.MaxValue;
        for (int j = 0; j < sites; j++)
        {
            if (j == i) continue;
            float ax = sx[j] - sx[i], ay = sy[j] - sy[i], az = sz[j] - sz[i];
            float len2 = ax * ax + ay * ay + az * az;
            float flat = ax * ax + az * az;
            if (flat <= 0f) continue;
            float dj = sxz[j] + (t - sy[j]) * (t - sy[j]), di = sxz[i] + (t - sy[i]) * (t - sy[i]);
            nearest = MathF.Min(nearest, (dj - di) / (2f * MathF.Sqrt(flat)));
        }
        return nearest - across;
    }

    /// <summary>How far a top is worn down <paramref name="fromCrack"/> blocks from a side crack: a bank, steepening
    /// towards the crack.</summary>
    private static float Bank(float fromCrack)
    {
        if (fromCrack >= BankWidth) return 0f;
        float r = 1f - MathF.Max(fromCrack, 0f) / BankWidth;
        return BankDrop * r * r;
    }

    /// <summary>How far an underside is worn back <paramref name="fromCrack"/> blocks from a crack: a quarter
    /// circle of <paramref name="radius"/>, meeting the crack wall upright.</summary>
    private static float Round(float fromCrack, float radius)
    {
        if (fromCrack >= radius) return 0f;
        float r = radius - MathF.Max(fromCrack, 0f);
        return radius - MathF.Sqrt(radius * radius - r * r);
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>Clouds lie over the landmasses, thinner over the higher landscapes' clusters, and clear over the open sky
/// between: land shows from afar as a bank of cloud. Thread-safe: cloud tiles are built on worker threads.</summary>
public sealed class IslandCloudDensity : ICloudDensityMap
{
    private readonly ulong _seed;

    public IslandCloudDensity(ulong seed) => _seed = seed;

    public float Density(float x, float z)
        => MathF.Max(IslandField.Coverage(_seed, IslandField.Landmass, x, z),
                     MathF.Max(0.8f * IslandField.Coverage(_seed, 2, x, z), 0.6f * IslandField.Coverage(_seed, 3, x, z)));
}
