using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;
using Xunit;
using static ClearSkies.Tests.HeadlessStreamingTests;

namespace ClearSkies.Tests;

/// <summary>Edits to the streamed world reaching chunks that aren't loaded: they never make a chunk with nothing else in
/// it where terrain hasn't loaded yet.</summary>
public class TerrainEditTests
{
    private const int S = ChunkData.Size;

    private static Vector3D<int> Cell(ChunkPosition p, int x = 5, int y = 5, int z = 5) => new(p.X * S + x, p.Y * S + y, p.Z * S + z);

    [Fact]
    public void AnEditWhereTerrainHasNotLoadedLeavesItToLoadWhole()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var load = Streaming(scene, db);
        var player = Interest(scene, 16, 16, View(64));
        Settle(load);

        // Ground and sky far off, where nothing is streamed: nothing is known there.
        var ground = new ChunkPosition(100, -1, 100);
        var sky = new ChunkPosition(100, 3, 100);
        Assert.False(scene.WorldVolume.IsEditable(ground));
        Assert.False(scene.WorldVolume.IsEditable(sky));
        var dig = Cell(ground);
        scene.WorldVolume.FillBox(dig, dig, BlockId.Air, BlockOrientation.Upright);
        var place = Cell(sky);
        scene.WorldVolume.SetBlock(place.X, place.Y, place.Z, BlockId.Wood, BlockOrientation.Upright);
        Assert.False(scene.WorldVolume.IsLoaded(ground));
        Assert.False(scene.WorldVolume.IsLoaded(sky));

        // Going there, the ground loads whole (the edit is the store's to have: there's no Host here to keep it).
        player.Get<Transform>().Position = new Vector3D<float>(100 * S + 16, 10, 100 * S + 16);
        Settle(load);
        Assert.Equal(BlockId.Stone, scene.WorldVolume.GetBlock(dig.X, dig.Y, dig.Z));
        Assert.Equal(BlockId.Stone, scene.WorldVolume.GetBlock(dig.X + 1, dig.Y, dig.Z));
    }

    [Fact]
    public void AnEditWhereThereIsKnownToBeNothingMakesTheChunk()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var load = Streaming(scene, db);
        Interest(scene, 16, 16, View(64));
        Settle(load);

        var sky = new ChunkPosition(0, 2, 0); // above the flat ground, within the view
        Assert.False(scene.WorldVolume.IsLoaded(sky));
        Assert.True(scene.WorldVolume.IsEditable(sky));
        var place = Cell(sky);
        scene.WorldVolume.SetBlock(place.X, place.Y, place.Z, BlockId.Wood, BlockOrientation.Upright);
        Assert.Equal(BlockId.Wood, scene.WorldVolume.GetBlock(place.X, place.Y, place.Z));
    }

    [Fact]
    public void AChunkEditedWhileItsColumnLoadsLoadsAgainFromTheStore()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var load = Streaming(scene, db);
        Interest(scene, 16, 16, View(64));
        load.Update(1 / 60f); // the nearest columns are dispatched, and haven't been added yet
        Assert.NotEqual(0, load.ColumnsInFlight);

        // Meanwhile the chunk under the interest is edited elsewhere, and the store has it as edited (all wood).
        var under = new ChunkPosition(0, -1, 0);
        Assert.False(scene.WorldVolume.IsEditable(under));
        var edited = new ChunkData();
        for (int z = 0; z < S; z++) for (int y = 0; y < S; y++) for (int x = 0; x < S; x++) edited.Set(x, y, z, BlockId.Wood);
        new SavedChunkStore(db).Save(under, edited);
        var dig = Cell(under);
        scene.WorldVolume.FillBox(dig, dig, BlockId.Air, BlockOrientation.Upright);

        Settle(load);
        Assert.Equal(BlockId.Wood, scene.WorldVolume.GetBlock(dig.X, dig.Y, dig.Z));
        Assert.Equal(BlockId.Stone, scene.WorldVolume.GetBlock(S + 5, -5, 5)); // the next column's, as generated
    }

    [Fact]
    public void TheAuthorityTurnsDownAnEditToTerrainItHasNotLoaded()
    {
        using var scene = new HeadlessScene();
        using var db = SaveDatabase.InMemory();
        var load = Streaming(scene, db);
        var player = scene.SpawnLocalPlayer(new Vector3(16, 5, 16), freeFly: true);
        scene.Tick(); // its terrain interest, but nothing streamed yet
        var edit = new EditVoxels
        {
            Volume = EntityRegistry.WorldVolume, Editor = player.Get<EntityId>(),
            Ops = new[] { VoxelOp.SetBlock(new(16, -2, 16), BlockId.Air, BlockOrientation.Upright) },
        };
        long rejected = scene.Commands.Stats.Rejected;
        scene.Commands.Send(edit);
        scene.Tick();
        Assert.Equal(rejected + 1, scene.Commands.Stats.Rejected);
        Assert.False(scene.WorldVolume.IsLoaded(new ChunkPosition(0, -1, 0)));

        Settle(load);
        scene.Commands.Send(edit);
        scene.Tick();
        Assert.Equal(rejected + 1, scene.Commands.Stats.Rejected);
        Assert.Equal(BlockId.Air, scene.WorldVolume.GetBlock(16, -2, 16));
        Assert.Equal(BlockId.Stone, scene.WorldVolume.GetBlock(16, -3, 16));
    }
}
