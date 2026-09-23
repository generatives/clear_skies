using ClearSkies.Engine.ECS;
using DefaultEcs;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.Voxels;

/// <summary>Helpers for spawning dynamic voxel grids.</summary>
public static class DynamicGridFactory
{
    /// <summary>
    /// Spawns a grid from an arbitrary set of grid-local voxels, centred at <paramref name="spawnWorld"/>.
    /// Registers the grid with the mesh system (so its chunks mesh; GPU lighting and body creation follow
    /// automatically next frame via GpuLightSystem/PhysicsBodySystem) and marks it the Selected Grid.
    /// </summary>
    public static DynamicGrid SpawnFromVoxels(
        World world, GridSelection selection,
        PhysVec spawnWorld, IEnumerable<(int X, int Y, int Z, BlockId Id, Facing Facing)> voxels)
    {
        var entity = world.CreateEntity();
        var grid = new DynamicGrid(spawnWorld);
        var volume = new ChunkVolume(entity, world);
        entity.Set(new ChunkGrid() { Volume = volume });
        foreach (var (x, y, z, id, facing) in voxels)
        {
            if (id == BlockId.Air) continue; // defensive; saved files shouldn't contain air entries
            volume.SetBlock(x, y, z, id, facing);
        }
        selection.Select(entity);
        return grid;
    }

    /// <summary>Spawns a grid containing a single block at local (0,0,0) whose centre is placed at
    /// <paramref name="spawnWorld"/>.</summary>
    public static DynamicGrid SpawnSingleBlock(
        World world, GridSelection selection,
        PhysVec spawnWorld, BlockId block, Facing facing = Facing.Up)
        => SpawnFromVoxels(world, selection, spawnWorld, new[] { (0, 0, 0, block, facing) });
}
