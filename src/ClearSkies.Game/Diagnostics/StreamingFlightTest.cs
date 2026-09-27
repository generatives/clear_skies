using System.Diagnostics;
using System.Text;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Gui;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Game.Diagnostics;

/// <summary>
/// A repeatable flight for judging streaming hitches: flies the free-fly camera level along the way it faces for
/// <see cref="_distance"/> blocks at <see cref="_speed"/> blocks/s, and straight back, so the world streams in ahead
/// and out behind, then back again. Meanwhile it records every frame's length, and at the end reports them (average,
/// percentiles, how many were long) with the longest time each timed step (<see cref="StepTimer"/>) took in one
/// frame, so a change can be compared on the same route. Start it from its debug panel, or with <c>--flight-test</c>,
/// which starts it once the world around the camera has loaded and closes the game when it's done.
/// </summary>
public sealed class StreamingFlightTest : ISystem, IDebugUiSystem
{
    private readonly EntitySet _cameras;
    private readonly Action? _whenDone;
    private readonly Stopwatch _clock = new();
    private readonly List<float> _frameMs = new();

    private float _distance = 1500f, _speed = 60f;
    private bool _running;
    private Vector3D<float> _start, _direction;
    private float _travelled;
    private double _lastFrame;
    private int[] _gcAtStart = new int[3];
    private string _report = "";

    // With --flight-test: wait this long for the world around the camera to load before flying.
    private float _autoStartIn = -1f;

    public StreamingFlightTest(World world, bool autoStart, Action? whenDone = null)
    {
        _cameras = world.GetEntities().With<Transform>().With<CameraComponent>().With<FreeFlyController>().AsSet();
        _whenDone = whenDone;
        if (autoStart) _autoStartIn = 20f;
    }

    public string DebugName => "Streaming flight test";

    public void DrawDebugUi()
    {
        ImGui.TextWrapped("Flies the camera level the way it faces, out and straight back, and reports the frame times " +
                          "and the longest time of each timed step. Stand somewhere with islands ahead; keep the " +
                          "route the same to compare changes. Resets the steps' \"worst ever\".");
        ImGui.SliderFloat("Distance (blocks)", ref _distance, 200f, 5000f, "%.0f");
        ImGui.SliderFloat("Speed (blocks/s)", ref _speed, 10f, 300f, "%.0f");
        if (_running)
        {
            ImGui.Text($"Flying: {_travelled:F0} / {2 * _distance:F0} blocks, {_frameMs.Count} frames");
            if (ImGui.Button("Stop")) Finish();
        }
        else if (ImGui.Button("Start flight")) Begin();
        if (_report.Length == 0) return;
        ImGui.SameLine();
        if (ImGui.Button("Copy report")) ImGui.SetClipboardText(_report);
        ImGui.Separator();
        ImGui.TextUnformatted(_report);
    }

    public void Update(float dt)
    {
        if (_autoStartIn > 0f && (_autoStartIn -= dt) <= 0f) Begin();
        if (!_running) return;

        double now = _clock.Elapsed.TotalMilliseconds;
        _frameMs.Add((float)(now - _lastFrame));
        _lastFrame = now;

        var e = _cameras.GetEntities()[0];
        ref var t = ref e.Get<Transform>();
        // Distance by the clock rather than dt, so a long frame moves the camera as far as it would have in real time.
        _travelled = MathF.Min(2 * _distance, (float)(now / 1000.0) * _speed);
        float along = _travelled <= _distance ? _travelled : 2 * _distance - _travelled;
        t.Position = _start + _direction * along;
        if (_travelled >= 2 * _distance) Finish();
    }

    private void Begin()
    {
        if (_cameras.Count == 0) return;
        var t = _cameras.GetEntities()[0].Get<Transform>();
        var forward = Engine.Math.Vec.Rotate(t.Rotation, new Vector3D<float>(0, 0, -1));
        forward.Y = 0;
        if (forward.LengthSquared < 1e-6f) forward = new Vector3D<float>(1, 0, 0);
        _direction = Vector3D.Normalize(forward);
        _start = t.Position;
        _travelled = 0;
        _frameMs.Clear();
        foreach (var timer in StepTimer.All) timer.Reset();
        for (int g = 0; g < 3; g++) _gcAtStart[g] = GC.CollectionCount(g);
        _clock.Restart();
        _lastFrame = 0;
        _running = true;
        Console.WriteLine($"[flight] {_distance:F0} blocks out and back at {_speed:F0} blocks/s from {_start}");
    }

    private void Finish()
    {
        _running = false;
        var e = _cameras.GetEntities()[0];
        e.Get<Transform>().Position = _start;
        _report = Report();
        Console.WriteLine(_report);
        _whenDone?.Invoke();
    }

    private string Report()
    {
        var sb = new StringBuilder();
        var ms = _frameMs.Skip(1).ToArray(); // the first frame's length includes whatever ran before the start
        if (ms.Length == 0) return "No frames recorded.";
        var sorted = ms.OrderBy(v => v).ToArray();
        float P(double q) => sorted[(int)System.Math.Min(sorted.Length - 1, q * sorted.Length)];
        float median = P(0.5);
        // A hitch: a frame over twice the median. Its excess over the median is the time the motion visibly stalls.
        var hitches = ms.Where(v => v > 2 * median).ToArray();
        sb.AppendLine($"Flight test: {_distance:F0} blocks out and back at {_speed:F0} blocks/s, {ms.Length} frames " +
                      $"in {_clock.Elapsed.TotalSeconds:F1} s");
        sb.AppendLine($"  frame ms: average {ms.Average():F1}, median {median:F1}, 95% {P(0.95):F1}, 99% {P(0.99):F1}, " +
                      $"longest {sorted[^1]:F1}");
        sb.AppendLine($"  hitches (over 2x median): {hitches.Length}, stalled {hitches.Sum(v => v - median):F0} ms in all; " +
                      $"over 33 ms: {ms.Count(v => v > 33.3f)}, over 50 ms: {ms.Count(v => v > 50f)}");
        sb.AppendLine($"  garbage collections: gen0 {GC.CollectionCount(0) - _gcAtStart[0]}, " +
                      $"gen1 {GC.CollectionCount(1) - _gcAtStart[1]}, gen2 {GC.CollectionCount(2) - _gcAtStart[2]}");
        sb.AppendLine("  longest step in one frame (ms), over 1 ms:");
        var steps = new List<(double Worst, double Average, string Name)>();
        foreach (var timer in StepTimer.All)
            for (int i = 0; i < timer.Count; i++)
                if (timer.WorstEver(i) > 1.0)
                    steps.Add((timer.WorstEver(i), timer.Average(i), $"{timer.Owner}: {timer.Name(i)}"));
        foreach (var (worst, average, name) in steps.OrderByDescending(s => s.Worst))
            sb.AppendLine($"    {worst,7:F1}  (average {average,5:F2})  {name}");
        return sb.ToString();
    }
}
