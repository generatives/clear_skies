namespace ClearSkies.Engine.Core;

/// <summary>
/// The last <see cref="Capacity"/> frames, one at a time, for the frame timings panel: how long each took, how many
/// ticks it ran and whether a garbage collection landed in it, so a one-off spike shows up where the smoothed averages
/// hide it. Each frame also keeps the systems that took longest in it, so the slowest recent frame can say where its
/// time went.
/// </summary>
public sealed class FrameHistory
{
    public const int Capacity = 300;

    /// <summary>How many of a frame's slowest systems it keeps.</summary>
    public const int TopSystems = 5;

    /// <summary>One frame: its length, ticks, the highest GC generation collected during it (-1 for none), CPU time
    /// in all its systems, and its <see cref="TopSystems"/> slowest (index into the host's systems, or
    /// <see cref="FrameBegin"/> / <see cref="FrameEnd"/>; -1 past the end).</summary>
    public struct Frame
    {
        public float Ms;
        public int Ticks;
        public int GcGeneration;
        public float CpuMs;
        public long Number;
        internal (int System, float Ms)[] Top;

        public ReadOnlySpan<(int System, float Ms)> Slowest => Top;
    }

    /// <summary>Stands for opening the frame (camera, acquire) in <see cref="Frame.Slowest"/>.</summary>
    public const int FrameBegin = -2;

    /// <summary>Stands for closing the frame (ImGui, submit, present) in <see cref="Frame.Slowest"/>.</summary>
    public const int FrameEnd = -3;

    private readonly Frame[] _frames = new Frame[Capacity];
    private readonly float[] _ms = new float[Capacity];
    private readonly float[] _ticks = new float[Capacity];
    private int _next;
    private long _recorded;

    public FrameHistory()
    {
        for (int i = 0; i < Capacity; i++) _frames[i].Top = new (int, float)[TopSystems];
    }

    /// <summary>Frames recorded so far (all of them, not just those still held).</summary>
    public long Recorded => _recorded;

    /// <summary>Frames held: up to <see cref="Capacity"/>.</summary>
    public int Count => (int)System.Math.Min(_recorded, Capacity);

    /// <summary>When set, frames aren't recorded, so the graph holds still to be looked at.</summary>
    public bool Paused { get; set; }

    /// <summary>Frame lengths (ms) and ticks per frame, oldest first from <see cref="Offset"/> (a ring), for plotting.</summary>
    public float[] Milliseconds => _ms;
    public float[] TicksPerFrame => _ticks;
    public int Offset => _recorded < Capacity ? 0 : _next;

    /// <summary>Records a frame. <paramref name="systemMs"/> is each system's CPU time in it.</summary>
    public void Record(double frameMs, int ticks, int gcGeneration, IReadOnlyList<double> systemMs,
                       double beginMs, double endMs)
    {
        if (Paused) return;
        ref var f = ref _frames[_next];
        f.Ms = (float)frameMs;
        f.Ticks = ticks;
        f.GcGeneration = gcGeneration;
        f.Number = _recorded;
        var top = f.Top;
        for (int k = 0; k < TopSystems; k++) top[k] = (-1, 0f);
        double cpu = beginMs + endMs;
        Consider(top, FrameBegin, (float)beginMs);
        Consider(top, FrameEnd, (float)endMs);
        for (int i = 0; i < systemMs.Count; i++)
        {
            cpu += systemMs[i];
            Consider(top, i, (float)systemMs[i]);
        }
        f.CpuMs = (float)cpu;
        _ms[_next] = f.Ms;
        _ticks[_next] = ticks;
        _next = (_next + 1) % Capacity;
        _recorded++;
    }

    /// <summary>Keeps <paramref name="top"/> sorted slowest first, inserting (system, ms) if it's among them.</summary>
    private static void Consider((int System, float Ms)[] top, int system, float ms)
    {
        if (ms <= 0f) return;
        int at = top.Length;
        while (at > 0 && (top[at - 1].System == -1 || top[at - 1].Ms < ms)) at--;
        if (at == top.Length) return;
        for (int k = top.Length - 1; k > at; k--) top[k] = top[k - 1];
        top[at] = (system, ms);
    }

    /// <summary>The <paramref name="ago"/>th most recent frame (0 = the latest).</summary>
    public ref readonly Frame Get(int ago) => ref _frames[((_next - 1 - ago) % Capacity + Capacity) % Capacity];

    /// <summary>Summary of the frames held: average and longest frame, and how many took over
    /// <paramref name="spikeFactor"/> times the average. <paramref name="slowestAgo"/> is how many frames ago the
    /// longest was.</summary>
    public (float AverageMs, float WorstMs, int Spikes, int SlowestAgo) Summarize(float spikeFactor = 1.5f)
    {
        int n = Count;
        if (n == 0) return (0, 0, 0, 0);
        double sum = 0;
        float worst = 0;
        int worstAgo = 0;
        for (int ago = 0; ago < n; ago++)
        {
            float ms = Get(ago).Ms;
            sum += ms;
            if (ms > worst) { worst = ms; worstAgo = ago; }
        }
        float average = (float)(sum / n);
        int spikes = 0;
        for (int ago = 0; ago < n; ago++)
            if (Get(ago).Ms > average * spikeFactor) spikes++;
        return (average, worst, spikes, worstAgo);
    }
}
