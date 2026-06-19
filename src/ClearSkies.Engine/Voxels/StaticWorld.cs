using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Generation;
using DefaultEcs;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// The streamed, world-anchored voxel terrain. Chunks are placed at their world origin and never
/// rotate; <see cref="ECS.ChunkLoadSystem"/> loads and unloads them around the camera. Block access
/// here is in world space, which for the static world is identical to volume-local space.
/// </summary>
public sealed class StaticWorld : ChunkVolume
{
    public StaticWorld(World world) : base(world) { }

    public BlockId GetBlockWorld(int wx, int wy, int wz) => GetBlock(wx, wy, wz);
    public void    SetBlockWorld(int wx, int wy, int wz, BlockId id) => SetBlock(wx, wy, wz, id);

    public void Load(ChunkPosition pos, IWorldGenerator generator)
    {
        if (IsLoaded(pos)) return;

        var data = new ChunkData();
        generator.Generate(data, pos);
        data.IsDirty = false;

        AddChunk(pos, data);
    }

    public void Unload(ChunkPosition pos)
    {
        var entry = GetEntry(pos);
        if (entry is null) return;

        if (entry.Mesh is not null)
        {
            if (entry.Entity.Has<MeshRenderer>())
                entry.Entity.Remove<MeshRenderer>();
            entry.Mesh.Dispose();
        }
        entry.Gpu?.Dispose();
        if (entry.Entity.IsAlive)
            entry.Entity.Dispose();

        _chunks.Remove(pos);
        MarkNeighboursDirty(pos);
    }
}
