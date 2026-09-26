using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// A queue of light slots taken nearest the camera first, by distance buckets <see cref="BucketSize"/> wide measured
/// from a reference point. Adding is O(1) and taking walks the buckets outward, so a queue of hundreds of thousands
/// of bricks costs nothing per frame beyond what is taken. The buckets are rebuilt from the camera's position only
/// once it has moved <see cref="RecentreDistance"/> from the reference: until then the order is off by at most that.
/// Entries aren't removed when they stop needing work; the taker checks each one and drops what is stale.
/// </summary>
internal sealed class NearestQueue
{
    private const float BucketSize = 8f;         // a brick
    private const int Buckets = 4096;             // out to 32 km; farther shares the last bucket
    private const float RecentreDistance = 32f;

    private readonly List<int>[] _buckets = new List<int>[Buckets];
    private readonly Func<int, Vector3D<float>> _centre;
    private readonly List<int> _scratch = new();
    private Vector3D<float> _ref;
    private int _first = Buckets; // no nonempty bucket before this
    private int _count;

    /// <param name="centre">Where a slot's brick is (world space).</param>
    public NearestQueue(Func<int, Vector3D<float>> centre)
    {
        _centre = centre;
        for (int i = 0; i < Buckets; i++) _buckets[i] = new List<int>();
    }

    public int Count => _count;

    public void Add(int slot)
    {
        int b = Bucket(slot);
        _buckets[b].Add(slot);
        if (b < _first) _first = b;
        _count++;
    }

    /// <summary>Puts back a slot just taken from <paramref name="bucket"/>, without measuring it again.</summary>
    public void PutBack(int slot, int bucket)
    {
        _buckets[bucket].Add(slot);
        if (bucket < _first) _first = bucket;
        _count++;
    }

    /// <summary>Re-buckets everything around <paramref name="camPos"/> if it has moved far enough from the last
    /// reference, dropping the entries <paramref name="keep"/> rejects.</summary>
    public void Recentre(Vector3D<float> camPos, Func<int, bool> keep)
    {
        if (Vector3D.DistanceSquared(camPos, _ref) < RecentreDistance * RecentreDistance) return;
        _ref = camPos;
        if (_count == 0) return;
        _scratch.Clear();
        for (int i = _first; i < Buckets; i++)
        {
            _scratch.AddRange(_buckets[i]);
            _buckets[i].Clear();
        }
        _first = Buckets;
        _count = 0;
        foreach (int slot in _scratch) if (keep(slot)) Add(slot);
    }

    /// <summary>Takes a slot from the nearest nonempty bucket.</summary>
    public bool TryTake(out int slot) => TryTake(out slot, out _);

    /// <summary>Takes a slot from the nearest nonempty bucket, and says which (for <see cref="PutBack"/>).</summary>
    public bool TryTake(out int slot, out int bucket)
    {
        for (; _first < Buckets; _first++)
        {
            var b = _buckets[_first];
            if (b.Count == 0) continue;
            slot = b[^1];
            b.RemoveAt(b.Count - 1);
            _count--;
            bucket = _first;
            return true;
        }
        slot = -1;
        bucket = -1;
        return false;
    }

    private int Bucket(int slot)
    {
        float d = Vector3D.Distance(_centre(slot), _ref);
        return System.Math.Min((int)(d / BucketSize), Buckets - 1);
    }
}
