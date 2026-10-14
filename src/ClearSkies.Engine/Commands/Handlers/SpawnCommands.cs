using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Support;
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

    /// <summary>Who will own it; None for the handler's default (see <see cref="SpawnHandler{TDescription, TKind}.DefaultOwner"/>).
    /// For a player, who plays them: the host always owns (simulates) players (see <see cref="SpawnPlayerHandler"/>).</summary>
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
public abstract class SpawnHandler<TDescription, TKind> : CommandHandler<Spawn<TDescription>>, ISpawnHandler
    where TDescription : class, IEntityDescription<TDescription>
{
    protected readonly World World;
    protected readonly EntityRegistry Registry;
    protected readonly Session Session;

    protected SpawnHandler(World world, EntityRegistry registry, Session session)
    {
        World = world;
        Registry = registry;
        Session = session;
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

    public bool Describes(Entity entity) => entity.Has<TKind>() && entity.Has<EntityId>();

    public byte[] Describe(Entity entity) => DescriptionBytes.Of(DescriptionOf(entity));

    public byte[] SpawnCommand(EntityId id, PeerId owner, ReadOnlySpan<byte> description) =>
        Serialize(new Spawn<TDescription> { Id = id, Owner = owner, Description = DescriptionBytes.Read<TDescription>(description) });
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
        // Riding on a grid that's here (held to it by anchors): put it where it sits on that grid as it is now, not where
        // the description has it in the world, so a ship docked on another comes back aboard however far that has moved.
        if (AnchorLinks.Support(d.Anchors) is { } support && Registry.Find(support.Target) is { } target && target.Has<Transform>())
            grid.Get<Transform>() = support.Place(target.Get<Transform>());
        return grid;
    }

    protected override GridDescription DescriptionOf(Entity grid) => DynamicGridFactory.Describe(grid, _physics);

    protected override void Select(Entity grid) => _selection?.Select(grid);
}

/// <summary>Spawns players (at startup, and for each player joining). The host owns and simulates every player; the
/// spawn's owner is who plays them (<see cref="Player.ControllingPeer"/>), whose machine predicts them. Players played
/// elsewhere are drawn with <see cref="PlayerModel"/>, where there's one (not headless). The local player gets the active
/// camera at their eye, if it's attached to nothing (see <see cref="EyeSystem"/>).</summary>
public sealed class SpawnPlayerHandler : SpawnHandler<PlayerDescription, Player>
{
    private readonly PhysicsWorld _physics;
    private readonly PlayerModel? _model;
    private readonly EntitySet _looseCameras;

    public SpawnPlayerHandler(World world, EntityRegistry registry, Session session, PhysicsWorld physics, PlayerModel? model = null)
        : base(world, registry, session)
    {
        _physics = physics;
        _model = model;
        _looseCameras = world.GetEntities().With<CameraComponent>().Without<Parent>().AsSet();
    }

    public override ushort Id => CommandIds.SpawnPlayer;

    /// <summary>After grids, which players may stand on.</summary>
    public override int Order => 10;

    protected override Entity Create(EntityId id, NetOwner owner, PlayerDescription d)
    {
        // On their ship as it is here, if they stand on one, and moving with its deck there, so they don't start at rest
        // on a moving ship and slide off it.
        d.Position = PlayerFactory.WorldPosition(d, Registry);
        Entity ship = default;
        if (!d.FreeFly && !d.Support.IsNone && Registry.TryGet(d.Support, out var support))
        {
            ship = support;
            if (support.Has<PhysicsBodyComponent>())
            {
                var body = support.Get<PhysicsBodyComponent>().Body;
                var (centre, _) = _physics.GetBodyPose(body);
                d.Velocity = _physics.GetBodyLinearVelocity(body) + Vector3.Cross(_physics.GetBodyAngularVelocity(body), d.Position - centre);
            }
            // Its body isn't here yet (a ship's copy arriving with them as a client joins), and when it comes it's at
            // rest until it's placed on its timeline: they ride along meanwhile (SupportSystem), and it carries them on
            // from there (RemoteBodyProxySystem), so they're not moving across it.
            else d.Velocity = Vector3.Zero;
        }
        // The spawn's owner plays them; the host simulates them.
        var controllingPeer = owner.Owner;
        bool controlledHere = owner.IsLocal;
        var player = PlayerFactory.Create(World, _physics, id, Session.OwnerFor(PeerId.Host), controllingPeer, controlledHere, d);
        // Standing on it from the start (SupportSystem keeps it once they touch it), so whatever moves the ship before then
        // takes them along (a copy placed on its timeline, see RemoteBodyProxySystem).
        if (ship.IsAlive && player.Has<Support>())
            (player.Get<Support>().Supporter, player.Get<Support>().LocalPosition) = (ship, d.LocalPosition);
        if (!controlledHere && _model is not null) player.Set(_model.Create());
        if (controlledHere)
            foreach (var camera in _looseCameras.GetEntities().ToArray())
                if (camera.Get<CameraComponent>().Active) { EyeSystem.Attach(camera, player); break; }
        return player;
    }

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
