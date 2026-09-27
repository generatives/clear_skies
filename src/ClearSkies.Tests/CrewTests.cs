using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Sync;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

public class CrewTests
{
    private static Entity LocalPlayerOf(HeadlessScene scene) => scene.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();

    /// <summary>A host with a ship (an 8×8 deck with a lever, a second lever on its axis and a wheel) and a player on it,
    /// and a client whose player walks on the same deck.</summary>
    private static (LoopbackGame game, Entity ship, HeadlessScene client, Entity crew) ShipWithCrew(double latencyMs = 50)
    {
        var game = new LoopbackGame(latencyMs);
        var voxels = new List<GridVoxel>();
        for (int x = 0; x < 8; x++) for (int z = 0; z < 8; z++) voxels.Add(new(x, 0, z, BlockId.Wood, BlockOrientation.Upright));
        voxels.Add(new(0, 1, 7, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.North)));
        voxels.Add(new(7, 1, 7, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.South)));
        voxels.Add(new(3, 1, 7, BlockId.SteeringWheel, BlockOrientation.From(Direction.Up, Direction.South)));
        var ship = game.Host.SpawnGrid(GridDescription.FromVoxels(new Vector3(0, 50, 0), voxels));
        game.Host.SpawnLocalPlayer(new Vector3(2, 52, 2)); // walking on deck
        game.Tick(2);
        var (client, _) = game.Join("crew");
        var crew = LocalPlayerOf(client);
        crew.Get<CharacterModeComponent>().FreeFly = false;
        crew.Get<CharacterControllerComponent>().Character.TeleportTo(new Vector3(-1, 51.4f, -1));
        game.Tick(60);
        return (game, ship, client, crew);
    }

    [Fact]
    public void AShipCopyFollowsItsOwner()
    {
        var (game, ship, client, _) = ShipWithCrew();
        using var _ = game;
        var copy = client.Registry.Find(ship.Get<NetId>().Value)!.Value;
        Assert.Equal(PhysicsMode.KinematicFollower, copy.Get<PhysicsPresence>().Mode);
        // Fly the (locked, kinematic) ship east at 6 blocks a second for 2 s.
        game.Host.Physics.SetBodyLinearVelocity(ship.Get<PhysicsBodyComponent>().Body, new Vector3(6, 0, 0));
        game.Tick(120);
        game.Host.Physics.SetBodyLinearVelocity(ship.Get<PhysicsBodyComponent>().Body, Vector3.Zero);
        game.Tick(30);
        var truth = ship.Get<Transform>().Position;
        var (followed, _) = client.Physics.GetBodyPose(copy.Get<PhysicsBodyComponent>().Body);
        Assert.True(truth.X > 11);
        Assert.True(MathF.Abs(followed.X - truth.X) < 0.05f, $"copy at {followed.X}, ship at {truth.X}");
    }

    [Fact]
    public void CrewRideAShipInFlight()
    {
        var (game, ship, client, crew) = ShipWithCrew();
        using var _ = game;
        var copy = client.Registry.Find(ship.Get<NetId>().Value)!.Value;
        Assert.Equal(copy, crew.Get<Support>().Supporter); // standing on the host's ship, as the client has it

        var body = ship.Get<PhysicsBodyComponent>().Body;
        game.Host.Physics.SetBodyLinearVelocity(body, new Vector3(4, 0, 3));
        game.Host.Physics.SetBodyAngularVelocity(body, new Vector3(0, 0.3f, 0)); // and turning
        game.Tick(240); // four seconds of flight
        Assert.Equal(copy, crew.Get<Support>().Supporter);
        var local = crew.Get<Support>().LocalPosition;
        Assert.InRange(local.Y, 0.5f, 2f);                  // still on the deck...
        Assert.InRange(MathF.Abs(local.X), 0f, 4f);         // ...not left behind
        Assert.InRange(MathF.Abs(local.Z), 0f, 4f);

        // The host sees the crew member on its deck too.
        var seen = game.Host.Registry.Find(crew.Get<NetId>().Value)!.Value;
        var shipPos = ship.Get<Transform>().Position;
        Assert.True(Vector3D.Distance(seen.Get<Transform>().Position, shipPos) < 6f);
    }

    [Fact]
    public void BothPlayersWorkTheShipsControls()
    {
        var (game, ship, client, _) = ShipWithCrew();
        using var _ = game;
        uint id = ship.Get<NetId>().Value;
        // The crew member pulls one lever; the host turns the wheel.
        client.Commands.Send(new SetLever { Lever = EntityAddress.OfBlock(id, new(0, 1, 7)), Value = 0.8f });
        game.Host.Commands.Send(new SetWheel { Wheel = EntityAddress.OfBlock(id, new(3, 1, 7)), Angle = -1.5f });
        game.Tick(30);
        foreach (var scene in new[] { game.Host, client })
        {
            var v = scene.Registry.Find(id)!.Value.Get<ChunkGrid>().Volume;
            v.TryGetBlockEntity(0, 1, 7, out var lever);
            v.TryGetBlockEntity(7, 1, 7, out var other);
            v.TryGetBlockEntity(3, 1, 7, out var wheel);
            Assert.Equal(0.8f, lever.Get<Lever>().Value);
            Assert.Equal(-0.8f, other.Get<Lever>().Value); // the same axis, facing the other way
            Assert.Equal(-1.5f, wheel.Get<SteeringWheel>().Angle);
        }
    }

    [Fact]
    public void ServoCopiesStandOnShipsButPassThroughTerrain()
    {
        var (game, ship, client, crew) = ShipWithCrew();
        using var _ = game;
        var copy = game.Host.Registry.Find(crew.Get<NetId>().Value)!.Value;
        Assert.Equal(PhysicsMode.ServoFollower, copy.Get<PhysicsPresence>().Mode);
        Assert.True(copy.Has<ServoBody>());
        var (onDeck, _) = game.Host.Physics.GetBodyPose(copy.Get<ServoBody>().Body);
        Assert.InRange(onDeck.Y, 50.8f, 52.5f); // resting on the deck (its top is at 50.5)

        // Terrain right where the crew member walks: their copy goes into it (their own machine keeps them out of it).
        for (int x = 10; x < 14; x++) for (int z = 10; z < 14; z++) game.Host.WorldVolume.SetBlock(x, 40, z, BlockId.Stone);
        crew.Get<CharacterModeComponent>().FreeFly = true;
        crew.Get<Transform>().Position = new Vector3D<float>(11.5f, 40.5f, 11.5f);
        game.Tick(60);
        var (inTerrain, _) = game.Host.Physics.GetBodyPose(copy.Get<ServoBody>().Body);
        Assert.True(Vector3.Distance(inTerrain, new Vector3(11.5f, 40.5f, 11.5f)) < 0.5f);
    }

    [Fact]
    public void AGridSnapshotWaitsForTheEditItFollows()
    {
        var (game, ship, client, crew) = ShipWithCrew(latencyMs: 0);
        using var _ = game;
        uint id = ship.Get<NetId>().Value;
        var copy = client.Registry.Find(id)!.Value;
        var bodies = (BodySync)client.Net!.Bodies!;
        uint applied = client.Commands.LastEventNumber(PeerId.Host, id);

        // A snapshot claiming a shape change the client hasn't seen yet.
        var w = new NetWriter();
        w.WriteUInt32(client.Clock.Tick + 50);
        w.WriteUInt16(1);
        new BodySnapshot { Entity = id, ShapeVersion = applied + 1, Position = new Vector3(100, 50, 0), Rotation = Quaternion.Identity }.Write(w);
        var r = new NetReader(w.Written);
        bodies.ReceiveFrame(PeerId.Host, ref r);
        Assert.Equal(1, bodies.Parked);
        Assert.NotEqual(client.Clock.Tick + 50, copy.Get<RemoteBody>().Buffer.LatestTick);

        // The edit arrives: the snapshot goes in.
        game.Host.Commands.Send(new EditVoxels { Volume = id, Editor = game.Host.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single().Get<NetId>().Value,
            Ops = new[] { VoxelOp.SetBlock(new(4, 1, 4), BlockId.Stone, BlockOrientation.Upright) } });
        game.Tick(3);
        Assert.Equal(0, bodies.Parked);
    }

    [Fact]
    public void AnEditMovesTheCentreOfMassTheSameEverywhere()
    {
        var (game, ship, client, crew) = ShipWithCrew();
        using var _ = game;
        uint id = ship.Get<NetId>().Value;
        client.Commands.Send(new EditVoxels { Volume = id, Editor = crew.Get<NetId>().Value,
            Ops = new[] { VoxelOp.FillBox(new(1, 2, 1), 1, BlockId.Stone, BlockOrientation.Upright) } });
        game.Tick(60);
        var copy = client.Registry.Find(id)!.Value;
        var a = ship.Get<ChunkGrid>().Volume.Pivot;
        var b = copy.Get<ChunkGrid>().Volume.Pivot;
        Assert.True(Vector3D.Distance(a, b) < 1e-3f, $"host pivot {a}, client pivot {b}");
        // And the copy sits where the ship is.
        var (followed, _) = client.Physics.GetBodyPose(copy.Get<PhysicsBodyComponent>().Body);
        var (truth, _) = game.Host.Physics.GetBodyPose(ship.Get<PhysicsBodyComponent>().Body);
        Assert.True(Vector3.Distance(followed, truth) < 0.05f);
    }
}
