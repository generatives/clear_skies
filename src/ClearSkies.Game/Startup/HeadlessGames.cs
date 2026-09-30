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
/// A host with no window (<c>--headless --host port</c>): the world's save and the host session, and nobody playing
/// here. It streams no terrain of its own (there's no local player), only the colliders the players' need.
/// </summary>
public static class DedicatedServerGame
{
    public static void Run(EngineHost host, LaunchOptions options)
    {
        var transport = HostGame.Listen(options);
        using var save = WorldSave.Open(options);
        var world = new GameWorld(host, options, Session.SinglePlayer(), save.Seed, save.Chunks,
                                  new ChunkCountBudget(HeadlessWorld.MaxChunks));
        using var hosting = new Hosting(world, save, transport);
        var net = hosting.Net;

        var commands = world.Commands;

        // Each 1/60 s tick: everything that arrived (commands, events, snapshots, session messages), the hierarchy, and the
        // save's streaming and autosave; then gameplay and physics as in a game with a window (see HostedGame).

        host.AddSystem(net, SystemStage.Simulation);
        host.AddSystem(world.Hierarchy, SystemStage.Simulation);
        host.AddSystem(hosting.Streaming, SystemStage.Simulation);
        host.AddSystem(hosting.Saver, SystemStage.Simulation);
        host.AddSystem(world.PhysicsBody, SystemStage.Simulation);
        host.AddSystem(new PlayerMovementSystem(host.World, commands), SystemStage.Simulation);
        host.AddSystem(world.BlockActions, SystemStage.Simulation);
        var levers = new LeverControlSystem(host.World, commands);
        var wheels = new SteeringWheelControlSystem(host.World, commands);
        host.AddSystem(commands, SystemStage.Simulation);
        host.AddSystem(levers, SystemStage.Simulation);
        host.AddSystem(wheels, SystemStage.Simulation);
        host.AddSystem(world.Flight, SystemStage.Simulation);
        host.AddSystem(world.CreatePresence(), SystemStage.Simulation);
        // Physics copies of bodies owned elsewhere (kinematic ships near the local player, servo copies of other
        // players), placed before the step, once the presence system has decided which copies exist.
        host.AddSystem(new FollowerSystem(host.World, host.Physics, world.RemoteBodies), SystemStage.Simulation);
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
        var (transport, welcome) = ClientGame.Connect(options);
        var session = new Session(SessionRole.Client, welcome.Peer);
        var world = new GameWorld(host, options, session, welcome.Seed, new NoChunkStore(),
                                  new ChunkCountBudget(HeadlessWorld.MaxChunks));
        using var net = new ClientSession(transport, welcome, session, world.Commands, world.Registry, host.World, host.Clock,
                                          world.TerrainLoaded);
        net.Ended += reason => { Console.WriteLine($"[net] session ended: {reason}"); host.Quit(); };

        var commands = world.Commands;

        // Each 1/60 s tick: everything that arrived (commands, events, snapshots, session messages), the hierarchy; then
        // gameplay and physics as in a game with a window (see ClientGame).

        host.AddSystem(net, SystemStage.Simulation);
        host.AddSystem(world.Hierarchy, SystemStage.Simulation);
        host.AddSystem(world.PhysicsBody, SystemStage.Simulation);
        host.AddSystem(new PlayerMovementSystem(host.World, commands), SystemStage.Simulation);
        host.AddSystem(world.BlockActions, SystemStage.Simulation);
        var levers = new LeverControlSystem(host.World, commands);
        var wheels = new SteeringWheelControlSystem(host.World, commands);
        host.AddSystem(commands, SystemStage.Simulation);
        host.AddSystem(levers, SystemStage.Simulation);
        host.AddSystem(wheels, SystemStage.Simulation);
        host.AddSystem(world.Flight, SystemStage.Simulation);
        host.AddSystem(world.CreatePresence(), SystemStage.Simulation);
        // Physics copies of bodies owned elsewhere (kinematic ships near the local player, servo copies of other
        // players), placed before the step, once the presence system has decided which copies exist.
        host.AddSystem(new FollowerSystem(host.World, host.Physics, world.RemoteBodies), SystemStage.Simulation);
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
