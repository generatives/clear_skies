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
/// 2. Re-window the buffer when the loaded set no longer fits the window or the window has grown wastefully
///    large — so the volume tracks the camera instead of growing without bound (the cause of the far-travel
///    buffer-limit crash). The new window's buffers are built on a background thread (see
///    <see cref="VolumeGpuResources.Prepare"/>/<see cref="_pendingRealloc"/>) and swapped in once ready, so the
///    ~60-90ms buffer create/destroy cost (unavoidable in one call, unlike the opacity upload below, which can
///    be spread across frames) never lands on a single frame — the old buffers keep rendering unaffected
///    while the new ones are built.
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

    /// <summary>At most one in-flight background re-window per volume. Populated when a rewindow starts,
    /// removed once its result is adopted (or it fails) — see <see cref="ProcessVolume"/>.</summary>
    private readonly Dictionary<ChunkVolume, (Task<VolumeGpuResources.PreparedBuffers> Task, long StartedAt)> _pendingRealloc = new();

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
            // First-ever allocation for this volume: nothing is rendering from it yet, so there's nothing to
            // double-buffer against — allocate synchronously, same as always.
            vol.VolumeGpu = VolumeGpuResources.Create(_ctx, tmin, tmax);
        }
        else
        {
            AdoptFinishedRealloc(vol);

            var g = vol.VolumeGpu;
            long cur = (long)g.DX * g.DY * g.DZ;
            long tgt = (long)(tmax.X - tmin.X + 1) * (tmax.Y - tmin.Y + 1) * (tmax.Z - tmin.Z + 1);

            // Re-window if the loaded set has moved outside the current allocation, or the allocation is now
            // wastefully large (e.g. after teleporting away from a previously explored region). Skip starting
            // a second background rewindow while one is already in flight for this volume — once it lands,
            // this check runs again against the (by-then-current) window and starts another if still needed.
            if ((!g.Covers(lmin, lmax) || cur > tgt * ShrinkFactor) && !_pendingRealloc.ContainsKey(vol))
                StartBackgroundRealloc(vol, g, tmin, tmax);
        }

        var gpu = vol.VolumeGpu!;

        // Upload each dirty chunk's opacity slice (chunk-major, one contiguous write) and rebuild its
        // emitter list. Budgeted per frame (block edits / newly streamed chunks) — also the path that catches
        // up any chunk a just-adopted background rewindow's snapshot missed or that changed while it was
        // still preparing (see StartBackgroundRealloc).
        foreach (var (pos, entry) in vol.All)
        {
            if (!entry.NeedsGpuUpload) continue;
            if (budget <= 0) break;

            gpu.UpdateChunkOpacity(pos, entry);
            entry.NeedsGpuUpload = false;
            budget--;
        }

        // Create / recreate the fragment-shader render bind group for LightA + Opacity (AO) + SunVis.
        if (gpu.RenderBindGroup == 0)
            gpu.RenderBindGroup = _renderer.CreateLightBindGroup(gpu.LightA, gpu.Opacity, gpu.SunVis);
    }

    /// <summary>Snapshots every loaded chunk's cached opacity words (main thread, so this never races the
    /// background task against live ChunkEntry/ChunkVolume state) and hands off building the new window's
    /// buffers to a background thread. See the class doc comment for why.</summary>
    private void StartBackgroundRealloc(ChunkVolume vol, VolumeGpuResources g, ChunkPosition tmin, ChunkPosition tmax)
    {
        var snapshot = new List<(ChunkPosition pos, uint[] words)>(vol.LoadedCount);
        foreach (var (pos, e) in vol.All)
        {
            if (e.PackedOpacityWords is not { } words)
            {
                // Not cached yet (a chunk still waiting on its very first upload) — compute it now, on the
                // main thread, rather than omitting it from the snapshot entirely (which would leave it
                // reading as all-air/no-occlusion in the new buffer until its turn in the post-swap budgeted
                // upload loop comes up).
                g.UpdateChunkOpacity(pos, e); // writes into the OLD (about to be replaced) buffer too; harmless
                words = e.PackedOpacityWords!;
            }
            snapshot.Add((pos, words));
        }

        var ctx = _ctx;
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var task = Task.Run(() =>
        {
            var prepared = VolumeGpuResources.Prepare(ctx, tmin, tmax);
            foreach (var (pos, words) in snapshot)
                prepared.WriteChunkOpacity(pos, words);
            return prepared;
        });
        _pendingRealloc[vol] = (task, startedAt);

        Console.WriteLine($"[gpu-realloc] {(vol == _staticWorld ? "static" : "grid")} starting background rewindow " +
                           $"cur={g.DX}x{g.DY}x{g.DZ} -> new={tmax.X-tmin.X+1}x{tmax.Y-tmin.Y+1}x{tmax.Z-tmin.Z+1} " +
                           $"loaded={vol.LoadedCount} snapshot={snapshot.Count}");
    }

    /// <summary>Adopts this volume's background rewindow if it has finished. A full re-flood of the whole
    /// (now-larger/shifted) window is unavoidable — the new LightA starts at ambient, same as any fresh
    /// allocation always has — but the existing capped, multi-frame flood (GpuLightSystem) already spreads
    /// that out smoothly, same as it does for ordinary streaming-in chunks.</summary>
    private void AdoptFinishedRealloc(ChunkVolume vol)
    {
        if (!_pendingRealloc.TryGetValue(vol, out var pending)) return;

        if (pending.Task.IsFaulted)
        {
            Console.WriteLine($"[gpu-realloc] {(vol == _staticWorld ? "static" : "grid")} background rewindow " +
                               $"FAILED: {pending.Task.Exception?.GetBaseException().Message}");
            _pendingRealloc.Remove(vol);
            return;
        }
        if (!pending.Task.IsCompletedSuccessfully) return; // still preparing; keep rendering from the old buffers

        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(pending.StartedAt).TotalMilliseconds;
        vol.VolumeGpu!.AdoptPrepared(pending.Task.Result);
        foreach (var (_, e) in vol.All) e.NeedsFlood = true;
        _pendingRealloc.Remove(vol);

        Console.WriteLine($"[gpu-realloc] {(vol == _staticWorld ? "static" : "grid")} adopted background rewindow " +
                           $"after {ms:F0}ms (built off the main thread)");
    }
}
