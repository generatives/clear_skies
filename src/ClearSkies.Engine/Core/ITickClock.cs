namespace ClearSkies.Engine.Core;

/// <summary>This machine's tick timeline, as clock sync sees it: the tick number, how far into the next tick the
/// frame is, and the two ways to line it up with the host's: a snap, or running a little faster or slower.</summary>
public interface ITickClock
{
    uint Tick { get; }
    float Alpha { get; }

    /// <summary>Tick rate relative to real time (1 = 60 Hz).</summary>
    double Rate { get; set; }

    /// <summary>Jumps to <paramref name="tick"/> and drops any partial tick.</summary>
    void Snap(uint tick);
}

/// <summary>The clock of an <see cref="EngineHost"/>: its <see cref="Time"/> and <see cref="TickClock"/>.</summary>
public sealed class HostTickClock : ITickClock
{
    private readonly Time _time;
    private readonly TickClock _clock;

    public HostTickClock(Time time, TickClock clock)
    {
        _time = time;
        _clock = clock;
    }

    public uint Tick => _time.Tick;
    public float Alpha => _clock.Alpha;
    public double Rate { get => _clock.Rate; set => _clock.Rate = value; }

    public void Snap(uint tick)
    {
        _time.Tick = tick;
        _clock.Reset();
    }
}

/// <summary>A clock stepped by hand, for tests and headless sessions.</summary>
public sealed class ManualTickClock : ITickClock
{
    public uint Tick { get; set; }
    public float Alpha { get; set; }
    public double Rate { get; set; } = 1.0;
    public void Snap(uint tick) { Tick = tick; Alpha = 0; }
}
