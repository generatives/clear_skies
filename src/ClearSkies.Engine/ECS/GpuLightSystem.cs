using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Runs the per-volume GPU block-light flood whenever any chunk in the volume has changed.
/// One flood covers the entire <see cref="ChunkVolume"/> buffer, so light propagates across chunk
/// boundaries naturally. Floods at most one volume per frame to keep the GPU busy without stalling.
///
/// Runs after <c>GpuResidencySystem</c> (opacity must be uploaded and RenderBindGroup created first).
/// </summary>
public sealed class GpuLightSystem : ISystem, IDisposable
{
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
        if (FloodVolume(_staticWorld)) return;

        foreach (ref readonly Entity e in _grids.GetEntities())
            if (FloodVolume(e.Get<DynamicGridComponent>().Grid)) return;
    }

    /// <summary>
    /// Floods the volume if any chunk is dirty and GPU residency is ready.
    /// Returns true if a flood was submitted (at most 1 per Update call).
    /// </summary>
    private bool FloodVolume(ChunkVolume vol)
    {
        if (vol.VolumeGpu == null) return false;                   // GPU buffers not yet allocated
        if (vol.VolumeGpu.OpacityDirty) return false;             // opacity upload still pending
        if (vol.VolumeGpu.RenderBindGroup == 0) return false;     // render bind group not yet ready

        // Check if any chunk needs a flood (block edit, CPU light change, or initial load).
        bool anyDirty = false;
        foreach (var (_, e) in vol.All)
        {
            if (e.NeedsFlood && !e.NeedsGpuUpload) { anyDirty = true; break; }
        }
        if (!anyDirty) return false;

        _flood.Flood(vol.VolumeGpu, vol.All);

        // Clear flood flags on all chunks — the entire volume was re-flooded.
        foreach (var (_, e) in vol.All) e.NeedsFlood = false;

        return true; // one flood per Update
    }

    public void Dispose() => _flood.Dispose();
}
