using ClearSkies.Engine.ECS;
using DefaultEcs;
using Silk.NET.Maths;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.Voxels;

/// <summary>Helpers for spawning dynamic voxel grids.</summary>
public static class DynamicGridFactory
{
    /// <summary>
    /// Spawns a grid from an arbitrary set of grid-local voxels, centred at <paramref name="spawnWorld"/>.
    /// Its chunks mesh, light and get a body automatically (ChunkMeshSystem/GpuLightSystem/PhysicsBodySystem),
    /// and it becomes the Selected Grid.
    ///
    /// The grid's Transform starts at <paramref name="spawnWorld"/> with the centre of the voxels' bounding box as
    /// its pivot. PhysicsBodySystem then moves the pivot to the true centre of mass without moving the grid, so
    /// the grid stays exactly where it spawned (for a single block the two centres coincide).
    /// </summary>
    public static void SpawnFromVoxels(
        World world, GridSelection selection,
        PhysVec spawnWorld, IEnumerable<(int X, int Y, int Z, BlockId Id, Facing Facing)> voxels)
    {
        // Defensive; saved files shouldn't contain air entries.
        var solid = voxels.Where(v => v.Id != BlockId.Air).ToList();

        var entity = world.CreateEntity();
        entity.Set(new DynamicGrid());
        var t = Transform.Identity;
        t.Position = new Vector3D<float>(spawnWorld.X, spawnWorld.Y, spawnWorld.Z);
        entity.Set(t);

        var volume = new ChunkVolume(entity, world) { Pivot = BoundsCentre(solid) };
        entity.Set(new ChunkGrid() { Volume = volume });
        foreach (var (x, y, z, id, facing) in solid)
            volume.SetBlock(x, y, z, id, facing);
        selection.Select(entity);
    }

    /// <summary>Spawns a grid containing a single block at local (0,0,0) whose centre is placed at
    /// <paramref name="spawnWorld"/>.</summary>
    public static void SpawnSingleBlock(
        World world, GridSelection selection,
        PhysVec spawnWorld, BlockId block, Facing facing = Facing.Up)
        => SpawnFromVoxels(world, selection, spawnWorld, new[] { (0, 0, 0, block, facing) });

    /// <summary>Centre of the voxels' bounding box (each voxel spans [v, v+1]), or zero if there are none.</summary>
    private static Vector3D<float> BoundsCentre(List<(int X, int Y, int Z, BlockId Id, Facing Facing)> voxels)
    {
        if (voxels.Count == 0) return Vector3D<float>.Zero;
        int nx = int.MaxValue, ny = int.MaxValue, nz = int.MaxValue;
        int xx = int.MinValue, xy = int.MinValue, xz = int.MinValue;
        foreach (var (x, y, z, _, _) in voxels)
        {
            nx = System.Math.Min(nx, x); xx = System.Math.Max(xx, x);
            ny = System.Math.Min(ny, y); xy = System.Math.Max(xy, y);
            nz = System.Math.Min(nz, z); xz = System.Math.Max(xz, z);
        }
        return new Vector3D<float>(nx + xx + 1, ny + xy + 1, nz + xz + 1) * 0.5f;
    }
}
