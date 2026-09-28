using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

public class DescriptionTests
{
    /// <summary>Describes <paramref name="entity"/> now and returns its description.</summary>
    internal static Description DescribeNow(HeadlessScene scene, Entity entity)
    {
        Description? got = null;
        void OnDescribed(Description d) { if (d.Entity == entity) got = d; }
        scene.Commands.Descriptions.Described += OnDescribed;
        DescribeRequest.Request(entity, DescribePurpose.Hash);
        scene.Commands.DescribeRequested();
        scene.Commands.Descriptions.Described -= OnDescribed;
        return got ?? throw new InvalidOperationException("Nothing described it.");
    }

    /// <summary>Applies a description in <paramref name="scene"/>, as a spawn event from the host.</summary>
    internal static void Spawn(HeadlessScene scene, Description d, uint eventNumber = 1)
    {
        scene.Commands.ReceiveEvent(new EventMeta(PeerId.Host, 1, PeerId.Host, d.Id, eventNumber, 0), d.HandlerId, d.Payload);
        scene.Commands.Update(0);
    }

    private static Entity Ship(HeadlessScene scene)
    {
        var voxels = new List<GridVoxel>();
        for (int x = 0; x < 5; x++) for (int z = 0; z < 4; z++) voxels.Add(new(x, 0, z, BlockId.Wood, BlockOrientation.Upright));
        voxels.Add(new(1, 1, 1, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.North)));
        voxels.Add(new(3, 1, 1, BlockId.SteeringWheel, BlockOrientation.From(Direction.Up, Direction.South)));
        voxels.Add(new(40, 0, 0, BlockId.Fan, BlockOrientation.From(Direction.East, Direction.Up))); // a second chunk
        var grid = scene.SpawnGrid(GridDescription.FromVoxels(new Vector3(10, 80, -20), voxels));
        var v = grid.Get<ChunkGrid>().Volume;
        grid.Set(new ShipControls { Forward = 0.35f, Turn = -0.4f });
        return grid;
    }

    [Fact]
    public void DescribingAGridAndSpawningFromItGivesTheSameHash()
    {
        using var a = new HeadlessScene();
        var grid = Ship(a);
        a.Commands.Send(new SetGridLocked { Grid = grid.Get<EntityId>(), Locked = false });
        a.Tick(20); // it falls and gets a body and velocity
        var d = DescribeNow(a, grid);

        using var b = new HeadlessScene();
        Spawn(b, d);
        var copy = b.Registry.Find(d.Id)!.Value;
        Assert.Equal(d.Hash, DescribeNow(b, copy).Hash);
        Assert.Equal(new ShipControls { Forward = 0.35f, Turn = -0.4f }, copy.Get<ShipControls>());
        Assert.False(copy.Get<DynamicGrid>().Locked);
    }

    [Fact]
    public void ASpawnedGridKeepsItsPlaceOnceItHasABody()
    {
        using var a = new HeadlessScene();
        var grid = Ship(a);
        a.Tick(3);
        var body = grid.Get<PhysicsBodyComponent>().Body; // turned, so its centre of mass is off its block origin
        a.Physics.SetBodyPose(body, a.Physics.GetBodyPose(body).position, Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 0.7f));
        a.Tick();
        var d = DescribeNow(a, grid);
        using var b = new HeadlessScene();
        Spawn(b, d);
        b.Tick(3);
        var copy = b.Registry.Find(d.Id)!.Value;
        Assert.True(copy.Has<PhysicsBodyComponent>());
        var (p, _) = b.Physics.GetBodyPose(copy.Get<PhysicsBodyComponent>().Body);
        var (original, _) = a.Physics.GetBodyPose(grid.Get<PhysicsBodyComponent>().Body);
        Assert.True(Vector3.Distance(p, original) < 1e-3f);
    }

    [Fact]
    public void DescribingAPlayerAndSpawningFromItGivesTheSameHash()
    {
        using var a = new HeadlessScene();
        var player = a.SpawnLocalPlayer(new Vector3(3, 70, 4), freeFly: true);
        player.Get<MouseLookComponent>().Yaw = 1.25f;
        var d = DescribeNow(a, player);
        using var b = new HeadlessScene();
        Spawn(b, d);
        var copy = b.Registry.Find(d.Id)!.Value;
        Assert.Equal(d.Hash, DescribeNow(b, copy).Hash);
        Assert.Equal("test", copy.Get<Player>().Name);
    }

    [Fact]
    public void ASpawnReceivedTwiceIsHarmless()
    {
        using var a = new HeadlessScene();
        var d = DescribeNow(a, Ship(a));
        using var b = new HeadlessScene();
        Spawn(b, d, eventNumber: 1);
        Spawn(b, d, eventNumber: 2); // a resync or repeated join: a new event with the same contents
        var grids = b.World.GetEntities().With<DynamicGrid>().AsEnumerable().ToList();
        Assert.Single(grids);
        Assert.Equal(d.Hash, DescribeNow(b, grids[0]).Hash);
        Assert.Equal(1, grids[0].Get<ChunkGrid>().Volume.All.Count(c => c.Key == new ChunkPosition(1, 0, 0)));
    }

    [Fact]
    public void OverwritingInPlaceReplacesBlocksAndKeepsTheEntity()
    {
        using var scene = new HeadlessScene();
        var grid = Ship(scene);
        var before = DescribeNow(scene, grid);
        var v = grid.Get<ChunkGrid>().Volume;
        v.SetBlock(2, 5, 2, BlockId.Stone); // diverge
        Spawn(scene, before, eventNumber: 5);
        Assert.True(grid.IsAlive);
        Assert.Equal(BlockId.Air, v.GetBlock(2, 5, 2));
        Assert.Equal(before.Hash, DescribeNow(scene, grid).Hash);
    }

    [Fact]
    public void ANewSpawnGetsAnIdFromTheAuthorityAndIsSelected()
    {
        using var scene = new HeadlessScene();
        scene.Commands.Send(new SpawnGrid
        {
            Grid = GridDescription.FromVoxels(new Vector3(0, 50, 0), new[] { new GridVoxel(0, 0, 0, BlockId.Stone, BlockOrientation.Upright) }),
            Select = true,
        });
        scene.Tick();
        var grid = scene.World.GetEntities().With<DynamicGrid>().AsEnumerable().Single();
        Assert.True(grid.Get<EntityId>() is { Value: >= EntityRegistry.FirstFreeId });
        Assert.True(grid.Get<NetOwner>().IsLocal);
        Assert.True(grid.Has<SelectedGridComponent>());
        Assert.True(grid.Has<OwnPresence>() && grid.Has<Engine.Physics.Support.Supportable>());
    }

    [Fact]
    public void DespawnRemovesTheEntityAndItsChildren()
    {
        using var scene = new HeadlessScene();
        var grid = Ship(scene);
        var chunks = grid.Get<ChunkGrid>().Volume.All.Select(c => c.Value.Entity).ToList();
        scene.Commands.Send(new DespawnEntity { Entity = grid.Get<EntityId>() });
        scene.Tick();
        Assert.False(grid.IsAlive);
        Assert.All(chunks, c => Assert.False(c.IsAlive));
    }

    [Fact]
    public void DespawningAPlayerRemovesItsCharacterBody()
    {
        using var scene = new HeadlessScene();
        var player = scene.SpawnLocalPlayer(new Vector3(0, 70, 0));
        scene.Tick();
        int bodies = scene.Physics.Simulation.Bodies.ActiveSet.Count;
        scene.Commands.Send(new DespawnEntity { Entity = player.Get<EntityId>() });
        scene.Tick(2);
        Assert.False(player.IsAlive);
        Assert.Equal(bodies - 1, scene.Physics.Simulation.Bodies.ActiveSet.Count);
    }

    [Fact]
    public void BreakingAGridsLastBlockSendsADespawn()
    {
        using var scene = new HeadlessScene();
        var grid = scene.SpawnGrid(GridDescription.FromVoxels(new Vector3(0, 50, 0), new[] { new GridVoxel(0, 0, 0, BlockId.Stone, BlockOrientation.Upright) }));
        var player = scene.SpawnLocalPlayer(new Vector3(0, 52, 0), freeFly: true);
        var despawns = 0;
        scene.Commands.Applied += (h, _, _) => { if (h.Id == CommandIds.DespawnEntity) despawns++; };
        scene.Commands.Send(new EditVoxels { Volume = grid.Get<EntityId>(), Editor = player.Get<EntityId>(),
            Ops = new[] { VoxelOp.SetBlock(new(0, 0, 0), BlockId.Air, BlockOrientation.Upright) } });
        scene.Tick();
        Assert.False(grid.IsAlive);
        Assert.Equal(1, despawns);
    }

    [Fact]
    public void RequestsMergeAndAreRemovedAfterDescribing()
    {
        using var scene = new HeadlessScene();
        var grid = Ship(scene);
        DescribeRequest.Request(grid, DescribePurpose.Send, PeerSet.Of(new PeerId(2)));
        DescribeRequest.Request(grid, DescribePurpose.Store, PeerSet.Of(new PeerId(3)));
        var req = grid.Get<DescribeRequest>();
        Assert.Equal(DescribePurpose.Send | DescribePurpose.Store, req.Purpose);
        Assert.True(req.SendTo.Contains(new PeerId(2)) && req.SendTo.Contains(new PeerId(3)));
        var seen = new List<Description>();
        scene.Commands.Descriptions.Described += seen.Add;
        scene.Tick();
        Assert.False(grid.Has<DescribeRequest>());
        Assert.Equal(req, Assert.Single(seen).Request);
    }

    [Fact]
    public void GridsAreDescribedBeforePlayers()
    {
        using var scene = new HeadlessScene();
        var player = scene.SpawnLocalPlayer(new Vector3(0, 70, 0));
        var grid = Ship(scene);
        DescribeRequest.Request(player, DescribePurpose.Send);
        DescribeRequest.Request(grid, DescribePurpose.Send);
        var order = new List<Entity>();
        scene.Commands.Descriptions.Described += d => order.Add(d.Entity);
        scene.Commands.DescribeRequested();
        Assert.Equal(new[] { grid, player }, order);
    }

    [Fact]
    public void GridDataRoundTripsThroughAStream()
    {
        var voxels = new List<GridVoxel> { new(-3, 4, 5, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.East)), new(0, 0, 0, BlockId.Stone, BlockOrientation.Upright) };
        using var ms = new MemoryStream();
        GridSerializer.Write(ms, voxels);
        ms.Position = 0;
        Assert.Equal(voxels, GridSerializer.Read(ms));
    }

    [Fact]
    public void AGridFileHoldsTheWholeDescription()
    {
        using var scene = new HeadlessScene();
        var grid = Ship(scene); // with ship-control settings
        var d = DynamicGridFactory.Describe(grid, scene.Physics);
        string path = Path.Combine(Path.GetTempPath(), $"ship-{Guid.NewGuid():N}.grid");
        try
        {
            d.SaveFile(path);
            var loaded = GridDescription.LoadFile(path);
            Assert.Equal(d.Voxels, loaded.Voxels);
            Assert.Equal(d.Controls, loaded.Controls);
            Assert.Equal(d.Locked, loaded.Locked);
            Assert.Equal(d.Body, loaded.Body);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnOldGridFileIsRefusedWithAReason()
    {
        string path = Path.Combine(Path.GetTempPath(), $"old-{Guid.NewGuid():N}.grid");
        try
        {
            using (var fs = File.Create(path)) // voxels alone, as .grid files were
                GridSerializer.Write(fs, new[] { new GridVoxel(0, 0, 0, BlockId.Stone, BlockOrientation.Upright) });
            var e = Assert.Throws<InvalidDataException>(() => GridDescription.LoadFile(path));
            Assert.Contains("older build", e.Message);
        }
        finally { File.Delete(path); }
    }
}
