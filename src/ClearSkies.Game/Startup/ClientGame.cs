using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Game.Startup.Systems;
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
/// <item>Build the world from the host's seed, with no save (the host keeps everything).</item>
/// <item>The client session loads terrain around the spawn and tells the host, which then sends the world (every entity,
/// as spawn events) and spawns our player; the camera waits at the spawn until then.</item>
/// </list>
/// </summary>
public static class ClientGame
{
    public static void Run(WindowedEngineHost host, LaunchOptions options)
    {
        var (transport, welcome) = Connect(options);
        var session = new Session(SessionRole.Client, welcome.Peer);
        using var view = new GameView(host, options);
        var world = new GameWorld(host, options, session, welcome.Seed, new NoChunkStore(), view.Budget, view.ChunkPreparer,
                                  view.PlayerModel);
        using var net = new ClientSession(transport, welcome, session, world.Commands, world.Registry, host.World, host.Clock,
                                          world.TerrainLoaded);
        net.Ended += reason => { Console.WriteLine($"[net] session ended: {reason}"); host.Quit(); };
        using var viewSystems = new ViewSystems(world, view);

        viewSystems.AddInput();
        world.AddTickStart(net);
        viewSystems.AddTickInput();
        world.AddTick(net);
        viewSystems.AddFlying(net);
        world.AddFrame();
        viewSystems.AddInteraction(net, transport);
        viewSystems.AddRender();

        var s = welcome.Spawn;
        var look = WorldSpawn.For(welcome.Seed);
        TestScene.AddCamera(host, new Vector3D<float>(s.X, s.Y + PlayerFactory.EyeHeight, s.Z), look.Yaw, look.Pitch, options.Camera);

        using (new QuitOnSignal(host)) world.Run();
    }

    /// <summary>Connects to <see cref="LaunchOptions.JoinAddress"/> (port 7777 by default) and says hello: returns once
    /// the host has welcomed us (or throws with its reason for refusing).</summary>
    public static (LaggedTransport Transport, Welcome Welcome) Connect(LaunchOptions options)
    {
        string joinAddress = options.JoinAddress!;
        int colon = joinAddress.LastIndexOf(':');
        string address = colon > 0 ? joinAddress[..colon] : joinAddress;
        int port = colon > 0 ? int.Parse(joinAddress[(colon + 1)..]) : 7777;
        Console.WriteLine($"[net] joining {address}:{port} as {options.PlayerName}");
        var transport = new LaggedTransport(LiteNetTransport.Join(address, port));
        var welcome = ClientSession.Connect(transport, new Hello(ProtocolVersion.Current, options.PlayerName, GenerationChecksum.Compute()),
                                            TimeSpan.FromSeconds(15));
        return (transport, welcome);
    }
}
