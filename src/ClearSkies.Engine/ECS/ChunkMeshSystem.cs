using System.Buffers;
using System.Collections.Concurrent;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Remeshes chunks flagged dirty across all registered <see cref="ChunkVolume"/>s into their
/// <see cref="ChunkRenderData"/>: the greedy-meshed cubes plus the chunk's model blocks (found in the same worker
/// job, resolved to their shared models through <see cref="BlockModelLibrary"/>). The greedy mesh itself
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

    private readonly World _ecsWorld;
    private EntitySet _dirtyChunks;
    private readonly Renderer _renderer;
    private readonly BlockModelLibrary _blockModels;
    private readonly ThreadLocal<GreedyMesher> _meshers;

    private int _inFlight = 0;
    private readonly ConcurrentQueue<Result> _results = new();
    private readonly List<GpuMesh> _removed = new();

    private int _totalMeshed;
    private double _uploadMs;

    /// <summary>A model block's cell and facing as found by the worker; resolved to a <see cref="ModelBlock"/>
    /// (which needs the GPU model) on the main thread.</summary>
    private readonly record struct ModelCell(byte X, byte Y, byte Z, BlockId Block, Facing Facing);

    private sealed record Result(Entity Entity, Vertex[] Verts, int VertCount, uint[] Idxs, int IdxCount,
                                 ModelCell[] Models, Exception? Error);

    public ChunkMeshSystem(World ecsWorld, Renderer renderer, BlockModelLibrary blockModels)
    {
        _ecsWorld = ecsWorld;
        _dirtyChunks = ecsWorld.GetEntities().With<Chunk>().With<Transform>().With<NeedsRemeshFlag>().AsSet();
        _renderer = renderer;
        _blockModels = blockModels;
        var atlas = renderer.Atlas;
        _meshers  = new ThreadLocal<GreedyMesher>(() => new GreedyMesher(atlas));

        _ecsWorld.SubscribeEntityDisposed(EntityDisposed);
    }

    private void EntityDisposed(in Entity e)
    {
        if (e.Has<ChunkRenderData>() && e.Get<ChunkRenderData>().Mesh is { } mesh)
            _removed.Add(mesh);
    }

    public void Update(float dt)
    {
        ApplyResults();
        Dispatch();
        Cleanup();
    }

    private void Dispatch()
    {
        if (_inFlight >= MaxInFlight) return;

        foreach (ref readonly Entity e in _dirtyChunks.GetEntities())
        {
            var chunk = e.Get<Chunk>();
            var entry = chunk.Entry;
            var pos = entry.Position;
            var volume = entry.Volume;

            // Fast path: pure air chunk.
            if (!entry.Data.HasAnySolid())
            {
                ClearMesh(entry);
                continue;
            }

            entry.Entity.Remove<NeedsRemeshFlag>();

            _inFlight += 1;

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
                    _results.Enqueue(new Result(entry.Entity, v, verts.Count, i, idxs.Count, FindModelBlocks(data), null));
                }
                catch (Exception e)
                {
                    _results.Enqueue(new Result(entry.Entity, Array.Empty<Vertex>(), 0, Array.Empty<uint>(), 0,
                                                Array.Empty<ModelCell>(), e));
                }
            }, null);

            if (_inFlight >= MaxInFlight) return;
        }
    }

    private void ApplyResults()
    {
        while (_results.TryDequeue(out var r))
        {
            _inFlight -= 1;
            var entity = r.Entity;
            try
            {
                if (r.Error is not null)
                {
                    Console.WriteLine($"[mesh] chunk failed: {r.Error}");
                    continue;
                }

                if (!entity.IsAlive) continue; // unloaded while meshing
                if (!entity.Has<Chunk>()) continue; // unloaded while meshing

                var chunk = entity.Get<Chunk>();
                var entry = chunk.Entry;
                var volume = entry.Volume;

                var models = ResolveModels(r.Models);
                if (r.VertCount == 0 && models.Length == 0)
                {
                    bool redirtied = entity.Has<NeedsRemeshFlag>();
                    ClearMesh(entry);
                    if (redirtied) entity.Set<NeedsRemeshFlag>();
                    continue;
                }

                GpuMesh? mesh = null;
                if (r.VertCount > 0)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    mesh = _renderer.UploadMesh(r.Verts.AsSpan(0, r.VertCount), r.Idxs.AsSpan(0, r.IdxCount));
                    _uploadMs += 0.05 * (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds - _uploadMs);
                }

                // The chunk's voxel base and the volume dims are derived live at draw time from the volume's
                // GPU resources (see ChunkRenderSystem), so a volume reallocation needs no remesh here. SetMesh clears
                // NeedsRemesh, so preserve a re-dirty that arrived while this job was in flight.
                {
                    bool redirtied = entity.Has<NeedsRemeshFlag>();

                    if (entity.Has<ChunkRenderData>())
                    {
                        entry.Entity.Get<ChunkRenderData>().Mesh?.Dispose();
                    }
                    entry.Entity.Remove<NeedsRemeshFlag>();
                    entry.Entity.Set(new ChunkRenderData
                    {
                        Mesh     = mesh,
                        Models   = models,
                        Grid     = volume.Gpu,
                        ChunkPos = entry.Position,
                    });

                    if (redirtied) entity.Set<NeedsRemeshFlag>();
                    _totalMeshed++;
                }
            }
            finally
            {
                if (r.Verts.Length > 0) ArrayPool<Vertex>.Shared.Return(r.Verts);
                if (r.Idxs.Length > 0) ArrayPool<uint>.Shared.Return(r.Idxs);
            }
        }
    }

    private void Cleanup()
    {
        foreach (var gpuMesh in _removed)
        {
            gpuMesh.Dispose();
        }

        _removed.Clear();
    }

    private static void ClearMesh(ChunkEntry entry)
    {   
        entry.Entity.Remove<NeedsRemeshFlag>();
        if (entry.Entity.Has<ChunkRenderData>())
            entry.Entity.Get<ChunkRenderData>().Mesh?.Dispose();
        entry.Entity.Remove<ChunkRenderData>();
    }

    // Which block ids are model blocks, so the per-voxel scan below is a table lookup.
    private static readonly bool[] IsModelBlock = BuildModelBlockTable();

    private static bool[] BuildModelBlockTable()
    {
        var t = new bool[256];
        for (int i = 0; i < t.Length; i++) t[i] = BlockRegistry.Get((BlockId)i).Model != null;
        return t;
    }

    /// <summary>Every model block in <paramref name="data"/> (worker thread).</summary>
    private static ModelCell[] FindModelBlocks(ChunkData data)
    {
        List<ModelCell>? found = null;
        int s = ChunkData.Size;
        for (int z = 0; z < s; z++)
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            var id = data.Get(x, y, z);
            if (!IsModelBlock[(byte)id]) continue;
            (found ??= new()).Add(new ModelCell((byte)x, (byte)y, (byte)z, id, data.GetFacing(x, y, z)));
        }
        return found?.ToArray() ?? Array.Empty<ModelCell>();
    }

    /// <summary>Attaches each found model block's GPU model (main thread: may load it); blocks whose model is
    /// unavailable are dropped.</summary>
    private ModelBlock[] ResolveModels(ModelCell[] cells)
    {
        if (cells.Length == 0) return Array.Empty<ModelBlock>();
        var result = new List<ModelBlock>(cells.Length);
        foreach (var c in cells)
            if (_blockModels.Get(c.Block) is { } model)
                result.Add(new ModelBlock(model, c.Block, c.X, c.Y, c.Z, c.Facing));
        return result.ToArray();
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Chunk Meshing";

    public void DrawDebugUi()
    {
        ImGui.Text($"Jobs in flight: {_inFlight} / {MaxInFlight}");
        ImGui.Text($"Upload (main thread, smoothed): {_uploadMs:F2} ms per chunk");
        ImGui.Text($"Chunks meshed (lifetime): {_totalMeshed}");
    }
}
