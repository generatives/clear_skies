namespace ClearSkies.Engine.Core;

/// <summary>Frame timing. Variable delta for logic/render plus a fixed step reserved for physics.</summary>
public sealed class Time
{
    private double _fpsAccum;
    private int _fpsFrames;

    public float DeltaSeconds { get; private set; }
    public double TotalSeconds { get; private set; }
    public float FixedStep { get; } = 1f / 60f;
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
