using DefaultEcs;

namespace ClearSkies.Engine.Entities;

/// <summary>
/// Looks entities up by entity ID, and hands out new IDs. Every entity with a <see cref="EntityId"/> is registered when
/// the component is set and forgotten when it's removed or the entity is disposed. <see cref="WorldVolume"/> is
/// reserved for the static world; other IDs come from blocks the host hands out (see <see cref="AddIdBlock"/>).
/// </summary>
public sealed class EntityRegistry : IDisposable
{
    /// <summary>The static world volume's ID, the same everywhere.</summary>
    public static readonly EntityId WorldVolume = new(1);

    /// <summary>IDs below this are reserved.</summary>
    public const uint FirstFreeId = 1024;

    /// <summary>IDs per block handed out by the host.</summary>
    public const uint BlockSize = 1024;

    private readonly Dictionary<EntityId, Entity> _byId = new();
    private readonly Queue<(uint Next, uint End)> _blocks = new();
    private (uint Next, uint End) _current;
    private readonly List<IDisposable> _subscriptions = new();

    public EntityRegistry(World world)
    {
        _subscriptions.Add(world.SubscribeComponentAdded<EntityId>((in Entity e, in EntityId id) => Register(e, id)));
        _subscriptions.Add(world.SubscribeComponentChanged<EntityId>((in Entity e, in EntityId old, in EntityId id) =>
        {
            Forget(e, old);
            Register(e, id);
        }));
        _subscriptions.Add(world.SubscribeComponentRemoved<EntityId>((in Entity e, in EntityId id) => Forget(e, id)));
        _subscriptions.Add(world.SubscribeEntityDisposed((in Entity e) =>
        {
            if (e.Has<EntityId>()) Forget(e, e.Get<EntityId>());
        }));
    }

    /// <summary>Called when this machine needs another block of IDs: set by whoever hands them out (the host's
    /// allocator, or a client asking the host).</summary>
    public Func<(uint First, uint Count)>? RequestBlock { get; set; }

    public int Count => _byId.Count;

    public bool TryGet(EntityId id, out Entity entity) => _byId.TryGetValue(id, out entity) && entity.IsAlive;

    public Entity? Find(EntityId id) => TryGet(id, out var e) ? e : null;

    public bool IsLive(EntityId id) => TryGet(id, out _);

    public IEnumerable<(EntityId Id, Entity Entity)> All => _byId.Select(p => (p.Key, p.Value));

    /// <summary>IDs left in the blocks this machine holds.</summary>
    public long IdsLeft => (_current.End - _current.Next) + _blocks.Sum(b => (long)(b.End - b.Next));

    /// <summary>Gives this machine IDs <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> − 1.</summary>
    public void AddIdBlock(uint first, uint count) => _blocks.Enqueue((first, first + count));

    /// <summary>A new ID no entity has had.</summary>
    public EntityId Allocate()
    {
        if (_current.Next >= _current.End)
        {
            // Normally the next block is already waiting: a client asks the host for one while it still has a quarter
            // of its current block left, so spawning never waits on a round trip. Only if none came in time (or on the
            // host, which hands them out itself) is one requested here.
            if (_blocks.Count == 0)
            {
                var request = RequestBlock ?? throw new InvalidOperationException("No entity IDs left and nowhere to get more.");
                var (first, count) = request();
                AddIdBlock(first, count);
            }
            _current = _blocks.Dequeue();
        }
        return new EntityId(_current.Next++);
    }

    private void Register(Entity e, EntityId id)
    {
        if (_byId.TryGetValue(id, out var existing) && existing.IsAlive && existing != e)
            throw new InvalidOperationException($"Entity ID {id} is already used by another entity.");
        _byId[id] = e;
    }

    private void Forget(Entity e, EntityId id)
    {
        if (_byId.TryGetValue(id, out var existing) && existing == e) _byId.Remove(id);
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
    }
}

/// <summary>Hands out blocks of entity IDs, never the same one twice. The host owns one; S5 keeps its next free ID in
/// the save so IDs stay unique across sessions.</summary>
public sealed class EntityIdAllocator
{
    public EntityIdAllocator(uint nextFree = EntityRegistry.FirstFreeId) => NextFree = System.Math.Max(nextFree, EntityRegistry.FirstFreeId);

    /// <summary>The first ID not handed out yet.</summary>
    public uint NextFree { get; private set; }

    public (uint First, uint Count) NextBlock()
    {
        uint first = NextFree;
        NextFree += EntityRegistry.BlockSize;
        return (first, EntityRegistry.BlockSize);
    }
}
