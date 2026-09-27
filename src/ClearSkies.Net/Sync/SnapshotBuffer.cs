using System.Numerics;
using ClearSkies.Net.Protocol;

namespace ClearSkies.Net.Sync;

/// <summary>
/// Snapshots of a body owned by another machine, newest last, for drawing it about <see cref="InterpolationDelay"/>
/// ticks behind: the pair around the render tick is interpolated in the support's space, and with no newer snapshot
/// the body is extrapolated from its last velocity for up to <see cref="MaxExtrapolationTicks"/>, then held.
/// </summary>
public sealed class SnapshotBuffer
{
    public const int Capacity = 32;
    public const double MaxExtrapolationTicks = 15;

    private readonly List<(uint Tick, BodySnapshot Snapshot)> _samples = new(Capacity);

    public int Count => _samples.Count;
    public uint LatestTick => _samples.Count > 0 ? _samples[^1].Tick : 0;
    public BodySnapshot? Latest => _samples.Count > 0 ? _samples[^1].Snapshot : null;

    /// <summary>Adds a snapshot; old or duplicate ticks (reordered packets) are ignored.</summary>
    public bool Add(uint tick, in BodySnapshot snapshot)
    {
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

    /// <summary>The body at <paramref name="renderTick"/>, in its support's space (or world space): support, position,
    /// rotation and look. Null before any snapshot has arrived.</summary>
    public Sample? At(double renderTick)
    {
        if (_samples.Count == 0) return null;
        // Before the first: hold the first.
        if (renderTick <= _samples[0].Tick) return From(_samples[0].Snapshot);

        for (int i = _samples.Count - 1; i > 0; i--)
        {
            var (t1, b) = _samples[i];
            var (t0, a) = _samples[i - 1];
            if (renderTick < t0 || renderTick > t1) continue;
            if (a.Support != b.Support) return From(renderTick - t0 < t1 - renderTick ? a : b); // changed support: no blending across spaces
            float f = (float)((renderTick - t0) / System.Math.Max(1, t1 - t0));
            return new Sample(b.Support, Vector3.Lerp(a.Position, b.Position, f), Quaternion.Slerp(a.Rotation, b.Rotation, f),
                              new LookAngles(LerpAngle(a.Look.Yaw, b.Look.Yaw, f), a.Look.Pitch + (b.Look.Pitch - a.Look.Pitch) * f),
                              b.LinearVelocity, (b.Flags & SnapshotFlags.HasLook) != 0);
        }

        // After the last: extrapolate from its velocity, for a while.
        var (tl, last) = _samples[^1];
        double ahead = System.Math.Min(renderTick - tl, MaxExtrapolationTicks);
        var s = From(last);
        if ((last.Flags & SnapshotFlags.Sleeping) != 0) return s;
        return s with { Position = last.Position + last.LinearVelocity * (float)(ahead / 60.0) };
    }

    private static Sample From(in BodySnapshot s) =>
        new(s.Support, s.Position, s.Rotation, s.Look, s.LinearVelocity, (s.Flags & SnapshotFlags.HasLook) != 0);

    private static float LerpAngle(float a, float b, float f)
    {
        float d = MathF.IEEERemainder(b - a, 2 * MathF.PI);
        return a + d * f;
    }

    public readonly record struct Sample(uint Support, Vector3 Position, Quaternion Rotation, LookAngles Look, Vector3 Velocity, bool HasLook);
}

/// <summary>ECS component holding a remote body's <see cref="SnapshotBuffer"/>.</summary>
public struct RemoteBody
{
    public SnapshotBuffer Buffer;
}
