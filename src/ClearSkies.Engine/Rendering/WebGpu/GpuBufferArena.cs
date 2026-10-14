using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// One GPU buffer shared out in runs of fixed-size elements, so everything kept in it can be bound with one bind group
/// (e.g. every chunk mesh's <see cref="ChunkQuad"/>s, read by vs_chunk through each draw's record). Each run is a
/// <see cref="BufferSpan"/>, given back when it's disposed. Runs are handed out first-fit from a free list that merges
/// neighbours when freed; when no run fits, the buffer doubles (its contents copied over on the GPU) up to the device's
/// limit for its kind of binding. A run can be reused at once when freed: the queue orders writes after the frames
/// already submitted, and a frame only draws what's still alive. Main thread only.
/// </summary>
public sealed class GpuBufferArena : IDisposable
{
    private readonly GpuContext _ctx;
    private readonly BufferUsage _usage;
    private readonly long _maxElements;
    private readonly List<(uint Start, uint Count)> _free = new(); // sorted by start, never adjacent
    private uint _capacity;

    /// <param name="elementBytes">Each element's size: runs start and end on whole elements (a multiple of 4, as buffer
    /// writes need).</param>
    /// <param name="usage">How the buffer is bound (e.g. <see cref="BufferUsage.Storage"/>); copies in and out are
    /// added, for writes and growing.</param>
    /// <param name="maxBytes">The largest the buffer may grow: the device's limit for how it's bound (e.g.
    /// <c>MaxStorageBufferBindingSize</c>), which is also capped at its largest buffer.</param>
    public GpuBufferArena(GpuContext ctx, uint elementBytes, BufferUsage usage, uint initialElements, ulong maxBytes)
    {
        if (elementBytes == 0 || elementBytes % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(elementBytes), "Elements must be a multiple of 4 bytes.");
        _ctx = ctx;
        ElementBytes = elementBytes;
        _usage = usage | BufferUsage.CopyDst | BufferUsage.CopySrc;
        _maxElements = (long)(System.Math.Min(maxBytes, ctx.AdapterLimits.MaxBufferSize) / elementBytes);
        _capacity = (uint)System.Math.Min(initialElements, _maxElements);
        Buffer = GpuBuffer.Create(ctx, (ulong)_capacity * elementBytes, _usage);
        _free.Add((0, _capacity));
    }

    public uint ElementBytes { get; }

    /// <summary>The buffer: replaced when it grows (see <see cref="Version"/>), so take it fresh rather than keep it.</summary>
    public GpuBuffer Buffer { get; private set; }

    /// <summary>Changes whenever <see cref="Buffer"/> is replaced, so bind groups over it are rebuilt.</summary>
    public int Version { get; private set; }

    public uint CapacityElements => _capacity;
    public long UsedElements { get; private set; }

    /// <summary>A run of <paramref name="count"/> elements (at least one), or null if even the largest buffer allowed
    /// can't fit it. Its contents are whatever was there before: write them with <see cref="BufferSpan.Write{T}"/>.</summary>
    public BufferSpan? Allocate(uint count)
    {
        if (count == 0) throw new ArgumentOutOfRangeException(nameof(count), "A run holds at least one element.");
        while (true)
        {
            for (int i = 0; i < _free.Count; i++)
            {
                var (start, n) = _free[i];
                if (n < count) continue;
                if (n == count) _free.RemoveAt(i);
                else _free[i] = (start + count, n - count);
                UsedElements += count;
                return new BufferSpan(this, start, count);
            }
            if (!Grow(count)) return null;
        }
    }

    /// <summary>A run holding <paramref name="data"/>, whole elements of it (null if there's no room, as
    /// <see cref="Allocate"/>).</summary>
    public unsafe BufferSpan? Upload<T>(ReadOnlySpan<T> data) where T : unmanaged
    {
        ulong bytes = (ulong)data.Length * (ulong)sizeof(T);
        if (bytes == 0 || bytes % ElementBytes != 0)
            throw new ArgumentException($"Expected a whole number of {ElementBytes}-byte elements, got {bytes} bytes.", nameof(data));
        var span = Allocate((uint)(bytes / ElementBytes));
        span?.Write(data);
        return span;
    }

    internal void Free(uint start, uint count)
    {
        UsedElements -= count;
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

    /// <summary>Doubles the buffer (at least enough for <paramref name="need"/> more elements), copying it over.</summary>
    private bool Grow(uint need)
    {
        long target = System.Math.Max((long)_capacity * 2, (long)_capacity + need);
        target = System.Math.Min(target, _maxElements);
        if (target <= _capacity) return false;
        var bigger = GpuBuffer.Create(_ctx, (ulong)target * ElementBytes, _usage);
        _ctx.CopyBufferToBuffer(Buffer, bigger, (ulong)_capacity * ElementBytes);
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

/// <summary>A run of a <see cref="GpuBufferArena"/>'s buffer: <see cref="Count"/> elements from <see cref="Start"/>
/// (what a shader indexes it by; <see cref="Offset"/> and <see cref="Bytes"/> in bytes). Stays put when the buffer
/// grows; disposing it gives the run back.</summary>
public sealed class BufferSpan : IDisposable
{
    private GpuBufferArena? _arena;

    internal BufferSpan(GpuBufferArena arena, uint start, uint count)
    {
        _arena = arena;
        Start = start;
        Count = count;
    }

    public uint Start { get; }
    public uint Count { get; }

    public ulong Offset => (ulong)Start * Arena.ElementBytes;
    public ulong Bytes => (ulong)Count * Arena.ElementBytes;

    public GpuBufferArena Arena => _arena ?? throw new ObjectDisposedException(nameof(BufferSpan));

    /// <summary>The buffer this run is in now (it's replaced when the arena grows).</summary>
    public GpuBuffer Buffer => Arena.Buffer;

    /// <summary>Writes <paramref name="data"/> into the run, from element <paramref name="firstElement"/> of it.</summary>
    public unsafe void Write<T>(ReadOnlySpan<T> data, uint firstElement = 0) where T : unmanaged
    {
        var arena = Arena;
        ulong offset = (ulong)firstElement * arena.ElementBytes;
        if (offset + (ulong)data.Length * (ulong)sizeof(T) > Bytes)
            throw new ArgumentOutOfRangeException(nameof(data), "The write runs past the end of the span.");
        arena.Buffer.Write(Offset + offset, data);
    }

    public void Dispose()
    {
        if (_arena is null) return;
        _arena.Free(Start, Count);
        _arena = null;
    }
}
