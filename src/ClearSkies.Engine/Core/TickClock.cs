namespace ClearSkies.Engine.Core;

/// <summary>
/// Turns variable frame times into a whole number of fixed simulation ticks. Each frame adds its duration; every full
/// <see cref="TickSeconds"/> collected runs one tick, up to <see cref="MaxTicksPerFrame"/> (a quarter of a second) a
/// frame, so a slow frame still keeps game time with real time: a host drawing at 10 fps (say, its window in the
/// background) must, or the machines following its clock fall out of step. A longer backlog (a hitch, a breakpoint) is
/// dropped rather than chased, so the game slows down for that frame instead of spiralling.
/// What's left over is <see cref="Alpha"/>: how far the frame is between the last tick and the next, for drawing.
/// </summary>
public sealed class TickClock : ITickClock
{
    /// <summary>The most ticks one frame runs; the backlog past it is dropped.</summary>
    public const int MaxTicksPerFrame = 15;

    // Frame times that add up to exactly one tick (1/60 s in 60 steps) mustn't come up a hair short from rounding.
    private const double Epsilon = 1e-9;

    private double _accumulator;

    public TickClock(double tickSeconds = 1.0 / 60.0) => TickSeconds = tickSeconds;

    /// <summary>Simulated time per tick.</summary>
    public double TickSeconds { get; }

    /// <summary>The number of the tick running now, or of the last one run. Ticks count up from 1; the host numbers
    /// each as it runs it.</summary>
    public uint Tick { get; internal set; }

    /// <summary>How much faster or slower than real time ticks run: 1 normally. Clock sync nudges it a little either
    /// way to line a client up with the host.</summary>
    public double Rate { get; set; } = 1.0;

    /// <summary>How far the current frame is from the last tick towards the next, 0 to 1.</summary>
    public float Alpha => (float)System.Math.Clamp(_accumulator / TickSeconds, 0.0, 1.0);

    /// <summary>Ticks dropped so far because a frame had more than <see cref="MaxTicksPerFrame"/> of them.</summary>
    public long DroppedTicks { get; private set; }

    /// <summary>Adds a frame's real duration and returns how many ticks to run for it.</summary>
    public int Advance(double frameSeconds)
    {
        _accumulator += System.Math.Max(0.0, frameSeconds) * Rate;
        int ticks = 0;
        while (_accumulator + Epsilon >= TickSeconds && ticks < MaxTicksPerFrame)
        {
            _accumulator -= TickSeconds;
            ticks++;
        }
        if (_accumulator + Epsilon >= TickSeconds)
        {
            long dropped = (long)((_accumulator + Epsilon) / TickSeconds);
            DroppedTicks += dropped;
            _accumulator -= dropped * TickSeconds;
            if (_accumulator + Epsilon >= TickSeconds) _accumulator = 0;
        }
        if (_accumulator < 0) _accumulator = 0;
        return ticks;
    }

    /// <summary>Jumps to <paramref name="tick"/> (clock sync lining up with another machine's) and forgets any partial
    /// tick.</summary>
    public void Snap(uint tick)
    {
        Tick = tick;
        _accumulator = 0;
    }

    /// <summary>Moves the tick number on by <paramref name="ticks"/> without running them (clock sync putting back
    /// ticks a slow frame dropped), keeping the partial tick.</summary>
    public void Skip(int ticks) => Tick += (uint)ticks;
}
