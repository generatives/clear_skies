using ClearSkies.Engine.Core;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Keeps the shared <see cref="GridStore"/> in sync with loaded chunks each PreRender tick:
/// <list type="number">
/// <item>releases the storage of chunks that unloaded and of ships that were despawned;</item>
/// <item>uploads new and edited chunks' occupancy (and with it, which bricks get light storage), up to
/// <see cref="UploadsPerFrame"/> per frame.</item>
/// </list>
/// Nothing is ever reallocated as the camera moves: the world's chunk table is toroidal, and every chunk takes and
/// returns fixed-size slots.
/// </summary>
public sealed class GpuResidencySystem : ISystem
{
    private const int UploadsPerFrame = 16;

    private readonly GridStore   _store;
    private readonly EntitySet   _needsGpuUpload;
    private readonly List<ChunkVolume> _removedGrids = new();
    private readonly List<(ChunkVolume, ChunkPosition)> _removedChunks = new();

    public GpuResidencySystem(World ecsWorld, ChunkVolume staticVolume, GridStore store)
    {
        _store       = store;
        _needsGpuUpload       = ecsWorld.GetEntities().With<Chunk>().With<NeedsGpuUploadFlag>().AsSet();
        _store.Register(staticVolume.Gpu, isWorld: true);

        ecsWorld.SubscribeEntityDisposed(OnEntityDisposed);
    }

    private void OnEntityDisposed(in Entity e)
    {
        if (e.Has<ChunkGrid>())
        {
            var grid = e.Get<ChunkGrid>().Volume;
            _removedGrids.Add(grid);
        }
        
        if (e.Has<Chunk>())
        {
            var chunk = e.Get<Chunk>();
            var entry = chunk.Entry;
            var volume = entry.Volume;
            var pos = entry.Position;
            _removedChunks.Add((volume, pos));
        }
    }

    public void Update(float dt)
    {
        // Despawned ships: their root entity is gone from the set.
        foreach (var g in _removedGrids) { _store.Unregister(g.Gpu); }
        _removedGrids.Clear();

        foreach (var (volume, pos) in _removedChunks)
        {
            _store.RemoveChunk(volume.Gpu, pos);
        }
        _removedChunks.Clear();

        int budget = UploadsPerFrame;
        foreach (var entity in _needsGpuUpload.GetEntities())
        {
            var chunk = entity.Get<Chunk>();
            var entry = chunk.Entry;
            var volume = entry.Volume;
            var pos = entry.Position;
            _store.UploadChunk(volume.Gpu, pos, entry);
            entity.Remove<NeedsGpuUploadFlag>();
            if (--budget <= 0) break;
        }
    }
}
