using System.Diagnostics;
using System.Text;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Game.Diagnostics;

/// <summary>
/// A repeatable flight for judging streaming hitches: flies the free-fly camera level along the way it faces for
/// <see cref="_distance"/> blocks at <see cref="_speed"/> blocks/s, and straight back, so the world streams in ahead
/// and out behind, then back again. Meanwhile it records every frame's length, and at the end reports them (average,
/// percentiles, how many were long) with the longest time each timed step (<see cref="StepTimer"/>) took in one
/// frame, so a change can be compared on the same route. It also reports GPU time per timed pass, draw calls, and
/// memory (light, mesh, occupancy, CPU chunk data) at the end and at its peak. Start it from
/// its debug panel, or with <c>--flight-test</c>, which starts it once the world around the spawn has loaded (the same
/// route every run) and closes the game when it's done.
/// </summary>
public sealed class StreamingFlightTest : ISystem, IDebugUiSystem
{
    private readonly WindowedEngineHost _host;
    private readonly EntitySet _cameras;
    private readonly Action? _whenDone;
    private readonly Stopwatch _clock = new();
    // Per frame: its length, garbage-collection pause and collections in it, GPU buffer writes queued in it, and its
    // three slowest systems (index into the host's systems, or FrameBegin / FrameEnd; -1 for none).
    private readonly ChunkVolume _world;
    private readonly GridStore _store;
    private readonly List<Frame> _frames = new();
    private readonly record struct Frame(float Ms, float GcPauseMs, int Collections, long Writes, long WriteBytes, int Draws,
                                         (int System, float Ms) Top1, (int System, float Ms) Top2, (int System, float Ms) Top3);
    private const int FrameBegin = -2, FrameEnd = -3;
    private TimeSpan _lastPause;
    private int _lastCollections;
    private long _lastWrites, _lastWriteBytes;

    private float _distance = 1500f, _speed = 60f;
    private bool _running;
    private Vector3D<float> _start, _direction;
    private float _travelled;
    private double _lastFrame;
    private int[] _gcAtStart = new int[3];
    private string _report = "";

    // With --flight-test: wait this long for the world around the camera to load before flying.
    private float _autoStartIn = -1f;

    // Peaks over the flight.
    private int _peakLightSlots, _peakOccSlots;
    private long _peakMeshBytes;

    // With --flight-test: the look (yaw, pitch) held for the whole flight, which also sets the heading, so every run
    // flies the same line and sees the same view however the mouse moved while the world loaded.
    private readonly (float Yaw, float Pitch)? _fixedLook;

    public StreamingFlightTest(WindowedEngineHost host, ChunkVolume world, GridStore store, bool autoStart, Action? whenDone = null,
                               (float Yaw, float Pitch)? fixedLook = null)
    {
        _host = host;
        _fixedLook = autoStart ? fixedLook : null;
        _world = world;
        _store = store;
        host.FrameEnded += RecordFrame;
        // Whatever free-flies: the camera itself, or the player the camera follows.
        _cameras = host.World.GetEntities().With<Transform>().With<FreeFlyController>().AsSet();
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
            ImGui.Text($"Flying: {_travelled:F0} / {2 * _distance:F0} blocks, {_frames.Count} frames");
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
        var e = _cameras.GetEntities()[0];
        ref var t = ref e.Get<Transform>();
        HoldLook(e);
        // Distance by the clock rather than dt, so a long frame moves the camera as far as it would have in real time.
        _travelled = MathF.Min(2 * _distance, (float)(now / 1000.0) * _speed);
        float along = _travelled <= _distance ? _travelled : 2 * _distance - _travelled;
        t.Position = _start + _direction * along;
        if (_travelled >= 2 * _distance) Finish();
    }

    /// <summary>At the end of each frame (from its end to this one's).</summary>
    private void RecordFrame()
    {
        if (!_running) return;
        double now = _clock.Elapsed.TotalMilliseconds;
        var pause = GC.GetTotalPauseDuration();
        int collections = GC.CollectionCount(0); // every collection includes gen0
        (int, float) a = (-1, 0f), b = (-1, 0f), c = (-1, 0f);
        void Consider(int system, double ms)
        {
            var x = (system, (float)ms);
            if (x.Item2 > a.Item2) (a, b, c) = (x, a, b);
            else if (x.Item2 > b.Item2) (b, c) = (x, b);
            else if (x.Item2 > c.Item2) c = x;
        }
        for (int i = 0; i < _host.SystemCount; i++) Consider(i, _host.LastSystemMs(i));
        Consider(FrameBegin, _host.LastFrameBeginMs);
        Consider(FrameEnd, _host.LastFrameEndMs);
        _frames.Add(new Frame((float)(now - _lastFrame), (float)(pause - _lastPause).TotalMilliseconds,
                              collections - _lastCollections, GpuBuffer.WriteCount - _lastWrites,
                              GpuBuffer.WriteBytes - _lastWriteBytes, _host.Renderer.DrawCount, a, b, c));
        _peakLightSlots = System.Math.Max(_peakLightSlots, _store.LightSlotsInUse);
        _peakOccSlots = System.Math.Max(_peakOccSlots, _store.OccSlotsInUse);
        _peakMeshBytes = System.Math.Max(_peakMeshBytes, GpuMesh.LiveBytes);
        _lastFrame = now;
        (_lastPause, _lastCollections) = (pause, collections);
        (_lastWrites, _lastWriteBytes) = (GpuBuffer.WriteCount, GpuBuffer.WriteBytes);
    }

    private string Name(int system) => system switch
    {
        FrameBegin => "Frame begin",
        FrameEnd => "Frame end (submit, present)",
        _ => _host.SystemName(system),
    };

    private void Begin()
    {
        if (_cameras.Count == 0) return;
        var flyer = _cameras.GetEntities()[0];
        if (flyer.Has<CharacterControllerComponent>()) Players.SetFreeFlying(flyer, true); // straight through anything
        HoldLook(flyer);
        var t = flyer.Get<Transform>();
        var forward = Engine.Math.Vec.Rotate(t.Rotation, new Vector3D<float>(0, 0, -1));
        forward.Y = 0;
        if (forward.LengthSquared < 1e-6f) forward = new Vector3D<float>(1, 0, 0);
        _direction = Vector3D.Normalize(forward);
        _start = t.Position;
        _travelled = 0;
        _frames.Clear();
        (_lastPause, _lastCollections) = (GC.GetTotalPauseDuration(), GC.CollectionCount(0));
        (_lastWrites, _lastWriteBytes) = (GpuBuffer.WriteCount, GpuBuffer.WriteBytes);
        foreach (var timer in StepTimer.All) timer.Reset();
        for (int g = 0; g < 3; g++) _gcAtStart[g] = GC.CollectionCount(g);
        _host.Context.Timer.BeginTotals();
        _peakLightSlots = _peakOccSlots = 0;
        _peakMeshBytes = 0;
        _clock.Restart();
        _lastFrame = 0;
        _running = true;
        Console.WriteLine($"[flight] {_distance:F0} blocks out and back at {_speed:F0} blocks/s from {_start}, heading {_direction}");
    }

    /// <summary>With a fixed look, puts the flyer's look and body turn back to it (after the mouse moved them).</summary>
    private void HoldLook(Entity flyer)
    {
        if (_fixedLook is not { } look || !flyer.Has<MouseLookComponent>()) return;
        ref var m = ref flyer.Get<MouseLookComponent>();
        (m.Yaw, m.Pitch) = look;
        flyer.Get<Transform>().Rotation = m.BodyRotation;
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
        var frames = _frames.Skip(1).ToArray(); // the first frame's length includes whatever ran before the start
        var ms = frames.Select(f => f.Ms).ToArray();
        if (ms.Length == 0) return "No frames recorded.";
        var sorted = ms.OrderBy(v => v).ToArray();
        float P(double q) => sorted[(int)System.Math.Min(sorted.Length - 1, q * sorted.Length)];
        float median = P(0.5);
        // A hitch: a frame over twice the median. Its excess over the median is the time the motion visibly stalls.
        var hitches = frames.Where(f => f.Ms > 2 * median).ToArray();
        sb.AppendLine($"Flight test: {_distance:F0} blocks out and back at {_speed:F0} blocks/s, {ms.Length} frames " +
                      $"in {_clock.Elapsed.TotalSeconds:F1} s, {BackgroundWork.Workers} background workers of " +
                      $"{Environment.ProcessorCount} cores");
        sb.AppendLine($"  from ({_start.X:F0}, {_start.Y:F0}, {_start.Z:F0}) heading ({_direction.X:F2}, {_direction.Z:F2})");
        sb.AppendLine($"  frame ms: average {ms.Average():F1}, median {median:F1}, 95% {P(0.95):F1}, 99% {P(0.99):F1}, " +
                      $"longest {sorted[^1]:F1}");
        sb.AppendLine($"  hitches (over 2x median): {hitches.Length}, stalled {hitches.Sum(f => f.Ms - median):F0} ms in all; " +
                      $"over 33 ms: {ms.Count(v => v > 33.3f)}, over 50 ms: {ms.Count(v => v > 50f)}");
        sb.AppendLine($"  garbage collections: gen0 {GC.CollectionCount(0) - _gcAtStart[0]}, " +
                      $"gen1 {GC.CollectionCount(1) - _gcAtStart[1]}, gen2 {GC.CollectionCount(2) - _gcAtStart[2]}; " +
                      $"paused {frames.Sum(f => f.GcPauseMs):F0} ms in all, longest in one frame {frames.Max(f => f.GcPauseMs):F1} ms");
        var gcHitches = hitches.Where(f => f.Collections > 0).ToArray();
        sb.AppendLine($"  hitches with a collection in them: {gcHitches.Length} of {hitches.Length}, " +
                      $"their pauses {gcHitches.Sum(f => f.GcPauseMs):F0} ms of their {gcHitches.Sum(f => f.Ms - median):F0} ms stall");
        sb.AppendLine($"  GPU buffer writes per frame: average {frames.Average(f => f.Writes):F0}, most {frames.Max(f => f.Writes)}; " +
                      $"KB per frame: average {frames.Average(f => f.WriteBytes) / 1024:F0}, most {frames.Max(f => f.WriteBytes) / 1024}");
        var draws = frames.Select(f => f.Draws).OrderBy(v => v).ToArray();
        sb.AppendLine($"  draw calls per frame: average {draws.Average():F0}, median {draws[draws.Length / 2]}, most {draws[^1]}");
        GpuReport(sb);
        MemoryReport(sb);
        sb.AppendLine("  longest frames (ms, GC pause ms, GPU writes, KB written; slowest systems):");
        foreach (var f in frames.OrderByDescending(f => f.Ms).Take(8))
        {
            sb.AppendLine($"    {f.Ms,6:F1}  {f.GcPauseMs,5:F1}  {f.Writes,6}  {f.WriteBytes / 1024,6}");
            foreach (var (system, systemMs) in new[] { f.Top1, f.Top2, f.Top3 })
                if (system != -1) sb.AppendLine($"        {systemMs,6:F1}  {Name(system)}");
        }
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

    /// <summary>GPU time per timed pass over the flight (average per frame, and in the frames it ran).</summary>
    private void GpuReport(StringBuilder sb)
    {
        var timer = _host.Context.Timer;
        if (!timer.Supported) { sb.AppendLine("  GPU time: not measurable here (no timestamp queries)"); return; }
        var (count, busy, passes) = timer.Totals();
        if (count == 0) { sb.AppendLine("  GPU time: no frames read back"); return; }
        sb.AppendLine($"  GPU ms per frame (timed passes, {count} frames{(timer.Calibrated ? "" : ", tick length not measured yet: may be off")}): " +
                      $"busy {busy / count:F2}");
        foreach (var (name, (ms, n)) in passes.OrderByDescending(p => p.Value.Ms))
            sb.AppendLine($"    {ms / count,7:F2}  (in the {n} frames it ran: {ms / n:F2})  {name}");
    }

    /// <summary>Memory at the end of the flight and at its peak.</summary>
    private void MemoryReport(StringBuilder sb)
    {
        const double Mb = 1024.0 * 1024.0;
        int dense = _world.DenseCount();
        double light = (double)_store.LightSlotsInUse * GridStore.SlotBytes / Mb;
        double occ = (double)_store.OccSlotsInUse * GridStore.WordsPerChunk * 4 / Mb;
        double mesh = GpuMesh.LiveBytes / Mb, wire = GpuMesh.LiveWireframeBytes / Mb;
        double cpu = (double)dense * ChunkData.Size * ChunkData.Size * ChunkData.Size / Mb;
        sb.AppendLine($"  memory at the end (MB; peak in brackets): light {light:F0} ({(double)_peakLightSlots * GridStore.SlotBytes / Mb:F0}), " +
                      $"mesh {mesh:F0} ({_peakMeshBytes / Mb:F0}; wireframe indices {wire:F0} of it), " +
                      $"occupancy {occ:F0} ({(double)_peakOccSlots * GridStore.WordsPerChunk * 4 / Mb:F0}), CPU chunk data {cpu:F0}");
        sb.AppendLine($"    {_store.LightSlotsInUse:N0} light bricks, {_store.WorldChunkCount:N0} world chunks on the GPU, " +
                      $"{_world.LoadedCount:N0} loaded ({dense:N0} dense)");
    }
}
