using System.Diagnostics;
using System.Runtime.InteropServices;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Each frame, remeshes up to <c>MeshesPerFrame</c> chunks that are flagged dirty across all
/// registered <see cref="ChunkVolume"/>s. Uploads the result to GPU and hands it back to the owning
/// volume along with the chunk's volume-space base coordinates (used by the fragment shader to map
/// local position → volume-space light sample).
/// </summary>
public sealed class ChunkMeshSystem : ISystem
{
    // Mesh() dropped from ~1.7ms to ~0.75ms avg per non-empty chunk after removing the mesher's
    // per-voxel stackalloc/Span indirection (see GenerationBenchmark) — 4/frame keeps roughly the same
    // per-frame time budget the old MeshesPerFrame=2 spent, at 2x the throughput.
    private const int MeshesPerFrame = 4;

    private readonly List<ChunkVolume> _volumes = new();
    private readonly Renderer     _renderer;
    private readonly GreedyMesher _mesher;

    private readonly Stopwatch _sw = new();
    private int _totalMeshed;

    public ChunkMeshSystem(ChunkVolume initial, Renderer renderer)
    {
        _volumes.Add(initial);
        _renderer = renderer;
        _mesher   = new GreedyMesher(renderer.Atlas);
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
            if (!entry.NeedsRemesh) continue;

            // Fast path: pure air chunk.
            if (!entry.Data.HasAnySolid())
            {
                entry.Mesh?.Dispose();
                entry.Mesh        = null;
                entry.NeedsRemesh = false;
                if (entry.Entity.Has<MeshRenderer>())
                    entry.Entity.Remove<MeshRenderer>();
                continue;
            }

            _sw.Restart();

            var (verts, idxs) = _mesher.Mesh(
                entry.Data,
                volume.GetData(pos.Offset(-1, 0, 0)), volume.GetData(pos.Offset( 1, 0, 0)),
                volume.GetData(pos.Offset( 0,-1, 0)), volume.GetData(pos.Offset( 0, 1, 0)),
                volume.GetData(pos.Offset( 0, 0,-1)), volume.GetData(pos.Offset( 0, 0, 1)));

            long meshMs = _sw.ElapsedMilliseconds;
            _sw.Restart();

            if (verts.Count == 0)
            {
                entry.Mesh?.Dispose();
                entry.Mesh        = null;
                entry.NeedsRemesh = false;
                if (entry.Entity.Has<MeshRenderer>())
                    entry.Entity.Remove<MeshRenderer>();
                if (++built >= MeshesPerFrame) return;
                continue;
            }

            var mesh      = _renderer.UploadMesh(CollectionsMarshal.AsSpan(verts), CollectionsMarshal.AsSpan(idxs));
            long uploadMs = _sw.ElapsedMilliseconds;

            // The chunk's voxel base and the volume dims are derived live at draw time from the volume's
            // GPU resources (see RenderSystem), so a volume reallocation needs no remesh here.
            volume.SetMesh(pos, mesh);
            _totalMeshed++;

            if (meshMs + uploadMs > 5)
                Console.WriteLine($"[mesh] chunk {pos} | {verts.Count} verts | mesh={meshMs}ms upload={uploadMs}ms | total={_totalMeshed}");

            if (++built >= MeshesPerFrame)
                return;
        }
    }
}
