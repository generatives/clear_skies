using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>A low-resolution copy of the world (a ChunkVolume with a Scale above 1, see LowResolutionCopy): placed at its scale, and
/// invisible to everything but meshing, lighting and drawing.</summary>
public class ScaledVolumeTests
{
    private static ChunkVolume Scaled(HeadlessScene scene, int scale)
    {
        var root = scene.World.CreateEntity();
        var volume = ChunkVolume.CreateLowResolutionCopy(root, scene.World, scale);
        root.Set<Rendered>();
        return volume;
    }

    private static ChunkData Solid()
    {
        var data = new ChunkData();
        for (int z = 0; z < ChunkData.Size; z++)
        for (int x = 0; x < ChunkData.Size; x++)
            data.Set(x, 0, z, BlockId.Stone);
        return data;
    }

    [Fact]
    public void ItsChunksArePlacedAtItsScale()
    {
        using var scene = new HeadlessScene();
        var volume = Scaled(scene, 2);
        var entry = volume.AddChunk(new ChunkPosition(1, 0, -1), Solid());
        scene.Tick();

        var t = entry.Entity.Get<Transform>();
        Assert.Equal(new Vector3D<float>(64, 0, -64), t.Position);
        Assert.Equal(new Vector3D<float>(2), t.Scale);
        var root = volume.Root.Get<Transform>();
        Assert.Equal(new Vector3D<float>(10, 20, 30), volume.VoxelToWorld(root, new Vector3D<float>(5, 10, 15)));
        Assert.Equal(new Vector3D<float>(5, 10, 15), volume.WorldToVoxel(root, new Vector3D<float>(10, 20, 30)));
    }

    [Fact]
    public void ItsChunksGetNoCollidersAndLeaveTheWorldsAlone()
    {
        using var scene = new HeadlessScene();
        scene.SpawnLocalPlayer(new Vector3(16, 40, 16), freeFly: true);
        scene.WorldVolume.SetBlock(16, 32, 16, BlockId.Stone);
        var pos = new ChunkPosition(0, 1, 0);
        Assert.True(scene.TickUntil(() => scene.PhysicsBodies.HasCollider(pos)));

        // A scaled chunk at the same position comes and goes: the world's collider stays.
        var volume = Scaled(scene, 2);
        var entry = volume.AddChunk(pos, Solid());
        scene.Tick(5);
        Assert.False(entry.Entity.Has<NeedsRecollideFlag>());
        volume.RemoveChunk(pos);
        scene.Tick(5);
        Assert.True(scene.PhysicsBodies.HasCollider(pos));
    }

    [Fact]
    public void ItsChunksHoldNoBlockEntities()
    {
        using var scene = new HeadlessScene();
        var volume = Scaled(scene, 2);
        var data = new ChunkData();
        data.Set(3, 3, 3, BlockId.Lever);
        volume.AddChunk(new ChunkPosition(0, 0, 0), data);
        Assert.Empty(volume.BlockEntities);
    }

    [Fact]
    public void DroppingItsBlocksLeavesAllAirAndReleasesThem()
    {
        using var scene = new HeadlessScene();
        var volume = Scaled(scene, 2);
        var data = Solid();
        var entry = volume.AddChunk(new ChunkPosition(0, 0, 0), data);
        entry.DropData();
        Assert.True(entry.DataDropped);
        Assert.False(entry.Data.HasAnyNonAir());
        Assert.Equal(BlockId.Air, data.Get(0, 0, 0)); // its arrays went back to the pool
        volume.RemoveChunk(new ChunkPosition(0, 0, 0));
    }
}
