using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Runs the per-volume GPU light flood whenever any chunk in the volume has changed. The flood is scoped
/// to the bounding box of the dirty chunks (see <see cref="FloodVolume"/>), so a single edit relights only
/// its neighbourhood rather than the whole volume; light still crosses chunk boundaries within the region.
/// Floods at most one volume per frame to keep the GPU busy without stalling.
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
    /// Floods the volume if any chunk is dirty and GPU residency is ready. The flood is scoped to the
    /// bounding box of dirty chunks (Phase 4.6 culling): full-height in Y (sky occlusion is a vertical
    /// column effect) and the dirty X/Z footprint plus a one-chunk lateral margin (≥ the max propagation
    /// radius of 15) so the relaxation's border reads stay correct. Returns true if a flood was submitted.
    /// </summary>
    private bool FloodVolume(ChunkVolume vol)
    {
        var gpu = vol.VolumeGpu;
        if (gpu == null) return false;                   // GPU buffers not yet allocated
        if (gpu.RenderBindGroup == 0) return false;      // render bind group not yet ready

        // Dirty chunk X/Z footprint (in chunk offsets from the volume Min). Only chunks whose opacity is
        // already uploaded count — a chunk still awaiting upload keeps its flag and is picked up next cycle.
        int minCX = int.MaxValue, minCZ = int.MaxValue, maxCX = int.MinValue, maxCZ = int.MinValue;
        foreach (var (pos, e) in vol.All)
        {
            if (!e.NeedsFlood || e.NeedsGpuUpload) continue;
            int cx = pos.X - gpu.Min.X, cz = pos.Z - gpu.Min.Z;
            if (cx < minCX) minCX = cx; if (cx > maxCX) maxCX = cx;
            if (cz < minCZ) minCZ = cz; if (cz > maxCZ) maxCZ = cz;
        }
        if (maxCX < minCX) return false; // nothing dirty (and ready)

        // Expand by a one-chunk lateral margin, clamp to the volume.
        minCX = System.Math.Max(0, minCX - 1); maxCX = System.Math.Min(gpu.DX - 1, maxCX + 1);
        minCZ = System.Math.Max(0, minCZ - 1); maxCZ = System.Math.Min(gpu.DZ - 1, maxCZ + 1);

        const int S = ChunkData.Size; // 32
        var region = new FloodRegion(
            Ox: minCX * S, Oy: 0, Oz: minCZ * S,
            Sx: (maxCX - minCX + 1) * S, Sy: gpu.VH, Sz: (maxCZ - minCZ + 1) * S);

        _flood.Flood(gpu, vol.All, region);

        // Clear the flood flag only on chunks fully represented in this flood: their opacity is uploaded.
        // (Sky + block are derived entirely on the GPU from opacity + emission, so opacity readiness is
        // the only requirement.) A chunk still awaiting its opacity upload was flooded as air, so we KEEP
        // its flag set; once the upload lands it triggers a corrective reflood instead of being stuck
        // with the wrong lighting (the cause of flat-lit undersides / seams during loading).
        foreach (var (_, e) in vol.All)
            if (!e.NeedsGpuUpload)
                e.NeedsFlood = false;

        return true; // one flood per Update
    }

    public void Dispose() => _flood.Dispose();
}
