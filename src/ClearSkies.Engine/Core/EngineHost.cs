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
    private readonly List<(ISystem system, SystemStage stage)> _systems = new();

    public EngineOptions Options { get; }
    public World World { get; }
    public GameWindow Window { get; }
    public GpuContext Context { get; }
    public Renderer Renderer { get; }
    public InputManager Input { get; }
    public PhysicsWorld Physics { get; }
    public Time Time { get; }
    public ImGuiController Gui { get; }

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

        Gui.RegisterDebugUi(new FrameTimingsPanel(this));
    }

    public void AddSystem(ISystem system, SystemStage stage)
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
        RunStage(SystemStage.Render, (float)dt);
    }

    private void RunStage(SystemStage stage, float dt)
    {
        for (int i = 0; i < _systems.Count; i++)
        {
            var (system, s) = _systems[i];
            if (s != stage) continue;
            _systemTimer.Restart();
            system.Update(dt);
            double ms = _systemTimer.Elapsed.TotalMilliseconds;
            _systemMs[i] += TimingSmoothing * (ms - _systemMs[i]);
        }
    }

    /// <summary>Debug panel listing each system's CPU time per frame, slowest first. GPU work isn't timed
    /// directly: if the frame takes much longer than the CPU total, the difference is GPU time (or vsync),
    /// and it usually shows up inside RenderSystem, where the frame waits to present.</summary>
    private sealed class FrameTimingsPanel : IDebugUiSystem
    {
        private readonly EngineHost _host;
        public FrameTimingsPanel(EngineHost host) => _host = host;
        public string DebugName => "Frame timings";

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
            rows.Sort((a, b) => b.ms.CompareTo(a.ms));

            double frameMs = h.Time.FramesPerSecond > 0 ? 1000.0 / h.Time.FramesPerSecond : 0;
            ImGuiNET.ImGui.Text($"Frame: {frameMs:F1} ms ({h.Time.FramesPerSecond} fps), systems CPU total: {total:F1} ms");
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
