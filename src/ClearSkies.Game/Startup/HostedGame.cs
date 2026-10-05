using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Game.Diagnostics;
using ClearSkies.Game.Hud;
using ClearSkies.Net.Debug;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Session;
using ClearSkies.Net.Transport;

namespace ClearSkies.Game.Startup;

/// <summary>
/// A game this machine hosts, alone (<see cref="SinglePlayerGame"/>) or with others joining
/// (<see cref="HostGame"/>): the world's save, the host session deciding everything, the local player and the save's
/// streaming. Alone is the same game with the transport off, so playing alone runs every command the way a host does.
/// </summary>
internal static class HostedGame
{
    public static void Run(WindowedEngineHost host, LaunchOptions options, LaggedTransport? transport)
    {
        using var save = WorldSave.Open(options);
        using var view = new GameView(host, options);
        var world = new GameWorld(host, options, Session.SinglePlayer(), save.Seed, save.Chunks, view.Budget, view.ChunkPreparer,
                                  view.PlayerModel);
        using var hosting = new Hosting(world, save, transport);
        var net = hosting.Net;
        var input = host.Input;
        var renderer = host.Renderer;
        var commands = world.Commands;

        // First each frame, before any ticks: the debug UI's frame (before any other system draws into it), then the
        // game UI's (after ImGui's, which resets whether the UI has the mouse; later systems declare elements, and
        // UiRenderSystem draws them in the HUD stage), mouse-look, and latching the frame's input for the ticks.
        host.AddSystem(host.Gui, SystemStage.Input);
        host.AddSystem(view.Ui, SystemStage.Input);
        host.AddSystem(new LookInputSystem(host.World, input), SystemStage.Input);
        host.AddSystem(view.InputSample, SystemStage.Input);

        // Each 1/60 s tick (0 or more a frame, see TickClock). First everything that arrived (commands, events,
        // snapshots, session messages), the hierarchy, the frame's input as the local player's PlayerInput (tick systems
        // read only that), and the save's streaming and autosave.
        host.AddSystem(net, SystemStage.Simulation);
        host.AddSystem(world.Hierarchy, SystemStage.Simulation);
        host.AddSystem(view.InputSample, SystemStage.Simulation);
        host.AddSystem(hosting.Streaming, SystemStage.Simulation);
        host.AddSystem(hosting.Saver, SystemStage.Simulation);
        host.AddSystem(world.PhysicsBody, SystemStage.Simulation);
        // Motion goals (WASD/jump/mode toggle) before the physics step, so its CollisionsDetected analysis sees them this
        // same tick (see Physics/Characters/).
        host.AddSystem(new PlayerMovementSystem(host.World, commands), SystemStage.Simulation);
        // Place, break, spawn and use controls (levers and wheels, whose control systems turn drags into commands as the
        // interactions are published), then apply every command sent this tick, then pose the controls from what the
        // commands set, so an arm is posed this tick where the view was turned to keep on it.
        host.AddSystem(world.BlockActions, SystemStage.Simulation);
        var levers = new LeverControlSystem(host.World, commands);
        var wheels = new SteeringWheelControlSystem(host.World, commands);
        host.AddSystem(commands, SystemStage.Simulation);
        host.AddSystem(levers, SystemStage.Simulation);
        host.AddSystem(wheels, SystemStage.Simulation);
        host.AddSystem(world.Flight, SystemStage.Simulation); // impulses before the physics step, integrated this same tick
        host.AddSystem(world.CreatePresence(), SystemStage.Simulation);
        // Physics copies of bodies owned elsewhere (kinematic ships near the local player, servo copies of other
        // players), placed before the step, once the presence system has decided which copies exist.
        host.AddSystem(new FollowerSystem(host.World, host.Physics, world.RemoteBodies), SystemStage.Simulation);
        host.AddSystem(host.Physics, SystemStage.Simulation); // one step
        host.AddSystem(new PhysicsTransformSyncSystem(host.World, host.Physics), SystemStage.Simulation); // body poses -> Transform
        host.AddSystem(world.RemoteBodies, SystemStage.Simulation); // bodies owned elsewhere -> Transform, about 100 ms behind
        host.AddSystem(world.Hierarchy, SystemStage.Simulation); // e.g. volume Transforms -> chunk Transforms
        host.AddSystem(new SupportSystem(host.World, host.Physics), SystemStage.Simulation); // what each character stands on or rides with
        host.AddSystem(world.Interpolation, SystemStage.Simulation); // records this tick's poses
        host.AddSystem(new BodySync(net, host.World, host.Physics), SystemStage.Simulation); // owned bodies, every second tick

        // Once each frame, after the ticks. What moves the camera itself, once a frame: --flight-test (flies once the world
        // has loaded, then quits), and the pilot, which puts the camera under a piloted grid (single-player only: off
        // while others are connected). Then the camera at the local player's eye (unless something moved it first),
        // what's drawn between the last two ticks (children follow), and terrain streamed around the
        // view.
        var spawn = WorldSpawn.For(save.Seed);
        bool flightTest = options.FlightTest;
        host.AddSystem(new StreamingFlightTest(host, world.StaticVolume, view.GridStore, flightTest,
                                               flightTest ? () => host.Quit() : null, (spawn.Yaw, spawn.Pitch)),
                       SystemStage.Frame);
        var pilot = new GridPilotSystem(host.World, input, host.Physics, world.StaticVolume, world.PhysicsBody, commands)
        {
            Disabled = () => net.OthersConnected,
        };
        host.AddSystem(pilot, SystemStage.Frame);
        host.AddSystem(new EyeSystem(host.World), SystemStage.Frame);
        host.AddSystem(world.Interpolation, SystemStage.Frame);
        host.AddSystem(world.Hierarchy, SystemStage.Frame);
        host.AddSystem(world.ChunkLoad, SystemStage.Frame);
        host.AddSystem(new FogSystem(host.World, world.Options.ViewDistance), SystemStage.Frame); // at the nearest terrain not ready
        // What the player points at and uses, the HUD (crosshair, hotbar), grids saved and loaded, and the debug panels.
        host.AddSystem(new BlockTargetSystem(host.World, input, renderer, world.BlockActions, world.EditLimits), SystemStage.Frame);
        host.AddSystem(new HudUi(view.Ui, input, world.BlockActions, pilot, renderer.Atlas,
                                 Path.Combine(AppContext.BaseDirectory, "Resources", "Icons")), SystemStage.Frame);
        var gridPersistence = new GridPersistenceSystem(host.World, view.Meshes, host.Physics, world.Selection, commands);
        host.AddSystem(gridPersistence, SystemStage.Frame);
        host.AddSystem(new AirshipDebugPanel(pilot, world.Flight, gridPersistence), SystemStage.Frame); // one "Airship" window
        host.AddSystem(new WireframeToggle(input, renderer), SystemStage.Frame);
        host.Gui.RegisterDebugUi(new NetDebugPanel(net, world.RemoteBodies, transport));

        view.AddRender(world);

        // The camera waits where the local player left off (or at the world's spawn point) until they spawn there,
        // once the world around them has loaded.
        var players = hosting.Players;
        var localPlayer = players.PlayerFor(options.PlayerName);
        var saved = players.Saved(localPlayer);
        var camera = saved is null
            ? TestScene.AddCamera(host, spawn.Eye, spawn.Yaw, spawn.Pitch, options.Camera)
            : TestScene.AddCamera(host, WorldSpawn.EyeAt(saved.Position), saved.Yaw, saved.Pitch, options.Camera);
        net.SpawnWhenReady(TestScene.LocalPlayer(localPlayer, options.PlayerName, saved, camera));
        if (save.IsNew) TestScene.SpawnTestShip(commands, camera.Eye);

        using (new QuitOnSignal(host)) world.Run();
        hosting.SaveAll();
    }
}

/// <summary>Playing alone: a hosted game nobody can join.</summary>
public static class SinglePlayerGame
{
    public static void Run(WindowedEngineHost host, LaunchOptions options) => HostedGame.Run(host, options, transport: null);
}

/// <summary>Hosting a game others can join, on <see cref="LaunchOptions.HostPort"/>.</summary>
public static class HostGame
{
    public static void Run(WindowedEngineHost host, LaunchOptions options) => HostedGame.Run(host, options, Listen(options));

    /// <summary>Listens for players joining on <see cref="LaunchOptions.HostPort"/>.</summary>
    public static LaggedTransport Listen(LaunchOptions options)
    {
        int port = options.HostPort!.Value;
        Console.WriteLine($"[net] hosting on port {port}");
        return new LaggedTransport(LiteNetTransport.Host(port));
    }
}
