using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Phase 4.2: runs the GPU block-light flood for chunks whose blocks changed. For each dirty chunk
/// (with GPU residency and current opacity), it clears+injects lamp emission and floods. Block light is
/// static unless edited, so this is edit-driven (no per-frame re-flood) — the GPU analogue of "bake
/// once". Runs after <c>GpuResidencySystem</c> (opacity must be uploaded first) and processes the static
/// world plus every dynamic grid.
/// </summary>
public sealed class GpuLightSystem : ISystem, IDisposable
{
    private const int FloodsPerFrame = 4;

    private readonly ChunkVolume   _staticWorld;
    private readonly EntitySet     _grids;
    private readonly GpuLightFlood _flood;

    public GpuLightSystem(World world, ChunkVolume staticWorld, GpuContext ctx)
    {
        _staticWorld = staticWorld;
        _grids       = world.GetEntities().With<DynamicGridComponent>().AsSet();
        _flood       = new GpuLightFlood(ctx);
    }

    public void Update(float dt)
    {
        int budget = FloodsPerFrame;

        if (!Process(_staticWorld, ref budget)) return;

        foreach (ref readonly Entity e in _grids.GetEntities())
            if (!Process(e.Get<DynamicGridComponent>().Grid, ref budget)) return;
    }

    private bool Process(ChunkVolume vol, ref int budget)
    {
        foreach (var (_, entry) in vol.All)
        {
            if (!entry.NeedsFlood) continue;
            if (entry.Gpu == null) continue;       // no residency yet (empty chunk, or budget-delayed)
            if (entry.NeedsGpuUpload) continue;     // wait until opacity is uploaded this/next frame

            _flood.Flood(entry.Gpu, entry.Data);
            entry.NeedsFlood = false;

            if (--budget <= 0) return false;
        }
        return true;
    }

    public void Dispose() => _flood.Dispose();
}
