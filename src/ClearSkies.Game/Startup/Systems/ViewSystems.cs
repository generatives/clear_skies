using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Game.Diagnostics;
using ClearSkies.Game.Hud;
using ClearSkies.Net.Debug;
using ClearSkies.Net.Session;
using ClearSkies.Net.Transport;
using Silk.NET.Input;

namespace ClearSkies.Game.Startup.Systems;

/// <summary>
/// The systems of a game shown in a window: what reads input and what draws. They go in around the world's (see
/// <see cref="GameWorld"/>), in this order: <see cref="AddInput"/> (before the ticks), <see cref="AddTickInput"/>
/// (early in the tick), <see cref="AddFlying"/> (the frame, before the world's), <see cref="AddInteraction"/> (after
/// it) and <see cref="AddRender"/>.
/// </summary>
public sealed class ViewSystems : IDisposable
{
    private readonly GameWorld _world;
    private readonly GameView _view;
    private readonly EngineHost _host;
    private readonly InputSampleSystem _inputSample;
    private readonly RenderSystems _render;

    public ViewSystems(GameWorld world, GameView view)
    {
        _world = world;
        _view = view;
        _host = view.Host;
        _inputSample = new InputSampleSystem(_host.World, _host.Input, _host.Time);
        Pilot = new GridPilotSystem(_host.World, _host.Input, _host.Physics, world.StaticVolume, world.Simulation.PhysicsBody,
                                    world.Commands);
        _render = new RenderSystems(world, view);
    }

    /// <summary>Pilot mode: the local player flying a grid, with the camera under it.</summary>
    public GridPilotSystem Pilot { get; }

    /// <summary>First each frame, before any ticks: the debug UI's and the game UI's frames, mouse-look, and collecting
    /// the frame's input for the ticks.</summary>
    public void AddInput()
    {
        _host.AddSystem(_host.Gui, SystemStage.Input); // opens ImGui's frame before any other system draws into it
        // The game UI opens its layout after ImGui's frame, since ImGui resets whether the UI has the mouse; later systems
        // declare elements, and UiRenderSystem draws them in the HUD stage.
        _host.AddSystem(_view.Ui, SystemStage.Input);
        _host.AddSystem(new LookInputSystem(_host.World, _host.Input), SystemStage.Input);
        _host.AddSystem(_inputSample, SystemStage.Input); // latches the frame's input
    }

    /// <summary>Early in the tick: the frame's input handed to the tick, as the local player's PlayerInput.</summary>
    public void AddTickInput() => _host.AddSystem(_inputSample, SystemStage.Simulation);

    /// <summary>Moving the camera once a frame (not per tick) while flying, before the interpolation, which then draws it
    /// there: --flight-test (flies once the world has loaded, then quits), and the pilot, which puts the camera under a
    /// piloted grid. Pilot mode and flight tuning are single-player only: off while others are connected.</summary>
    public void AddFlying(NetSession net)
    {
        bool flightTest = _world.Options.FlightTest;
        _host.AddSystem(new StreamingFlightTest(_host, flightTest, flightTest ? () => _host.Window.Native.Close() : null),
                        SystemStage.Frame);
        _host.AddSystem(Pilot, SystemStage.Frame);
        Pilot.Disabled = () => net.OthersConnected;
    }

    /// <summary>After the world's frame systems: what the player points at and uses, the HUD, and the debug panels
    /// (the network's, with <paramref name="lag"/>'s sliders if the transport has them).</summary>
    public void AddInteraction(NetSession net, LaggedTransport? lag)
    {
        var sim = _world.Simulation;
        var input = _host.Input;
        var renderer = _host.Renderer;
        _host.AddSystem(new BlockTargetSystem(_host.World, input, renderer, sim.BlockActions, _world.EditLimits), SystemStage.Frame);
        _host.AddSystem(new HudUi(_view.Ui, input, sim.BlockActions, Pilot, renderer.Atlas,
                                  Path.Combine(AppContext.BaseDirectory, "Resources", "Icons")), SystemStage.Frame); // crosshair, hotbar
        var gridPersistence = new GridPersistenceSystem(_host.World, _view.Meshes, _host.Physics, _world.Selection, _world.Commands);
        _host.AddSystem(gridPersistence, SystemStage.Frame);
        // The airship's debug panels (pilot, flight, save/load) in one "Airship" window.
        _host.AddSystem(new AirshipDebugPanel(Pilot, sim.Flight, gridPersistence), SystemStage.Frame);
        _host.AddSystem(new LambdaSystem(() =>
        {
            if (!input.WasKeyPressed(Key.Tab)) return;
            renderer.WireframeMode = !renderer.WireframeMode;
            Console.WriteLine($"[debug] wireframe: {renderer.WireframeMode}");
        }), SystemStage.Frame);
        _host.Gui.RegisterDebugUi(new NetDebugPanel(net, sim.RemoteBodies, lag));
    }

    /// <summary>What's on the GPU, then drawing: see <see cref="RenderSystems"/>.</summary>
    public void AddRender() => _render.Add();

    public void Dispose() => _render.Dispose();
}
