using ClearSkies.Engine.Core;
using ClearSkies.Net.Protocol;

namespace ClearSkies.Net.Session;

/// <summary>
/// Keeps a client on the host's tick timeline. Every 250 ms the client pings; the host answers with its tick at once.
/// Each answer gives a round trip and an estimate of the host's tick now (its tick when it answered, plus half the
/// round trip); the estimate used is the average of the lowest-round-trip half of the last 16 answers, because answers
/// delayed by jitter overestimate the one-way time. Off by more than <see cref="SnapTicks"/> (joining, a hitch): snap.
/// Otherwise slew, running ticks up to 2% faster or slower until within half a tick. The host never adjusts.
/// </summary>
public sealed class ClockSync
{
    public const double PingIntervalMs = 250;
    public const int SampleCount = 16;
    public const double SnapTicks = 10;
    public const double MaxSlew = 0.02;
    public const double TickMs = 1000.0 / 60.0;

    private readonly ITickClock _clock;
    private readonly List<(double RttMs, double Offset)> _samples = new();
    private double _lastPing = double.NegativeInfinity;
    private readonly Queue<double> _snapTimes = new();

    public ClockSync(ITickClock clock) => _clock = clock;

    /// <summary>The round trip, in milliseconds (lowest-half average).</summary>
    public double RoundTripMs { get; private set; }

    /// <summary>How far the host's tick is ahead of ours, in ticks, as last estimated.</summary>
    public double Offset { get; private set; }

    public bool HasEstimate => _samples.Count > 0;

    /// <summary>Raised when the clock is snapped, with how many ticks it moved.</summary>
    public event Action<double>? Snapped;
    public long Snaps { get; private set; }

    /// <summary>Snaps in the last minute.</summary>
    public int SnapsPerMinute => _snapTimes.Count;

    /// <summary>True when it's time to send another ping.</summary>
    public bool ShouldPing(double nowMs)
    {
        if (nowMs - _lastPing < PingIntervalMs) return false;
        _lastPing = nowMs;
        return true;
    }

    private double LocalTick => _clock.Tick + (double)_clock.Alpha;

    /// <summary>Takes the host's answer to a ping and corrects the clock.</summary>
    public void OnPong(in TimePong pong, double nowMs)
    {
        double rtt = System.Math.Max(0, nowMs - pong.ClientTimeMs);
        double hostNow = pong.HostTick + pong.HostFraction + rtt / 2 / TickMs;
        _samples.Add((rtt, hostNow - LocalTick));
        if (_samples.Count > SampleCount) _samples.RemoveAt(0);
        Correct(nowMs);
    }

    /// <summary>Estimates the offset from the samples with the lowest round trips (the least delayed), then snaps the
    /// clock if it's far out, or slews it.</summary>
    private void Correct(double nowMs)
    {
        var best = _samples.OrderBy(s => s.RttMs).Take(System.Math.Max(1, _samples.Count / 2)).ToList();
        RoundTripMs = best.Average(s => s.RttMs);
        Offset = best.Average(s => s.Offset);

        while (_snapTimes.Count > 0 && nowMs - _snapTimes.Peek() > 60_000) _snapTimes.Dequeue();
        if (System.Math.Abs(Offset) > SnapTicks)
        {
            double before = LocalTick;
            long target = (long)System.Math.Round(before + Offset);
            _clock.Snap((uint)System.Math.Max(0, target));
            // Every stored offset was measured against the old tick numbers.
            double applied = LocalTick - before;
            for (int i = 0; i < _samples.Count; i++) _samples[i] = (_samples[i].RttMs, _samples[i].Offset - applied);
            Offset = 0;
            _clock.Rate = 1;
            Snaps++;
            _snapTimes.Enqueue(nowMs);
            Snapped?.Invoke(applied);
        }
        else
        {
            // Slew: faster when behind, slower when ahead, proportionally, until within half a tick.
            _clock.Rate = System.Math.Abs(Offset) < 0.5 ? 1 : 1 + System.Math.Clamp(Offset * 0.01, -MaxSlew, MaxSlew);
        }
    }

    /// <summary>Snaps straight to a known host tick (on Welcome, before any pings).</summary>
    public void SnapTo(uint hostTick)
    {
        double before = LocalTick;
        _clock.Snap(hostTick);
        Snapped?.Invoke(LocalTick - before);
        _samples.Clear();
        Offset = 0;
    }
}
