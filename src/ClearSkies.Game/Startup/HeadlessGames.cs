using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Session;
using ClearSkies.Net.Sync;

namespace ClearSkies.Game.Startup;

/// <summary>
/// A host with no window (<c>--headless --host port</c>): the world's save, the Host, and the authority's Participant,
/// with nobody playing here. It streams no terrain of its own (there's no local player), only the colliders the players
/// need.
/// </summary>
public static class DedicatedServerGame
{
    public static void Run(EngineHost host, LaunchOptions options)
    {
        var transport = HostGame.Listen(options);
        using var save = WorldSave.Open(options);
        var chunks = new HostChunkStore();
        var world = new GameWorld(host, options, Session.SinglePlayer(), save.Seed, chunks,
                                  new ChunkCountBudget(HeadlessWorld.MaxChunks));
        using var hosting = new Hosting(world, chunks, save, transport, playerName: null);
        var net = hosting.Net;

        var commands = world.Commands;

        // Each 1/60 s tick: what other machines sent, the Host, this machine's Participant, the hierarchy; then gameplay
        // and physics as in a game with a window (see HostedGame).

        if (hosting.Network is { } network) host.AddSystem(network, SystemStage.Simulation);
        host.AddSystem(hosting.Host, SystemStage.Simulation);
        host.AddSystem(net, SystemStage.Simulation);
        host.AddSystem(world.Hierarchy, SystemStage.Simulation);
        host.AddSystem(world.PhysicsBody, SystemStage.Simulation);
        host.AddSystem(new PlayerMovementSystem(host.World, commands), SystemStage.Simulation);
        host.AddSystem(new GrappleSystem(host.World, host.Physics), SystemStage.Simulation); // rope pulls, also before the step
        host.AddSystem(world.BlockActions, SystemStage.Simulation);
        var levers = new LeverControlSystem(host.World, commands);
        var wheels = new SteeringWheelControlSystem(host.World, commands);
        host.AddSystem(commands, SystemStage.Simulation);
        host.AddSystem(levers, SystemStage.Simulation);
        host.AddSystem(wheels, SystemStage.Simulation);
        host.AddSystem(world.Flight, SystemStage.Simulation);
        host.AddSystem(world.AirShapes, SystemStage.Simulation); // ships' drag entries, after any edit's new body shape
        host.AddSystem(world.AirResistance, SystemStage.Simulation); // drag through the wind, also before the step
        host.AddSystem(world.CreatePresence(), SystemStage.Simulation);
        // Physics copies of ships simulated elsewhere (kinematic, near the local player), placed before the step, once the
        // presence system has decided which copies exist.
        host.AddSystem(new RemoteBodyProxySystem(host.World, host.Physics, world.RemoteBodies), SystemStage.Simulation);
        host.AddSystem(host.Physics, SystemStage.Simulation);
        host.AddSystem(new PhysicsTransformSyncSystem(host.World, host.Physics), SystemStage.Simulation);
        host.AddSystem(world.RemoteBodies, SystemStage.Simulation);
        host.AddSystem(world.Hierarchy, SystemStage.Simulation);
        host.AddSystem(new SupportSystem(host.World, host.Physics), SystemStage.Simulation);
        host.AddSystem(world.Interpolation, SystemStage.Simulation);
        host.AddSystem(new BodySync(net, host.World, host.Physics), SystemStage.Simulation);

        // Each frame: terrain streamed around the interest.
        host.AddSystem(world.Interpolation, SystemStage.Frame);
        host.AddSystem(world.Hierarchy, SystemStage.Frame);
        host.AddSystem(world.ChunkLoad, SystemStage.Frame);

        if (save.IsNew) TestScene.SpawnTestShip(world.Commands, WorldSpawn.For(save.Seed).Eye);

        using (new QuitOnSignal(host)) world.Run();
        hosting.SaveAll();
    }
}

/// <summary>A player with no window (<c>--headless --join address</c>), for testing: joins, and stands where it
/// spawns.</summary>
public static class BotClientGame
{
    public static void Run(EngineHost host, LaunchOptions options)
    {
        var (_, link) = ClientGame.Connect(options);
        using var hostLink = link;
        var welcome = link.Welcome;
        var session = new Session(SessionRole.Client, welcome.Peer);
        var chunks = new HostChunkStore();
        var world = new GameWorld(host, options, session, welcome.Seed, chunks,
                                  new ChunkCountBudget(HeadlessWorld.MaxChunks));
        using var net = SimulationParticipant.Join(link, session, world.Commands, world.Registry, host.World, host.Clock, world.TerrainReadyFor,
                                                   chunks);
        net.Ended += reason => { Console.WriteLine($"[net] session ended: {reason}"); host.Quit(); };

        var commands = world.Commands;

        // Each 1/60 s tick: everything that arrived (commands, events, snapshots, session messages), the hierarchy; then
        // gameplay and physics as in a game with a window (see ClientGame).

        host.AddSystem(link, SystemStage.Simulation);
        host.AddSystem(net, SystemStage.Simulation);
        host.AddSystem(world.Hierarchy, SystemStage.Simulation);
        host.AddSystem(new OwnPlayerPrediction(net, host.World, world.Registry), SystemStage.Simulation); // its (idle) input to the Host
        host.AddSystem(world.PhysicsBody, SystemStage.Simulation);
        host.AddSystem(new PlayerMovementSystem(host.World, commands), SystemStage.Simulation);
        host.AddSystem(new GrappleSystem(host.World, host.Physics), SystemStage.Simulation); // rope pulls, also before the step
        host.AddSystem(world.BlockActions, SystemStage.Simulation);
        var levers = new LeverControlSystem(host.World, commands);
        var wheels = new SteeringWheelControlSystem(host.World, commands);
        host.AddSystem(commands, SystemStage.Simulation);
        host.AddSystem(levers, SystemStage.Simulation);
        host.AddSystem(wheels, SystemStage.Simulation);
        host.AddSystem(world.Flight, SystemStage.Simulation);
        host.AddSystem(world.AirShapes, SystemStage.Simulation); // ships' drag entries, after any edit's new body shape
        host.AddSystem(world.AirResistance, SystemStage.Simulation); // drag through the wind, also before the step
        host.AddSystem(world.CreatePresence(), SystemStage.Simulation);
        // Physics copies of ships simulated elsewhere (kinematic, near the local player), placed before the step, once the
        // presence system has decided which copies exist.
        host.AddSystem(new RemoteBodyProxySystem(host.World, host.Physics, world.RemoteBodies), SystemStage.Simulation);
        host.AddSystem(host.Physics, SystemStage.Simulation);
        host.AddSystem(new PhysicsTransformSyncSystem(host.World, host.Physics), SystemStage.Simulation);
        host.AddSystem(world.RemoteBodies, SystemStage.Simulation);
        host.AddSystem(world.Hierarchy, SystemStage.Simulation);
        host.AddSystem(new SupportSystem(host.World, host.Physics), SystemStage.Simulation);
        host.AddSystem(world.Interpolation, SystemStage.Simulation);
        host.AddSystem(new BodySync(net, host.World, host.Physics), SystemStage.Simulation);

        // Each frame: terrain streamed around the interest.
        host.AddSystem(world.Interpolation, SystemStage.Frame);
        host.AddSystem(world.Hierarchy, SystemStage.Frame);
        host.AddSystem(world.ChunkLoad, SystemStage.Frame);

        using (new QuitOnSignal(host)) world.Run();
    }
}

internal static class HeadlessWorld
{
    /// <summary>World chunks loaded at most with nothing drawn: well past what the headless view distance (256 blocks)
    /// loads, so it's only a backstop.</summary>
    public const int MaxChunks = 32_768;
}
