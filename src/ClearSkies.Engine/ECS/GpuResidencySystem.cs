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
    private readonly ChunkVolume _staticWorld;
    private readonly EntitySet   _grids;
    private readonly HashSet<DynamicGrid> _known = new();
    private readonly List<DynamicGrid> _gone = new();

    public GpuResidencySystem(World ecsWorld, StaticWorld staticWorld, GridStore store)
    {
        _store       = store;
        _staticWorld = staticWorld;
        _grids       = ecsWorld.GetEntities().With<DynamicGridComponent>().AsSet();
        _store.Register(staticWorld.Gpu, isWorld: true);
    }

    public void Update(float dt)
    {
        // Despawned ships: their root entity is gone from the set.
        _gone.Clear();
        foreach (var g in _known)
        {
            bool alive = false;
            foreach (ref readonly Entity e in _grids.GetEntities())
                if (ReferenceEquals(e.Get<DynamicGridComponent>().Grid, g)) { alive = true; break; }
            if (!alive) _gone.Add(g);
        }
        foreach (var g in _gone) { _store.Unregister(g.Gpu); _known.Remove(g); }

        int budget = UploadsPerFrame;
        ProcessVolume(_staticWorld, ref budget);
        foreach (ref readonly Entity e in _grids.GetEntities())
        {
            var grid = e.Get<DynamicGridComponent>().Grid;
            _known.Add(grid);
            ProcessVolume(grid, ref budget);
        }
    }

    private void ProcessVolume(ChunkVolume vol, ref int budget)
    {
        // Removals first, so a chunk that loads onto the same wrapped table entry this frame finds it free.
        foreach (var pos in vol.RemovedChunks) _store.RemoveChunk(vol.Gpu, pos);
        vol.RemovedChunks.Clear();

        if (budget <= 0) return;
        foreach (var (pos, entry) in vol.All)
        {
            if (!entry.NeedsGpuUpload) continue;
            _store.UploadChunk(vol.Gpu, pos, entry);
            entry.NeedsGpuUpload = false;
            if (--budget <= 0) break;
        }
    }
}
