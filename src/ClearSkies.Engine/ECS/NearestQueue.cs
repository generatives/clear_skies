using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// A queue of light slots taken nearest the camera first, by distance buckets <see cref="BucketSize"/> wide measured
/// from a reference point. Adding is O(1) and taking walks the buckets outward, so a queue of hundreds of thousands
/// of bricks costs nothing per frame beyond what is taken. The buckets are rebuilt from the camera's position only
/// once it has moved <see cref="RecentreDistance"/> from the reference: until then the order is off by at most that.
/// A rebuild is spread over frames (<see cref="RebucketPerFrame"/> entries each, nearest first by the old order), so
/// a big queue doesn't cost a frame spike every time the camera moves on; entries not re-bucketed yet can't be taken.
/// Entries aren't removed when they stop needing work; the taker checks each one and drops what is stale.
/// </summary>
internal sealed class NearestQueue
{
    private const float BucketSize = 8f;         // a brick
    private const int Buckets = 4096;             // out to 32 km; farther shares the last bucket
    private const float RecentreDistance = 32f;
    private const int RebucketPerFrame = 32768;

    private readonly List<int>[] _buckets = new List<int>[Buckets];
    private readonly Func<int, Vector3D<float>> _centre;
    private readonly List<int> _pending = new(); // waiting to be re-bucketed around _ref, from _pendingAt on
    private int _pendingAt;
    private readonly List<int> _rest = new();
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

    /// <summary>Starts re-bucketing everything around <paramref name="camPos"/> if it has moved far enough from the
    /// last reference, and carries on with what is still waiting, dropping the entries <paramref name="keep"/>
    /// rejects. Call once a frame, before taking.</summary>
    public void Recentre(Vector3D<float> camPos, Func<int, bool> keep)
    {
        if (Vector3D.DistanceSquared(camPos, _ref) >= RecentreDistance * RecentreDistance)
        {
            _ref = camPos;
            // Everything goes back to waiting, nearest (by the old reference) first; anything still waiting from
            // the last rebuild goes after it.
            _rest.Clear();
            for (int i = _pendingAt; i < _pending.Count; i++) _rest.Add(_pending[i]);
            _pending.Clear();
            _pendingAt = 0;
            for (int i = _first; i < Buckets; i++)
            {
                _pending.AddRange(_buckets[i]);
                _buckets[i].Clear();
            }
            _pending.AddRange(_rest);
            _first = Buckets;
        }
        int end = System.Math.Min(_pending.Count, _pendingAt + RebucketPerFrame);
        for (; _pendingAt < end; _pendingAt++)
        {
            int slot = _pending[_pendingAt];
            _count--;
            if (keep(slot)) Add(slot);
        }
        if (_pendingAt == _pending.Count) { _pending.Clear(); _pendingAt = 0; }
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
