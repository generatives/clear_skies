using ClearSkies.Engine.ECS;
using DefaultEcs;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.Voxels;

/// <summary>Helpers for spawning dynamic voxel grids.</summary>
public static class DynamicGridFactory
{
    /// <summary>
    /// Spawns a grid containing a single block at local (0,0,0) whose centre is placed at
    /// <paramref name="spawnWorld"/>. The grid is registered with both the light and mesh systems
    /// (so its interior BFS runs and its chunks mesh); its body is created next frame by
    /// <see cref="GridShapeSystem"/>.
    /// </summary>
    public static DynamicGrid SpawnSingleBlock(
        World world, LightSystem lightSystem, ChunkMeshSystem meshSystem,
        PhysVec spawnWorld, BlockId block)
    {
        var grid = new DynamicGrid(world, spawnWorld);
        grid.SetBlock(0, 0, 0, block);
        lightSystem.RegisterVolume(grid);
        meshSystem.RegisterVolume(grid);
        return grid;
    }
}
