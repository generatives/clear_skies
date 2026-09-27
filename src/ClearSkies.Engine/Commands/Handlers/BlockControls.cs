using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Serialization;
using DefaultEcs;

namespace ClearSkies.Engine.Commands.Handlers;

/// <summary>Sets a lever (and, through the axis rule, every lever on its axis in its volume).</summary>
public struct SetLever : ICommand
{
    public EntityAddress Lever;
    public float Value;
    public readonly EntityAddress Target => Lever;
}

/// <summary>A volume's levers on the same axis move together: setting one sets every other lever that levers along the
/// same line (north face the same way or the opposite way; the opposite way gets the negated value, so all of them ask
/// for the same thing).</summary>
public sealed class SetLeverHandler : PredictedCommandHandler<SetLever, List<(Entity Lever, float Value)>>
{
    private readonly BlockEntities _blocks;
    public SetLeverHandler(BlockEntities blocks) => _blocks = blocks;

    public override ushort Id => CommandIds.SetLever;
    public override bool Coalesce => true;

    public override void Write(NetWriter w, in SetLever c) { c.Lever.Write(w); w.WriteSingle(c.Value); }
    public override SetLever Read(ref NetReader r) => new() { Lever = EntityAddress.Read(ref r), Value = r.ReadSingle() };

    public override Verdict Validate(ref SetLever c, in CommandContext ctx)
    {
        if (!float.IsFinite(c.Value) || _blocks.Find(c.Lever) is not { } lever || !lever.Has<Lever>()) return Verdict.Reject;
        c.Value = System.Math.Clamp(c.Value, -1f, 1f);
        return Verdict.Accept;
    }

    public override void Apply(in SetLever e, in ApplyContext ctx)
    {
        if (_blocks.Find(e.Lever) is not { } lever || !lever.Has<Lever>()) return;
        foreach (var (other, sign) in _blocks.LeversOnSameAxis(lever))
            other.Get<Lever>().Value = e.Value * sign;
    }

    public override List<(Entity Lever, float Value)> Capture(in SetLever c)
    {
        var undo = new List<(Entity, float)>();
        if (_blocks.Find(c.Lever) is { } lever && lever.Has<Lever>())
            foreach (var (other, _) in _blocks.LeversOnSameAxis(lever)) undo.Add((other, other.Get<Lever>().Value));
        return undo;
    }

    public override void Restore(in List<(Entity Lever, float Value)> undo)
    {
        foreach (var (lever, value) in undo)
            if (lever.IsAlive && lever.Has<Lever>()) lever.Get<Lever>().Value = value;
    }
}

/// <summary>Turns a ship's wheel (and every other wheel on the ship with it).</summary>
public struct SetWheel : ICommand
{
    public EntityAddress Wheel;
    public float Angle;
    public readonly EntityAddress Target => Wheel;
}

public sealed class SetWheelHandler : PredictedCommandHandler<SetWheel, List<(Entity Wheel, float Angle)>>
{
    private readonly BlockEntities _blocks;
    public SetWheelHandler(BlockEntities blocks) => _blocks = blocks;

    public override ushort Id => CommandIds.SetWheel;
    public override bool Coalesce => true;

    public override void Write(NetWriter w, in SetWheel c) { c.Wheel.Write(w); w.WriteSingle(c.Angle); }
    public override SetWheel Read(ref NetReader r) => new() { Wheel = EntityAddress.Read(ref r), Angle = r.ReadSingle() };

    public override Verdict Validate(ref SetWheel c, in CommandContext ctx)
    {
        if (!float.IsFinite(c.Angle) || _blocks.Find(c.Wheel) is not { } wheel || !wheel.Has<SteeringWheel>()) return Verdict.Reject;
        c.Angle = System.Math.Clamp(c.Angle, -SteeringWheel.MaxAngle, SteeringWheel.MaxAngle);
        return Verdict.Accept;
    }

    public override void Apply(in SetWheel e, in ApplyContext ctx)
    {
        if (_blocks.Find(e.Wheel) is not { } wheel || !wheel.Has<SteeringWheel>()) return;
        foreach (var other in _blocks.WheelsOnGrid(wheel)) other.Get<SteeringWheel>().Angle = e.Angle;
    }

    public override List<(Entity Wheel, float Angle)> Capture(in SetWheel c)
    {
        var undo = new List<(Entity, float)>();
        if (_blocks.Find(c.Wheel) is { } wheel && wheel.Has<SteeringWheel>())
            foreach (var other in _blocks.WheelsOnGrid(wheel)) undo.Add((other, other.Get<SteeringWheel>().Angle));
        return undo;
    }

    public override void Restore(in List<(Entity Wheel, float Angle)> undo)
    {
        foreach (var (wheel, angle) in undo)
            if (wheel.IsAlive && wheel.Has<SteeringWheel>()) wheel.Get<SteeringWheel>().Angle = angle;
    }
}
