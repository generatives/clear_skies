namespace ClearSkies.Engine.Core;

/// <summary>
/// Reusable arrays of one fixed length, for data that is allocated and dropped at a steady rate, such as a chunk's
/// blocks while the world streams: a returned array goes to the next caller instead of the garbage collector, so a
/// steady stream stops allocating. Thread-safe. Rented arrays are not cleared: they hold whatever their last user left.
/// </summary>
public sealed class FixedArrayPool<T>
{
    private readonly Stack<T[]> _free = new();

    /// <param name="length">The length of every array.</param>
    /// <param name="maxKept">How many returned arrays are kept for reuse; any more are left to the collector.</param>
    /// <param name="pinned">Whether new arrays go on the pinned object heap, where a collection never copies them: for
    /// large arrays that live long.</param>
    public FixedArrayPool(int length, int maxKept, bool pinned = false)
    {
        Length = length;
        MaxKept = maxKept;
        Pinned = pinned;
    }

    public int Length { get; }
    public int MaxKept { get; }
    public bool Pinned { get; }

    /// <summary>Arrays waiting to be reused.</summary>
    public int Count { get { lock (_free) return _free.Count; } }

    /// <summary>A returned array if there is one, else a new one (uninitialized).</summary>
    public T[] Rent()
    {
        lock (_free)
            if (_free.TryPop(out var a)) return a;
        return GC.AllocateUninitializedArray<T>(Length, Pinned);
    }

    /// <summary>Gives <paramref name="array"/> back for reuse (null is ignored). The caller must not use it again.</summary>
    public void Return(T[]? array)
    {
        if (array == null) return;
        if (array.Length != Length)
            throw new ArgumentException($"Expected an array of {Length}, got {array.Length}.", nameof(array));
        lock (_free)
            if (_free.Count < MaxKept) _free.Push(array);
    }
}
