using ClearSkies.Engine.Core;
using ClearSkies.Net.Protocol;

namespace ClearSkies.Net.Session;

/// <summary>
/// Keeps a client on the host's tick timeline. Every 250 ms the client pings; the host answers with its tick at once.
/// Each answer gives a round trip and an estimate of the host's tick when it arrived (its tick when it answered, plus
/// half the round trip), carried forward in real time to now, whatever this clock has done since (slewed, snapped);
/// the estimate used is the average of the lowest-round-trip half of the last 16 answers, because answers delayed by
/// jitter overestimate the one-way time. Off by more than <see cref="SnapTicks"/> (joining, a hitch): snap.
/// Otherwise slew, running ticks up to 2% faster or slower until within half a tick. The host never adjusts.
/// <para>While joining (<see cref="Settling"/>), nothing is drawn or predicted from the clock yet, so it snaps to every
/// estimate more than <see cref="SettleTicks"/> out instead. It's <see cref="Settled"/> once
/// <see cref="SettleAnswers"/> answers in a row (two seconds) have found it within that, so the game starts on the
/// host's timeline rather than slewing towards it for seconds with players already moving. Loose on purpose: while
/// loading, long frames hold the answers back and make them a tick or so noisier than in play. <see cref="Update"/>
/// ends settling then, or after <see cref="MaxSettleMs"/> regardless (<see cref="Ready"/>), and snaps to the estimate,
/// still behind the joining screen, rather than leave the last tick or two to slew.</para>
/// <para>A frame too slow to run all its ticks (loading, a hitch) drops the rest, which leaves the clock exactly that
/// many behind the host's. <see cref="Update"/> puts them straight back (skipping their numbers, not running them), so
/// it isn't left to the estimate to notice, a few answers at a time, with a snap or two on the way.</para>
/// </summary>
public sealed class ClockSync
{
    public const double PingIntervalMs = 250;
    public const int SampleCount = 16;
    public const double SnapTicks = 10;
    public const double MaxSlew = 0.02;
    public const double TickMs = 1000.0 / 60.0;
    /// <summary>How far out, in ticks, settling snaps, and the estimates must stay within to be settled: the answers'
    /// own jitter is a good part of a tick, and snapping to it only adds more. The last of it is slewed.</summary>
    public const double SettleTicks = 2;

    /// <summary>Answers in a row within <see cref="SettleTicks"/> to be <see cref="Settled"/>: two seconds' worth.</summary>
    public const int SettleAnswers = 8;

    /// <summary>The longest settling waits, in milliseconds: a jittery connection may never quite settle, and it
    /// carries on slewing once joined.</summary>
    public const double MaxSettleMs = 10_000;

    private readonly ITickClock _clock;
    private readonly List<(double RttMs, double HostTick, double AtMs)> _samples = new(); // the host's tick at AtMs
    private double _lastPing = double.NegativeInfinity;
    private readonly Queue<double> _snapTimes = new();

    private long _dropped;
    private double _settleFrom = double.NaN;

    public ClockSync(ITickClock clock)
    {
        _clock = clock;
        _dropped = clock.DroppedTicks;
    }

    /// <summary>Ticks put back after slow frames dropped them (see <see cref="Update"/>).</summary>
    public long SkippedTicks { get; private set; }

    /// <summary>The round trip, in milliseconds (lowest-half average).</summary>
    public double RoundTripMs { get; private set; }

    /// <summary>How far the host's tick is ahead of ours, in ticks, as last estimated.</summary>
    public double Offset { get; private set; }

    public bool HasEstimate => _samples.Count > 0;

    /// <summary>Joining: snap to every estimate more than <see cref="SettleTicks"/> out, rather than slew.</summary>
    public bool Settling { get; set; }

    /// <summary>Done settling (or never settling): the game can be predicted from it.</summary>
    public bool Ready => !Settling;

    /// <summary>Ends settling once <see cref="Settled"/>, or after <see cref="MaxSettleMs"/> regardless.</summary>
    private void Settle(double nowMs)
    {
        if (double.IsNaN(_settleFrom)) _settleFrom = nowMs;
        if (!Settled && nowMs - _settleFrom <= MaxSettleMs) return;
        double waited = (nowMs - _settleFrom) / 1000;
        Console.WriteLine(Settled
            ? $"[net] clock settled after {waited:0.0} s: {Offset:+0.00;-0.00} ticks off, round trip {RoundTripMs:0} ms, {Snaps} snaps, {SkippedTicks} dropped ticks put back"
            : $"[net] clock didn't settle in {waited:0.0} s: {Offset:+0.00;-0.00} ticks off, round trip {RoundTripMs:0} ms, {Snaps} snaps, {SkippedTicks} dropped ticks put back; joining anyway");
        EndSettling(nowMs);
    }

    /// <summary>Joined: snaps to the estimate if it's more than a tick out (nothing is drawn from the clock yet), and
    /// from here on slews.</summary>
    private void EndSettling(double nowMs)
    {
        Settling = false;
        if (_samples.Count == 0) return;
        Estimate(nowMs);
        if (System.Math.Abs(Offset) > 1) Snap(nowMs, log: false);
    }

    /// <summary><see cref="SettleAnswers"/> answers in a row have found the clock within <see cref="SettleTicks"/> of the host's.</summary>
    public bool Settled => _steady >= SettleAnswers;
    private int _steady;

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

    /// <summary>Each tick: skips any ticks the clock dropped since the last, back onto the host's timeline (every
    /// estimate stays good: it was measured before the drop, against where the clock is again, and snapshots' lateness
    /// too, so nothing is shifted); then, while <see cref="Settling"/>, sees whether that's done.</summary>
    public void Update(double nowMs)
    {
        long dropped = _clock.DroppedTicks - _dropped;
        _dropped = _clock.DroppedTicks;
        if (dropped > 0)
        {
            _clock.Skip((int)dropped);
            SkippedTicks += dropped;
        }
        if (Settling) Settle(nowMs);
    }

    private double LocalTick => _clock.Now;

    /// <summary>Takes the host's answer to a ping and corrects the clock.</summary>
    public void OnPong(in TimePong pong, double nowMs)
    {
        double rtt = System.Math.Max(0, nowMs - pong.ClientTimeMs);
        double hostNow = pong.HostTick + pong.HostFraction + rtt / 2 / TickMs;
        _samples.Add((rtt, hostNow, nowMs));
        if (_samples.Count > SampleCount) _samples.RemoveAt(0);
        Correct(nowMs);
    }

    /// <summary>Estimates the offset from the samples with the lowest round trips (the least delayed), each carried on
    /// to now: the host runs in real time.</summary>
    private void Estimate(double nowMs)
    {
        var best = _samples.OrderBy(s => s.RttMs).Take(System.Math.Max(1, _samples.Count / 2)).ToList();
        RoundTripMs = best.Average(s => s.RttMs);
        Offset = best.Average(s => s.HostTick + (nowMs - s.AtMs) / TickMs) - LocalTick;
    }

    /// <summary>Snaps the clock if it's far out, or slews it.</summary>
    private void Correct(double nowMs)
    {
        Estimate(nowMs);

        while (_snapTimes.Count > 0 && nowMs - _snapTimes.Peek() > 60_000) _snapTimes.Dequeue();
        if (System.Math.Abs(Offset) > (Settling ? SettleTicks : SnapTicks)) Snap(nowMs, log: !Settling);
        else
        {
            // Slew: faster when behind, slower when ahead, proportionally, until within half a tick.
            _steady = System.Math.Abs(Offset) <= SettleTicks ? _steady + 1 : 0; // (what settling doesn't snap)
            if (!Settling && System.Math.Abs(Offset) >= 1 && System.Math.Abs(_clock.Rate - 1) < 1e-9)
                Console.WriteLine($"[net] clock {System.Math.Abs(Offset):0.0} ticks {(Offset > 0 ? "behind" : "ahead")}: slewing");
            _clock.Rate = System.Math.Abs(Offset) < 0.5 ? 1 : 1 + System.Math.Clamp(Offset * 0.01, -MaxSlew, MaxSlew);
        }
    }

    /// <summary>Jumps the clock to the estimate.</summary>
    private void Snap(double nowMs, bool log)
    {
        double before = LocalTick;
        long target = (long)System.Math.Round(before + Offset);
        _clock.Snap((uint)System.Math.Max(0, target));
        double applied = LocalTick - before;
        Offset = 0;
        _clock.Rate = 1;
        Snaps++;
        _steady = 0;
        if (log) Console.WriteLine($"[net] clock snapped by {applied:+0.0;-0.0} ticks (round trip {RoundTripMs:0} ms)");
        _snapTimes.Enqueue(nowMs);
        Snapped?.Invoke(applied);
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
