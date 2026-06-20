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

            // Compute chunk-space base and volume size from the current VolumeGpuResources.
            // Falls back to 0,0,0 / 32,32,32 if GPU residency isn't ready yet
            // (full-bright buffer handles the 0-31 range just fine).
            int cbx = 0, cby = 0, cbz = 0;
            int vsx = ChunkData.Size, vsy = ChunkData.Size, vsz = ChunkData.Size;
            if (volume.VolumeGpu != null)
            {
                var (bx, by, bz) = volume.VolumeGpu.ChunkVoxelBase(pos);
                cbx = bx; cby = by; cbz = bz;
                vsx = volume.VolumeGpu.VW;
                vsy = volume.VolumeGpu.VH;
                vsz = volume.VolumeGpu.VD;
            }

            volume.SetMesh(pos, mesh, cbx, cby, cbz, vsx, vsy, vsz);
            _totalMeshed++;

            if (meshMs + uploadMs > 5)
                Console.WriteLine($"[mesh] chunk {pos} | {verts.Length} verts | mesh={meshMs}ms upload={uploadMs}ms | total={_totalMeshed}");

            if (++built >= MeshesPerFrame)
                return;
        }
    }
}
