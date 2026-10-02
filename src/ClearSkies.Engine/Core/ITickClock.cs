namespace ClearSkies.Engine.Core;

/// <summary>This machine's tick timeline, as clock sync sees it: the tick number, how far into the next tick the
/// frame is, and the two ways to line it up with the host's: a snap, or running a little faster or slower.</summary>
/// <remarks>The game's is its <see cref="EngineHost"/>'s <see cref="TickClock"/>.</remarks>
public interface ITickClock
{
    uint Tick { get; }
    float Alpha { get; }

    /// <summary>Tick rate relative to real time (1 = 60 Hz).</summary>
    double Rate { get; set; }

    /// <summary>Jumps to <paramref name="tick"/> and drops any partial tick.</summary>
    void Snap(uint tick);

    /// <summary>Ticks dropped so far by frames too slow to run them all.</summary>
    long DroppedTicks { get; }

    /// <summary>Moves the tick number on by <paramref name="ticks"/> without running them, keeping the partial tick.</summary>
    void Skip(int ticks);
}

/// <summary>A clock stepped by hand, for tests and headless sessions.</summary>
public sealed class ManualTickClock : ITickClock
{
    public uint Tick { get; set; }
    public float Alpha { get; set; }
    public double Rate { get; set; } = 1.0;
    public void Snap(uint tick) { Tick = tick; Alpha = 0; }
    public long DroppedTicks { get; set; }
    public void Skip(int ticks) => Tick += (uint)ticks;
}
