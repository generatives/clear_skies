namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Hands out runs of consecutive pages from a pool of <see cref="Capacity"/> pages, for <see cref="WebGpu.WorldMeshPool"/>
/// (a chunk mesh's quads sit in one run). Free runs are kept by size class and merged with their neighbours when freed,
/// so a remeshed chunk's run is reused and the pool doesn't splinter; past the highest run in use, pages are handed out
/// in order.
/// </summary>
public sealed class PageAllocator
{
    private const int Classes = 32;

    // Free runs below the top: their length by start, their start by end (one past the last page), and their starts
    // by size class (floor(log2(length))).
    private readonly Dictionary<int, int> _lengthAt = new();
    private readonly Dictionary<int, int> _startEndingAt = new();
    private readonly HashSet<int>[] _byClass = new HashSet<int>[Classes];
    private int _top;

    public PageAllocator(int capacity)
    {
        Capacity = capacity;
        for (int i = 0; i < Classes; i++) _byClass[i] = new HashSet<int>();
    }

    /// <summary>Pages in the pool; can be raised (the pool grew).</summary>
    public int Capacity { get; set; }

    /// <summary>Pages handed out and not yet freed.</summary>
    public int Used { get; private set; }

    /// <summary>One past the highest page ever in use now: the part of the pool that holds anything.</summary>
    public int Top => _top;

    /// <summary>The first page of a run of <paramref name="pages"/> (at least 1), or -1 if the pool has no room.</summary>
    public int Alloc(int pages)
    {
        for (int c = ClassOf(pages); c < Classes; c++)
        {
            foreach (int start in _byClass[c])
            {
                int length = _lengthAt[start];
                if (length < pages) continue; // the lowest class holds some shorter runs
                RemoveFree(start, length);
                if (length > pages) AddFree(start + pages, length - pages);
                Used += pages;
                return start;
            }
        }
        if (_top + pages > Capacity) return -1;
        int at = _top;
        _top += pages;
        Used += pages;
        return at;
    }

    public void Free(int start, int pages)
    {
        Used -= pages;
        int end = start + pages;
        if (_lengthAt.TryGetValue(end, out int after)) { RemoveFree(end, after); end += after; }
        if (_startEndingAt.TryGetValue(start, out int before)) { RemoveFree(before, start - before); start = before; }
        if (end == _top) _top = start;
        else AddFree(start, end - start);
    }

    private void AddFree(int start, int length)
    {
        _lengthAt[start] = length;
        _startEndingAt[start + length] = start;
        _byClass[ClassOf(length)].Add(start);
    }

    private void RemoveFree(int start, int length)
    {
        _lengthAt.Remove(start);
        _startEndingAt.Remove(start + length);
        _byClass[ClassOf(length)].Remove(start);
    }

    private static int ClassOf(int length) => 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)length);
}
