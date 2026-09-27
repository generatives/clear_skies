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
    private readonly EntitySet   _cameras;
    private readonly List<Entity> _nearest = new();
    private readonly List<ChunkVolume> _removedGrids = new();
    private readonly List<(ChunkVolume, ChunkPosition)> _removedChunks = new();

    public GpuResidencySystem(World ecsWorld, ChunkVolume staticVolume, GridStore store)
    {
        _store       = store;
        _needsGpuUpload       = ecsWorld.GetEntities().With<Chunk>().With<NeedsGpuUploadFlag>().With<Rendered>().AsSet();
        _unrenderedChunks     = ecsWorld.GetEntities().With<Chunk>().WhenRemoved<Rendered>().AsSet();
        _unrenderedGrids      = ecsWorld.GetEntities().With<ChunkGrid>().With<DynamicGrid>().WhenRemoved<Rendered>().AsSet();
        _cameras              = ecsWorld.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
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

    private readonly EntitySet _unrenderedChunks, _unrenderedGrids;

    public void Update(float dt)
    {
        // No longer drawn: a chunk's storage and light bricks go, and it's uploaded afresh if drawn again; a grid's whole
        // GPU registration goes (re-registered on its first upload).
        foreach (var e in _unrenderedChunks.GetEntities().ToArray())
        {
            if (e.Has<Rendered>()) continue;
            var entry = e.Get<Chunk>().Entry;
            _store.RemoveChunk(entry.Volume.Gpu, entry.Position);
            e.Set<NeedsGpuUploadFlag>();
        }
        _unrenderedChunks.Complete();
        foreach (var e in _unrenderedGrids.GetEntities().ToArray())
            if (!e.Has<Rendered>()) _store.Unregister(e.Get<ChunkGrid>().Volume.Gpu);
        _unrenderedGrids.Complete();

        // Despawned ships: their root entity is gone from the set.
        foreach (var g in _removedGrids) { _store.Unregister(g.Gpu); }
        _removedGrids.Clear();

        foreach (var (volume, pos) in _removedChunks)
        {
            _store.RemoveChunk(volume.Gpu, pos);
        }
        _removedChunks.Clear();

        // Closest to the camera first (see NearestChunks).
        NearestChunks.Select(_needsGpuUpload, _cameras, UploadsPerFrame, _nearest);
        foreach (var entity in _nearest)
        {
            var chunk = entity.Get<Chunk>();
            var entry = chunk.Entry;
            var volume = entry.Volume;
            var pos = entry.Position;
            _store.UploadChunk(volume.Gpu, pos, entry);
            entity.Remove<NeedsGpuUploadFlag>();
        }
    }
}
