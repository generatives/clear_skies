using ClearSkies.Engine.Entities;
using System.Numerics;
using ClearSkies.Net.Protocol;

namespace ClearSkies.Net.Sync;

/// <summary>
/// Snapshots of a body owned by another machine, newest last, for playing it back <see cref="Delay"/> ticks behind:
/// the pair around the sampled tick is interpolated in the support's space, and with no newer snapshot the body is
/// extrapolated from its last velocity for up to <see cref="MaxExtrapolationTicks"/>, then held.
/// <para>The delay is as short as keeps a newer snapshot in hand: each arrival's lateness (our tick when it arrived,
/// less the tick it was taken on) is kept for a couple of seconds, and what's <see cref="Needed"/> is the latest of
/// them plus the gap between snapshots. Arrivals wobble by a tick with frame timing, so the delay holds steady while
/// it's within <see cref="Slack"/> above that: it only goes up when snapshots come later than it allows, and down when
/// it's well over. Changing it plays the body faster or slower, up to <see cref="MaxSlew"/>, which shows as a stutter
/// in how it moves; far off (a hitch on either machine) it jumps. Lateness includes any error in the clocks, so it
/// copes with the sender's ticks running behind or ahead of ours as well as with the network.</para>
/// </summary>
public sealed class SnapshotBuffer
{
    public const int Capacity = 32;
    /// <summary>A second: long enough to carry a ship on through its owner stalling (another game loading on the same
    /// machine, say) rather than stop it dead under whoever is aboard.</summary>
    public const double MaxExtrapolationTicks = 60;
    /// <summary>Ticks between a sender's snapshots.</summary>
    public const double SendInterval = 2;
    /// <summary>Arrivals the lateness is judged over: about two seconds' worth.</summary>
    public const int LatenessWindow = 60;
    /// <summary>The most faster or slower than real time the delay is caught up by (2%).</summary>
    public const double MaxSlew = 0.02;
    /// <summary>How far over <see cref="Needed"/> the delay may be before it comes down, in ticks; it's set half a tick
    /// over, so a tick's wobble either way in arrivals leaves it be.</summary>
    public const double Slack = 1.5, Headroom = 0.5;
    /// <summary>Further off than this, in ticks, the delay jumps instead.</summary>
    public const double JumpTicks = 6;
    public const double MinDelay = 2;

    private readonly List<(uint Tick, BodySnapshot Snapshot)> _samples = new(Capacity);
    private readonly Queue<double> _lateness = new(LatenessWindow);
    private bool _hasDelay;

    /// <summary>How far behind our tick the body is drawn, in ticks.</summary>
    public double Delay { get; private set; }

    /// <summary>The least delay that keeps a newer snapshot in hand: the latest recent arrival plus the gap between
    /// snapshots (and <see cref="Margin"/>).</summary>
    public double Needed { get; private set; }

    /// <summary>What <see cref="Delay"/> is heading for: where it is while it's enough and not far over
    /// <see cref="Needed"/>, else half a tick over that.</summary>
    public double TargetDelay { get; private set; }

    /// <summary>Extra ticks of delay on top of what's needed (a debug setting).</summary>
    public double Margin { get; set; }

    public int Count => _samples.Count;
    public uint LatestTick => _samples.Count > 0 ? _samples[^1].Tick : 0;
    public BodySnapshot? Latest => _samples.Count > 0 ? _samples[^1].Snapshot : null;

    /// <summary>Adds a snapshot taken on <paramref name="tick"/> that arrived on our tick <paramref name="arrived"/>;
    /// old or duplicate ticks (reordered packets) are ignored.</summary>
    public bool Add(uint tick, in BodySnapshot snapshot, double arrived)
    {
        if (_lateness.Count == LatenessWindow) _lateness.Dequeue();
        _lateness.Enqueue(arrived - tick);
        Needed = System.Math.Max(MinDelay, _lateness.Max() + SendInterval) + Margin;
        if (!_hasDelay) (Delay, TargetDelay, _hasDelay) = (Needed + Headroom, Needed + Headroom, true);

        if (_samples.Count > 0 && tick <= _samples[^1].Tick)
        {
            // Out of order: slot it in if it's new, so interpolation still has it.
            int i = _samples.FindIndex(s => s.Tick >= tick);
            if (i < 0 || _samples[i].Tick == tick) return false;
            _samples.Insert(i, (tick, snapshot));
        }
        else _samples.Add((tick, snapshot));
        if (_samples.Count > Capacity) _samples.RemoveAt(0);
        return true;
    }

    /// <summary>Moves <see cref="Delay"/> towards <see cref="TargetDelay"/> over <paramref name="ticks"/>; true if it
    /// jumped there instead.</summary>
    public bool UpdateDelay(double ticks)
    {
        if (Delay < Needed || Delay > Needed + Slack) TargetDelay = Needed + Headroom;
        double off = TargetDelay - Delay;
        if (System.Math.Abs(off) > JumpTicks)
        {
            Delay = TargetDelay;
            return true;
        }
        Delay += System.Math.Clamp(off, -MaxSlew * ticks, MaxSlew * ticks);
        return false;
    }

    /// <summary>Our clock jumped by <paramref name="ticks"/> (clock sync snapped it): what's been measured against it
    /// moves with it, so the body carries on being drawn where it was.</summary>
    public void ShiftClock(double ticks)
    {
        int n = _lateness.Count;
        for (int i = 0; i < n; i++) _lateness.Enqueue(_lateness.Dequeue() + ticks);
        Needed += ticks;
        TargetDelay += ticks;
        Delay += ticks;
    }

    /// <summary>The body at <paramref name="renderTick"/>, in its support's space (or world space): support, position,
    /// rotation and look. Null before any snapshot has arrived.</summary>
    public Sample? At(double renderTick)
    {
        if (_samples.Count == 0) return null;
        // Before the first: where it was then, back along its velocity (a ship just sent, moving, doesn't stand still
        // until the delay catches up with its first snapshot, then lurch off at full speed under whoever's aboard).
        if (renderTick <= _samples[0].Tick) return Extrapolated(_samples[0].Snapshot, renderTick - _samples[0].Tick);

        for (int i = _samples.Count - 1; i > 0; i--)
        {
            var (t1, b) = _samples[i];
            var (t0, a) = _samples[i - 1];
            if (renderTick < t0 || renderTick > t1) continue;
            if (a.Support != b.Support) return From(renderTick - t0 < t1 - renderTick ? a : b); // changed support: no blending across spaces
            float f = (float)((renderTick - t0) / System.Math.Max(1, t1 - t0));
            return new Sample(b.Support, Vector3.Lerp(a.Position, b.Position, f), Quaternion.Slerp(a.Rotation, b.Rotation, f),
                              new LookAngles(LerpAngle(a.Look.Yaw, b.Look.Yaw, f), a.Look.Pitch + (b.Look.Pitch - a.Look.Pitch) * f),
                              b.LinearVelocity, (b.Flags & SnapshotFlags.HasLook) != 0, (b.Flags & SnapshotFlags.FreeFlying) != 0);
        }

        // After the last: extrapolate from its velocity, for a while (a body at rest sends none, so stays put).
        var (tl, last) = _samples[^1];
        return Extrapolated(last, renderTick - tl);
    }

    /// <summary>A snapshot moved along its velocity by <paramref name="ticks"/> (back if negative), up to
    /// <see cref="MaxExtrapolationTicks"/>. Only in world space: on a support, the velocity (a world one) isn't motion
    /// on it, so it stays where it was there.</summary>
    private static Sample Extrapolated(in BodySnapshot snapshot, double ticks)
    {
        var s = From(snapshot);
        if (!snapshot.Support.IsNone) return s;
        ticks = System.Math.Clamp(ticks, -MaxExtrapolationTicks, MaxExtrapolationTicks);
        return s with { Position = snapshot.Position + snapshot.LinearVelocity * (float)(ticks / 60.0) };
    }

    private static Sample From(in BodySnapshot s) =>
        new(s.Support, s.Position, s.Rotation, s.Look, s.LinearVelocity, (s.Flags & SnapshotFlags.HasLook) != 0,
            (s.Flags & SnapshotFlags.FreeFlying) != 0);

    private static float LerpAngle(float a, float b, float f)
    {
        float d = MathF.IEEERemainder(b - a, 2 * MathF.PI);
        return a + d * f;
    }

    public readonly record struct Sample(EntityId Support, Vector3 Position, Quaternion Rotation, LookAngles Look, Vector3 Velocity,
                                         bool HasLook, bool FreeFlying);
}

/// <summary>ECS component holding a remote body's <see cref="SnapshotBuffer"/>.</summary>
public struct RemoteBody
{
    public SnapshotBuffer Buffer;
    /// <summary>Its next pose is a jump (its first, or the delay jumped), to be drawn there straight away.</summary>
    public bool Jumped;
}
