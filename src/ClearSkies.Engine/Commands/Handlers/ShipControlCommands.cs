using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.Commands.Handlers;

/// <summary>Sets a ship's thrust along one of its axes (see <see cref="ShipControls"/>): what dragging a lever sends.</summary>
public struct SetShipThrust : ICommand
{
    public EntityId Ship;
    public ThrustAxis Axis;
    public float Value;
    public readonly EntityAddress Target => EntityAddress.Of(Ship);
}

public sealed class SetShipThrustHandler : PredictedCommandHandler<SetShipThrust, SetShipThrust>
{
    private readonly EntityRegistry _registry;
    public SetShipThrustHandler(EntityRegistry registry) => _registry = registry;

    public override ushort Id => CommandIds.SetShipThrust;

    /// <summary>A drag sends a setting every tick: only the latest per ship and axis goes.</summary>
    public override bool Coalesces(in SetShipThrust earlier, in SetShipThrust later) =>
        earlier.Ship == later.Ship && earlier.Axis == later.Axis;

    public override void Write(NetWriter w, in SetShipThrust c) { c.Ship.Write(w); w.WriteByte((byte)c.Axis); w.WriteSingle(c.Value); }
    public override SetShipThrust Read(ref NetReader r) =>
        new() { Ship = EntityId.Read(ref r), Axis = (ThrustAxis)r.ReadByte(), Value = r.ReadSingle() };

    public override Verdict Validate(ref SetShipThrust c, in CommandContext ctx)
    {
        if (!float.IsFinite(c.Value) || c.Axis > ThrustAxis.Up || ShipControlCommands.Ship(_registry, c.Ship) is null)
            return Verdict.Reject;
        c.Value = System.Math.Clamp(c.Value, -1f, 1f);
        return Verdict.Accept;
    }

    public override void Apply(in SetShipThrust e, in ApplyContext ctx)
    {
        if (ShipControlCommands.Ship(_registry, e.Ship) is { } ship) ship.Get<ShipControls>().SetThrust(e.Axis, e.Value);
    }

    /// <summary>The undo is the same command with the setting as it was.</summary>
    public override SetShipThrust Capture(in SetShipThrust c) => c with
    {
        Value = ShipControlCommands.Ship(_registry, c.Ship) is { } ship
            ? ship.Get<ShipControls>().Thrust(c.Axis) : 0f,
    };

    public override void Restore(in SetShipThrust undo) => Apply(undo, default);
}

/// <summary>Sets a ship's turn (see <see cref="ShipControls.Turn"/>): what turning a wheel sends.</summary>
public struct SetShipTurn : ICommand
{
    public EntityId Ship;
    public float Value;
    public readonly EntityAddress Target => EntityAddress.Of(Ship);
}

public sealed class SetShipTurnHandler : PredictedCommandHandler<SetShipTurn, SetShipTurn>
{
    private readonly EntityRegistry _registry;
    public SetShipTurnHandler(EntityRegistry registry) => _registry = registry;

    public override ushort Id => CommandIds.SetShipTurn;

    /// <summary>A drag sends a setting every tick: only the latest per ship goes.</summary>
    public override bool Coalesces(in SetShipTurn earlier, in SetShipTurn later) => earlier.Ship == later.Ship;

    public override void Write(NetWriter w, in SetShipTurn c) { c.Ship.Write(w); w.WriteSingle(c.Value); }
    public override SetShipTurn Read(ref NetReader r) => new() { Ship = EntityId.Read(ref r), Value = r.ReadSingle() };

    public override Verdict Validate(ref SetShipTurn c, in CommandContext ctx)
    {
        if (!float.IsFinite(c.Value) || ShipControlCommands.Ship(_registry, c.Ship) is null) return Verdict.Reject;
        c.Value = System.Math.Clamp(c.Value, -1f, 1f);
        return Verdict.Accept;
    }

    public override void Apply(in SetShipTurn e, in ApplyContext ctx)
    {
        if (ShipControlCommands.Ship(_registry, e.Ship) is { } ship) ship.Get<ShipControls>().Turn = e.Value;
    }

    /// <summary>The undo is the same command with the setting as it was.</summary>
    public override SetShipTurn Capture(in SetShipTurn c) => c with
    {
        Value = ShipControlCommands.Ship(_registry, c.Ship) is { } ship
            ? ship.Get<ShipControls>().Turn : 0f,
    };

    public override void Restore(in SetShipTurn undo) => Apply(undo, default);
}

/// <summary>Anchors a ship or lets it go (see <see cref="ShipControls.Anchored"/>): what flicking a toggle sends, and
/// what <see cref="AnchorSystem"/> sends to turn the toggles back off when nothing is in reach.</summary>
public struct SetShipAnchored : ICommand
{
    public EntityId Ship;
    public bool Anchored;
    public readonly EntityAddress Target => EntityAddress.Of(Ship);
}

public sealed class SetShipAnchoredHandler : PredictedCommandHandler<SetShipAnchored, SetShipAnchored>
{
    private readonly EntityRegistry _registry;
    public SetShipAnchoredHandler(EntityRegistry registry) => _registry = registry;

    public override ushort Id => CommandIds.SetShipAnchored;

    public override void Write(NetWriter w, in SetShipAnchored c) { c.Ship.Write(w); w.WriteBool(c.Anchored); }
    public override SetShipAnchored Read(ref NetReader r) => new() { Ship = EntityId.Read(ref r), Anchored = r.ReadBool() };

    public override Verdict Validate(ref SetShipAnchored c, in CommandContext ctx) =>
        ShipControlCommands.Ship(_registry, c.Ship) is null ? Verdict.Reject : Verdict.Accept;

    public override void Apply(in SetShipAnchored e, in ApplyContext ctx)
    {
        if (ShipControlCommands.Ship(_registry, e.Ship) is { } ship) ship.Get<ShipControls>().Anchored = e.Anchored;
    }

    /// <summary>The undo is the same command with the setting as it was.</summary>
    public override SetShipAnchored Capture(in SetShipAnchored c) => c with
    {
        Anchored = ShipControlCommands.Ship(_registry, c.Ship) is { } ship && ship.Get<ShipControls>().Anchored,
    };

    public override void Restore(in SetShipAnchored undo) => Apply(undo, default);
}

internal static class ShipControlCommands
{
    /// <summary>The grid <paramref name="id"/> names, if it's live here: ship controls live on it (every grid has them:
    /// see DynamicGridFactory).</summary>
    public static Entity? Ship(EntityRegistry registry, EntityId id) =>
        registry.Find(id) is { } e && e.Has<DynamicGrid>() ? e : null;
}
