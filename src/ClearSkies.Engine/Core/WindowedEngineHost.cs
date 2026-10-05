using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Windowing;

namespace ClearSkies.Engine.Core;

/// <summary>
/// An <see cref="EngineHost"/> shown in a window: adds the window, GPU context, renderer, input and the ImGui debug
/// UI, and the render stages, and drives the window's loop (update stages, then render stages, each frame). The window
/// is initialized eagerly in the constructor so game content (which uploads meshes via <see cref="Renderer"/>) can be
/// built before <see cref="Run"/> starts the loop.
/// </summary>
public sealed class WindowedEngineHost : EngineHost
{
    public EngineOptions Options { get; }
    public GameWindow Window { get; }
    public GpuContext Context { get; }
    public Renderer Renderer { get; }
    public InputManager Input { get; }
    public ImGuiController Gui { get; }

    /// <summary>The frame the render stages draw into, opened and closed by the host around them; its context is
    /// what every <see cref="IRenderSystem"/> is handed.</summary>
    internal RenderFrame Frame { get; }

    public WindowedEngineHost(EngineOptions options)
    {
        Options = options;
        Window = new GameWindow(options);
        Window.Native.Initialize();   // create the native window now (needed for the WebGPU surface)

        Context = GpuContext.Create(Window, options);
        Renderer = new Renderer(Context);
        Input = new InputManager(Window);
        Gui = new ImGuiController(Renderer, Input);

        Window.Update += OnUpdate;
        Window.Render += OnRender;
        Window.Resize += Renderer.OnResize;

        Frame = new RenderFrame(World, Renderer, Gui, Time);
        Gui.RegisterDebugUi(Frame);
        Gui.RegisterDebugUi(new FrameTimingsPanel(this));
        Gui.RegisterDebugUi(Context.Timer);
    }

    /// <summary>Runs at most <paramref name="fps"/> frames a second, with vsync off. With vsync on, a window in the
    /// background can be held to far fewer by the compositor; this keeps two instances side by side (a host and a
    /// client) both at a steady rate.</summary>
    public void CapFrameRate(int fps)
    {
        Context.VSync = false;
        _framePeriod = 1.0 / fps;
        PowerThrottling.FineTimer();
    }

    private double _framePeriod;  // 0: uncapped
    private double _nextFrame;
    private readonly System.Diagnostics.Stopwatch _paceClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Waits until the next frame is due under <see cref="CapFrameRate"/>: sleeps most of the way, then spins
    /// the last couple of milliseconds, as sleeps overshoot.</summary>
    private void Pace()
    {
        if (_framePeriod <= 0) return;
        double now;
        while ((now = _paceClock.Elapsed.TotalSeconds) < _nextFrame)
        {
            if (_nextFrame - now > 0.002) Thread.Sleep(1);
            else Thread.SpinWait(50);
        }
        // Due a period after the last one; after a slow frame, a period from now (no burst to catch up).
        _nextFrame = System.Math.Max(_nextFrame + _framePeriod, now);
    }

    /// <summary>Schedules <paramref name="system"/> in a render stage (RenderWorld onwards), after the systems already
    /// in it. A system that draws in several stages is added once per stage, each at the point in that stage where it
    /// should run.</summary>
    public void AddSystem(IRenderSystem system, SystemStage stage)
    {
        if (!IsRenderStage(stage))
            throw new ArgumentException($"{stage} is an update stage; it takes an {nameof(ISystem)}.", nameof(stage));
        if (_systems.Exists(s => s.system == system && s.stage == stage))
            throw new ArgumentException($"{system.GetType().Name} is already scheduled in {stage}.", nameof(stage));
        Schedule(system, stage);
    }

    private protected override void OnDebugUiScheduled(IDebugUiSystem panel) => Gui.RegisterDebugUi(panel);

    /// <summary>The last few hundred frames one by one (see the Frame timings panel).</summary>
    public FrameHistory History { get; } = new();
    private readonly int[] _gcSeen = new int[3];

    /// <summary>Raised at the end of every frame, after it was presented: diagnostics that look at single frames
    /// (the streaming flight test) read <see cref="EngineHost.LastSystemMs"/> here.</summary>
    public event Action? FrameEnded;

    /// <summary>CPU time (ms) opening (camera, acquire) and closing (ImGui, submit, present) the frame just ended.</summary>
    public double LastFrameBeginMs { get; private set; }
    public double LastFrameEndMs { get; private set; }

    /// <summary>Runs the window's loop until it closes.</summary>
    public override void Run()
    {
        Window.Native.Title = Options.Title;
        Window.Run();
    }

    /// <summary>Closes the window, which ends <see cref="Run"/>.</summary>
    public override void Quit() => Window.Native.Close();

    private void OnUpdate(double dt)
    {
        Pace();
        Update(dt);
        Input.NewFrame();  // clear after all stages read, before next frame's events fire
    }

    private void OnRender(double dt)
    {
        Time.Advance(dt);
        Window.Native.Title = $"{Options.Title} — {Time.FramesPerSecond} fps";

        // The render stages only make sense inside an open frame; when it can't be opened (no active camera, no
        // swapchain image) they're skipped entirely, and End still closes ImGui's frame.
        _systemTimer.Restart();
        bool open = Frame.TryBegin();
        double beginMs = LastFrameBeginMs = _systemTimer.Elapsed.TotalMilliseconds;
        _frameBeginMs += TimingSmoothing * (beginMs - _frameBeginMs);
        if (open)
            for (var stage = SystemStage.RenderWorld; stage <= SystemStage.RenderHud; stage++)
                RunRenderStage(stage, Frame.Context);

        _systemTimer.Restart();
        Frame.End();
        double endMs = LastFrameEndMs = _systemTimer.Elapsed.TotalMilliseconds;
        _frameEndMs += TimingSmoothing * (endMs - _frameEndMs);
        SmoothTimes(render: true);
        History.Record(dt * 1000.0, Time.TicksLastFrame, GcGenerationSinceLastFrame(), _rawMs, beginMs, endMs);
        FrameEnded?.Invoke();
    }

    /// <summary>The highest GC generation collected since the last call, or -1 for none.</summary>
    private int GcGenerationSinceLastFrame()
    {
        int highest = -1;
        for (int g = 0; g < 3; g++)
        {
            int count = GC.CollectionCount(g);
            if (count != _gcSeen[g]) highest = g;
            _gcSeen[g] = count;
        }
        return highest;
    }

    // Smoothed CPU time of opening (camera uniform, swapchain acquire) and closing (ImGui, submit, present) the frame.
    private double _frameBeginMs, _frameEndMs;

    private void RunRenderStage(SystemStage stage, in Rendering.RenderContext frame)
    {
        for (int i = 0; i < _systems.Count; i++)
        {
            var (system, s) = _systems[i];
            if (s != stage) continue;
            _systemTimer.Restart();
            ((IRenderSystem)system).Render(stage, frame);
            RecordTime(i);
        }
    }

    /// <summary>Debug panel listing each system's CPU time per frame, slowest first. GPU work isn't timed
    /// directly: if the frame takes much longer than the CPU total, the difference is GPU time (or vsync),
    /// and it usually shows up in "Frame begin" / "Frame end", where the frame waits to acquire / present.</summary>
    private sealed class FrameTimingsPanel : IDebugUiSystem
    {
        private readonly WindowedEngineHost _host;
        public FrameTimingsPanel(WindowedEngineHost host) => _host = host;
        public string DebugName => "Frame timings";

        // Garbage collection: collections and pause time since the last sample, sampled about once a second. A pause
        // stops every thread, so it shows up as time in whichever system was running.
        private readonly System.Diagnostics.Stopwatch _gcClock = System.Diagnostics.Stopwatch.StartNew();
        private readonly int[] _gcCounts = new int[3], _gcRate = new int[3];
        private TimeSpan _gcPause;
        private double _gcPausePerSec, _gcSampleSecs;
        private long _allocBytes;
        private double _allocMbPerSec;

        private void SampleGc()
        {
            double secs = _gcClock.Elapsed.TotalSeconds;
            if (secs < 1.0) return;
            _gcClock.Restart();
            _gcSampleSecs = secs;
            for (int g = 0; g < 3; g++)
            {
                int c = GC.CollectionCount(g);
                _gcRate[g] = c - _gcCounts[g];
                _gcCounts[g] = c;
            }
            var pause = GC.GetTotalPauseDuration();
            _gcPausePerSec = (pause - _gcPause).TotalMilliseconds / secs;
            _gcPause = pause;
            long alloc = GC.GetTotalAllocatedBytes();
            _allocMbPerSec = (alloc - _allocBytes) / secs / (1024 * 1024);
            _allocBytes = alloc;
        }

        public void DrawDebugUi()
        {
            var h = _host;
            double total = 0;
            var rows = new List<(string name, double ms)>(h._systems.Count);
            for (int i = 0; i < h._systems.Count; i++)
            {
                var (system, stage) = h._systems[i];
                rows.Add(($"{system.GetType().Name} ({stage})", h._systemMs[i]));
                total += h._systemMs[i];
            }
            rows.Add(("Frame begin (camera, acquire)", h._frameBeginMs));
            rows.Add(("Frame end (ImGui, submit, present)", h._frameEndMs));
            total += h._frameBeginMs + h._frameEndMs;
            rows.Sort((a, b) => b.ms.CompareTo(a.ms));

            double frameMs = h.Time.FramesPerSecond > 0 ? 1000.0 / h.Time.FramesPerSecond : 0;
            ImGuiNET.ImGui.Text($"Frame: {frameMs:F1} ms ({h.Time.FramesPerSecond} fps), systems CPU total: {total:F1} ms");
            ImGuiNET.ImGui.Text($"Tick {h.Clock.Tick}: {h.Time.TicksLastFrame} tick(s) last frame, {h.Clock.DroppedTicks} dropped in all");
            var context = h.Context;
            bool vsync = context.VSync;
            if (ImGuiNET.ImGui.Checkbox("VSync", ref vsync)) context.VSync = vsync;
            ImGuiNET.ImGui.SameLine();
            ImGuiNET.ImGui.TextDisabled(vsync ? "(frame rate capped at the display's refresh; the wait shows in Frame begin/end)"
                                              : $"(presenting with {context.PresentModeInUse})");
            SampleGc();
            ImGuiNET.ImGui.Text($"GC: {_gcPausePerSec:F1} ms paused per second; collections gen0/1/2 {_gcRate[0]}/{_gcRate[1]}/{_gcRate[2]} " +
                                $"in the last {_gcSampleSecs:F1} s; allocating {_allocMbPerSec:F0} MB/s; heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
            DrawHistory();
            ImGuiNET.ImGui.Separator();
            ImGuiNET.ImGui.TextDisabled("Smoothed CPU time per frame:");
            foreach (var (name, ms) in rows)
                ImGuiNET.ImGui.Text($"{ms,7:F2} ms  {name}");
        }

        /// <summary>The last few hundred frames one by one, and where the slowest of them spent its time.</summary>
        private void DrawHistory()
        {
            var history = _host.History;
            if (history.Count == 0) return;
            ImGuiNET.ImGui.Separator();
            var (average, worst, spikes, slowestAgo) = history.Summarize();
            ImGuiNET.ImGui.Text($"Last {history.Count} frames: average {average:F1} ms, longest {worst:F1} ms, " +
                                $"{spikes} over 1.5x the average");
            bool paused = history.Paused;
            if (ImGuiNET.ImGui.Checkbox("Hold graph", ref paused)) history.Paused = paused;

            float width = ImGuiNET.ImGui.GetContentRegionAvail().X;
            float top = System.Math.Max(40f, worst * 1.1f);
            ImGuiNET.ImGui.PlotLines("##frames", ref history.Milliseconds[0], history.Count, history.Offset,
                                     $"frame ms (0 to {top:F0})", 0f, top, new System.Numerics.Vector2(width, 70));
            ImGuiNET.ImGui.PlotHistogram("##ticks", ref history.TicksPerFrame[0], history.Count, history.Offset,
                                         "ticks per frame (0 to 3)", 0f, 3f, new System.Numerics.Vector2(width, 30));

            ref readonly var slowest = ref history.Get(slowestAgo);
            string gc = slowest.GcGeneration < 0 ? "no GC" : $"GC gen {slowest.GcGeneration}";
            ImGuiNET.ImGui.Text($"Longest: {slowest.Ms:F1} ms, {slowestAgo} frames ago; {slowest.Ticks} tick(s), {gc}, " +
                                $"{slowest.CpuMs:F1} ms CPU in systems");
            foreach (var (system, ms) in slowest.Slowest)
            {
                if (system == -1) break;
                string name = system switch
                {
                    FrameHistory.FrameBegin => "Frame begin (camera, acquire)",
                    FrameHistory.FrameEnd => "Frame end (ImGui, submit, present)",
                    _ => $"{_host._systems[system].system.GetType().Name} ({_host._systems[system].stage})",
                };
                ImGuiNET.ImGui.Text($"  {ms,7:F2} ms  {name}");
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        Gui.Dispose();
        Renderer.Dispose();
        Context.Dispose();
        Input.Dispose();
        Window.Dispose();
    }
}
