using System.Diagnostics;
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

            if (verts.Length == 0)
            {
                entry.Mesh?.Dispose();
                entry.Mesh        = null;
                entry.NeedsRemesh = false;
                if (entry.Entity.Has<MeshRenderer>())
                    entry.Entity.Remove<MeshRenderer>();
                if (++built >= MeshesPerFrame) return;
                continue;
            }

            var mesh      = _renderer.UploadMesh(verts, idxs);
            long uploadMs = _sw.ElapsedMilliseconds;

            // The chunk's voxel base and the volume dims are derived live at draw time from the volume's
            // GPU resources (see RenderSystem), so a volume reallocation needs no remesh here.
            volume.SetMesh(pos, mesh);
            _totalMeshed++;

            if (meshMs + uploadMs > 5)
                Console.WriteLine($"[mesh] chunk {pos} | {verts.Length} verts | mesh={meshMs}ms upload={uploadMs}ms | total={_totalMeshed}");

            if (++built >= MeshesPerFrame)
                return;
        }
    }
}
