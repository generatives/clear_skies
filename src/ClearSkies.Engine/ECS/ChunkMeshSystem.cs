using System.Diagnostics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Each frame, remeshes up to <c>MeshesPerFrame</c> chunks that are flagged dirty across all
/// registered <see cref="ChunkVolume"/>s (the static world plus any dynamic grids), uploads the
/// result to GPU, and hands it back to the owning volume. The per-frame budget is shared across
/// every volume.
/// </summary>
public sealed class ChunkMeshSystem : ISystem
{
    private const int MeshesPerFrame = 2;

    private readonly List<ChunkVolume> _volumes = new();
    private readonly Renderer     _renderer;
    private readonly GreedyMesher _mesher = new();

    private readonly Stopwatch _sw = new();
    private int _totalMeshed;

    public ChunkMeshSystem(ChunkVolume initial, Renderer renderer)
    {
        _volumes.Add(initial);
        _renderer = renderer;
    }

    public void RegisterVolume(ChunkVolume volume)
    {
        if (!_volumes.Contains(volume)) _volumes.Add(volume);
    }

    public void UnregisterVolume(ChunkVolume volume) => _volumes.Remove(volume);

    public void Update(float dt)
    {
        int built = 0;

        foreach (var volume in _volumes)
        foreach (var (pos, entry) in volume.All)
        {
            if (!entry.NeedsRemesh) continue; // meshing is light-independent — no relight gate

            // Fast path: pure air chunk — skip the greedy mesher entirely (~0.03ms vs ~11ms).
            if (!entry.Data.HasAnySolid())
            {
                entry.Mesh?.Dispose();
                entry.Mesh        = null;
                entry.NeedsRemesh = false;
                if (entry.Entity.Has<MeshRenderer>())
                    entry.Entity.Remove<MeshRenderer>();
                continue; // don't count against budget; these are cheap
            }

            _sw.Restart();

            // Neighbour ChunkData is for face-culling at chunk borders.
            var (verts, idxs) = _mesher.Mesh(
                entry.Data,
                volume.GetData(pos.Offset(-1, 0, 0)), volume.GetData(pos.Offset( 1, 0, 0)),
                volume.GetData(pos.Offset( 0,-1, 0)), volume.GetData(pos.Offset( 0, 1, 0)),
                volume.GetData(pos.Offset( 0, 0,-1)), volume.GetData(pos.Offset( 0, 0, 1)));

            long meshMs = _sw.ElapsedMilliseconds;
            _sw.Restart();

            if (verts.Length == 0)
            {
                // Chunk had solids but they're all occluded — no faces visible.
                entry.Mesh?.Dispose();
                entry.Mesh        = null;
                entry.NeedsRemesh = false;
                if (entry.Entity.Has<MeshRenderer>())
                    entry.Entity.Remove<MeshRenderer>();
                if (++built >= MeshesPerFrame) return;
                continue;
            }

            var mesh = _renderer.UploadMesh(verts, idxs);
            long uploadMs = _sw.ElapsedMilliseconds;

            // Per-chunk light binding: created once per chunk over its LightA buffer (stable). The
            // flood always leaves its result in LightA, so this binding stays valid. Falls back to
            // the shared full-bright buffer (handle 0) until GPU residency exists for this chunk.
            if (entry.Gpu != null && entry.Gpu.RenderBindGroup == 0)
                entry.Gpu.RenderBindGroup = _renderer.CreateLightBindGroup(entry.Gpu.LightA);
            nint lbg = entry.Gpu?.RenderBindGroup ?? 0;

            volume.SetMesh(pos, mesh, lbg);
            _totalMeshed++;

            if (meshMs + uploadMs > 5)
                Console.WriteLine($"[mesh] chunk {pos} | {verts.Length} verts | mesh={meshMs}ms upload={uploadMs}ms | total={_totalMeshed}");

            if (++built >= MeshesPerFrame)
                return;
        }
    }
}
