using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Keeps <see cref="VolumeGpuResources"/> in sync with loaded chunks each PreRender tick.
///
/// Responsibilities:
/// 1. Create the per-volume GPU buffer on first load, sized to a window around the loaded chunks.
/// 2. Re-window the buffer (reallocate) when the loaded set no longer fits the window or the window has
///    grown wastefully large — so the volume tracks the camera instead of growing without bound (the
///    cause of the far-travel buffer-limit crash).
/// 3. Upload each dirty chunk's opacity slice on block edits, up to <see cref="UploadsPerFrame"/> per frame.
/// 4. Ensure the volume's render bind group (LightA → group 2) is created.
/// </summary>
public sealed class GpuResidencySystem : ISystem
{
    private const int UploadsPerFrame = 8;

    /// <summary>Chunks of padding around the loaded set when (re)windowing. Larger = less frequent reallocs
    /// (each crossing of the margin re-windows) at the cost of more VRAM and a bigger re-flood per crossing.</summary>
    private const int WindowMargin = 2;

    /// <summary>Re-window to shrink when the current allocation exceeds this multiple of the needed size.</summary>
    private const int ShrinkFactor = 3;

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
        if (!vol.TryGetLoadedBounds(out var lmin, out var lmax)) return;

        // Target window = loaded AABB padded by WindowMargin chunks. The margin absorbs camera travel so the
        // volume doesn't re-window every chunk boundary.
        var tmin = new ChunkPosition(lmin.X - WindowMargin, lmin.Y - WindowMargin, lmin.Z - WindowMargin);
        var tmax = new ChunkPosition(lmax.X + WindowMargin, lmax.Y + WindowMargin, lmax.Z + WindowMargin);

        if (vol.VolumeGpu == null)
        {
            vol.VolumeGpu = VolumeGpuResources.Create(_ctx, tmin, tmax);
        }
        else
        {
            var g = vol.VolumeGpu;
            long cur = (long)g.DX * g.DY * g.DZ;
            long tgt = (long)(tmax.X - tmin.X + 1) * (tmax.Y - tmin.Y + 1) * (tmax.Z - tmin.Z + 1);

            // Re-window if the loaded set has moved outside the current allocation, or the allocation is now
            // wastefully large (e.g. after teleporting away from a previously explored region).
            if (!g.Covers(lmin, lmax) || cur > tgt * ShrinkFactor)
            {
                g.Reallocate(tmin, tmax);

                // Fresh, empty buffers. Re-upload every loaded chunk's opacity NOW (each is a cheap ~4 KB
                // contiguous write) rather than draining it at UploadsPerFrame — that avoids a long window of
                // half-uploaded opacity, which during travel would never settle before the next re-window.
                foreach (var (pos, e) in vol.All)
                {
                    g.UpdateChunkOpacity(pos, e);
                    e.NeedsGpuUpload = false;
                    e.NeedsFlood     = true;
                }
            }
        }

        var gpu = vol.VolumeGpu!;

        // Upload each dirty chunk's opacity slice (chunk-major, one contiguous write) and rebuild its
        // emitter list. Budgeted per frame (block edits / newly streamed chunks).
        foreach (var (pos, entry) in vol.All)
        {
            if (!entry.NeedsGpuUpload) continue;
            if (budget <= 0) break;

            gpu.UpdateChunkOpacity(pos, entry);
            entry.NeedsGpuUpload = false;
            budget--;
        }

        // Create / recreate the fragment-shader render bind group for LightA.
        if (gpu.RenderBindGroup == 0)
            gpu.RenderBindGroup = _renderer.CreateLightBindGroup(gpu.LightA);
    }
}
