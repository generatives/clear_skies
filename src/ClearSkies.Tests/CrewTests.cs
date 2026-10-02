using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Input;
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
    internal static (LoopbackGame game, Entity ship, HeadlessScene client, Entity crew) ShipWithCrew(double latencyMs = 50)
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
        PlaceCrew(game, crew, new Vector3(-1, 51.4f, -1));
        game.Tick(60);
        return (game, ship, client, crew);
    }

    private static Entity Simulated(LoopbackGame game, Entity crew) => game.Simulated(crew);

    /// <summary>Puts a client's player, walking, at <paramref name="at"/>: on the host, which simulates them, and on
    /// the client, which predicts them.</summary>
    internal static void PlaceCrew(LoopbackGame game, Entity crew, Vector3 at)
    {
        foreach (var e in new[] { game.Simulated(crew), crew }) Players.SetFreeFlying(e, false);
        game.Teleport(crew, at);
    }

    [Fact]
    public void AShipCopyFollowsItsOwner()
    {
        var (game, ship, client, _) = ShipWithCrew();
        using var _ = game;
        var copy = client.Registry.Find(ship.Get<EntityId>())!.Value;
        Assert.Equal(PhysicsMode.KinematicFollower, copy.Get<PhysicsPresence>().Mode);
        // Fly the (locked, kinematic) ship east at 6 blocks a second for 2 s.
        game.Host.Physics.SetBodyLinearVelocity(ship.Get<PhysicsBodyComponent>().Body, new Vector3(6, 0, 0));
        game.Tick(120);
        game.Host.Physics.SetBodyLinearVelocity(ship.Get<PhysicsBodyComponent>().Body, Vector3.Zero);
        game.Tick(30);
        var (truth, _) = game.Host.Physics.GetBodyPose(ship.Get<PhysicsBodyComponent>().Body);
        var (followed, _) = client.Physics.GetBodyPose(copy.Get<PhysicsBodyComponent>().Body);
        Assert.True(truth.X > 11);
        Assert.True(MathF.Abs(followed.X - truth.X) < 0.05f, $"copy at {followed.X}, ship at {truth.X}");
    }

    [Fact]
    public void CrewRideAShipInFlight()
    {
        var (game, ship, client, crew) = ShipWithCrew();
        using var _ = game;
        var copy = client.Registry.Find(ship.Get<EntityId>())!.Value;
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
        var seen = game.Host.Registry.Find(crew.Get<EntityId>())!.Value;
        var shipPos = ship.Get<Transform>().Position;
        Assert.True(Vector3D.Distance(seen.Get<Transform>().Position, shipPos) < 6f);
    }

    /// <summary>A small unlocked ship (5×5 Wood), flown up hard on its levers with someone on a corner: the host's own
    /// player, or a client's (simulated on the host, which flies the ship, from the client's input). Returns how far it tipped at most while
    /// standing (the first second) and while climbing (four seconds), in degrees, and how far it climbed.</summary>
    private static (float Standing, float Climbing, float Climbed) CornerTilt(bool remote)
    {
        using var game = new LoopbackGame(50);
        var voxels = new List<GridVoxel>();
        for (int x = 0; x < 5; x++) for (int z = 0; z < 5; z++) voxels.Add(new(x, 0, z, BlockId.Wood, BlockOrientation.Upright));
        var ship = game.Host.SpawnGrid(GridDescription.FromVoxels(new Vector3(0, 50, 0), voxels));
        var corner = new Vector3(2f, 51.4f, 2f);
        game.Host.SpawnLocalPlayer(remote ? new Vector3(30, 80, 30) : corner, freeFly: remote);
        game.Host.AddBeforePhysics(new AirshipFlightSystem(game.Host.World, game.Host.Physics) { FreePropulsion = true });
        game.Tick(2);
        Entity? crew = null;
        if (remote)
        {
            var (client, _) = game.Join("crew");
            crew = LocalPlayerOf(client);
            PlaceCrew(game, crew.Value, corner);
        }
        var id = ship.Get<EntityId>();
        game.Host.Commands.Send(new SetGridLocked { Grid = id, Locked = false });
        var body = ship.Get<PhysicsBodyComponent>().Body;
        float Tilt()
        {
            var (_, q) = game.Host.Physics.GetBodyPose(body);
            return MathF.Acos(Math.Clamp(Vector3.Transform(Vector3.UnitY, q).Y, -1f, 1f)) * 180f / MathF.PI;
        }
        float standing = 0f, climbing = 0f;
        for (int t = 0; t < 60; t++) { game.Tick(); standing = MathF.Max(standing, Tilt()); }

        game.Host.Commands.Send(new SetShipThrust { Ship = id, Axis = ThrustAxis.Up, Value = 1f });
        float startY = game.Host.Physics.GetBodyPose(body).position.Y;
        for (int t = 0; t < 240; t++) { game.Tick(); climbing = MathF.Max(climbing, Tilt()); }
        if (crew is { } c) Assert.Equal(game.Clients[0].Scene.Registry.Find(id), c.Get<Support>().Supporter); // still aboard
        return (standing, climbing, game.Host.Physics.GetBodyPose(body).position.Y - startY);
    }

    [Fact]
    public void ACrewMemberOnACornerTipsTheShipAsMuchAsTheHostsOwnPlayer()
    {
        // The host simulates a client's crew member as it does its own player, so they press on the deck the same.
        var local = CornerTilt(remote: false);
        var crew = CornerTilt(remote: true);
        Assert.True(local.Climbed > 20f && crew.Climbed > 20f, $"climbed {local.Climbed:F1} / {crew.Climbed:F1}");
        Assert.True(MathF.Abs(crew.Standing - local.Standing) < 1.5f, $"standing: host's {local.Standing:F1}°, crew's {crew.Standing:F1}°");
        Assert.True(MathF.Abs(crew.Climbing - local.Climbing) < 3f, $"climbing: host's {local.Climbing:F1}°, crew's {crew.Climbing:F1}°");
    }

    [Fact]
    public void ACrewMemberWalksAndJumpsTheSameOnHostAndClientOnAMovingDeck()
    {
        var (game, ship, client, crew) = ShipWithCrew();
        using var _ = game;
        var simulated = Simulated(game, crew);
        var prediction = (OwnPlayerPrediction)client.Net!.Prediction!;
        game.Host.Physics.SetBodyLinearVelocity(ship.Get<PhysicsBodyComponent>().Body, new Vector3(4, 0, 3));
        game.Tick(30);
        float deck = simulated.Get<Support>().LocalPosition.Y;
        float highest = 0f;
        long correctionsBefore = prediction.Corrections;
        for (int t = 0; t < 150; t++)
        {
            // Walk half a second (2.5 blocks, staying aboard), jump, then stand.
            crew.Get<PlayerInput>() = new PlayerInput
            {
                Held = t < 30 ? PlayerButtons.Back : PlayerButtons.None,
                Pressed = t == 30 ? PlayerButtons.Up : PlayerButtons.None,
            };
            game.Tick();
            highest = MathF.Max(highest, simulated.Get<Support>().LocalPosition.Y - deck);
        }
        Assert.True(highest > 0.6f, $"rose {highest:F2} on the host"); // it jumped there too
        Assert.Equal(ship, simulated.Get<Support>().Supporter);          // and landed back on the deck
        var there = simulated.Get<Support>().LocalPosition;
        var here = crew.Get<Support>().LocalPosition;
        Assert.True(Vector3.Distance(there, here) < 0.1f, $"host has them at {there} on deck, client at {here}");
        Assert.True(Vector3.Distance(there, new Vector3(-1, there.Y, -1)) > 2f, "they walked"); // (moved, by the host too)
        Assert.True(prediction.LargestCorrection < 0.3f, $"corrected by up to {prediction.LargestCorrection:F2} ({prediction.Corrections - correctionsBefore} times)");
    }

    [Fact]
    public void CrewStayAboardWhileTheHostDrawsSlowly()
    {
        // The host at 8 fps (its window in the background, say), running its ticks as the game would; the client at 60.
        var (game, ship, client, crew) = ShipWithCrew(20);
        using var _ = game;
        var copy = client.Registry.Find(ship.Get<EntityId>())!.Value;
        game.Host.Physics.SetBodyLinearVelocity(ship.Get<PhysicsBodyComponent>().Body, new Vector3(6, 0, 0));
        var hostClock = new ClearSkies.Engine.Core.TickClock();
        int jumps = 0;
        for (int frame = 1; frame <= 600; frame++)
        {
            game.Network.ManualTime += 1000.0 / 60.0;
            if (frame % 8 == 0) game.Host.Tick(hostClock.Advance(8 / 60.0));
            uint before = client.Clock.Tick;
            client.Tick();
            if (client.Clock.Tick < before || client.Clock.Tick > before + 2) jumps++; // clock sync snapped
        }
        Assert.Equal(0, jumps);
        Assert.Equal(copy, crew.Get<Support>().Supporter); // still on deck
        Assert.InRange(client.RemoteBodies!.Delays.Most, 0, 12); // drawn a little behind, not seconds
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrewStayPutOnDeckThroughAHostStall(bool turning)
    {
        // The host stalling to 1 fps for a few seconds (another game loading on the same machine, say), running a
        // second of ticks at once each frame, while its ship flies on with a client's player aboard. Its snapshots
        // arrive in bursts a second apart.
        var (game, ship, client, crew) = ShipWithCrew(20);
        using var _ = game;
        var copy = client.Registry.Find(ship.Get<EntityId>())!.Value;
        game.Host.Physics.SetBodyLinearVelocity(ship.Get<PhysicsBodyComponent>().Body, new Vector3(10, 0, 0));
        // Turning, its copy can't be carried on along its snapshots' velocity: each burst puts it somewhere else.
        if (turning) game.Host.Physics.SetBodyAngularVelocity(ship.Get<PhysicsBodyComponent>().Body, new Vector3(0, 0.3f, 0));
        game.Tick(60);
        var start = crew.Get<Support>().LocalPosition;
        var hostClock = new ClearSkies.Engine.Core.TickClock();
        float slid = 0f;
        for (int frame = 1; frame <= 360; frame++)
        {
            game.Network.ManualTime += 1000.0 / 60.0;
            bool stalled = frame <= 240;
            if (!stalled || frame % 60 == 0) game.Host.Tick(hostClock.Advance(stalled ? 1.0 : 1 / 60.0));
            client.Tick();
            if (crew.Get<Support>().Supporter == copy)
                slid = MathF.Max(slid, Vector2.Distance(new Vector2(crew.Get<Support>().LocalPosition.X, crew.Get<Support>().LocalPosition.Z),
                                                        new Vector2(start.X, start.Z)));
        }
        Assert.Equal(copy, crew.Get<Support>().Supporter); // still on deck
        Assert.True(slid < 0.5f, $"slid {slid:F2} on the deck");
    }

    [Fact]
    public void BothPlayersWorkTheShipsControls()
    {
        var (game, ship, client, _) = ShipWithCrew();
        using var _ = game;
        var id = ship.Get<EntityId>();
        // The crew member pulls one lever; the host turns the wheel.
        client.Registry.Find(id)!.Value.Get<ChunkGrid>().Volume.TryGetBlockEntity(0, 1, 7, out var pulled);
        var (axis, sign) = ShipControls.LeverAxis(pulled.Get<BlockRef>());
        client.Commands.Send(new SetShipThrust { Ship = id, Axis = axis, Value = 0.8f * sign });
        game.Host.Commands.Send(new SetShipTurn { Ship = id, Value = -1.5f / SteeringWheel.MaxAngle });
        game.Tick(30);
        foreach (var scene in new[] { game.Host, client })
        {
            var v = scene.Registry.Find(id)!.Value.Get<ChunkGrid>().Volume;
            v.TryGetBlockEntity(0, 1, 7, out var lever);
            v.TryGetBlockEntity(7, 1, 7, out var other);
            v.TryGetBlockEntity(3, 1, 7, out var wheel);
            Assert.Equal(0.8f, ShipControls.LeverValue(lever.Get<BlockRef>()), 5);
            Assert.Equal(-0.8f, ShipControls.LeverValue(other.Get<BlockRef>()), 5); // the same axis, facing the other way
            Assert.Equal(-1.5f, SteeringWheel.Angle(wheel.Get<BlockRef>()), 5);
        }
    }

    [Fact]
    public void AClientsPlayerIsSimulatedOnTheHostAndPredictedOnTheClient()
    {
        var (game, ship, client, crew) = ShipWithCrew();
        using var _ = game;
        var simulated = Simulated(game, crew);
        Assert.Equal(PhysicsMode.Simulated, simulated.Get<PhysicsPresence>().Mode);
        Assert.True(simulated.Has<RemoteInput>()); // moved by the client's input
        Assert.Equal(PhysicsMode.Simulated, crew.Get<PhysicsPresence>().Mode);
        Assert.Equal(ship, simulated.Get<Support>().Supporter);

        // Off onto terrain: the host has its colliders around them, so they stand on it there too.
        for (int x = 10; x < 14; x++) for (int z = 10; z < 14; z++) game.Host.WorldVolume.SetBlock(x, 40, z, BlockId.Stone);
        for (int x = 10; x < 14; x++) for (int z = 10; z < 14; z++) client.WorldVolume.SetBlock(x, 40, z, BlockId.Stone);
        game.Tick(30); // the stone's colliders, built on each machine
        PlaceCrew(game, crew, new Vector3(11.5f, 42f, 11.5f));
        game.Tick(90);
        var there = simulated.Get<CharacterControllerComponent>().Character.Position;
        var here = crew.Get<CharacterControllerComponent>().Character.Position;
        Assert.InRange(there.Y, 41.7f, 41.9f); // standing on it (its top is at 41)
        Assert.True(Vector3.Distance(there, here) < 0.1f, $"host has them at {there}, client at {here}");

        // Flying, on both.
        client.Commands.Send(new SetMoveMode { Player = crew.Get<EntityId>(), FreeFly = true });
        game.Tick(30);
        Assert.True(crew.Has<FreeFlying>());
        Assert.True(simulated.Has<FreeFlying>());
    }

    /// <summary>Where a grid's voxel (0,0,0) is drawn.</summary>
    private static Vector3 DrawnOrigin(Entity grid)
    {
        var p = grid.Get<Transform>().Position;
        return new Vector3(p.X, p.Y, p.Z);
    }

    /// <summary>Where a grid's centre of mass is in its block space.</summary>
    private static Vector3D<float> Com(Entity grid) => grid.Get<PhysicsBodyComponent>().Offset;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnEditDoesNotShakeTheShipsCopy(bool byTheCrew)
    {
        var (game, ship, client, crew) = ShipWithCrew();
        using var _ = game;
        var id = ship.Get<EntityId>();
        var copy = client.Registry.Find(id)!.Value;
        var before = DrawnOrigin(copy);
        var com = Com(copy);
        var editor = byTheCrew ? crew : game.Host.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        (byTheCrew ? client : game.Host).Commands.Send(new EditVoxels { Volume = id, Editor = editor.Get<EntityId>(),
            Ops = new[] { VoxelOp.FillBox(new(1, 2, 1), 1, BlockId.Stone, BlockOrientation.Upright) } });
        for (int i = 0; i < 40; i++)
        {
            game.Tick();
            float off = Vector3.Distance(DrawnOrigin(copy), before);
            Assert.True(off < 0.01f, $"{i} ticks after the edit the copy is drawn {off} blocks off");
        }
        Assert.True(Vector3D.Distance(Com(copy), com) > 0.5f); // the edit did move its centre of mass
    }

    [Fact]
    public void AnEditMovesTheCentreOfMassTheSameEverywhere()
    {
        var (game, ship, client, crew) = ShipWithCrew();
        using var _ = game;
        var id = ship.Get<EntityId>();
        client.Commands.Send(new EditVoxels { Volume = id, Editor = crew.Get<EntityId>(),
            Ops = new[] { VoxelOp.FillBox(new(1, 2, 1), 1, BlockId.Stone, BlockOrientation.Upright) } });
        game.Tick(60);
        var copy = client.Registry.Find(id)!.Value;
        var a = Com(ship);
        var b = Com(copy);
        Assert.True(Vector3D.Distance(a, b) < 1e-3f, $"host centre of mass {a}, client centre of mass {b}");
        // And the copy sits where the ship is.
        var (followed, _) = client.Physics.GetBodyPose(copy.Get<PhysicsBodyComponent>().Body);
        var (truth, _) = game.Host.Physics.GetBodyPose(ship.Get<PhysicsBodyComponent>().Body);
        Assert.True(Vector3.Distance(followed, truth) < 0.05f);
    }
}
