using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Ownership;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>N5: bubbles, handover, epochs and forwarding.</summary>
public class OwnershipTests
{
    private static Entity LocalPlayerOf(HeadlessScene scene) => scene.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();

    /// <summary>An 8×8 deck with a lever, locked (kinematic), at <paramref name="at"/>.</summary>
    private static Entity SpawnShip(HeadlessScene scene, Vector3 at)
    {
        var voxels = new List<GridVoxel>();
        for (int x = 0; x < 8; x++) for (int z = 0; z < 8; z++) voxels.Add(new(x, 0, z, BlockId.Wood, BlockOrientation.Upright));
        voxels.Add(new(0, 1, 7, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.North)));
        return scene.SpawnGrid(GridDescription.FromVoxels(at, voxels));
    }

    private static float LeverOf(HeadlessScene scene, uint ship)
    {
        scene.Registry.Find(ship)!.Value.Get<ChunkGrid>().Volume.TryGetBlockEntity(0, 1, 7, out var lever);
        return lever.Get<Lever>().Value;
    }

    private static void Fly(HeadlessScene scene, Entity ship, Vector3 velocity) =>
        scene.Physics.SetBodyLinearVelocity(ship.Get<PhysicsBodyComponent>().Body, velocity);

    private static Vector3 BodyPosition(HeadlessScene scene, Entity e) => scene.Physics.GetBodyPose(e.Get<PhysicsBodyComponent>().Body).position;

    private static void Walk(HeadlessScene scene)
    {
        var me = LocalPlayerOf(scene);
        me.Get<Engine.Input.PlayerInput>() = new Engine.Input.PlayerInput { Pressed = Engine.Input.PlayerButtons.ToggleFly };
        scene.Tick();
        me.Get<Engine.Input.PlayerInput>() = default;
    }

    /// <summary>The host's player is far off (outside the client's window); a ship is by the client's spawn.</summary>
    private static (LoopbackGame game, Entity ship, HeadlessScene client) LoneClientWithAShip(double latencyMs = 50)
    {
        var game = new LoopbackGame(latencyMs);
        var ship = SpawnShip(game.Host, new Vector3(0, 50, 0));
        game.Host.SpawnLocalPlayer(new Vector3(1500, 60, 0), freeFly: true);
        game.Tick(2);
        var (client, _) = game.Join("a");
        game.Host.EnableBubbles();
        return (game, ship, client);
    }

    [Fact]
    public void AClientAloneWithAShipOwnsItAndItsControlsAnswerAtOnce()
    {
        var (game, ship, client) = LoneClientWithAShip();
        using var _ = game;
        uint id = ship.Get<NetId>().Value;
        game.Tick(30);
        var copy = client.Registry.Find(id)!.Value;
        Assert.True(copy.Get<NetOwner>().IsLocal);
        Assert.Equal(PhysicsMode.Simulated, copy.Get<PhysicsPresence>().Mode);
        Assert.False(ship.Get<NetOwner>().IsLocal); // the host follows it now
        Assert.Equal(copy.Get<NetOwner>().Owner, ship.Get<NetOwner>().Owner);

        // The lever answers in the same tick: no round trip.
        client.Commands.Send(new SetLever { Lever = EntityAddress.OfBlock(id, new(0, 1, 7)), Value = 0.6f });
        client.Tick();
        Assert.Equal(0.6f, LeverOf(client, id));
        Assert.Equal(0, client.Commands.PendingCount);
        game.Tick(20);
        Assert.Equal(0.6f, LeverOf(game.Host, id));
    }

    [Fact]
    public void AShipKeepsFlyingThroughAHandover()
    {
        using var game = new LoopbackGame(latencyMs: 50);
        var ship = SpawnShip(game.Host, new Vector3(0, 50, 0));
        game.Host.SpawnLocalPlayer(new Vector3(1500, 60, 0), freeFly: true);
        game.Tick(2);
        var (client, _) = game.Join("a");
        Fly(game.Host, ship, new Vector3(6, 0, 0));
        game.Tick(30);
        float before = BodyPosition(game.Host, ship).X;
        game.Host.EnableBubbles();
        game.Tick(120); // two seconds: handed to the client, which carries on flying it

        var copy = client.Registry.Find(ship.Get<NetId>().Value)!.Value;
        Assert.True(copy.Get<NetOwner>().IsLocal);
        float after = BodyPosition(client, copy).X;
        Assert.InRange(after - before, 10f, 14f); // ~12 blocks in 2 s at 6 blocks a second: no lost or doubled ground
        Assert.InRange(client.Physics.GetBodyLinearVelocity(copy.Get<PhysicsBodyComponent>().Body).X, 5.5f, 6.5f);
        // And the host, following, sees it about where the client has it.
        Assert.True(MathF.Abs(game.Host.Registry.Find(ship.Get<NetId>().Value)!.Value.Get<Transform>().Position.X - after) < 1.5f);
    }

    [Fact]
    public void AShipLeftBehindGoesBackToTheHost()
    {
        var (game, ship, client) = LoneClientWithAShip();
        using var _ = game;
        uint id = ship.Get<NetId>().Value;
        game.Tick(30);
        Assert.True(client.Registry.Find(id)!.Value.Get<NetOwner>().IsLocal);
        ushort epoch = ship.Get<NetOwner>().Epoch;

        // The client flies off, out of the ship's reach and window: it's released to the host, with its body as it was.
        LocalPlayerOf(client).Get<Transform>().Position = new Vector3D<float>(-1200, 60, 0);
        Assert.True(game.TickUntil(() => ship.Get<NetOwner>().IsLocal, 120));
        game.Tick(2);
        Assert.True(BodySyncOlder(epoch, ship.Get<NetOwner>().Epoch));
        Assert.Equal(0, game.Host.Ownership!.TimedOut); // the client answered
        Assert.InRange(BodyPosition(game.Host, ship).Y, 49f, 51f);
    }

    private static bool BodySyncOlder(ushort a, ushort b) => ClearSkies.Net.Sync.BodySync.Older(a, b);

    [Fact]
    public void PlayersNearEachOtherShareABubbleAndItsOwnerDecidesTheirTerrainEdits()
    {
        var (game, ship, client) = LoneClientWithAShip();
        using var _ = game;
        game.Tick(30);
        var clientPeer = client.Session.LocalPeer;
        var hostPlayer = LocalPlayerOf(game.Host);
        Assert.Equal(PeerId.Host, game.Host.Session.BubbleOwnerOf(PeerId.Host));

        // The host flies over: merged, and the client (who owns the ship) owns the merged bubble.
        hostPlayer.Get<Transform>().Position = new Vector3D<float>(10, 60, 20);
        game.Tick(30);
        Assert.Equal(clientPeer, game.Host.Session.BubbleOwnerOf(PeerId.Host));
        Assert.Equal(clientPeer, client.Session.BubbleOwnerOf(PeerId.Host)); // told to everyone
        Assert.Equal(clientPeer, ship.Get<NetOwner>().Owner);

        // The host's own terrain edit goes to the client to decide.
        game.Host.WorldVolume.SetBlock(12, 48, 20, BlockId.Stone);
        client.WorldVolume.SetBlock(12, 48, 20, BlockId.Stone);
        long sent = game.Host.Commands.Stats.Sent;
        game.Host.Commands.Send(new EditVoxels { Volume = NetRegistry.WorldVolume, Editor = hostPlayer.Get<NetId>().Value,
            Ops = new[] { VoxelOp.SetBlock(new(12, 58, 20), BlockId.Wood, BlockOrientation.Upright) } });
        game.Tick(20);
        Assert.Equal(sent + 1, game.Host.Commands.Stats.Sent);
        Assert.Equal(BlockId.Wood, client.WorldVolume.GetBlock(12, 58, 20));
        Assert.Equal(BlockId.Wood, game.Host.WorldVolume.GetBlock(12, 58, 20));
        Assert.Equal(0, game.Host.Commands.PendingCount);

        // Flying apart again: split, but not before the 5 s minimum.
        hostPlayer.Get<Transform>().Position = new Vector3D<float>(400, 60, 0);
        game.Tick(60);
        Assert.Equal(clientPeer, game.Host.Session.BubbleOwnerOf(PeerId.Host));
        game.Tick(300);
        Assert.Equal(PeerId.Host, game.Host.Session.BubbleOwnerOf(PeerId.Host));
        Assert.Equal(clientPeer, ship.Get<NetOwner>().Owner); // it stays with the client
    }

    [Fact]
    public void BubblesChain()
    {
        using var game = new LoopbackGame();
        game.Host.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        game.Tick(2);
        var (a, _) = game.Join("a");
        var (b, _) = game.Join("b");
        var bubbles = game.Host.EnableBubbles();
        // Host — a — b in a line, 50 apart: host and b are 100 apart, but merged through a.
        LocalPlayerOf(a).Get<Transform>().Position = new Vector3D<float>(50, 60, 0);
        LocalPlayerOf(b).Get<Transform>().Position = new Vector3D<float>(100, 60, 0);
        game.Tick(40);
        Assert.Equal(3, bubbles.BubbleOwners.Count);
        Assert.Single(bubbles.BubbleOwners.Values.Distinct());
        Assert.Equal(PeerId.Host, bubbles.BubbleOwners.Values.First()); // nobody owns anything: the host

        LocalPlayerOf(b).Get<Transform>().Position = new Vector3D<float>(300, 60, 0);
        game.Tick(400);
        Assert.Equal(2, bubbles.BubbleOwners.Values.Distinct().Count());
    }

    [Fact]
    public void ACommandThatArrivesAfterAHandoverIsForwarded()
    {
        using var game = new LoopbackGame(latencyMs: 100);
        var ship = SpawnShip(game.Host, new Vector3(0, 50, 0));
        game.Tick(2);
        var (client, clientNet) = game.Join("a");
        uint id = ship.Get<NetId>().Value;

        // The client pulls the lever (the host owns the ship, so it goes there)...
        client.Commands.Send(new SetLever { Lever = EntityAddress.OfBlock(id, new(0, 1, 7)), Value = 0.4f });
        client.Tick();
        Assert.Equal(1, client.Commands.PendingCount);
        // ...and meanwhile the host hands the ship to the client.
        game.Host.Ownership!.Assign(ship, clientNet.Session.LocalPeer);
        game.Tick(60);

        var copy = client.Registry.Find(id)!.Value;
        Assert.True(copy.Get<NetOwner>().IsLocal);
        Assert.Equal(1, game.Host.Commands.Stats.Forwarded); // it reached the host after the change
        Assert.Equal(0, client.Commands.PendingCount);       // decided by the client itself in the end
        Assert.Equal(0.4f, LeverOf(client, id));
        Assert.Equal(0.4f, LeverOf(game.Host, id));

        // And back: the host's command goes to the client, which has let go by the time it arrives.
        game.Host.Ownership.Assign(ship, PeerId.Host);
        game.Host.Commands.Send(new SetLever { Lever = EntityAddress.OfBlock(id, new(0, 1, 7)), Value = -0.3f });
        game.Tick(60);
        Assert.True(ship.Get<NetOwner>().IsLocal);
        Assert.Equal(1, client.Commands.Stats.Forwarded);
        Assert.Equal(0, game.Host.Commands.PendingCount);
        Assert.Equal(-0.3f, LeverOf(game.Host, id));
        Assert.Equal(-0.3f, LeverOf(client, id));
    }

    [Fact]
    public void CrewStayOnDeckThroughAHandoverEitherWay()
    {
        using var game = new LoopbackGame(latencyMs: 50);
        var ship = SpawnShip(game.Host, new Vector3(0, 50, 0));
        var hostPlayer = game.Host.SpawnLocalPlayer(new Vector3(-2, 51.4f, -1));
        game.Tick(2);
        var (client, clientNet) = game.Join("crew");
        Walk(client);
        var crew = LocalPlayerOf(client);
        crew.Get<CharacterControllerComponent>().Character.TeleportTo(new Vector3(2, 51.4f, 2));
        game.Tick(60);
        Fly(game.Host, ship, new Vector3(4, 0, 2));
        game.Tick(60);
        var copy = client.Registry.Find(ship.Get<NetId>().Value)!.Value;
        Assert.Equal(copy, crew.Get<Support>().Supporter);

        // To the crew's machine, with them (and the host's player) on board.
        game.Host.Ownership!.Assign(ship, clientNet.Session.LocalPeer);
        game.Tick(120);
        Assert.True(copy.Get<NetOwner>().IsLocal);
        AssertOnDeck(crew, copy);
        AssertOnDeck(hostPlayer, ship);

        // And back to the host.
        game.Host.Ownership.Assign(ship, PeerId.Host);
        game.Tick(120);
        Assert.True(ship.Get<NetOwner>().IsLocal);
        AssertOnDeck(crew, copy);
        AssertOnDeck(hostPlayer, ship);
        Assert.InRange(game.Host.Physics.GetBodyLinearVelocity(ship.Get<PhysicsBodyComponent>().Body).X, 3.5f, 4.5f);
    }

    private static void AssertOnDeck(Entity player, Entity ship)
    {
        Assert.Equal(ship, player.Get<Support>().Supporter);
        var local = player.Get<Support>().LocalPosition;
        Assert.InRange(local.Y, 0.5f, 2f);
        Assert.InRange(MathF.Abs(local.X), 0f, 4.5f);
        Assert.InRange(MathF.Abs(local.Z), 0f, 4.5f);
    }

    [Fact]
    public void ALeavingOwnersShipGoesToTheHost()
    {
        var (game, ship, client) = LoneClientWithAShip();
        using var _ = game;
        var (other, _) = game.Join("b");
        uint id = ship.Get<NetId>().Value;
        game.Tick(30);
        var aNet = game.Clients[0].Net;
        Assert.Equal(aNet.Session.LocalPeer, ship.Get<NetOwner>().Owner);
        // The ship's with a (b spawned next to it too, but it keeps the owner it has).
        aNet.Dispose();
        game.Clients.RemoveAll(c => c.Net == aNet);
        game.Tick(30);
        // Taken by the host, then passed on to b, whose bubble it's in now.
        var bPeer = game.Clients[0].Net.Session.LocalPeer;
        Assert.Equal(bPeer, ship.Get<NetOwner>().Owner);
        Assert.True(other.Registry.Find(id)!.Value.Get<NetOwner>().IsLocal);
    }

    [Fact]
    public void StaleSnapshotsFromAnOldOwnerAreIgnored()
    {
        var (game, ship, client) = LoneClientWithAShip();
        using var _ = game;
        game.Tick(30);
        uint id = ship.Get<NetId>().Value;
        var epoch = ship.Get<NetOwner>().Epoch;
        Assert.True(epoch > 0);
        var w = new NetWriter();
        w.WriteUInt32(game.Host.Clock.Tick + 5);
        w.WriteUInt16(1);
        new ClearSkies.Net.Protocol.BodySnapshot { Entity = id, Epoch = (ushort)(epoch - 1), Position = new Vector3(500, 50, 0), Rotation = Quaternion.Identity }.Write(w);
        var r = new NetReader(w.Written);
        game.HostNet.Bodies!.ReceiveFrame(PeerId.Host, ref r);
        Assert.NotEqual(game.Host.Clock.Tick + 5, ship.Get<ClearSkies.Net.Sync.RemoteBody>().Buffer.LatestTick);
    }

    [Fact]
    public void ADriftedCopyOfAClientsShipIsResyncedFromIt()
    {
        var (game, ship, client) = LoneClientWithAShip();
        using var _ = game;
        game.Tick(30);
        uint id = ship.Get<NetId>().Value;
        Assert.False(ship.Get<NetOwner>().IsLocal);
        // A bug on the host: a block only it has.
        ship.Get<ChunkGrid>().Volume.SetBlock(2, 3, 2, BlockId.Stone);
        game.Tick(ClearSkies.Net.Sync.DivergenceCheck.IntervalTicks + 30);
        var check = game.HostNet.Divergence!;
        Assert.Equal(1, check.Mismatches);
        Assert.Equal(BlockId.Air, game.Host.Registry.Find(id)!.Value.Get<ChunkGrid>().Volume.GetBlock(2, 3, 2));
        Assert.True(client.Registry.Find(id)!.Value.Get<NetOwner>().IsLocal); // still the client's
    }
}
