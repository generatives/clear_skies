using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Game.Diagnostics;
using ClearSkies.Game.Hud;
using ClearSkies.Net.Debug;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Session;
using ClearSkies.Net.Transport;
using Silk.NET.Maths;

namespace ClearSkies.Game.Startup;

/// <summary>
/// Joining someone else's game, at <see cref="LaunchOptions.JoinAddress"/>. In order:
/// <list type="number">
/// <item>Connect and say hello (name, protocol version, world-generation checksum); the host welcomes us or refuses
/// with a reason. The welcome says who we are, the world's seed, the host's tick, and where our player will spawn.</item>
/// <item>Build the world from the host's seed, with no save (the host keeps everything, edited terrain included: it's
/// loaded from the host as it streams in).</item>
/// <item>Join as a Participant: the Host streams us what's in our View Volume (around our player), each entity spawning
/// once what it needs has loaded here; the camera waits at the spawn until our player has. The hosting machine
/// simulates everything, our player included; we play it by sending our input, and predict it meanwhile.</item>
/// </list>
/// </summary>
public static class ClientGame
{
    public static void Run(WindowedEngineHost host, LaunchOptions options)
    {
        var (transport, remoteHost) = Connect(options);
        using var hostLink = remoteHost;
        var welcome = remoteHost.Welcome;
        var session = new Session(SessionRole.Client, welcome.Peer);
        using var view = new GameView(host, options);
        var chunks = new HostChunkStore();
        var world = new GameWorld(host, options, session, welcome.Seed, chunks, view.Budget, view.ChunkPreparer,
                                  view.PlayerModel);
        using var net = SimulationParticipant.Join(remoteHost, session, world.Commands, world.Registry, host.World, host.Clock, world.TerrainReadyFor,
                                                   chunks);
        net.Ended += reason => { Console.WriteLine($"[net] session ended: {reason}"); host.Quit(); };
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
        // snapshots, session messages), the hierarchy, and the frame's input as the local player's PlayerInput (tick
        // systems read only that). The host simulates our player; we predict them meanwhile: the prediction checks
        // itself against what the host last said, then sends it this tick's input.
        host.AddSystem(remoteHost, SystemStage.Simulation);
        host.AddSystem(net, SystemStage.Simulation);
        host.AddSystem(world.Hierarchy, SystemStage.Simulation);
        host.AddSystem(view.InputSample, SystemStage.Simulation);
        host.AddSystem(new OwnPlayerPrediction(net, host.World, world.Registry), SystemStage.Simulation);
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
        host.AddSystem(world.AirShapes, SystemStage.Simulation); // ships' drag entries, after any edit's new body shape
        host.AddSystem(world.AirResistance, SystemStage.Simulation); // drag through the wind, also before the step
        host.AddSystem(world.CreatePresence(), SystemStage.Simulation);
        // Physics copies of ships simulated elsewhere (kinematic, near the local player), placed before the step, once the
        // presence system has decided which copies exist.
        host.AddSystem(new RemoteBodyProxySystem(host.World, host.Physics, world.RemoteBodies), SystemStage.Simulation);
        host.AddSystem(host.Physics, SystemStage.Simulation); // one step
        host.AddSystem(new PhysicsTransformSyncSystem(host.World, host.Physics), SystemStage.Simulation); // body poses -> Transform
        host.AddSystem(world.RemoteBodies, SystemStage.Simulation); // ships and other players -> Transform, about 100 ms behind
        host.AddSystem(world.SyncedState, SystemStage.Simulation); // their block entities' synced state, as played back
        host.AddSystem(world.Hierarchy, SystemStage.Simulation); // e.g. volume Transforms -> chunk Transforms
        host.AddSystem(new SupportSystem(host.World, host.Physics), SystemStage.Simulation); // what each character stands on or rides with
        host.AddSystem(world.Interpolation, SystemStage.Simulation); // records this tick's poses
        host.AddSystem(new BodySync(net, host.World, host.Physics), SystemStage.Simulation); // owned bodies, every second tick

        // Once each frame, after the ticks. What moves the camera itself, once a frame: --flight-test (flies once the world
        // has loaded, then quits), and the pilot, which puts the camera under a piloted grid (single-player only: off
        // while others are connected). Then the camera at the local player's eye (unless something moved it first),
        // what's drawn between the last two ticks (children follow), and terrain streamed around the
        // view.
        var spawn = WorldSpawn.For(welcome.Seed);
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
        host.AddSystem(new JoiningScreen(view.Ui, net), SystemStage.Frame); // over everything until our player arrives
        var gridPersistence = new GridPersistenceSystem(host.World, view.Meshes, host.Physics, world.Selection, commands);
        host.AddSystem(gridPersistence, SystemStage.Frame);
        host.AddSystem(new AirshipDebugPanel(pilot, world.Flight, gridPersistence), SystemStage.Frame); // one "Airship" window
        host.AddSystem(new WireframeToggle(input, renderer), SystemStage.Frame);
        host.AddSystem(new WindDebugSystem(host.World, world.Wind), SystemStage.Frame); // the "Wind" window and arrows
        host.Gui.RegisterDebugUi(new NetDebugPanel(net, null, world.RemoteBodies, transport));

        view.AddRender(world);

        var s = welcome.Spawn;
        TestScene.AddCamera(host, new Vector3D<float>(s.X, s.Y + PlayerFactory.EyeHeight, s.Z), spawn.Yaw, spawn.Pitch, options.Camera);

        using (new QuitOnSignal(host)) world.Run();
    }

    /// <summary>Connects to <see cref="LaunchOptions.JoinAddress"/> (port 7777 by default) and says hello: returns once
    /// the host has welcomed us (or throws with its reason for refusing).</summary>
    public static (LaggedTransport Transport, RemoteHost Host) Connect(LaunchOptions options)
    {
        string joinAddress = options.JoinAddress!;
        int colon = joinAddress.LastIndexOf(':');
        string address = colon > 0 ? joinAddress[..colon] : joinAddress;
        int port = colon > 0 ? int.Parse(joinAddress[(colon + 1)..]) : 7777;
        Console.WriteLine($"[net] joining {address}:{port} as {options.PlayerName}");
        var transport = new LaggedTransport(LiteNetTransport.Join(address, port));
        var link = RemoteHost.Connect(transport, new Hello(ProtocolVersion.Current, options.PlayerName, GenerationChecksum.Compute()),
                                      TimeSpan.FromSeconds(15));
        return (transport, link);
    }
}
