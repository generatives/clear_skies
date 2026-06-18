using ClearSkies.Engine.Input;
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
    public Time Time { get; }

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

        Window.Update += OnUpdate;
        Window.Render += OnRender;
        Window.Resize += Renderer.OnResize;
    }

    public void AddSystem(ISystem system, SystemStage stage) => _systems.Add((system, stage));

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
        foreach (var (system, s) in _systems)
            if (s == stage)
                system.Update(dt);
    }

    public void Dispose()
    {
        Renderer.Dispose();
        Context.Dispose();
        Input.Dispose();
        World.Dispose();
        Window.Dispose();
    }
}
