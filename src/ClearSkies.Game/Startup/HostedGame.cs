using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Game.Startup.Systems;
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
    public static void Run(EngineHost host, LaunchOptions options, LaggedTransport? transport)
    {
        // The world's save: Saves/Worlds/<name>.db. A new world's seed is --seed's (1337 by default); after that it's the
        // save's.
        using var save = SaveDatabase.Open(SaveDatabase.PathFor(options.WorldName));
        bool newWorld = save.Seed is null;
        ulong seed = save.Seed ?? options.NewWorldSeed;
        if (newWorld) save.Seed = seed;
        Console.WriteLine($"[save] world '{options.WorldName}' ({(newWorld ? "new" : "loaded")}), seed {seed}");

        var session = Session.SinglePlayer();
        using var view = new GameView(host, options);
        var world = new GameWorld(host, options, session, seed, new DatabaseChunkStore(save), view.Budget, view.ChunkPreparer,
                                  view.PlayerModel);
        // Entity IDs come in blocks from the save's next free ID, so they never repeat across sessions.
        var ids = new EntityIdAllocator(save.NextFreeId);
        world.Registry.RequestBlock = ids.NextBlock;
        var persistence = new PersistenceSystems(world, save, ids);
        var players = new SavedPlayers(save, persistence.Saver, seed);
        using var net = new HostSession(transport, session, world.Commands, world.Registry, host.World, host.Clock, ids, seed,
                                        GenerationChecksum.Compute(), players);
        using var viewSystems = new ViewSystems(world, view);

        viewSystems.AddInput();
        world.AddTickStart(net);
        viewSystems.AddTickInput();
        persistence.Add(host); // the save's streaming and autosave
        world.AddTick(net);
        viewSystems.AddFlying(net);
        world.AddFrame();
        viewSystems.AddInteraction(net, transport);
        viewSystems.AddRender();

        var spawn = WorldSpawn.For(seed);
        var camera = TestScene.AddCamera(host, spawn.Eye, spawn.Yaw, spawn.Pitch, options.Camera);
        var localPlayer = players.PlayerFor(options.PlayerName);
        TestScene.SpawnLocalPlayer(world.Commands, localPlayer, options.PlayerName, players.Saved(localPlayer), camera);
        if (newWorld) TestScene.SpawnTestShip(world.Commands, camera.Eye);

        using (new QuitOnSignal(host)) world.Run();
        persistence.Saver.SaveAll(); // everything, in one transaction, on exit
    }
}

/// <summary>Playing alone: a hosted game nobody can join.</summary>
public static class SinglePlayerGame
{
    public static void Run(EngineHost host, LaunchOptions options) => HostedGame.Run(host, options, transport: null);
}

/// <summary>Hosting a game others can join, on <see cref="LaunchOptions.HostPort"/>.</summary>
public static class HostGame
{
    public static void Run(EngineHost host, LaunchOptions options)
    {
        int port = options.HostPort!.Value;
        var transport = new LaggedTransport(LiteNetTransport.Host(port));
        Console.WriteLine($"[net] hosting on port {port}");
        HostedGame.Run(host, options, transport);
    }
}
