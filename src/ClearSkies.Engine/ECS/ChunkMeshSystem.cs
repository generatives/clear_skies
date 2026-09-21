using System.Buffers;
using System.Collections.Concurrent;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Remeshes chunks flagged dirty across all registered <see cref="ChunkVolume"/>s. The greedy mesh itself
/// (~0.65ms per non-empty chunk, see StreamingBenchmark) runs on thread-pool workers, one
/// <see cref="GreedyMesher"/> per thread; only the GPU upload and the hand-back to the owning volume happen
/// here on the main thread.
///
/// A chunk has at most one job in flight. Workers read <see cref="ChunkData"/> while the main thread may edit
/// it; a torn read is harmless because any edit sets <see cref="ChunkEntry.NeedsRemesh"/> again, so the chunk
/// is re-dispatched once its current job lands (whose result is still applied — it's newer than the mesh on
/// screen). A result is dropped if its chunk was unloaded or its volume unregistered meanwhile.
/// </summary>
public sealed class ChunkMeshSystem : ISystem, IDebugUiSystem
{
    /// <summary>Jobs in flight at once — bounds both worker load and the number of uploads landing per frame.</summary>
    private static readonly int MaxInFlight = System.Math.Max(2, Environment.ProcessorCount / 2);

    private readonly List<ChunkVolume> _volumes = new();
    private readonly Renderer _renderer;
    private readonly ThreadLocal<GreedyMesher> _meshers;

    private readonly HashSet<ChunkEntry> _inFlight = new();
    private readonly ConcurrentQueue<Result> _results = new();

    private int _totalMeshed;
    private double _uploadMs;

    private sealed record Result(ChunkVolume Volume, ChunkPosition Pos, ChunkEntry Entry,
                                 Vertex[] Verts, int VertCount, uint[] Idxs, int IdxCount, Exception? Error);

    public ChunkMeshSystem(ChunkVolume initial, Renderer renderer)
    {
        _volumes.Add(initial);
        _renderer = renderer;
        var atlas = renderer.Atlas;
        _meshers  = new ThreadLocal<GreedyMesher>(() => new GreedyMesher(atlas));
    }

    public void RegisterVolume(ChunkVolume volume)
    {
        if (!_volumes.Contains(volume)) _volumes.Add(volume);
    }

    public void UnregisterVolume(ChunkVolume volume) => _volumes.Remove(volume);

    public void Update(float dt)
    {
        ApplyResults();
        Dispatch();
    }

    private void Dispatch()
    {
        if (_inFlight.Count >= MaxInFlight) return;

        foreach (var volume in _volumes)
        foreach (var (pos, entry) in volume.All)
        {
            if (!entry.NeedsRemesh || _inFlight.Contains(entry)) continue;

            // Fast path: pure air chunk.
            if (!entry.Data.HasAnySolid())
            {
                ClearMesh(entry);
                continue;
            }

            entry.NeedsRemesh = false;
            _inFlight.Add(entry);

            var data = entry.Data;
            var nX = volume.GetData(pos.Offset(-1, 0, 0)); var pX = volume.GetData(pos.Offset(1, 0, 0));
            var nY = volume.GetData(pos.Offset(0, -1, 0)); var pY = volume.GetData(pos.Offset(0, 1, 0));
            var nZ = volume.GetData(pos.Offset(0, 0, -1)); var pZ = volume.GetData(pos.Offset(0, 0, 1));
            var vol = volume;
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try
                {
                    // The mesher's lists are per-thread scratch, so copy out before this thread meshes again.
                    var (verts, idxs) = _meshers.Value!.Mesh(data, nX, pX, nY, pY, nZ, pZ);
                    var v = ArrayPool<Vertex>.Shared.Rent(System.Math.Max(1, verts.Count));
                    var i = ArrayPool<uint>.Shared.Rent(System.Math.Max(1, idxs.Count));
                    verts.CopyTo(v);
                    idxs.CopyTo(i);
                    _results.Enqueue(new Result(vol, pos, entry, v, verts.Count, i, idxs.Count, null));
                }
                catch (Exception e)
                {
                    _results.Enqueue(new Result(vol, pos, entry, Array.Empty<Vertex>(), 0, Array.Empty<uint>(), 0, e));
                }
            }, null);

            if (_inFlight.Count >= MaxInFlight) return;
        }
    }

    private void ApplyResults()
    {
        while (_results.TryDequeue(out var r))
        {
            _inFlight.Remove(r.Entry);
            try
            {
                if (r.Error is not null)
                {
                    Console.WriteLine($"[mesh] chunk {r.Pos} failed: {r.Error}");
                    continue;
                }
                // Unloaded (or its grid destroyed) while meshing — nothing to hand the mesh to.
                if (!_volumes.Contains(r.Volume) || r.Volume.GetEntry(r.Pos) != r.Entry) continue;

                if (r.VertCount == 0)
                {
                    bool redirtiedEmpty = r.Entry.NeedsRemesh;
                    ClearMesh(r.Entry);
                    r.Entry.NeedsRemesh = redirtiedEmpty;
                    continue;
                }

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var mesh = _renderer.UploadMesh(r.Verts.AsSpan(0, r.VertCount), r.Idxs.AsSpan(0, r.IdxCount));
                _uploadMs += 0.05 * (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds - _uploadMs);

                // The chunk's voxel base and the volume dims are derived live at draw time from the volume's
                // GPU resources (see RenderSystem), so a volume reallocation needs no remesh here. SetMesh clears
                // NeedsRemesh, so preserve a re-dirty that arrived while this job was in flight.
                bool redirtied = r.Entry.NeedsRemesh;
                r.Volume.SetMesh(r.Pos, mesh);
                r.Entry.NeedsRemesh = redirtied;
                _totalMeshed++;
            }
            finally
            {
                if (r.Verts.Length > 0) ArrayPool<Vertex>.Shared.Return(r.Verts);
                if (r.Idxs.Length > 0) ArrayPool<uint>.Shared.Return(r.Idxs);
            }
        }
    }

    private static void ClearMesh(ChunkEntry entry)
    {
        entry.Mesh?.Dispose();
        entry.Mesh        = null;
        entry.NeedsRemesh = false;
        if (entry.Entity.Has<MeshRenderer>())
            entry.Entity.Remove<MeshRenderer>();
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Chunk Meshing";

    public void DrawDebugUi()
    {
        ImGui.Text($"Jobs in flight: {_inFlight.Count} / {MaxInFlight}");
        ImGui.Text($"Upload (main thread, smoothed): {_uploadMs:F2} ms per chunk");
        ImGui.Text($"Chunks meshed (lifetime): {_totalMeshed}");
    }
}
