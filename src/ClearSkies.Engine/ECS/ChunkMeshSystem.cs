using System.Diagnostics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Each frame, remeshes up to <c>MeshesPerFrame</c> chunks that are flagged dirty,
/// uploads the result to GPU, and hands it back to the <see cref="ChunkManager"/>.
/// </summary>
public sealed class ChunkMeshSystem : ISystem
{
    private const int MeshesPerFrame = 2;

    private readonly ChunkManager _manager;
    private readonly Renderer     _renderer;
    private readonly GreedyMesher _mesher = new();

    private readonly Stopwatch _sw = new();
    private int _totalMeshed;

    public ChunkMeshSystem(ChunkManager manager, Renderer renderer)
    {
        _manager  = manager;
        _renderer = renderer;
    }

    public void Update(float dt)
    {
        int built = 0;

        foreach (var (pos, entry) in _manager.All)
        {
            if (!entry.NeedsRemesh) continue;

            // Fast path: pure air chunk — skip the greedy mesher entirely (~0.03ms vs ~11ms).
            // Still O(32k) but just a tight loop vs 6 face sweeps with function calls.
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

            var (verts, idxs) = _mesher.Mesh(
                entry.Data,
                _manager.GetData(pos.Offset(-1, 0, 0)),
                _manager.GetData(pos.Offset( 1, 0, 0)),
                _manager.GetData(pos.Offset( 0,-1, 0)),
                _manager.GetData(pos.Offset( 0, 1, 0)),
                _manager.GetData(pos.Offset( 0, 0,-1)),
                _manager.GetData(pos.Offset( 0, 0, 1)));

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
                if (++built >= MeshesPerFrame) break;
                continue;
            }

            var mesh = _renderer.UploadMesh(verts, idxs);
            long uploadMs = _sw.ElapsedMilliseconds;

            _manager.SetMesh(pos, mesh);
            _totalMeshed++;

            if (meshMs + uploadMs > 5)
                Console.WriteLine($"[mesh] chunk {pos} | {verts.Length} verts | mesh={meshMs}ms upload={uploadMs}ms | total={_totalMeshed}");

            if (++built >= MeshesPerFrame)
                break;
        }
    }
}
