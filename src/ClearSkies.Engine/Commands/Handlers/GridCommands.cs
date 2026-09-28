using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Commands.Handlers;

/// <summary>Locks a grid in place (kinematic) or frees it.</summary>
public struct SetGridLocked : ICommand
{
    public uint Grid;
    public bool Locked;
    public readonly EntityAddress Target => EntityAddress.Of(Grid);
}

/// <summary>Sets <see cref="DynamicGrid.Locked"/>. The owner's physics makes the body kinematic or dynamic.</summary>
public sealed class SetGridLockedHandler : CommandHandler<SetGridLocked>
{
    private readonly NetRegistry _registry;
    private readonly PhysicsWorld _physics;

    public SetGridLockedHandler(NetRegistry registry, PhysicsWorld physics)
    {
        _registry = registry;
        _physics = physics;
    }

    public override ushort Id => CommandIds.SetGridLocked;

    public override void Write(NetWriter w, in SetGridLocked c) { w.WriteUInt32(c.Grid); w.WriteBool(c.Locked); }
    public override SetGridLocked Read(ref NetReader r) => new() { Grid = r.ReadUInt32(), Locked = r.ReadBool() };

    public override Verdict Validate(ref SetGridLocked c, in CommandContext ctx)
        => _registry.Find(c.Grid) is { } e && e.Has<DynamicGrid>() ? Verdict.Accept : Verdict.Reject;

    public override void Apply(in SetGridLocked e, in ApplyContext ctx)
    {
        if (_registry.Find(e.Grid) is { } grid && grid.Has<DynamicGrid>()) grid.Get<DynamicGrid>().Locked = e.Locked;
    }

    public override void AfterApply(in SetGridLocked e, in CommandContext ctx)
    {
        if (_registry.Find(e.Grid) is not { } grid || !grid.Has<PhysicsBodyComponent>()) return;
        ref readonly var dg = ref grid.Get<DynamicGrid>();
        _physics.SetBodyKinematic(grid.Get<PhysicsBodyComponent>().Body, dg.Locked, dg.Inertia);
    }
}

/// <summary>Turns a grid upright and stops it spinning.</summary>
public struct RightGrid : ICommand
{
    public uint Grid;
    public readonly EntityAddress Target => EntityAddress.Of(Grid);
}

/// <summary>Apply does nothing: righting is a physics effect on the owner, which everyone else sees through body sync.</summary>
public sealed class RightGridHandler : CommandHandler<RightGrid>
{
    private readonly NetRegistry _registry;
    private readonly PhysicsWorld _physics;

    public RightGridHandler(NetRegistry registry, PhysicsWorld physics)
    {
        _registry = registry;
        _physics = physics;
    }

    public override ushort Id => CommandIds.RightGrid;

    public override void Write(NetWriter w, in RightGrid c) => w.WriteUInt32(c.Grid);
    public override RightGrid Read(ref NetReader r) => new() { Grid = r.ReadUInt32() };

    public override Verdict Validate(ref RightGrid c, in CommandContext ctx)
        => _registry.Find(c.Grid) is { } e && e.Has<DynamicGrid>() ? Verdict.Accept : Verdict.Reject;

    public override void Apply(in RightGrid e, in ApplyContext ctx) { }

    public override void AfterApply(in RightGrid e, in CommandContext ctx)
    {
        if (_registry.Find(e.Grid) is not { } grid || !grid.Has<PhysicsBodyComponent>()) return;
        var body = grid.Get<PhysicsBodyComponent>().Body;
        var (pos, _) = _physics.GetBodyPose(body);
        _physics.SetBodyPose(body, pos, System.Numerics.Quaternion.Identity);
        _physics.SetBodyAngularVelocity(body, System.Numerics.Vector3.Zero);
    }
}

/// <summary>Switches a player between walking and free-fly.</summary>
public struct SetMoveMode : ICommand
{
    public uint Player;
    public bool FreeFly;
    public readonly EntityAddress Target => EntityAddress.Of(Player);
}

public sealed class SetMoveModeHandler : PredictedCommandHandler<SetMoveMode, (uint Player, bool FreeFly)>
{
    private readonly NetRegistry _registry;
    public SetMoveModeHandler(NetRegistry registry) => _registry = registry;

    public override ushort Id => CommandIds.SetMoveMode;

    public override void Write(NetWriter w, in SetMoveMode c) { w.WriteUInt32(c.Player); w.WriteBool(c.FreeFly); }
    public override SetMoveMode Read(ref NetReader r) => new() { Player = r.ReadUInt32(), FreeFly = r.ReadBool() };

    public override Verdict Validate(ref SetMoveMode c, in CommandContext ctx)
        => _registry.Find(c.Player) is { } e && e.Has<Player>() ? Verdict.Accept : Verdict.Reject;

    public override void Apply(in SetMoveMode e, in ApplyContext ctx) => Set(e.Player, e.FreeFly);

    public override (uint Player, bool FreeFly) Capture(in SetMoveMode c)
        => (c.Player, _registry.Find(c.Player) is { } p && p.Has<FreeFlying>());

    public override void Restore(in (uint Player, bool FreeFly) undo) => Set(undo.Player, undo.FreeFly);

    private void Set(uint player, bool freeFly)
    {
        if (_registry.Find(player) is { } p && p.Has<Player>()) Players.SetFreeFlying(p, freeFly);
    }
}
