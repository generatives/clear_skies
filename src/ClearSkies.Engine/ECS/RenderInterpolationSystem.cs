using ClearSkies.Engine.Core;
using ClearSkies.Engine.Math;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Draws an entity whose <see cref="Transform"/> the ticks move between its last two ticked poses, so motion looks
/// smooth whatever the frame rate. Ticks keep the true pose; see <see cref="RenderInterpolationSystem"/>.
/// </summary>
public struct SmoothedTransform
{
    /// <summary>Only smooth the position: the rotation belongs to something per-frame (the camera's mouse-look).</summary>
    public bool PositionOnly;

    internal Transform Previous, Current, Drawn;
    internal Vector3D<float> Pivot;
    internal bool Started;
}

/// <summary>
/// Eases the drawn pose of something that just changed hands (network ownership) from where it was drawn to where its
/// new source puts it, over <see cref="Seconds"/>, rather than snapping: the offset from the new pose to the old one,
/// shrinking to nothing.
/// </summary>
public struct HandoverBlend
{
    public const float Duration = 0.25f;
    public Vector3D<float> Offset;
    public Quaternion<float> Rotation; // old drawn rotation relative to the new one
    public float Seconds;              // left

    /// <summary>The part of the offset still applied (1 at the start, eased out to 0).</summary>
    public readonly float Weight
    {
        get
        {
            float f = System.Math.Clamp(Seconds / Duration, 0f, 1f);
            return f * f * (3 - 2 * f);
        }
    }
}

/// <summary>
/// Smooths drawing between fixed ticks. Ticks run at 60 Hz and frames at any rate, so a frame usually falls between
/// two ticks; this draws each <see cref="SmoothedTransform"/> entity <see cref="Time.Alpha"/> of the way from its
/// previous tick's pose to its latest (one tick behind, which is what makes it smooth).
///
/// Three parts: <see cref="BeginTick"/> first in each tick puts the true pose back, so simulation never sees a drawn
/// one; <see cref="EndTick"/> last in each tick records the tick's pose; and <see cref="Update"/> each frame after the
/// ticks writes the drawn pose (schedule a <see cref="HierarchyTransformSystem"/> after it so children follow). If
/// something outside the ticks moved the entity between them (a spawn, a teleport), its Transform no longer matches
/// what was drawn: that position is taken as-is, with no smoothing across the jump.
///
/// Dynamic grids get a <see cref="SmoothedTransform"/> automatically. When a grid's blocks change, its pivot (the
/// centre of mass) moves and its Transform with it while the blocks stay put; the previous pose is moved the same
/// way so the edit doesn't make the grid twitch.
/// </summary>
public sealed class RenderInterpolationSystem : ISystem
{
    private readonly EntitySet _smoothed;
    private readonly EntitySet _unsmoothedGrids;
    private readonly Time _time;

    public RenderInterpolationSystem(World world, Time time)
    {
        _smoothed = world.GetEntities().With<Transform>().With<SmoothedTransform>().AsSet();
        _unsmoothedGrids = world.GetEntities().With<PhysicsBodyComponent>().With<Transform>().Without<SmoothedTransform>().AsSet();
        _time = time;
        BeginTick = new LambdaSystem(Begin);
        EndTick = new LambdaSystem(End);
    }

    /// <summary>Schedule first in the Tick stage.</summary>
    public ISystem BeginTick { get; }

    /// <summary>Schedule last in the Tick stage.</summary>
    public ISystem EndTick { get; }

    private void Begin()
    {
        foreach (var e in _unsmoothedGrids.GetEntities().ToArray()) e.Set(new SmoothedTransform());

        foreach (ref readonly Entity e in _smoothed.GetEntities())
        {
            ref var s = ref e.Get<SmoothedTransform>();
            ref var t = ref e.Get<Transform>();
            if (Paused(e) || !s.Started) continue;
            if (Matches(t, s.Current, s.PositionOnly)) continue; // a second tick in one frame: nothing was drawn between
            if (Matches(t, s.Drawn, s.PositionOnly))
            {
                t.Position = s.Current.Position;
                if (!s.PositionOnly) t.Rotation = s.Current.Rotation;
            }
            else
                Restart(ref s, t, e); // moved outside the ticks: take it as it is
        }
    }

    private void End()
    {
        foreach (ref readonly Entity e in _smoothed.GetEntities())
        {
            ref var s = ref e.Get<SmoothedTransform>();
            ref readonly var t = ref e.Get<Transform>();
            if (Paused(e)) { s.Started = false; continue; }
            if (!s.Started) { Restart(ref s, t, e); continue; }

            s.Previous = s.Current;
            s.Current = t;
            if (e.Has<ChunkGrid>())
            {
                var pivot = e.Get<ChunkGrid>().Volume.Pivot;
                if (pivot != s.Pivot)
                    s.Previous.Position += Vec.Rotate(s.Previous.Rotation, pivot - s.Pivot);
                s.Pivot = pivot;
            }
        }
    }

    public void Update(float dt)
    {
        float alpha = _time.Alpha;
        foreach (ref readonly Entity e in _smoothed.GetEntities())
        {
            ref var s = ref e.Get<SmoothedTransform>();
            if (!s.Started || Paused(e)) continue;
            ref var t = ref e.Get<Transform>();
            if (!Matches(t, s.Current, s.PositionOnly) && !Matches(t, s.Drawn, s.PositionOnly))
            {
                Restart(ref s, t, e); // moved since the last tick by something outside the ticks
                continue;
            }
            t.Position = Vector3D.Lerp(s.Previous.Position, s.Current.Position, alpha);
            if (!s.PositionOnly) t.Rotation = Quaternion<float>.Slerp(s.Previous.Rotation, s.Current.Rotation, alpha);
            if (e.Has<HandoverBlend>()) Blend(e, ref t, dt, !s.PositionOnly);
            s.Drawn = t;
        }
    }

    private static void Blend(Entity e, ref Transform t, float dt, bool rotation)
    {
        ref var blend = ref e.Get<HandoverBlend>();
        float w = blend.Weight;
        t.Position += blend.Offset * w;
        if (rotation) t.Rotation = Quaternion<float>.Slerp(Quaternion<float>.Identity, blend.Rotation, w) * t.Rotation;
        blend.Seconds -= dt;
        if (blend.Seconds <= 0) e.Remove<HandoverBlend>();
    }

    /// <summary>GridPilotSystem places the camera itself while it follows a grid; bodies owned elsewhere are drawn from
    /// their snapshots instead (RemoteBodySystem).</summary>
    private static bool Paused(Entity e) =>
        e.Has<CameraGridFollowComponent>() || (e.Has<Entities.NetOwner>() && !e.Get<Entities.NetOwner>().IsLocal);

    private static void Restart(ref SmoothedTransform s, in Transform t, Entity e)
    {
        s.Previous = s.Current = s.Drawn = t;
        s.Pivot = e.Has<ChunkGrid>() ? e.Get<ChunkGrid>().Volume.Pivot : default;
        s.Started = true;
    }

    private static bool Matches(in Transform a, in Transform b, bool positionOnly)
        => a.Position == b.Position && (positionOnly || a.Rotation == b.Rotation);
}
