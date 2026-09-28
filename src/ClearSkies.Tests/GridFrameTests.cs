using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>A grid's Transform is its block space; its body sits at the centre of mass inside it.</summary>
public class GridFrameTests
{
    private static Vector3 V(Vector3D<float> v) => new(v.X, v.Y, v.Z);

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    private static Vector3 BodyPosition(HeadlessScene scene, DefaultEcs.Entity grid)
        => scene.Physics.GetBodyPose(grid.Get<PhysicsBodyComponent>().Body).position;

    [Fact]
    public void ASpawnedGridIsCentredWhereItWasSpawnedWithItsBodyAtItsCentreOfMass()
    {
        using var scene = new HeadlessScene();
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0)); // 8×1×8 stone: voxels span (0..8, 0..1, 0..8)
        scene.Tick(2);

        var volume = grid.Get<ChunkGrid>().Volume;
        ref readonly var t = ref grid.Get<Transform>();
        Near(new Vector3(-4, 49.5f, -4), V(t.Position)); // voxel (0,0,0)'s corner
        Near(new Vector3(0, 50, 0), V(volume.VoxelToWorld(t, new Vector3D<float>(4, 0.5f, 4))));
        Near(new Vector3(4, 0.5f, 4), V(volume.Pivot));
        Near(new Vector3(0, 50, 0), BodyPosition(scene, grid));
    }

    [Fact]
    public void EditingMovesTheBodyButNotTheGridOrItsBlocks()
    {
        using var scene = new HeadlessScene();
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0));
        scene.Tick(2);
        var volume = grid.Get<ChunkGrid>().Volume;
        var before = grid.Get<Transform>();
        var chunk = volume.All.First().Value.Entity;
        var chunkBefore = chunk.Get<Transform>().Position;
        var pivotBefore = volume.Pivot;

        for (int y = 1; y <= 4; y++) volume.SetBlock(7, y, 7, BlockId.Stone);
        scene.Tick(2);

        ref readonly var after = ref grid.Get<Transform>();
        Near(V(before.Position), V(after.Position));
        Near(V(chunkBefore), V(chunk.Get<Transform>().Position));
        Assert.True(volume.Pivot.X > pivotBefore.X && volume.Pivot.Y > pivotBefore.Y && volume.Pivot.Z > pivotBefore.Z,
                    $"the centre of mass should move towards the new column: {pivotBefore} → {volume.Pivot}");
        Near(V(after.Position) + V(volume.Pivot), BodyPosition(scene, grid));
    }

    [Fact]
    public void ATurnedGridMapsVoxelsThroughItsRotationAndKeepsItsBodyAtItsCentreOfMass()
    {
        using var scene = new HeadlessScene();
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0));
        scene.Tick(2);
        var volume = grid.Get<ChunkGrid>().Volume;
        var body = grid.Get<PhysicsBodyComponent>().Body;
        var turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 0.7f);
        scene.Physics.SetBodyPose(body, new Vector3(10, 60, -5), turn);
        scene.Tick();

        ref readonly var t = ref grid.Get<Transform>();
        // The body (the centre of mass) is where the pivot lands.
        Near(new Vector3(10, 60, -5), V(volume.VoxelToWorld(t, volume.Pivot)));
        Near(V(t.Position), V(volume.VoxelToWorld(t, Vector3D<float>.Zero)));
        var voxel = new Vector3D<float>(3.25f, 0.5f, 6.75f);
        Near(V(voxel), V(volume.WorldToVoxel(t, volume.VoxelToWorld(t, voxel))));

        // An edit while turned still leaves the blocks where they were.
        var corner = V(volume.VoxelToWorld(t, Vector3D<float>.Zero));
        volume.SetBlock(0, 1, 0, BlockId.Stone);
        scene.Tick(2);
        Near(corner, V(volume.VoxelToWorld(grid.Get<Transform>(), Vector3D<float>.Zero)));
        Near(V(volume.VoxelToWorld(grid.Get<Transform>(), volume.Pivot)), BodyPosition(scene, grid));
    }
}
