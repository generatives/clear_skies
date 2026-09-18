using System.Collections.Generic;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Physics;
using DefaultEcs;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.Voxels;

/// <summary>Helpers for spawning dynamic voxel grids.</summary>
public static class DynamicGridFactory
{
    /// <summary>
    /// Spawns a grid from an arbitrary set of grid-local voxels, centred at <paramref name="spawnWorld"/>.
    /// Registers the grid with the mesh system (so its chunks mesh; GPU lighting and body creation follow
    /// automatically next frame via GpuLightSystem/GridShapeSystem) and marks it the Selected Grid.
    /// </summary>
    public static DynamicGrid SpawnFromVoxels(
        World world, ChunkMeshSystem meshSystem, GridSelection selection,
        PhysVec spawnWorld, IEnumerable<(int X, int Y, int Z, BlockId Id, Facing Facing)> voxels)
    {
        var grid = new DynamicGrid(world, spawnWorld);
        foreach (var (x, y, z, id, facing) in voxels)
        {
            if (id == BlockId.Air) continue; // defensive; saved files shouldn't contain air entries
            grid.SetBlock(x, y, z, id, facing);
        }
        meshSystem.RegisterVolume(grid);
        selection.Select(grid.Root);
        return grid;
    }

    /// <summary>Spawns a grid containing a single block at local (0,0,0) whose centre is placed at
    /// <paramref name="spawnWorld"/>.</summary>
    public static DynamicGrid SpawnSingleBlock(
        World world, ChunkMeshSystem meshSystem, GridSelection selection,
        PhysVec spawnWorld, BlockId block, Facing facing = Facing.Up)
        => SpawnFromVoxels(world, meshSystem, selection, spawnWorld, new[] { (0, 0, 0, block, facing) });

    /// <summary>
    /// Tears a grid down: unregisters it from meshing, removes its physics body and shape (if a body
    /// was created), releases its GPU lighting resources, disposes every chunk mesh/entity, and finally
    /// disposes the root entity (which also drops any <see cref="SelectedGridComponent"/> it carried).
    /// After this call <paramref name="grid"/> must not be used again.
    /// </summary>
    public static void Despawn(PhysicsWorld physics, ChunkMeshSystem meshSystem, DynamicGrid grid)
    {
        meshSystem.UnregisterVolume(grid);

        if (grid.BodyCreated)
        {
            var shape = physics.GetBodyShape(grid.Body);
            physics.RemoveBody(grid.Body);
            physics.RemoveCompound(shape);
        }

        grid.VolumeGpu?.Dispose();

        foreach (var (_, entry) in grid.All)
        {
            entry.Mesh?.Dispose();
            if (entry.Entity.IsAlive) entry.Entity.Dispose();
        }

        if (grid.Root.IsAlive) grid.Root.Dispose();
    }
}
