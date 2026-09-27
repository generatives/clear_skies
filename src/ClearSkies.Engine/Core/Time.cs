namespace ClearSkies.Engine.Core;

/// <summary>Frame and tick timing. Frames have a variable duration (<see cref="DeltaSeconds"/>); gameplay and physics
/// run in fixed ticks of <see cref="TickSeconds"/> (see <see cref="TickClock"/>), numbered by <see cref="Tick"/>.</summary>
public sealed class Time
{
    private double _fpsAccum;
    private int _fpsFrames;

    public float DeltaSeconds { get; private set; }
    public double TotalSeconds { get; private set; }

    /// <summary>Simulated time per tick: 1/60 s.</summary>
    public float TickSeconds { get; } = 1f / 60f;

    /// <summary>The same as <see cref="TickSeconds"/>; the physics step is one tick.</summary>
    public float FixedStep => TickSeconds;

    /// <summary>The number of the tick running now, or of the last one run. Ticks count up from 1.</summary>
    public uint Tick { get; internal set; }

    /// <summary>How far the frame being drawn is between the last two ticks (0 = the previous tick, 1 = the latest):
    /// interpolated drawing (see <see cref="ECS.TickInterpolationSystem"/>) places things this far along.</summary>
    public float Alpha { get; internal set; }

    /// <summary>Ticks run during the last frame (0 to <see cref="TickClock.MaxTicksPerFrame"/>).</summary>
    public int TicksLastFrame { get; internal set; }

    public int FramesPerSecond { get; private set; }

    internal void Advance(double dt)
    {
        DeltaSeconds = (float)dt;
        TotalSeconds += dt;

        _fpsAccum += dt;
        _fpsFrames++;
        if (_fpsAccum >= 0.5)
        {
            FramesPerSecond = (int)(_fpsFrames / _fpsAccum);
            _fpsAccum = 0;
            _fpsFrames = 0;
        }
    }
}
