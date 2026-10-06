using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// One storage buffer holding every chunk mesh's <see cref="ChunkQuad"/>s, each mesh a run of it, so chunk draws share
/// one bind group (vs_chunk reads a draw's run through its record). Runs are handed out first-fit from a free list that
/// merges neighbours when freed; when no run fits, the buffer doubles (its contents copied over on the GPU) up to the
/// device's storage binding limit. A run can be reused at once when freed: the queue orders writes after the frames
/// already submitted, and a frame only draws meshes still alive.
/// </summary>
internal sealed class ChunkQuadArena : IDisposable
{
    private readonly GpuContext _ctx;
    private readonly long _maxQuads;
    private readonly List<(uint Start, uint Count)> _free = new(); // sorted by start, never adjacent
    private uint _capacity;

    public ChunkQuadArena(GpuContext ctx, uint initialQuads)
    {
        _ctx = ctx;
        var limits = ctx.AdapterLimits;
        _maxQuads = (long)(System.Math.Min(limits.MaxStorageBufferBindingSize, limits.MaxBufferSize) / ChunkQuad.SizeBytes);
        _capacity = (uint)System.Math.Min(initialQuads, _maxQuads);
        Buffer = GpuBuffer.Create(ctx, (ulong)_capacity * ChunkQuad.SizeBytes,
                                  BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
        _free.Add((0, _capacity));
    }

    /// <summary>The buffer: replaced when it grows (see <see cref="Version"/>).</summary>
    public GpuBuffer Buffer { get; private set; }

    /// <summary>Changes whenever <see cref="Buffer"/> is replaced, so bind groups over it are rebuilt.</summary>
    public int Version { get; private set; }

    public uint CapacityQuads => _capacity;
    public long UsedQuads { get; private set; }

    /// <summary>A run of <paramref name="count"/> quads (its first quad), or -1 if even the largest buffer the device
    /// allows can't fit it.</summary>
    public long Alloc(uint count)
    {
        while (true)
        {
            for (int i = 0; i < _free.Count; i++)
            {
                var (start, n) = _free[i];
                if (n < count) continue;
                if (n == count) _free.RemoveAt(i);
                else _free[i] = (start + count, n - count);
                UsedQuads += count;
                return start;
            }
            if (!Grow(count)) return -1;
        }
    }

    public void Free(uint start, uint count)
    {
        if (count == 0) return;
        UsedQuads -= count;
        int i = 0;
        while (i < _free.Count && _free[i].Start < start) i++;
        _free.Insert(i, (start, count));
        // Merge with the following run, then the preceding one.
        if (i + 1 < _free.Count && _free[i].Start + _free[i].Count == _free[i + 1].Start)
        {
            _free[i] = (_free[i].Start, _free[i].Count + _free[i + 1].Count);
            _free.RemoveAt(i + 1);
        }
        if (i > 0 && _free[i - 1].Start + _free[i - 1].Count == _free[i].Start)
        {
            _free[i - 1] = (_free[i - 1].Start, _free[i - 1].Count + _free[i].Count);
            _free.RemoveAt(i);
        }
    }

    /// <summary>Doubles the buffer (at least enough for <paramref name="need"/> more quads), copying it over.</summary>
    private bool Grow(uint need)
    {
        long target = System.Math.Max((long)_capacity * 2, (long)_capacity + need);
        target = System.Math.Min(target, _maxQuads);
        if (target <= _capacity) return false;
        var bigger = GpuBuffer.Create(_ctx, (ulong)target * ChunkQuad.SizeBytes,
                                      BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc);
        _ctx.CopyBufferToBuffer(Buffer, bigger, (ulong)_capacity * ChunkQuad.SizeBytes);
        Buffer.Dispose();
        Buffer = bigger;
        Version++;
        // The new space is free, merged with a free run at the old end.
        uint added = (uint)(target - _capacity);
        if (_free.Count > 0 && _free[^1].Start + _free[^1].Count == _capacity)
            _free[^1] = (_free[^1].Start, _free[^1].Count + added);
        else _free.Add((_capacity, added));
        _capacity = (uint)target;
        return true;
    }

    public void Dispose() => Buffer.Dispose();
}
