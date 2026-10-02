using ClearSkies.Engine.Core;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Marks an entity that ticks move, to be drawn between its last two ticked poses (see
/// <see cref="TickInterpolationSystem"/>). Its <see cref="Transform"/> stays the true pose; the pose to draw is its
/// <see cref="DrawnTransform"/>.
/// </summary>
public struct InterpolatedTransform
{
    /// <summary>Only interpolate the position: the rotation belongs to something per-frame (the camera's mouse-look).</summary>
    public bool PositionOnly;

    internal Transform Previous, Current;
    internal bool Started;

    /// <summary>This tick's move is a jump: draw it at its new pose from now, rather than sliding there over the tick.
    /// (A move made outside the ticks is taken as one anyway.)</summary>
    public void Teleport() => Started = false;
}

/// <summary>
/// Where to draw an entity this frame, when that isn't its <see cref="Transform"/>: set each frame for entities with an
/// <see cref="InterpolatedTransform"/> and everything under them in the hierarchy. Read it with
/// <see cref="Drawing.DrawnPose"/>, which falls back to the Transform.
/// </summary>
public struct DrawnTransform
{
    public Transform Value;
    internal long Frame;
    internal bool FromParent;
}

public static class Drawing
{
    /// <summary>Where to draw <paramref name="e"/>: its <see cref="DrawnTransform"/> if it has one, else its
    /// <see cref="Transform"/>. Anything drawn, and the camera it's seen from, should be placed with this.</summary>
    public static Transform DrawnPose(this Entity e) =>
        e.Has<DrawnTransform>() ? e.Get<DrawnTransform>().Value : e.Get<Transform>();
}

/// <summary>
/// Interpolates drawing between fixed ticks. Ticks run at 60 Hz and frames at any rate, so a frame usually falls
/// between two ticks; each frame this places every <see cref="InterpolatedTransform"/> entity's
/// <see cref="DrawnTransform"/> <see cref="Time.Alpha"/> of the way from its previous tick's pose to its latest (one
/// tick behind, which is what makes it smooth), and its children's with it. Transforms are never touched: ticks and
/// everything else always see the true pose.
///
/// Registered in two stages: last in <see cref="SystemStage.Simulation"/> it records each tick's pose, and in
/// <see cref="SystemStage.Frame"/> it writes the drawn poses. If something outside the ticks moved the entity (a spawn, a teleport), its Transform no
/// longer matches the last tick's: that position is taken as-is, with no interpolation across the jump.
///
/// Models' nodes are drawn between ticks too: at the end of each tick this records every <see cref="RenderedModel"/>'s
/// node rotations, which the renderer blends by the same fraction (see <see cref="RenderedModel.ComputeDrawnPose"/>),
/// so a lever's arm or a wheel set by the ticks moves smoothly.
///
/// A player's view turns once per tick with the ship they stand on, or to keep on a control they hold (see
/// <see cref="MouseLookComponent.TurnYaw"/>);
/// with <see cref="InterpolatedTransform.PositionOnly"/> the player's yaw, and their <see cref="Eye"/> camera's pitch,
/// are drawn behind by the same fraction of that turn as the ship, so the two stay together between ticks. A tick that
/// didn't turn the view clears the last one's turn.
///
/// Dynamic grids get an <see cref="InterpolatedTransform"/> automatically. A grid's Transform is its block space, which
/// edits don't move (only its body moves, to the new centre of mass), so an edit doesn't make the grid twitch.
///
/// Bodies owned by another machine are drawn the same way: their Transforms are set each tick from their snapshots
/// (RemoteBodySystem), so here they're no different from anything simulated locally.
/// </summary>
public sealed class TickInterpolationSystem : IStagedSystem
{
    private readonly EntitySet _interpolated;
    private readonly EntitySet _uninterpolatedGrids;
    private readonly EntitySet _drawn;
    private readonly EntitySet _lookers;
    private readonly EntitySet _models;
    private readonly List<Entity> _stale = new();
    private readonly Time _time;
    private long _frame;
    private float _alpha;

    public TickInterpolationSystem(World world, Time time)
    {
        _interpolated = world.GetEntities().With<Transform>().With<InterpolatedTransform>().AsSet();
        _uninterpolatedGrids = world.GetEntities().With<PhysicsBodyComponent>().With<Transform>().Without<InterpolatedTransform>().AsSet();
        _drawn = world.GetEntities().With<DrawnTransform>().AsSet();
        _lookers = world.GetEntities().With<MouseLookComponent>().AsSet();
        _models = world.GetEntities().With<RenderedModel>().AsSet();
        _time = time;
        world.SubscribeComponentRemoved((in Entity e, in InterpolatedTransform _) => { if (e.Has<DrawnTransform>()) e.Remove<DrawnTransform>(); });
    }

    /// <summary>Last in each tick (<see cref="SystemStage.Simulation"/>): records the tick's poses. Each frame
    /// (<see cref="SystemStage.Frame"/>, after the ticks): writes the drawn poses.</summary>
    public void Update(SystemStage stage, float dt)
    {
        if (stage == SystemStage.Simulation) EndTick();
        else Draw();
    }

    private void EndTick()
    {
        foreach (var e in _uninterpolatedGrids.GetEntities().ToArray()) e.Set(new InterpolatedTransform());
        foreach (ref readonly Entity e in _lookers.GetEntities())
            e.Get<MouseLookComponent>().EndTick();
        foreach (ref readonly Entity e in _models.GetEntities())
            e.Get<RenderedModel>().EndTick();

        foreach (ref readonly Entity e in _interpolated.GetEntities())
        {
            ref var s = ref e.Get<InterpolatedTransform>();
            ref readonly var t = ref e.Get<Transform>();
            if (!s.Started) { Restart(ref s, t, e); continue; }

            s.Previous = s.Current;
            s.Current = t;
        }
    }

    private void Draw()
    {
        _frame++;
        float alpha = _alpha = _time.Alpha;
        foreach (ref readonly Entity e in _interpolated.GetEntities())
        {
            ref var s = ref e.Get<InterpolatedTransform>();
            if (!s.Started)
            {
                _stale.Add(e); // drawn where it is
                continue;
            }
            ref readonly var t = ref e.Get<Transform>();
            if (!Matches(t, s.Current, s.PositionOnly)) Restart(ref s, t, e); // moved by something outside the ticks

            var drawn = t;
            // From the previous pose by the step, not Vector3D.Lerp's a·(1−t) + b·t: that rounds both terms at the
            // pose's magnitude, so far from the origin (the spawn is 18 km out) something standing still is drawn up to
            // a float's step off, differently every frame: it vibrates.
            drawn.Position = s.Previous.Position + (s.Current.Position - s.Previous.Position) * alpha;
            if (!s.PositionOnly) drawn.Rotation = Quaternion<float>.Slerp(s.Previous.Rotation, s.Current.Rotation, alpha);
            else if (e.Has<MouseLookComponent>()) DrawLook(e.Get<MouseLookComponent>(), ref drawn, alpha);
            SetDrawn(e, drawn, fromParent: false);
            DrawChildren(e, drawn);
        }

        // Children no longer under anything interpolated (and entities not started) are drawn where they are.
        foreach (ref readonly Entity e in _drawn.GetEntities())
            if (e.Get<DrawnTransform>().FromParent && e.Get<DrawnTransform>().Frame != _frame) _stale.Add(e);
        foreach (var e in _stale) if (e.Has<DrawnTransform>()) e.Remove<DrawnTransform>();
        _stale.Clear();
    }

    private void SetDrawn(Entity e, in Transform drawn, bool fromParent)
    {
        if (e.Has<DrawnTransform>())
        {
            ref var d = ref e.Get<DrawnTransform>();
            d.Value = drawn;
            d.Frame = _frame;
            d.FromParent = fromParent;
        }
        else
            e.Set(new DrawnTransform { Value = drawn, Frame = _frame, FromParent = fromParent });
    }

    /// <summary>Everything under <paramref name="parent"/> in the hierarchy is drawn relative to its drawn pose.</summary>
    private void DrawChildren(Entity parent, in Transform parentDrawn)
    {
        if (!parent.Has<Children>()) return;
        foreach (var child in parent.Get<Children>().Entities)
        {
            if (!child.IsAlive || !child.Has<LocalTransform>() || child.Has<InterpolatedTransform>()) continue;
            var local = child.Get<LocalTransform>();
            if (child.Has<Eye>() && parent.Has<MouseLookComponent>())
                local.Rotation = DrawnHead(parent.Get<MouseLookComponent>(), _alpha);
            var drawn = Hierarchy.Compose(parentDrawn, local);
            SetDrawn(child, drawn, fromParent: true);
            DrawChildren(child, drawn);
        }
    }

    /// <summary>A view turned by the ship it stands on is drawn behind by the part of the latest tick's turn the ship's
    /// drawing hasn't reached yet, so the two turn together; mouse-look on top is still per frame. This is the yaw, on
    /// the player; the pitch is the eye's (<see cref="DrawnHead"/>).</summary>
    private static void DrawLook(in MouseLookComponent look, ref Transform drawn, float alpha)
    {
        if (look.TurnYaw == 0f) return;
        drawn.Rotation = Quaternion<float>.CreateFromYawPitchRoll(look.Yaw - (1f - alpha) * look.TurnYaw, 0f, 0f);
    }

    /// <summary>The eye's pitch as drawn: behind like the yaw (see <see cref="DrawLook"/>).</summary>
    private static Quaternion<float> DrawnHead(in MouseLookComponent look, float alpha) =>
        Quaternion<float>.CreateFromYawPitchRoll(0f, look.Pitch - (1f - alpha) * look.TurnPitch, 0f);

    private static void Restart(ref InterpolatedTransform s, in Transform t, Entity e)
    {
        s.Previous = s.Current = t;
        s.Started = true;
    }

    private static bool Matches(in Transform a, in Transform b, bool positionOnly)
        => a.Position == b.Position && (positionOnly || a.Rotation == b.Rotation);
}
