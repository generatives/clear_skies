using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Phase 4.0: keeps per-chunk GPU lighting buffers resident and their opacity bitset in sync with block
/// edits. For every chunk that has solids and is flagged <see cref="ChunkEntry.NeedsGpuUpload"/>, it
/// lazily allocates <see cref="ChunkGpuResources"/> and uploads the opacity bitset. Empty chunks are
/// skipped (no residency until they contain solids). The static world is processed plus every dynamic
/// grid (discovered via <see cref="DynamicGridComponent"/>, like <c>GridTransformSystem</c>).
///
/// Nothing reads the light buffers yet — the GPU flood that consumes them arrives in Phase 4.2.
/// </summary>
public sealed class GpuResidencySystem : ISystem
{
    // Bound first-frame allocation cost (each upload creates ~260 KB of buffers + a 4 KB write).
    private const int UploadsPerFrame = 16;

    private readonly ChunkVolume _staticWorld;
    private readonly GpuContext  _ctx;
    private readonly EntitySet   _grids;

    public GpuResidencySystem(World world, ChunkVolume staticWorld, GpuContext ctx)
    {
        _staticWorld = staticWorld;
        _ctx         = ctx;
        _grids       = world.GetEntities().With<DynamicGridComponent>().AsSet();
    }

    public void Update(float dt)
    {
        int budget = UploadsPerFrame;

        if (!Process(_staticWorld, ref budget)) return;

        foreach (ref readonly Entity e in _grids.GetEntities())
            if (!Process(e.Get<DynamicGridComponent>().Grid, ref budget)) return;
    }

    // Returns false when the per-frame budget is exhausted (stop for this frame).
    private bool Process(ChunkVolume vol, ref int budget)
    {
        foreach (var (_, entry) in vol.All)
        {
            if (!entry.NeedsGpuUpload) continue;

            // No GPU residency for empty chunks yet; revisit when the flood needs air-chunk light (4.2).
            if (!entry.Data.HasAnySolid())
            {
                entry.NeedsGpuUpload = false;
                continue;
            }

            bool justCreated = entry.Gpu == null;
            entry.Gpu ??= ChunkGpuResources.Create(_ctx);
            entry.Gpu.UploadOpacity(entry.Data);
            entry.NeedsGpuUpload = false;

            // If residency lagged behind meshing, force a remesh so ChunkMeshSystem attaches the
            // per-chunk light bind group (otherwise the chunk would stay on the full-bright fallback).
            if (justCreated) entry.NeedsRemesh = true;

            if (--budget <= 0) return false;
        }
        return true;
    }
}
