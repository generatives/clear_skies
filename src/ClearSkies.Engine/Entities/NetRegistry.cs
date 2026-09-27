using DefaultEcs;

namespace ClearSkies.Engine.Entities;

/// <summary>
/// Looks entities up by network ID, and hands out new IDs. Every entity with a <see cref="NetId"/> is registered when
/// the component is set and forgotten when it's removed or the entity is disposed. <see cref="WorldVolume"/> is
/// reserved for the static world; other IDs come from blocks the host hands out (see <see cref="AddIdBlock"/>).
/// </summary>
public sealed class NetRegistry : IDisposable
{
    /// <summary>The static world volume's ID, the same everywhere.</summary>
    public const uint WorldVolume = 1;

    /// <summary>IDs below this are reserved.</summary>
    public const uint FirstFreeId = 1024;

    /// <summary>IDs per block handed out by the host.</summary>
    public const uint BlockSize = 1024;

    private readonly Dictionary<uint, Entity> _byId = new();
    private readonly Queue<(uint Next, uint End)> _blocks = new();
    private (uint Next, uint End) _current;
    private readonly List<IDisposable> _subscriptions = new();

    public NetRegistry(World world)
    {
        World = world;
        _subscriptions.Add(world.SubscribeComponentAdded<NetId>((in Entity e, in NetId id) => Register(e, id.Value)));
        _subscriptions.Add(world.SubscribeComponentChanged<NetId>((in Entity e, in NetId old, in NetId id) =>
        {
            Forget(e, old.Value);
            Register(e, id.Value);
        }));
        _subscriptions.Add(world.SubscribeComponentRemoved<NetId>((in Entity e, in NetId id) => Forget(e, id.Value)));
        _subscriptions.Add(world.SubscribeEntityDisposed((in Entity e) =>
        {
            if (e.Has<NetId>()) Forget(e, e.Get<NetId>().Value);
        }));
    }

    /// <summary>Called when this machine needs another block of IDs: set by whoever hands them out (the host's
    /// allocator, or a client asking the host).</summary>
    public Func<(uint First, uint Count)>? RequestBlock { get; set; }

    public int Count => _byId.Count;

    public World World { get; }

    public bool TryGet(uint id, out Entity entity) => _byId.TryGetValue(id, out entity) && entity.IsAlive;

    public Entity? Find(uint id) => TryGet(id, out var e) ? e : null;

    public bool IsLive(uint id) => TryGet(id, out _);

    public IEnumerable<(uint Id, Entity Entity)> All => _byId.Select(p => (p.Key, p.Value));

    /// <summary>IDs left in the blocks this machine holds.</summary>
    public long IdsLeft => (_current.End - _current.Next) + _blocks.Sum(b => (long)(b.End - b.Next));

    /// <summary>Gives this machine IDs <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> − 1.</summary>
    public void AddIdBlock(uint first, uint count) => _blocks.Enqueue((first, first + count));

    /// <summary>A new ID no entity has had.</summary>
    public uint Allocate()
    {
        if (_current.Next >= _current.End)
        {
            if (_blocks.Count == 0)
            {
                var request = RequestBlock ?? throw new InvalidOperationException("No network IDs left and nowhere to get more.");
                var (first, count) = request();
                AddIdBlock(first, count);
            }
            _current = _blocks.Dequeue();
        }
        return _current.Next++;
    }

    private void Register(Entity e, uint id)
    {
        if (_byId.TryGetValue(id, out var existing) && existing.IsAlive && existing != e)
            throw new InvalidOperationException($"Network ID {id} is already used by another entity.");
        _byId[id] = e;
    }

    private void Forget(Entity e, uint id)
    {
        if (_byId.TryGetValue(id, out var existing) && existing == e) _byId.Remove(id);
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
    }
}

/// <summary>Hands out blocks of network IDs, never the same one twice. The host owns one; S5 keeps its next free ID in
/// the save so IDs stay unique across sessions.</summary>
public sealed class NetIdAllocator
{
    public NetIdAllocator(uint nextFree = NetRegistry.FirstFreeId) => NextFree = System.Math.Max(nextFree, NetRegistry.FirstFreeId);

    /// <summary>The first ID not handed out yet.</summary>
    public uint NextFree { get; private set; }

    public (uint First, uint Count) NextBlock()
    {
        uint first = NextFree;
        NextFree += NetRegistry.BlockSize;
        return (first, NetRegistry.BlockSize);
    }
}
