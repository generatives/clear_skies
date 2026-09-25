using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Windowing;
using DefaultEcs;

namespace ClearSkies.Engine.Core;

/// <summary>
/// Owns the window, ECS world, renderer, input, and the system schedule, and drives the main loop.
/// The window is initialized eagerly in the constructor so game content (which uploads meshes via
/// <see cref="Renderer"/>) can be built before <see cref="Run"/> starts the loop.
/// </summary>
public sealed class EngineHost : IDisposable
{
    // Update stages hold ISystems, render stages IRenderSystems (enforced by AddSystem).
    private readonly List<(object system, SystemStage stage)> _systems = new();

    public EngineOptions Options { get; }
    public World World { get; }
    public GameWindow Window { get; }
    public GpuContext Context { get; }
    public Renderer Renderer { get; }
    public InputManager Input { get; }
    public PhysicsWorld Physics { get; }
    public Time Time { get; }
    public ImGuiController Gui { get; }

    /// <summary>The frame the render stages draw into, opened and closed by the host around them; its context is
    /// what every <see cref="IRenderSystem"/> is handed.</summary>
    internal RenderFrame Frame { get; }

    public EngineHost(EngineOptions options)
    {
        Options = options;
        World = new World();
        Window = new GameWindow(options);
        Window.Native.Initialize();   // create the native window now (needed for the WebGPU surface)

        Context = GpuContext.Create(Window, options);
        Renderer = new Renderer(Context);
        Input = new InputManager(Window);
        Time = new Time();
        // Milestone 5 airships need real gravity for weight/lift to mean anything (a Buoyant block
        // counteracting nothing is meaningless). Gentler than Earth to fit the "magical steampunk sky
        // world" and give Fan/Buoyant tuning room (see AirshipFlightSystem's debug sliders).
        Physics = new PhysicsWorld(new System.Numerics.Vector3(0f, -6f, 0f), Time.FixedStep);
        Gui = new ImGuiController(Renderer, Input);

        Window.Update += OnUpdate;
        Window.Render += OnRender;
        Window.Resize += Renderer.OnResize;

        Frame = new RenderFrame(World, Renderer, Gui, Time);
        Gui.RegisterDebugUi(Frame);
        Gui.RegisterDebugUi(new FrameTimingsPanel(this));
    }

    /// <summary>Schedules <paramref name="system"/> in an update stage (Input, Logic or PreRender), after the systems
    /// already in it.</summary>
    public void AddSystem(ISystem system, SystemStage stage)
    {
        if (IsRenderStage(stage))
            throw new ArgumentException($"{stage} is a render stage; it takes an {nameof(IRenderSystem)}.", nameof(stage));
        Schedule(system, stage);
    }

    /// <summary>Schedules <paramref name="system"/> in a render stage (RenderWorld onwards), after the systems already
    /// in it.</summary>
    public void AddSystem(IRenderSystem system, SystemStage stage)
    {
        if (!IsRenderStage(stage))
            throw new ArgumentException($"{stage} is an update stage; it takes an {nameof(ISystem)}.", nameof(stage));
        Schedule(system, stage);
    }

    private static bool IsRenderStage(SystemStage stage) => stage >= SystemStage.RenderWorld;

    private void Schedule(object system, SystemStage stage)
    {
        _systems.Add((system, stage));
        _systemMs.Add(0.0);
        if (system is IDebugUiSystem debugUi) Gui.RegisterDebugUi(debugUi);
    }

    // Per-system CPU time (ms, smoothed), parallel to _systems.
    private readonly List<double> _systemMs = new();
    private readonly System.Diagnostics.Stopwatch _systemTimer = new();
    private const double TimingSmoothing = 0.05;

    public void Run()
    {
        Window.Native.Title = Options.Title;
        Window.Run();
    }

    private void OnUpdate(double dt)
    {
        RunStage(SystemStage.Input, (float)dt);
        RunStage(SystemStage.Logic, (float)dt);
        RunStage(SystemStage.PreRender, (float)dt);
        Input.NewFrame();   // clear after all stages read, before next frame's events fire
    }

    private void OnRender(double dt)
    {
        Time.Advance(dt);
        Window.Native.Title = $"{Options.Title} — {Time.FramesPerSecond} fps";

        // The render stages only make sense inside an open frame; when it can't be opened (no active camera, no
        // swapchain image) they're skipped entirely, and End still closes ImGui's frame.
        _systemTimer.Restart();
        bool open = Frame.TryBegin();
        _frameBeginMs += TimingSmoothing * (_systemTimer.Elapsed.TotalMilliseconds - _frameBeginMs);
        if (open)
            for (var stage = SystemStage.RenderWorld; stage <= SystemStage.RenderHud; stage++)
                RunRenderStage(stage, Frame.Context);

        _systemTimer.Restart();
        Frame.End();
        _frameEndMs += TimingSmoothing * (_systemTimer.Elapsed.TotalMilliseconds - _frameEndMs);
    }

    // Smoothed CPU time of opening (camera uniform, swapchain acquire) and closing (ImGui, submit, present) the frame.
    private double _frameBeginMs, _frameEndMs;

    private void RunStage(SystemStage stage, float dt)
    {
        for (int i = 0; i < _systems.Count; i++)
        {
            var (system, s) = _systems[i];
            if (s != stage) continue;
            _systemTimer.Restart();
            ((ISystem)system).Update(dt);
            RecordTime(i);
        }
    }

    private void RunRenderStage(SystemStage stage, in Rendering.RenderContext frame)
    {
        for (int i = 0; i < _systems.Count; i++)
        {
            var (system, s) = _systems[i];
            if (s != stage) continue;
            _systemTimer.Restart();
            ((IRenderSystem)system).Render(frame);
            RecordTime(i);
        }
    }

    private void RecordTime(int i) =>
        _systemMs[i] += TimingSmoothing * (_systemTimer.Elapsed.TotalMilliseconds - _systemMs[i]);

    /// <summary>Debug panel listing each system's CPU time per frame, slowest first. GPU work isn't timed
    /// directly: if the frame takes much longer than the CPU total, the difference is GPU time (or vsync),
    /// and it usually shows up in "Frame begin" / "Frame end", where the frame waits to acquire / present.</summary>
    private sealed class FrameTimingsPanel : IDebugUiSystem
    {
        private readonly EngineHost _host;
        public FrameTimingsPanel(EngineHost host) => _host = host;
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
            SampleGc();
            ImGuiNET.ImGui.Text($"GC: {_gcPausePerSec:F1} ms paused per second; collections gen0/1/2 {_gcRate[0]}/{_gcRate[1]}/{_gcRate[2]} " +
                                $"in the last {_gcSampleSecs:F1} s; allocating {_allocMbPerSec:F0} MB/s; heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
            ImGuiNET.ImGui.Separator();
            foreach (var (name, ms) in rows)
                ImGuiNET.ImGui.Text($"{ms,7:F2} ms  {name}");
        }
    }

    public void Dispose()
    {
        Physics.Dispose();
        Gui.Dispose();
        Renderer.Dispose();
        Context.Dispose();
        Input.Dispose();
        World.Dispose();
        Window.Dispose();
    }
}
