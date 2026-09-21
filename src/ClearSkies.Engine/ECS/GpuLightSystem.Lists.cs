using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

// Ray-traced lighting: per-chunk light and grid lists (clustered shading with the chunk as the cluster, from the
// design doc). Each frame, every chunk with a brick in that frame's work gets a list of the grids its rays can hit and
// the lamps that can reach it, so a voxel's cost depends on what is near it, not on how many ships and lamps exist.
// The lists are rebuilt from scratch every frame for just those chunks: ships and their lamps move, and the work is
// only ever a few thousand bricks, so there is nothing worth keeping between frames. Layout: GpuRayLightPass's WGSL.
public sealed partial class GpuLightSystem
{
    // How far a voxel's short rays reach: bounce rays 16 voxels, lamp rays at most a lamp's level (15) from the lamp
    // cell's centre. A ship within this of a chunk can block its bounce or lamp rays; farther ships only matter for
    // sun rays, which are tested separately along the sun direction.
    private const float ListReach = 17f;

    private uint[] _listWords = new uint[65536];
    private int _listCount;
    private int[] _listStamp = Array.Empty<int>();   // per chunk-table entry: the frame its list was last built
    private int[] _lampStamp = Array.Empty<int>();   // per lamp: the chunk it was last listed for (dedupes cells)
    private int _lampStampGen;
    private int[] _litIndex = Array.Empty<int>();    // grid index -> index into _lit, or -1
    private readonly Dictionary<ChunkPosition, List<int>> _lampCells = new();
    private readonly Stack<List<int>> _cellPool = new();

    private int _dbgListChunks, _dbgListGrids, _dbgListLamps;

    /// <summary>Builds and uploads this frame's lists for the chunks of the given light slots (every slot any pass
    /// touches this frame). Uses this frame's poses and the ships' current world bounds.</summary>
    private void BuildLists(ReadOnlySpan<uint> slots, Vector3D<float> sunDir)
    {
        _dbgListChunks = _dbgListGrids = _dbgListLamps = 0;
        if (slots.IsEmpty) return; // no dispatch reads them this frame
        int tableCap = _store.TableCapacity;
        if (_listStamp.Length < tableCap) Array.Resize(ref _listStamp, tableCap);
        int lampBase = 2 + tableCap;

        // Empty list, then every chunk's head pointing at it until its own list is built.
        _listCount = 0;
        EnsureListWords(lampBase + 8 * _lamps.Count);
        Array.Clear(_listWords, 0, lampBase);
        _listCount = lampBase;

        // Lamp records, and a world-chunk hash of which lamps reach which chunks.
        foreach (var cell in _lampCells.Values) { cell.Clear(); _cellPool.Push(cell); }
        _lampCells.Clear();
        if (_lampStamp.Length < _lamps.Count) _lampStamp = new int[_lamps.Count * 2];
        for (int i = 0; i < _lamps.Count; i++)
        {
            var l = _lamps[i];
            _listWords[_listCount++] = BitConverter.SingleToUInt32Bits(l.World.X);
            _listWords[_listCount++] = BitConverter.SingleToUInt32Bits(l.World.Y);
            _listWords[_listCount++] = BitConverter.SingleToUInt32Bits(l.World.Z);
            _listWords[_listCount++] = BitConverter.SingleToUInt32Bits(l.Level);
            _listWords[_listCount++] = BitConverter.SingleToUInt32Bits(l.Color.X);
            _listWords[_listCount++] = BitConverter.SingleToUInt32Bits(l.Color.Y);
            _listWords[_listCount++] = BitConverter.SingleToUInt32Bits(l.Color.Z);
            _listWords[_listCount++] = 0u;

            var (a, b) = LampReach(l);
            int x0 = FloorDiv(a.X), y0 = FloorDiv(a.Y), z0 = FloorDiv(a.Z);
            int x1 = FloorDiv(b.X), y1 = FloorDiv(b.Y), z1 = FloorDiv(b.Z);
            for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var key = new ChunkPosition(x, y, z);
                if (!_lampCells.TryGetValue(key, out var cell))
                    _lampCells[key] = cell = _cellPool.Count > 0 ? _cellPool.Pop() : new List<int>();
                cell.Add(i);
            }
        }

        int maxIndex = 0;
        foreach (var lg in _lit) maxIndex = System.Math.Max(maxIndex, lg.Handle.Index + 1);
        if (_litIndex.Length < maxIndex) _litIndex = new int[maxIndex * 2];
        Array.Fill(_litIndex, -1);
        for (int i = 0; i < _lit.Count; i++) _litIndex[_lit[i].Handle.Index] = i;

        foreach (uint s in slots)
        {
            int slot = (int)s;
            int gi = _store.SlotGrid[slot];
            if (gi < 0 || gi >= _litIndex.Length || _litIndex[gi] < 0) continue;
            var lg = _lit[_litIndex[gi]];
            if (!lg.Handle.Chunks.TryGetValue(_store.SlotChunk[slot], out var rec)) continue;
            if (_listStamp[rec.TableIndex] == _frame) continue;
            _listStamp[rec.TableIndex] = _frame;
            _listWords[2 + rec.TableIndex] = (uint)_listCount;
            AppendChunkList(lg, rec.Pos, sunDir);
            _dbgListChunks++;
        }

        _rayLight.UploadLists(_listWords.AsSpan(0, _listCount), lampBase);
    }

    /// <summary>Appends chunk <paramref name="pos"/> of <paramref name="lg"/>'s list: the grids its rays can hit, then
    /// the lamps whose reach overlaps it.</summary>
    private void AppendChunkList(in LitGrid lg, ChunkPosition pos, Vector3D<float> sunDir)
    {
        ChunkWorldBox(lg, pos, out var cMin, out var cMax);
        var reach = new Vector3D<float>(ListReach);
        // A sun ray leaves from anywhere in the chunk: the chunk box swept toward the sun, tested as a ray from its
        // centre against each ship box grown by the chunk's half size (plus the sample jitter).
        var centre = (cMin + cMax) * 0.5f;
        var half = (cMax - cMin) * 0.5f + new Vector3D<float>(1f);

        int countAt = AppendListWord(0);
        uint count = 0;
        foreach (var other in _lit)
        {
            var h = other.Handle;
            bool include = h.IsWorld || h == lg.Handle;
            if (!include && h.HasSolid)
            {
                var st = _gridStates[h];
                include = Overlaps(cMin - reach, cMax + reach, st.CurWorldMin, st.CurWorldMax)
                       || RayHitsBox(centre, -sunDir, st.CurWorldMin - half, st.CurWorldMax + half);
            }
            if (!include) continue;
            AppendListWord((uint)h.Index);
            count++;
        }
        _listWords[countAt] = count;
        _dbgListGrids += (int)count;

        countAt = AppendListWord(0);
        count = 0;
        _lampStampGen++;
        int x0 = FloorDiv(cMin.X), y0 = FloorDiv(cMin.Y), z0 = FloorDiv(cMin.Z);
        int x1 = FloorDiv(cMax.X - 0.001f), y1 = FloorDiv(cMax.Y - 0.001f), z1 = FloorDiv(cMax.Z - 0.001f);
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            if (!_lampCells.TryGetValue(new ChunkPosition(x, y, z), out var cell)) continue;
            foreach (int i in cell)
            {
                if (_lampStamp[i] == _lampStampGen) continue;
                _lampStamp[i] = _lampStampGen;
                var (a, b) = LampReach(_lamps[i]);
                if (!Overlaps(a, b, cMin, cMax)) continue;
                AppendListWord((uint)i);
                count++;
            }
        }
        _listWords[countAt] = count;
        _dbgListLamps += (int)count;
    }

    /// <summary>World-space box a lamp's light can reach (its level is its reach radius, from the cell centre).</summary>
    private static (Vector3D<float>, Vector3D<float>) LampReach(WorldLamp l)
    {
        var r = new Vector3D<float>(l.Level + 0.5f);
        return (l.World - r, l.World + r);
    }

    /// <summary>World-space bounds of chunk <paramref name="pos"/> of a grid, under its current pose.</summary>
    private static void ChunkWorldBox(in LitGrid lg, ChunkPosition pos, out Vector3D<float> mn, out Vector3D<float> mx)
    {
        var lo = pos.WorldOrigin;
        if (lg.Handle.IsWorld) { mn = lo; mx = lo + new Vector3D<float>(S); return; }
        mn = new Vector3D<float>(float.MaxValue);
        mx = new Vector3D<float>(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var w = lg.VoxelToWorld.TransformPoint(lo + new Vector3D<float>((c & 1) * S, ((c >> 1) & 1) * S, (c >> 2) * S));
            mn = Vector3D.Min(mn, w);
            mx = Vector3D.Max(mx, w);
        }
    }

    /// <summary>Whether the ray o + t·d, t ≥ 0, meets the box.</summary>
    private static bool RayHitsBox(Vector3D<float> o, Vector3D<float> d, Vector3D<float> mn, Vector3D<float> mx)
    {
        float t0 = 0f, t1 = float.MaxValue;
        return Slab(o.X, d.X, mn.X, mx.X, ref t0, ref t1) && Slab(o.Y, d.Y, mn.Y, mx.Y, ref t0, ref t1)
            && Slab(o.Z, d.Z, mn.Z, mx.Z, ref t0, ref t1);
    }

    private static bool Slab(float o, float d, float lo, float hi, ref float t0, ref float t1)
    {
        if (MathF.Abs(d) < 1e-8f) return o >= lo && o <= hi;
        float ta = (lo - o) / d, tb = (hi - o) / d;
        if (ta > tb) (ta, tb) = (tb, ta);
        t0 = MathF.Max(t0, ta);
        t1 = MathF.Min(t1, tb);
        return t0 <= t1;
    }

    private static int FloorDiv(float v) => (int)MathF.Floor(v / S);

    private int AppendListWord(uint w)
    {
        EnsureListWords(_listCount + 1);
        _listWords[_listCount] = w;
        return _listCount++;
    }

    private void EnsureListWords(int n)
    {
        if (_listWords.Length < n) Array.Resize(ref _listWords, System.Math.Max(n, _listWords.Length * 2));
    }
}
