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
    public static void Run(WindowedEngineHost host, LaunchOptions options, LaggedTransport? transport)
    {
        using var save = WorldSave.Open(options);
        using var view = new GameView(host, options);
        var world = new GameWorld(host, options, Session.SinglePlayer(), save.Seed, save.Chunks, view.Budget, view.ChunkPreparer,
                                  view.PlayerModel);
        using var hosting = new Hosting(world, save, transport);
        using var viewSystems = new ViewSystems(world, view);
        var net = hosting.Net;

        viewSystems.AddInput();
        world.AddTickStart(net);
        viewSystems.AddTickInput();
        hosting.Persistence.Add(host); // the save's streaming and autosave
        world.AddTick(net);
        viewSystems.AddFlying(net);
        world.AddFrame();
        viewSystems.AddInteraction(net, transport);
        viewSystems.AddRender();

        var spawn = WorldSpawn.For(save.Seed);
        var camera = TestScene.AddCamera(host, spawn.Eye, spawn.Yaw, spawn.Pitch, options.Camera);
        var players = hosting.Players;
        var localPlayer = players.PlayerFor(options.PlayerName);
        TestScene.SpawnLocalPlayer(world.Commands, localPlayer, options.PlayerName, players.Saved(localPlayer), camera);
        if (save.IsNew) TestScene.SpawnTestShip(world.Commands, camera.Eye);

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
