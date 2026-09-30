using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Session;

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

        world.AddTickStart(net);
        hosting.Persistence.Add(host); // the save's streaming and autosave
        world.AddTick(net);
        world.AddFrame();

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

        world.AddTickStart(net);
        world.AddTick(net);
        world.AddFrame();

        using (new QuitOnSignal(host)) world.Run();
    }
}

internal static class HeadlessWorld
{
    /// <summary>World chunks loaded at most with nothing drawn: well past what the headless view distance (256 blocks)
    /// loads, so it's only a backstop.</summary>
    public const int MaxChunks = 32_768;
}
