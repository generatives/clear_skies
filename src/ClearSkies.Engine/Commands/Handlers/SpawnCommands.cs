using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.Commands.Handlers;

/// <summary>Creates a grid from its description, or overwrites the grid with that ID in place.</summary>
public struct SpawnGrid : ICommand
{
    /// <summary>0 in a new spawn: the authority assigns one.</summary>
    public uint Id;

    /// <summary>Who will own it; set by the authority.</summary>
    public PeerId Owner;

    public GridDescription Grid;

    /// <summary>Select it on the machine that asked for it (the G key, loading a .grid file).</summary>
    public bool Select;

    public readonly EntityAddress Target => EntityAddress.Of(Id);
}

/// <summary>
/// Spawns grids (G, loading a .grid file, loading from storage, joining, resyncing), and describes live ones. The
/// authority assigns the network ID and owns the new grid. Apply creates it, or overwrites it in place if that ID
/// already exists, so a spawn received twice is harmless.
/// </summary>
public sealed class SpawnGridHandler : CommandHandler<SpawnGrid>, IDescriber
{
    private readonly World _world;
    private readonly NetRegistry _registry;
    private readonly Session _session;
    private readonly PhysicsWorld _physics;
    private readonly GridSelection? _selection;
    private readonly EntitySet _requested;

    public SpawnGridHandler(World world, NetRegistry registry, Session session, PhysicsWorld physics, GridSelection? selection = null)
    {
        _world = world;
        _registry = registry;
        _session = session;
        _physics = physics;
        _selection = selection;
        _requested = world.GetEntities().With<DescribeRequest>().With<DynamicGrid>().With<ChunkGrid>().With<NetId>().AsSet();
    }

    public override ushort Id => CommandIds.SpawnGrid;

    public override void Write(NetWriter w, in SpawnGrid c)
    {
        w.WriteUInt32(c.Id);
        w.WriteUInt32(c.Owner.Value);
        w.WriteBool(c.Select);
        c.Grid.Write(w);
    }

    public override SpawnGrid Read(ref NetReader r) => new()
    {
        Id = r.ReadUInt32(), Owner = new PeerId(r.ReadUInt32()), Select = r.ReadBool(), Grid = GridDescription.Read(ref r),
    };

    /// <summary>A new grid is decided by the host (the spawning player's bubble owner, from N5); an existing one by its owner.</summary>
    public override PeerId Authority(in SpawnGrid c, in AuthorityContext ctx) => c.Id == 0 ? ctx.Host : ctx.OwnerOf(c.Target);

    public override Verdict Validate(ref SpawnGrid c, in CommandContext ctx)
    {
        if (c.Grid is null || c.Grid.Voxels.Count == 0) return Verdict.Reject;
        foreach (var v in c.Grid.Voxels) if (!BlockRegistry.IsDefined(v.Id)) return Verdict.Reject;
        if (c.Id == 0) c.Id = _registry.Allocate();
        if (c.Owner == PeerId.None) c.Owner = _session.LocalPeer;
        return Verdict.Accept;
    }

    public override void Apply(in SpawnGrid e, in ApplyContext ctx)
    {
        Entity grid;
        if (_registry.TryGet(e.Id, out var existing) && existing.Has<DynamicGrid>())
        {
            DynamicGridFactory.Fill(existing, e.Grid);
            grid = existing;
        }
        else grid = DynamicGridFactory.Create(_world, e.Id, e.Grid);
        grid.Set(_session.OwnerFor(e.Owner));
        if (e.Select && ctx.Origin == _session.LocalPeer) _selection?.Select(grid);
    }

    public int Order => 0;

    public void Describe(DescriptionSink sink)
    {
        foreach (var grid in _requested.GetEntities().ToArray())
        {
            var owner = grid.Has<NetOwner>() ? grid.Get<NetOwner>().Owner : _session.LocalPeer;
            sink.Add(grid, new SpawnGrid { Id = grid.Get<NetId>().Value, Owner = owner, Grid = DynamicGridFactory.Describe(grid, _physics) });
        }
    }
}

/// <summary>Creates a player from their description, or moves the player with that ID to it.</summary>
public struct SpawnPlayer : ICommand
{
    /// <summary>0 in a new spawn: the host assigns one.</summary>
    public uint Id;

    /// <summary>The peer the player plays on: they own their own character.</summary>
    public PeerId Owner;

    public PlayerDescription Player;

    public readonly EntityAddress Target => EntityAddress.Of(Id);
}

/// <summary>Spawns players (at startup, and for each player joining), and describes live ones. Decided by the host.</summary>
public sealed class SpawnPlayerHandler : CommandHandler<SpawnPlayer>, IDescriber
{
    private readonly World _world;
    private readonly NetRegistry _registry;
    private readonly Session _session;
    private readonly PhysicsWorld _physics;
    private readonly EntitySet _requested;

    public SpawnPlayerHandler(World world, NetRegistry registry, Session session, PhysicsWorld physics)
    {
        _world = world;
        _registry = registry;
        _session = session;
        _physics = physics;
        _requested = world.GetEntities().With<DescribeRequest>().With<Player>().With<NetId>().AsSet();
    }

    public override ushort Id => CommandIds.SpawnPlayer;

    public override void Write(NetWriter w, in SpawnPlayer c)
    {
        w.WriteUInt32(c.Id);
        w.WriteUInt32(c.Owner.Value);
        c.Player.Write(w);
    }

    public override SpawnPlayer Read(ref NetReader r) => new()
    {
        Id = r.ReadUInt32(), Owner = new PeerId(r.ReadUInt32()), Player = PlayerDescription.Read(ref r),
    };

    public override PeerId Authority(in SpawnPlayer c, in AuthorityContext ctx) => ctx.Host;

    public override Verdict Validate(ref SpawnPlayer c, in CommandContext ctx)
    {
        if (c.Player is null) return Verdict.Reject;
        if (c.Id == 0) c.Id = _registry.Allocate();
        if (c.Owner == PeerId.None) c.Owner = ctx.Sender;
        return Verdict.Accept;
    }

    public override void Apply(in SpawnPlayer e, in ApplyContext ctx)
    {
        if (_registry.TryGet(e.Id, out var existing) && existing.Has<Player>())
        {
            PlayerFactory.Fill(existing, e.Player);
            existing.Set(_session.OwnerFor(e.Owner));
            return;
        }
        PlayerFactory.Create(_world, _physics, e.Id, _session.OwnerFor(e.Owner), e.Player);
    }

    /// <summary>After grids, which players may stand on.</summary>
    public int Order => 10;

    public void Describe(DescriptionSink sink)
    {
        foreach (var player in _requested.GetEntities().ToArray())
        {
            var owner = player.Has<NetOwner>() ? player.Get<NetOwner>().Owner : _session.LocalPeer;
            sink.Add(player, new SpawnPlayer { Id = player.Get<NetId>().Value, Owner = owner, Player = PlayerFactory.Describe(player) });
        }
    }
}

/// <summary>Removes an entity (a grid, a player) and everything attached to it.</summary>
public struct DespawnEntity : ICommand
{
    public uint Entity;

    /// <summary>Unloading: the entity stays in storage. Otherwise it's gone for good and leaves the save too.</summary>
    public bool KeepStored;
    public readonly EntityAddress Target => EntityAddress.Of(Entity);
}

public sealed class DespawnEntityHandler : CommandHandler<DespawnEntity>
{
    private readonly NetRegistry _registry;
    public DespawnEntityHandler(NetRegistry registry) => _registry = registry;

    public override ushort Id => CommandIds.DespawnEntity;

    public override void Write(NetWriter w, in DespawnEntity c) { w.WriteUInt32(c.Entity); w.WriteBool(c.KeepStored); }
    public override DespawnEntity Read(ref NetReader r) => new() { Entity = r.ReadUInt32(), KeepStored = r.ReadBool() };

    public override Verdict Validate(ref DespawnEntity c, in CommandContext ctx)
        => c.Entity != NetRegistry.WorldVolume && _registry.IsLive(c.Entity) ? Verdict.Accept : Verdict.Reject;

    public override void Apply(in DespawnEntity e, in ApplyContext ctx)
    {
        if (_registry.TryGet(e.Entity, out var entity)) Hierarchy.DestroyRecursive(entity);
    }
}
