using ClearSkies.Engine.Core;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Gui;
using DefaultEcs;
using ImGuiNET;

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
                   $"most released in a frame: {_mostReleased}");
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
    private readonly List<(ChunkVolume, ChunkPosition)> _removedChunks = new();

    public GpuResidencySystem(World ecsWorld, ChunkVolume staticVolume, GridStore store)
    {
        _store       = store;
        _needsGpuUpload       = ecsWorld.GetEntities().With<Chunk>().With<NeedsGpuUploadFlag>().AsSet();
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

    public void Update(float dt)
    {
        _steps.Start();
        _uploaded = 0;
        // Despawned ships: their root entity is gone from the set.
        foreach (var g in _removedGrids) { _store.Unregister(g.Gpu); }
        _removedGrids.Clear();

        foreach (var (volume, pos) in _removedChunks)
        {
            _store.RemoveChunk(volume.Gpu, pos);
        }
        _released = _removedChunks.Count;
        _mostReleased = System.Math.Max(_mostReleased, _released);
        _removedChunks.Clear();
        _steps.Lap(RemovedStep);

        // Closest to the camera first (see NearestChunks).
        NearestChunks.Select(_needsGpuUpload, _cameras, UploadsPerFrame, _nearest);
        _steps.Lap(ChooseStep);
        foreach (var entity in _nearest)
        {
            if (_uploaded > 0 && _steps.SinceLap() > UploadBudgetMs) break;
            var chunk = entity.Get<Chunk>();
            var entry = chunk.Entry;
            var volume = entry.Volume;
            var pos = entry.Position;
            _store.UploadChunk(volume.Gpu, pos, entry);
            entity.Remove<NeedsGpuUploadFlag>();
            _uploaded++;
        }
        _steps.Lap(UploadStep);
    }
}
