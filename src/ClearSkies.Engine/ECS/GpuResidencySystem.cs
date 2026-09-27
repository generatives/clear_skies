using ClearSkies.Engine.Core;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Gui;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Keeps the shared <see cref="GridStore"/> in sync with loaded chunks each PreRender tick:
/// <list type="number">
/// <item>releases the storage of ships that were despawned, and of chunks that unloaded, oldest first for up to
/// <see cref="ReleaseBudgetMs"/> per frame (flying unloads a ring of chunks at once; the rest wait). A chunk is
/// released at once when one being uploaded needs its place: the same position, or its cell of the world index;</item>
/// <item>uploads new and edited chunks' occupancy (and with it, which bricks get light storage), up to
/// <see cref="UploadsPerFrame"/> per frame.</item>
/// </list>
/// Nothing is ever reallocated as the camera moves: the world's chunk table is toroidal, and every chunk takes and
/// returns fixed-size slots.
/// </summary>
public sealed class GpuResidencySystem : ISystem, IDebugUiSystem
{
    // CPU time of each step, and how many chunks went each way, for the debug panel.
    private const int RemovedStep = 0, ChooseStep = 1, UploadStep = 2;
    private readonly StepTimer _steps = new("Releasing removed chunks and grids", "Choosing chunks to upload", "Uploading") { Owner = "GPU residency" };
    private int _released, _uploaded, _mostReleased;

    public string DebugName => "GPU residency";

    public void DrawDebugUi()
    {
        ImGui.Text($"Last frame: released {_released} chunks, uploaded {_uploaded} (at most {UploadsPerFrame}); " +
                   $"most released in a frame: {_mostReleased}; waiting to be released: {_waitingRemoval.Count}");
        _steps.Draw();
    }

    private const int UploadsPerFrame = 16;

    /// <summary>Main-thread time uploading per frame: past it, the rest of the frame's chunks wait for the next (the
    /// nearest still go first).</summary>
    private const double UploadBudgetMs = 2.0;

    private readonly GridStore   _store;
    private readonly EntitySet   _needsGpuUpload;
    private readonly EntitySet   _cameras;
    private readonly List<Entity> _nearest = new();
    private readonly List<ChunkVolume> _removedGrids = new();
    // Chunks that unloaded, waiting to be released, oldest first from _removedAt; the map says which are still waiting
    // (one released early for an upload is taken out of it and skipped when its turn comes), with the light bricks
    // each held when it unloaded (see GridStore.WorldBricksReleasing).
    private readonly List<(GridHandle Grid, ChunkPosition Pos)> _removedChunks = new();
    private readonly Dictionary<(GridHandle, ChunkPosition), int> _waitingRemoval = new();
    private int _removedAt;

    /// <summary>Main-thread time releasing unloaded chunks per frame.</summary>
    private const double ReleaseBudgetMs = 1.5;

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
            var key = (entry.Volume.Gpu, entry.Position);
            if (_waitingRemoval.ContainsKey(key)) return;
            int bricks = key.Gpu.IsWorld ? _store.BricksOf(key.Gpu, key.Position) : 0;
            _waitingRemoval[key] = bricks;
            _removedChunks.Add(key);
            if (key.Gpu.IsWorld && key.Gpu.Chunks.ContainsKey(key.Position))
            {
                _store.WorldChunksReleasing++;
                _store.WorldBricksReleasing += bricks;
            }
        }
    }

    private readonly EntitySet _unrenderedChunks, _unrenderedGrids;

    /// <summary>Releases a chunk if it is waiting to be.</summary>
    private bool Release((GridHandle Grid, ChunkPosition Pos) key)
    {
        if (!_waitingRemoval.Remove(key, out int bricks)) return false;
        if (key.Grid.IsWorld && key.Grid.Chunks.ContainsKey(key.Pos))
        {
            _store.WorldChunksReleasing--;
            _store.WorldBricksReleasing -= bricks;
        }
        _store.RemoveChunk(key.Grid, key.Pos);
        return true;
    }

    public void Update(float dt)
    {
        _steps.Start();
        _uploaded = 0;
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

        _released = 0;
        while (_removedAt < _removedChunks.Count && (_released == 0 || _steps.SinceLap() < ReleaseBudgetMs))
        {
            var key = _removedChunks[_removedAt++];
            if (Release(key)) _released++;
        }
        if (_removedAt == _removedChunks.Count) { _removedChunks.Clear(); _removedAt = 0; }
        _mostReleased = System.Math.Max(_mostReleased, _released);
        _steps.Lap(RemovedStep);

        // Closest to the camera first (see NearestChunks).
        NearestChunks.Select(_needsGpuUpload, _cameras, UploadsPerFrame, _nearest);
        _steps.Lap(ChooseStep);
        foreach (var entity in _nearest)
        {
            if (_uploaded > 0 && _steps.SinceLap() > UploadBudgetMs) break;
            var chunk = entity.Get<Chunk>();
            var entry = chunk.Entry;
            var grid = entry.Volume.Gpu;
            var pos = entry.Position;
            // Its position's (or its world index cell's) old chunk, if that is still waiting to be released, goes first.
            Release((grid, pos));
            if (_store.WorldCellHolder(grid, pos, out var holder)) Release((grid, holder));
            _store.UploadChunk(grid, pos, entry);
            entity.Remove<NeedsGpuUploadFlag>();
            _uploaded++;
        }
        _steps.Lap(UploadStep);
    }
}
