using ClearSkies.Engine.Core;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Gui;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Keeps the shared <see cref="GridStore"/> in sync with drawn chunks. The chunk pipeline's Light store stage (see
/// <see cref="LightStoreStage"/>) puts a drawn chunk's occupancy in it, and with it, which bricks get light storage
/// (<see cref="Upload"/>), and takes it out once the chunk's no longer drawn (<see cref="Hide"/>). Each PreRender tick
/// this releases what those left, the storage of ships that are no longer drawn or were despawned, and that of chunks
/// that unloaded, oldest first for up to <see cref="ReleaseBudgetMs"/> per frame (flying unloads a ring of chunks at
/// once; the rest wait). A chunk is released at once when one being uploaded needs its place: the same position, or
/// its cell of the world index. Nothing is ever reallocated as the camera moves: the world's chunk table is toroidal,
/// and every chunk takes and returns fixed-size slots.
/// </summary>
public sealed class GpuResidencySystem : ISystem, IDebugUiSystem
{
    // CPU time of each step, and how many chunks went each way, for the debug panel.
    private readonly StepTimer _steps = new("Releasing removed chunks and grids") { Owner = "GPU residency" };
    private int _released, _uploaded, _uploadedShown, _mostReleased;

    public string DebugName => "GPU residency";

    public void DrawDebugUi()
    {
        ImGui.Text($"Last frame: released {_released} chunks, uploaded {_uploadedShown}; " +
                   $"most released in a frame: {_mostReleased}; waiting to be released: {_waitingRemoval.Count}");
        _steps.Draw();
    }

    private readonly GridStore   _store;
    private readonly List<ChunkVolume> _removedGrids = new();
    // Chunks no longer drawn, taken out of the store at the next tick with the rest of the frame's removals (unless
    // drawn again first).
    private readonly HashSet<(GridHandle Grid, ChunkPosition Pos)> _hidden = new();
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
        _unrenderedGrids      = ecsWorld.GetEntities().With<ChunkGrid>().With<DynamicGrid>().WhenRemoved<Rendered>().AsSet();
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
            _hidden.Remove(key); // released in turn instead
            if (_waitingRemoval.ContainsKey(key)) return;
            int bricks = IsTheWorld(key.Gpu) ? _store.BricksOf(key.Gpu, key.Position) : 0;
            _waitingRemoval[key] = bricks;
            _removedChunks.Add(key);
            if (IsTheWorld(key.Gpu) && key.Gpu.Chunks.ContainsKey(key.Position))
            {
                _store.WorldChunksReleasing++;
                _store.WorldBricksReleasing += bricks;
            }
        }
    }

    private readonly EntitySet _unrenderedGrids;

    /// <summary>Whether a grid is the world itself, whose chunks count against its light budget (see
    /// GridStore.WorldChunksReleasing); a scaled static grid's don't.</summary>
    private static bool IsTheWorld(GridHandle g) => g.IsWorld && !g.IsLowResolutionCopy;

    /// <summary>Releases a chunk if it is waiting to be.</summary>
    private bool Release((GridHandle Grid, ChunkPosition Pos) key)
    {
        if (!_waitingRemoval.Remove(key, out int bricks)) return false;
        if (IsTheWorld(key.Grid) && key.Grid.Chunks.ContainsKey(key.Pos))
        {
            _store.WorldChunksReleasing--;
            _store.WorldBricksReleasing -= bricks;
        }
        _store.RemoveChunk(key.Grid, key.Pos);
        return true;
    }

    /// <summary>Puts a drawn chunk's occupancy in the store, new or edited (main thread).</summary>
    internal void Upload(ChunkEntry entry)
    {
        var grid = entry.Volume.Gpu;
        var pos = entry.Position;
        _hidden.Remove((grid, pos));
        if (entry.Volume.IsLowResolutionCopy) _store.RegisterStatic(grid, entry.Volume.Scale); // lit as a static grid
        // Its position's (or its world index cell's) old chunk, if that is still waiting to be released, goes first.
        Release((grid, pos));
        if (_store.WorldCellHolder(grid, pos, out var holder)) Release((grid, holder));
        _store.UploadChunk(grid, pos, entry);
        entry.Entity.Set<ChunkUploadedFlag>();
        _uploaded++;
    }

    /// <summary>Takes a chunk that's no longer drawn out of the store, at the next tick.</summary>
    internal void Hide(ChunkEntry entry)
    {
        if (!entry.Entity.IsAlive) return; // unloaded: released in turn (see OnEntityDisposed)
        entry.Entity.Remove<ChunkUploadedFlag>();
        _hidden.Add((entry.Volume.Gpu, entry.Position));
    }

    public void Update(float dt)
    {
        _steps.Start();
        _uploadedShown = _uploaded;
        _uploaded = 0;
        _store.BeginRemovals(); // the chunks next to those released are looked at once, at the end (see GridStore)
        // No longer drawn: a chunk's storage and light bricks go, and a grid's whole GPU registration (re-registered on
        // its first upload).
        foreach (var (grid, pos) in _hidden) _store.RemoveChunk(grid, pos);
        _hidden.Clear();
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
        _store.EndRemovals();
        _steps.Lap(0);
    }
}
