using System.Diagnostics;
using ImGuiNET;

namespace ClearSkies.Engine.Gui;

/// <summary>
/// Times the steps of a system's frame for its debug panel, so a spike can be traced to the step it came from: each
/// step's smoothed time, its longest in the last <see cref="Window"/> frames, and its longest since the panel's Reset.
/// Call <see cref="Start"/> at the top of the frame's work and <see cref="Lap"/> at the end of each step (a step
/// lapped twice in a frame adds up); a frame is counted at the next <see cref="Start"/>, so an early return needs
/// nothing special.
/// </summary>
public sealed class StepTimer
{
    /// <summary>How many recent frames "recent worst" covers (about 5 s at 60 fps).</summary>
    public const int Window = 300;

    private const double Smoothing = 0.1;

    private readonly string[] _names;
    private readonly double[] _frame, _average, _worstEver;
    private readonly float[][] _recent;
    private readonly Stopwatch _sw = new();
    private double _last;
    private int _next;
    private bool _open;

    /// <summary>Every step timer made, so a report (the streaming flight test) can list the worst steps of all.</summary>
    public static IReadOnlyList<StepTimer> All => _all;
    private static readonly List<StepTimer> _all = new();

    /// <summary>Whose steps these are (the system's debug name), for reports that list several timers.</summary>
    public string Owner { get; init; } = "";

    public StepTimer(params string[] names)
    {
        lock (_all) _all.Add(this);
        _names = names;
        _frame = new double[names.Length];
        _average = new double[names.Length];
        _worstEver = new double[names.Length];
        _recent = new float[names.Length][];
        for (int i = 0; i < names.Length; i++) _recent[i] = new float[Window];
    }

    public int Count => _names.Length;
    public string Name(int step) => _names[step];

    /// <summary>A step's smoothed time per frame (ms).</summary>
    public double Average(int step) => _average[step];

    /// <summary>A step's longest time in one frame (ms) over the last <see cref="Window"/> frames.</summary>
    public double RecentWorst(int step)
    {
        float worst = 0;
        foreach (float ms in _recent[step]) worst = System.Math.Max(worst, ms);
        return worst;
    }

    /// <summary>A step's longest time in one frame (ms) since the last <see cref="Reset"/>.</summary>
    public double WorstEver(int step) => _worstEver[step];

    /// <summary>Starts timing a frame's work (counting the previous frame's).</summary>
    public void Start()
    {
        if (_open) Commit();
        _open = true;
        _sw.Restart();
        _last = 0;
    }

    /// <summary>Ends step <paramref name="step"/>: the time since the previous lap (or <see cref="Start"/>).</summary>
    public void Lap(int step)
    {
        double t = _sw.Elapsed.TotalMilliseconds;
        _frame[step] += t - _last;
        _last = t;
    }

    public void Reset() => Array.Clear(_worstEver);

    private void Commit()
    {
        for (int i = 0; i < _names.Length; i++)
        {
            double ms = _frame[i];
            _average[i] += Smoothing * (ms - _average[i]);
            _recent[i][_next] = (float)ms;
            if (ms > _worstEver[i]) _worstEver[i] = ms;
            _frame[i] = 0;
        }
        _next = (_next + 1) % Window;
    }

    /// <summary>A table of the steps for a debug panel, with a button to reset the all-time worst.</summary>
    public void Draw(string title = "CPU time by step (ms):")
    {
        ImGui.Text(title);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Reset worst##{GetHashCode()}")) Reset();
        ImGui.TextDisabled("   average   worst 5 s   worst ever");
        for (int i = 0; i < _names.Length; i++)
            ImGui.Text($"  {_average[i],8:F2}  {RecentWorst(i),10:F2}  {_worstEver[i],11:F2}   {_names[i]}");
    }
}
