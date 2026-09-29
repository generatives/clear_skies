using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.Commands.Handlers;

/// <summary>
/// Creates an entity (a grid, a player) from its description. Sent to spawn something new (with no ID: the host
/// assigns one), and also how a live entity's description reaches a joining client or comes back out of storage.
/// </summary>
public struct Spawn<TDescription> : ICommand where TDescription : class, IEntityDescription<TDescription>
{
    /// <summary>None in a new spawn: the host assigns one.</summary>
    public EntityId Id;

    /// <summary>Who will own it; None for the handler's default (see <see cref="SpawnHandler{TDescription, TKind}.DefaultOwner"/>).</summary>
    public PeerId Owner;

    public TDescription Description;

    /// <summary>Select it on the machine that asked for it, where the kind can be selected (a grid: the G key, loading a
    /// .grid file).</summary>
    public bool Select;

    public readonly EntityAddress Target => EntityAddress.Of(Id);
}

/// <summary>
/// Spawns one kind of entity from its description, and describes live ones of that kind (<typeparamref name="TKind"/>
/// marks them) for sending and storing. Decided by the host, which assigns the ID. A spawn is only ever for an ID that
/// isn't live: one that is is rejected, or, arriving as an event, left alone. A subclass says how to create its kind
/// and describe it, and what makes a description valid.
/// </summary>
public abstract class SpawnHandler<TDescription, TKind> : CommandHandler<Spawn<TDescription>>, IDescriber
    where TDescription : class, IEntityDescription<TDescription>
{
    protected readonly World World;
    protected readonly EntityRegistry Registry;
    protected readonly Session Session;
    private readonly EntitySet _requested;

    protected SpawnHandler(World world, EntityRegistry registry, Session session)
    {
        World = world;
        Registry = registry;
        Session = session;
        _requested = world.GetEntities().With<DescribeRequest>().With<TKind>().With<EntityId>().AsSet();
    }

    /// <summary>Order among describers each tick: supports (grids) before what they support (players).</summary>
    public abstract int Order { get; }

    /// <summary>Creates the entity, owned by <paramref name="owner"/>.</summary>
    protected abstract Entity Create(EntityId id, NetOwner owner, TDescription description);

    /// <summary>A live entity's description.</summary>
    protected abstract TDescription DescriptionOf(Entity entity);

    protected virtual bool IsValid(TDescription description) => true;

    /// <summary>Who owns a new one when the spawn doesn't say: whoever asked for it.</summary>
    protected virtual PeerId DefaultOwner(in CommandContext ctx) => ctx.Sender;

    /// <summary>Selects a new one on the machine that asked for it, if the spawn says to and the kind can be.</summary>
    protected virtual void Select(Entity entity) { }

    public override void Write(NetWriter w, in Spawn<TDescription> c)
    {
        c.Id.Write(w);
        w.WriteUInt32(c.Owner.Value);
        w.WriteBool(c.Select);
        c.Description.Write(w);
    }

    public override Spawn<TDescription> Read(ref NetReader r) => new()
    {
        Id = EntityId.Read(ref r), Owner = new PeerId(r.ReadUInt32()), Select = r.ReadBool(), Description = TDescription.Read(ref r),
    };

    public override PeerId Authority(in Spawn<TDescription> c, in AuthorityContext ctx) => ctx.Host;

    public override Verdict Validate(ref Spawn<TDescription> c, in CommandContext ctx)
    {
        if (c.Description is null || !IsValid(c.Description)) return Verdict.Reject;
        if (c.Id.IsNone) c.Id = Registry.Allocate();
        else if (Registry.IsLive(c.Id)) return Verdict.Reject; // already here
        if (c.Owner == PeerId.None) c.Owner = DefaultOwner(ctx);
        return Verdict.Accept;
    }

    public override void Apply(in Spawn<TDescription> e, in ApplyContext ctx)
    {
        if (Registry.IsLive(e.Id)) return; // already here (sent twice): keep what's here
        var entity = Create(e.Id, Session.OwnerFor(e.Owner), e.Description);
        if (e.Select && ctx.Origin == Session.LocalPeer) Select(entity);
    }

    public void Describe(DescriptionSink sink)
    {
        foreach (var entity in _requested.GetEntities().ToArray())
        {
            var owner = entity.Has<NetOwner>() ? entity.Get<NetOwner>().Owner : Session.LocalPeer;
            sink.Add(entity, new Spawn<TDescription> { Id = entity.Get<EntityId>(), Owner = owner, Description = DescriptionOf(entity) });
        }
    }
}

/// <summary>Spawns grids (G, loading a .grid file, loading from storage, joining). The host owns a new grid.</summary>
public sealed class SpawnGridHandler : SpawnHandler<GridDescription, DynamicGrid>
{
    private readonly PhysicsWorld _physics;
    private readonly GridSelection? _selection;

    public SpawnGridHandler(World world, EntityRegistry registry, Session session, PhysicsWorld physics, GridSelection? selection = null)
        : base(world, registry, session)
    {
        _physics = physics;
        _selection = selection;
    }

    public override ushort Id => CommandIds.SpawnGrid;
    public override int Order => 0;

    protected override bool IsValid(GridDescription d) => d.Voxels.Count > 0 && d.Voxels.All(v => BlockRegistry.IsDefined(v.Id));

    protected override PeerId DefaultOwner(in CommandContext ctx) => Session.LocalPeer;

    protected override Entity Create(EntityId id, NetOwner owner, GridDescription d)
    {
        var grid = DynamicGridFactory.Create(World, id, d);
        grid.Set(owner);
        return grid;
    }

    protected override GridDescription DescriptionOf(Entity grid) => DynamicGridFactory.Describe(grid, _physics);

    protected override void Select(Entity grid) => _selection?.Select(grid);
}

/// <summary>Spawns players (at startup, and for each player joining). A player owns their own character.</summary>
public sealed class SpawnPlayerHandler : SpawnHandler<PlayerDescription, Player>
{
    private readonly PhysicsWorld _physics;

    public SpawnPlayerHandler(World world, EntityRegistry registry, Session session, PhysicsWorld physics)
        : base(world, registry, session) => _physics = physics;

    public override ushort Id => CommandIds.SpawnPlayer;

    /// <summary>After grids, which players may stand on.</summary>
    public override int Order => 10;

    protected override Entity Create(EntityId id, NetOwner owner, PlayerDescription d) => PlayerFactory.Create(World, _physics, id, owner, d);

    protected override PlayerDescription DescriptionOf(Entity player) => PlayerFactory.Describe(player);
}

/// <summary>Removes an entity (a grid, a player) and everything attached to it.</summary>
public struct DespawnEntity : ICommand
{
    public EntityId Entity;

    /// <summary>Unloading: the entity stays in storage. Otherwise it's gone for good and leaves the save too.</summary>
    public bool KeepStored;
    public readonly EntityAddress Target => EntityAddress.Of(Entity);
}

public sealed class DespawnEntityHandler : CommandHandler<DespawnEntity>
{
    private readonly EntityRegistry _registry;
    public DespawnEntityHandler(EntityRegistry registry) => _registry = registry;

    public override ushort Id => CommandIds.DespawnEntity;

    public override void Write(NetWriter w, in DespawnEntity c) { c.Entity.Write(w); w.WriteBool(c.KeepStored); }
    public override DespawnEntity Read(ref NetReader r) => new() { Entity = EntityId.Read(ref r), KeepStored = r.ReadBool() };

    public override Verdict Validate(ref DespawnEntity c, in CommandContext ctx)
        => c.Entity != EntityRegistry.WorldVolume && _registry.IsLive(c.Entity) ? Verdict.Accept : Verdict.Reject;

    public override void Apply(in DespawnEntity e, in ApplyContext ctx)
    {
        if (_registry.TryGet(e.Entity, out var entity)) Hierarchy.DestroyRecursive(entity);
    }
}
