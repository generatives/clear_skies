using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

public class NetRegistryTests
{
    [Fact]
    public void LooksEntitiesUpByIdAndForgetsDisposedOnes()
    {
        using var world = new World();
        using var registry = new EntityRegistry(world);
        var e = world.CreateEntity();
        e.Set(new EntityId(5000));
        Assert.True(registry.TryGet(new EntityId(5000), out var found));
        Assert.Equal(e, found);
        e.Dispose();
        Assert.False(registry.IsLive(new EntityId(5000)));
    }

    [Fact]
    public void ChangingAnIdMovesTheEntry()
    {
        using var world = new World();
        using var registry = new EntityRegistry(world);
        var e = world.CreateEntity();
        e.Set(new EntityId(10));
        e.Set(new EntityId(11));
        Assert.False(registry.IsLive(new EntityId(10)));
        Assert.True(registry.IsLive(new EntityId(11)));
    }

    [Fact]
    public void AllocatedIdsNeverRepeatAcrossBlocks()
    {
        using var world = new World();
        using var registry = new EntityRegistry(world);
        var allocator = new EntityIdAllocator();
        registry.RequestBlock = allocator.NextBlock;
        var seen = new HashSet<EntityId>();
        for (int i = 0; i < 5000; i++) Assert.True(seen.Add(registry.Allocate()));
        Assert.All(seen, id => Assert.True(id.Value >= EntityRegistry.FirstFreeId));
    }

    [Fact]
    public void AllocatingWithNoBlocksAndNoSourceFails()
    {
        using var world = new World();
        using var registry = new EntityRegistry(world);
        Assert.Throws<InvalidOperationException>(() => registry.Allocate());
    }

    [Fact]
    public void NewGridsAreNetworkedOwnedHereAndSupportable()
    {
        using var scene = new HeadlessScene();
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0));
        Assert.True(grid.Has<EntityId>());
        Assert.True(scene.Registry.IsLive(grid.Get<EntityId>()));
        Assert.True(grid.Get<NetOwner>().IsLocal);
        Assert.True(grid.Has<OwnPresence>());
        Assert.True(grid.Has<Supportable>());
    }
}

public class PresenceTests
{
    [Fact]
    public void OwnedGridIsSimulatedDrawnAndHasColliderInterest()
    {
        using var scene = new HeadlessScene();
        scene.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0));
        scene.Tick();
        Assert.Equal(PhysicsMode.Simulated, grid.Get<PhysicsPresence>().Mode);
        Assert.True(grid.Has<Rendered>());
        Assert.Equal(EntityPresenceSystem.ColliderRange, grid.Get<TerrainInterest>().ColliderRadius);
        Assert.Equal(0f, grid.Get<TerrainInterest>().DrawRadius);
        scene.Tick();
        Assert.True(grid.Has<PhysicsBodyComponent>()); // the body follows the presence
    }

    [Fact]
    public void LocalPlayersTerrainInterestIsDrawnToTheViewDistance()
    {
        using var scene = new HeadlessScene();
        var player = scene.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        scene.Tick();
        Assert.Equal(500f, player.Get<TerrainInterest>().DrawRadius); // the scene's view distance
        Assert.Equal(EntityPresenceSystem.ColliderRange, player.Get<TerrainInterest>().ColliderRadius);
        Assert.Equal(PhysicsMode.Simulated, player.Get<PhysicsPresence>().Mode);
        Assert.True(player.Has<Rendered>());
    }

    private static Entity RemoteEntity(HeadlessScene scene, Vector3 position, bool player)
    {
        var e = scene.World.CreateEntity();
        e.Set(new Transform { Position = new Vector3D<float>(position.X, position.Y, position.Z), Rotation = Quaternion<float>.Identity, Scale = Vector3D<float>.One });
        e.Set(scene.Session.OwnerFor(new PeerId(7)));
        e.Set<OwnPresence>();
        if (player) e.Set(new Player { Id = PlayerId.New(), Name = "other" });
        return e;
    }

    [Fact]
    public void AnotherPlayerWithinLoadRangeIsACharacterFollowerWithNoTerrainInterest()
    {
        using var scene = new HeadlessScene();
        scene.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        var near = RemoteEntity(scene, new Vector3(100, 60, 0), player: true);
        var far = RemoteEntity(scene, new Vector3(5000, 60, 0), player: true);
        scene.Tick();
        Assert.Equal(PhysicsMode.CharacterFollower, near.Get<PhysicsPresence>().Mode);
        Assert.False(near.Has<TerrainInterest>());
        Assert.False(far.Has<PhysicsPresence>());
    }

    [Fact]
    public void AGridOwnedElsewhereNearTheLocalPlayerIsAKinematicFollower()
    {
        using var scene = new HeadlessScene();
        scene.Presence.ViewDistance = 1000f; // entities are drawn as far as the terrain
        scene.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        var near = RemoteEntity(scene, new Vector3(50, 60, 0), player: false);
        var far = RemoteEntity(scene, new Vector3(900, 60, 0), player: false);
        scene.Tick();
        Assert.Equal(PhysicsMode.KinematicFollower, near.Get<PhysicsPresence>().Mode);
        Assert.False(far.Has<PhysicsPresence>());
        Assert.True(far.Has<Rendered>()); // still drawn within render distance
    }

    [Fact]
    public void DistanceRulesHaveHysteresis()
    {
        using var scene = new HeadlessScene();
        scene.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        var other = RemoteEntity(scene, new Vector3(990, 60, 0), player: true);
        scene.Tick();
        Assert.True(other.Has<PhysicsPresence>());
        other.Get<Transform>().Position = new Vector3D<float>(1050, 60, 0); // past 1000 but within 1100
        scene.Tick();
        Assert.True(other.Has<PhysicsPresence>());
        other.Get<Transform>().Position = new Vector3D<float>(1200, 60, 0);
        scene.Tick();
        Assert.False(other.Has<PhysicsPresence>());
        other.Get<Transform>().Position = new Vector3D<float>(1050, 60, 0); // coming back: not until within 1000
        scene.Tick();
        Assert.False(other.Has<PhysicsPresence>());
    }

    [Fact]
    public void RenderDistanceFollowsTheViewDistanceUpToItsLimit()
    {
        using var scene = new HeadlessScene(); // view distance 500
        Assert.Equal(500f, scene.Presence.RenderDistance);
        scene.Presence.RenderDistanceLimit = 200f;
        Assert.Equal(200f, scene.Presence.RenderDistance);
        scene.Presence.ViewDistance = 150f;
        Assert.Equal(150f, scene.Presence.RenderDistance);
    }

    [Fact]
    public void RenderDistanceRemovesRenderedFromTheGridAndItsChildren()
    {
        using var scene = new HeadlessScene();
        scene.Presence.RenderDistanceLimit = 100f;
        scene.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0));
        scene.Tick(2);
        var chunks = grid.Get<ChunkGrid>().Volume.All.Select(c => c.Value.Entity).ToList();
        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.True(c.Has<Rendered>()));

        grid.Get<Transform>().Position = new Vector3D<float>(1000, 50, 0);
        scene.Physics.SetBodyPose(grid.Get<PhysicsBodyComponent>().Body, new Vector3(1000, 50, 0), Quaternion.Identity);
        scene.Tick();
        Assert.False(grid.Has<Rendered>());
        Assert.All(chunks, c => Assert.False(c.Has<Rendered>()));
    }

    [Fact]
    public void ChildrenAttachedLaterInheritRendered()
    {
        using var scene = new HeadlessScene();
        scene.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0));
        scene.Tick();
        var volume = grid.Get<ChunkGrid>().Volume;
        // A lever on the deck, and a block far enough out to start a new chunk.
        volume.SetBlock(1, 1, 1, BlockId.Lever, BlockOrientation.Upright);
        volume.SetBlock(40, 0, 0, BlockId.Stone);
        Assert.True(volume.TryGetBlockEntity(1, 1, 1, out var lever));
        Assert.True(lever.Has<Rendered>());
        Assert.True(volume.GetEntry(new ChunkPosition(1, 0, 0))!.Entity.Has<Rendered>());
    }

    [Fact]
    public void TerrainChunksGetCollidersNearInterestsOnly()
    {
        using var scene = new HeadlessScene();
        scene.WorldVolume.SetBlock(0, 0, 0, BlockId.Stone);        // near the player
        scene.WorldVolume.SetBlock(3200, 0, 0, BlockId.Stone);     // far from everything
        scene.SpawnLocalPlayer(new Vector3(0, 20, 0), freeFly: true);
        scene.Tick();
        var near = scene.WorldVolume.GetEntry(new ChunkPosition(0, 0, 0))!.Entity;
        var far = scene.WorldVolume.GetEntry(new ChunkPosition(100, 0, 0))!.Entity;
        Assert.True(near.Has<OwnPresence>());
        Assert.True(far.Has<OwnPresence>(), $"far alive {far.IsAlive}, loaded {scene.WorldVolume.LoadedCount}, near==far {near==far}, pos {string.Join(",", scene.WorldVolume.All.Select(c=>c.Key.ToString()))}");
        Assert.Equal(PhysicsMode.Static, near.Get<PhysicsPresence>().Mode);
        Assert.False(far.Has<PhysicsPresence>());
        Assert.True(near.Has<Rendered>());
        Assert.False(far.Has<Rendered>()); // beyond the view distance
    }

    [Fact]
    public void TerrainCollidersAreDroppedOnceNothingIsNear()
    {
        using var scene = new HeadlessScene();
        scene.WorldVolume.SetBlock(0, 0, 0, BlockId.Stone);
        var player = scene.SpawnLocalPlayer(new Vector3(0, 20, 0), freeFly: true);
        scene.Tick();
        var chunk = scene.WorldVolume.GetEntry(new ChunkPosition(0, 0, 0))!.Entity;
        Assert.True(chunk.Has<PhysicsPresence>());
        player.Get<Transform>().Position = new Vector3D<float>(0, 20, 400);
        scene.Tick(31); // the drop sweep runs every 30 ticks
        Assert.False(chunk.Has<PhysicsPresence>());
    }
}

public class SupportTests
{
    private static (HeadlessScene scene, Entity grid, Entity player) StandingOnPlatform()
    {
        var scene = new HeadlessScene();
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0));
        var player = scene.SpawnLocalPlayer(new Vector3(0, 52, 0));
        Assert.True(scene.TickUntil(() => player.Get<Support>().HasSupporter, 300), "the player never landed on the platform");
        return (scene, grid, player);
    }

    [Fact]
    public void StandingOnAGridMakesItTheSupport()
    {
        var (scene, grid, player) = StandingOnPlatform();
        using var _ = scene;
        Assert.Equal(grid, player.Get<Support>().Supporter);
        // The local pose is relative to the grid: about a capsule's half height above the deck's centre.
        Assert.InRange(player.Get<Support>().LocalPosition.Y, 0.5f, 2f);
    }

    [Fact]
    public void TheSupportIsKeptWhileAboveItAndReleasedHalfASecondAfterLeaving()
    {
        var (scene, grid, player) = StandingOnPlatform();
        using var _ = scene;
        // Off to the side of the platform, in mid-air.
        player.Get<CharacterControllerComponent>().Character.TeleportTo(new Vector3(30, 60, 0));
        scene.Tick(20); // a third of a second
        Assert.Equal(grid, player.Get<Support>().Supporter);
        scene.Tick(15); // over half a second
        Assert.False(player.Get<Support>().HasSupporter);
    }

    [Fact]
    public void JumpingStraightUpKeepsTheSupport()
    {
        var (scene, grid, player) = StandingOnPlatform();
        using var _ = scene;
        player.Get<Engine.Input.PlayerInput>() = new Engine.Input.PlayerInput { Pressed = Engine.Input.PlayerButtons.Up };
        scene.Tick();
        player.Get<Engine.Input.PlayerInput>() = default;
        for (int i = 0; i < 60; i++)
        {
            scene.Tick();
            Assert.Equal(grid, player.Get<Support>().Supporter);
        }
    }

    [Fact]
    public void TheSupportIsReleasedAtOnceWhenItNoLongerExists()
    {
        var (scene, grid, player) = StandingOnPlatform();
        using var _ = scene;
        player.Get<CharacterControllerComponent>().Character.TeleportTo(new Vector3(0, 60, 0)); // airborne above it
        scene.Tick();
        Assert.Equal(grid, player.Get<Support>().Supporter);
        Hierarchy.DestroyRecursive(grid);
        scene.Tick();
        Assert.False(player.Get<Support>().HasSupporter);
    }

    [Fact]
    public void SwitchingSupportTakesOneTick()
    {
        var (scene, grid, player) = StandingOnPlatform();
        using var _ = scene;
        var other = scene.SpawnPlatform(new Vector3(40, 50, 0));
        scene.Tick(2);
        // Onto the other platform, just above its deck.
        player.Get<CharacterControllerComponent>().Character.TeleportTo(new Vector3(40, 51.2f, 0));
        Assert.True(scene.TickUntil(() => player.Get<Support>().Supporter == other, 60));
    }

    [Fact]
    public void TheViewTurnsWithTheSupport()
    {
        var (scene, grid, player) = StandingOnPlatform();
        using var _ = scene;
        float yawBefore = player.Get<MouseLookComponent>().Yaw;
        // Turn the (kinematic) platform a little each tick.
        var body = grid.Get<PhysicsBodyComponent>().Body;
        for (int i = 0; i < 30; i++)
        {
            var (p, q) = scene.Physics.GetBodyPose(body);
            scene.Physics.SetBodyPose(body, p, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.01f) * q);
            scene.Tick();
        }
        float turned = player.Get<MouseLookComponent>().Yaw - yawBefore;
        Assert.InRange(turned, 0.25f, 0.31f);
        // The last tick's turn is kept, for drawing the view turning with the drawn ship.
        Assert.Equal(0.01f, player.Get<MouseLookComponent>().TurnYaw, 3);
    }

    [Fact]
    public void FreeFlyHasNoSupport()
    {
        var (scene, grid, player) = StandingOnPlatform();
        using var _ = scene;
        Players.SetFreeFlying(player, true);
        scene.Tick();
        Assert.False(player.Get<Support>().HasSupporter);
    }
}
