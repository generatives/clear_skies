using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Keeps <see cref="VolumeGpuResources"/> in sync with loaded chunks each PreRender tick.
///
/// Responsibilities:
/// 1. Create the per-volume GPU buffer on first load.
/// 2. Expand the buffer (reallocate) when new chunks extend beyond the current bounds.
/// 3. Upload each dirty chunk's opacity slice on block edits, up to <see cref="UploadsPerFrame"/> per frame.
/// 4. Ensure the volume's render bind group (LightA → group 2) is created.
/// </summary>
public sealed class GpuResidencySystem : ISystem
{
    private const int UploadsPerFrame = 8;

    private readonly GpuContext  _ctx;
    private readonly Renderer    _renderer;
    private readonly ChunkVolume _staticWorld;
    private readonly EntitySet   _grids;

    public GpuResidencySystem(World ecsWorld, StaticWorld staticWorld, GpuContext ctx, Renderer renderer)
    {
        _ctx         = ctx;
        _renderer    = renderer;
        _staticWorld = staticWorld;
        _grids       = ecsWorld.GetEntities().With<DynamicGridComponent>().AsSet();
    }

    public void Update(float dt)
    {
        int budget = UploadsPerFrame;
        ProcessVolume(_staticWorld, ref budget);
        foreach (ref readonly Entity e in _grids.GetEntities())
        {
            if (budget <= 0) break;
            ProcessVolume(e.Get<DynamicGridComponent>().Grid, ref budget);
        }
    }

    private void ProcessVolume(ChunkVolume vol, ref int budget)
    {
        if (vol.LoadedCount == 0) return;

        EnsureVolumeExists(vol);
        var gpu = vol.VolumeGpu!;

        // Expand bounds if any loaded chunk now lies outside the allocated range.
        if (gpu.EnsureContains(vol.BoundsMin, vol.BoundsMax))
        {
            // Reallocation creates fresh, empty light/opacity buffers — every chunk must re-upload its
            // opacity and re-flood. No remesh needed: the mesh geometry is unchanged, and the shader's
            // chunkBase/volSize are derived live from the (resized) volume at draw time (see RenderSystem).
            foreach (var (_, e) in vol.All)
            {
                e.NeedsGpuUpload = true;
                e.NeedsFlood     = true;
            }
        }

        // Upload opacity for dirty chunks (budgeted).
        foreach (var (pos, entry) in vol.All)
        {
            if (!entry.NeedsGpuUpload) continue;
            if (budget <= 0) break;

            gpu.UpdateChunkOpacity(pos, entry.Data);
            entry.NeedsGpuUpload = false;
            budget--;
        }

        gpu.UploadOpacityIfDirty();

        // Create / recreate the fragment-shader render bind group for LightA.
        if (gpu.RenderBindGroup == 0)
            gpu.RenderBindGroup = _renderer.CreateLightBindGroup(gpu.LightA);
    }

    private void EnsureVolumeExists(ChunkVolume vol)
    {
        if (vol.VolumeGpu != null) return;
        vol.VolumeGpu = VolumeGpuResources.Create(_ctx, vol.BoundsMin, vol.BoundsMax);
    }
}
