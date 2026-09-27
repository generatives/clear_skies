using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Sync;
using DefaultEcs;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Per-client load windows, the divergence check, rejoining onto a moving ship, and the host's bandwidth.</summary>
public class WindowTests
{
    private static Entity LocalPlayerOf(HeadlessScene scene) => scene.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();

    private static void Teleport(HeadlessScene scene, Entity grid, Vector3 position)
    {
        var body = grid.Get<PhysicsBodyComponent>().Body;
        var (_, rotation) = scene.Physics.GetBodyPose(body);
        scene.Physics.SetBodyPose(body, position, rotation);
    }

    [Fact]
    public void EntitiesComeAndGoWithAClientsWindow()
    {
        using var game = new LoopbackGame();
        game.HostNet.LoadWindow = 100;
        game.HostNet.ForgetWindow = 120;
        var near = game.Host.SpawnPlatform(new Vector3(20, 50, 0));
        var far = game.Host.SpawnPlatform(new Vector3(300, 50, 0));
        var hostPlayer = game.Host.SpawnLocalPlayer(new Vector3(300, 52, 3), freeFly: true);
        game.Tick(2);
        var (client, clientNet) = game.Join("a");
        uint nearId = near.Get<NetId>().Value, farId = far.Get<NetId>().Value;
        Assert.True(client.Registry.IsLive(nearId));
        Assert.False(client.Registry.IsLive(farId));
        Assert.False(client.Registry.IsLive(hostPlayer.Get<NetId>().Value)); // the host's player is out there too

        // An edit to the far grid doesn't go to the client, and its body isn't sent either.
        game.Host.Commands.Send(new EditVoxels { Volume = farId, Editor = hostPlayer.Get<NetId>().Value,
            Ops = new[] { VoxelOp.SetBlock(new(2, 1, 2), BlockId.Wood, BlockOrientation.Upright) } });
        long before = game.HostNet.Transport!.Stats.BytesSent;
        game.Tick(30);
        Assert.False(client.Registry.IsLive(farId));
        Assert.True(game.HostNet.Transport.Stats.BytesSent - before < 4000, "only the near grid's snapshots and clock sync");

        // The far grid flies in: the client gets it, edit included.
        Teleport(game.Host, far, new Vector3(40, 50, 0));
        game.Tick(10);
        Assert.True(client.Registry.IsLive(farId));
        Assert.Equal(BlockId.Wood, client.Registry.Find(farId)!.Value.Get<ChunkGrid>().Volume.GetBlock(2, 1, 2));
        Assert.True(game.HostNet.Knows(clientNet.Session.LocalPeer, farId));

        // Past the forget window (not merely the load window) it's forgotten there.
        Teleport(game.Host, far, new Vector3(110, 50, 0));
        game.Tick(10);
        Assert.True(client.Registry.IsLive(farId)); // between the two: kept
        Teleport(game.Host, far, new Vector3(200, 50, 0));
        game.Tick(10);
        Assert.False(client.Registry.IsLive(farId));
        Assert.False(game.HostNet.Knows(clientNet.Session.LocalPeer, farId));
        Assert.True(game.Host.Registry.IsLive(farId)); // only forgotten, not despawned
    }

    [Fact]
    public void TheWindowFollowsTheClientsPlayer()
    {
        using var game = new LoopbackGame();
        game.HostNet.LoadWindow = 100;
        game.HostNet.ForgetWindow = 120;
        var far = game.Host.SpawnPlatform(new Vector3(300, 50, 0));
        game.Tick(2);
        var (client, _) = game.Join("a");
        uint farId = far.Get<NetId>().Value;
        Assert.False(client.Registry.IsLive(farId));
        var me = LocalPlayerOf(client);
        me.Get<Transform>().Position = new(280, 55, 0); // flying over
        Assert.True(game.TickUntil(() => client.Registry.IsLive(farId), 60));
        me.Get<Transform>().Position = new(0, 60, 0);
        Assert.True(game.TickUntil(() => !client.Registry.IsLive(farId), 60));
    }

    [Fact]
    public void ADriftedCopyIsResynced()
    {
        using var game = new LoopbackGame();
        var grid = game.Host.SpawnPlatform(new Vector3(20, 50, 0));
        game.Tick(2);
        var (client, clientNet) = game.Join("a");
        uint id = grid.Get<NetId>().Value;
        var copy = client.Registry.Find(id)!.Value;
        // A bug: a block that only the client has.
        copy.Get<ChunkGrid>().Volume.SetBlock(1, 3, 1, BlockId.Stone);
        var check = (DivergenceCheck)clientNet.Divergence!;
        game.Tick(DivergenceCheck.IntervalTicks + 30);
        Assert.Equal(1, check.Mismatches);
        copy = client.Registry.Find(id)!.Value;
        Assert.Equal(BlockId.Air, copy.Get<ChunkGrid>().Volume.GetBlock(1, 3, 1));
        // And the next check agrees.
        game.Tick(DivergenceCheck.IntervalTicks);
        Assert.Equal(1, check.Mismatches);
        Assert.True(check.Checked >= 2);
    }

    [Fact]
    public void InAgreementNothingIsResynced()
    {
        using var game = new LoopbackGame(latencyMs: 50);
        var grid = game.Host.SpawnPlatform(new Vector3(20, 50, 0));
        var hostPlayer = game.Host.SpawnLocalPlayer(new Vector3(20, 52, 3), freeFly: true);
        game.Tick(2);
        var (client, clientNet) = game.Join("a");
        uint id = grid.Get<NetId>().Value;
        // Edits from both sides along the way.
        for (int i = 0; i < 6; i++)
        {
            game.Host.Commands.Send(new EditVoxels { Volume = id, Editor = hostPlayer.Get<NetId>().Value,
                Ops = new[] { VoxelOp.SetBlock(new(i, 1, 0), BlockId.Wood, BlockOrientation.Upright) } });
            client.Commands.Send(new EditVoxels { Volume = id, Editor = LocalPlayerOf(client).Get<NetId>().Value,
                Ops = new[] { VoxelOp.SetBlock(new(i, 1, 5), BlockId.Stone, BlockOrientation.Upright) } });
            game.Tick(100);
        }
        var check = (DivergenceCheck)clientNet.Divergence!;
        Assert.True(check.Checked >= 1);
        Assert.Equal(0, check.Mismatches);
    }

    [Fact]
    public void APlayerWhoLeftOnAMovingShipRejoinsOnIt()
    {
        using var game = new LoopbackGame(latencyMs: 30);
        var voxels = new List<GridVoxel>();
        for (int x = 0; x < 8; x++) for (int z = 0; z < 8; z++) voxels.Add(new(x, 0, z, BlockId.Wood, BlockOrientation.Upright));
        var ship = game.Host.SpawnGrid(GridDescription.FromVoxels(new Vector3(0, 50, 0), voxels));
        game.Tick(2);

        // Leaving saves the player's SpawnPlayer command (as the game does, through storage).
        var saved = new Dictionary<PlayerId, byte[]>();
        game.HostNet.PlayerLeaving = p => DescribeRequest.Request(p, DescribePurpose.Store);
        game.Host.Commands.Descriptions.Described += d =>
        {
            if ((d.Request.Purpose & DescribePurpose.Store) == 0 || !d.Entity.Has<Player>()) return;
            saved[d.Entity.Get<Player>().Id] = d.Payload;
            game.Host.Commands.Send(new DespawnEntity { Entity = d.NetId, KeepStored = true });
        };
        game.SpawnFor = id => (saved.TryGetValue(id, out var s) ? s : null, new Vector3(0, 60, 0));

        var who = PlayerId.New();
        var (client, clientNet) = game.Join("crew", player: who);
        var crew = LocalPlayerOf(client);
        crew.Get<Engine.Input.PlayerInput>() = new Engine.Input.PlayerInput { Pressed = Engine.Input.PlayerButtons.ToggleFly };
        client.Tick();
        crew.Get<Engine.Input.PlayerInput>() = default;
        Assert.False(crew.Get<CharacterModeComponent>().FreeFly);
        crew.Get<CharacterControllerComponent>().Character.TeleportTo(new Vector3(-1, 51.4f, -1));
        game.Tick(60);
        var seen = game.Host.Registry.Find(crew.Get<NetId>().Value)!.Value;
        Assert.Equal(ship, seen.Get<Support>().Supporter); // the host knows what they're standing on

        var body = ship.Get<PhysicsBodyComponent>().Body;
        game.Host.Physics.SetBodyLinearVelocity(body, new Vector3(5, 0, 0));
        game.Tick(60);
        clientNet.Dispose();
        game.Clients.RemoveAll(c => c.Net == clientNet);
        client.Dispose();
        game.Tick(10);
        Assert.True(saved.ContainsKey(who));

        game.Tick(120); // the ship flies on, 10 blocks, while they're away
        var (again, _) = game.Join("crew", player: who);
        var back = LocalPlayerOf(again);
        game.Tick(30);
        var shipCopy = again.Registry.Find(ship.Get<NetId>().Value)!.Value;
        Assert.Equal(shipCopy, back.Get<Support>().Supporter);
        var local = back.Get<Support>().LocalPosition;
        Assert.InRange(local.Y, 0.5f, 2f);
        Assert.InRange(MathF.Abs(local.X), 0f, 4f);
        Assert.InRange(MathF.Abs(local.Z), 0f, 4f);
    }

    [Fact]
    public void FiveClientsAndTenMovingShipsFitTheHostsBandwidth()
    {
        using var game = new LoopbackGame();
        var ships = new List<Entity>();
        for (int i = 0; i < 10; i++)
        {
            var voxels = new List<GridVoxel>();
            for (int x = 0; x < 6; x++) for (int z = 0; z < 12; z++) voxels.Add(new(x, 0, z, BlockId.Wood, BlockOrientation.Upright));
            ships.Add(game.Host.SpawnGrid(GridDescription.FromVoxels(new Vector3(i * 20 - 100, 50, 0), voxels)));
        }
        game.Host.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        game.Tick(2);
        for (int i = 0; i < 5; i++) game.Join($"p{i}");
        foreach (var ship in ships)
        {
            var body = ship.Get<PhysicsBodyComponent>().Body;
            game.Host.Physics.SetBodyLinearVelocity(body, new Vector3(3, 0, 1));
            game.Host.Physics.SetBodyAngularVelocity(body, new Vector3(0, 0.2f, 0));
        }
        // Everyone moving about too.
        foreach (var (scene, _) in game.Clients) LocalPlayerOf(scene).Get<Transform>().Position += new Silk.NET.Maths.Vector3D<float>(1, 0, 0);
        game.Tick(60);

        var stats = game.HostNet.Transport!.Stats;
        long before = stats.BytesSent;
        game.Tick(300);
        double kbPerSecond = (stats.BytesSent - before) / 1024.0 / 5.0;
        Console.WriteLine($"[bandwidth] host sends {kbPerSecond:0.0} KB/s to 5 clients with 10 moving ships");
        Assert.True(kbPerSecond < 150, $"host sends {kbPerSecond:0.0} KB/s");
        Assert.True(kbPerSecond > 10, $"host sends {kbPerSecond:0.0} KB/s: are snapshots going out at all?");
        foreach (var (scene, _) in game.Clients)
            Assert.Equal(10, scene.World.GetEntities().With<DynamicGrid>().AsEnumerable().Count());
    }
}
