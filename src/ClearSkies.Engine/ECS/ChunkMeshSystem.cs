using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
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
    private readonly EntitySet _cameras;
    private readonly List<Entity> _nearest = new();
    private readonly Renderer _renderer;
    private readonly BlockModelLibrary _blockModels;
    private readonly ThreadLocal<GreedyMesher> _meshers;

    private int _inFlight = 0;
    private readonly ConcurrentQueue<Result> _results = new();
    private readonly List<GpuMesh> _removed = new();

    private int _totalMeshed;
    private double _uploadMs, _createMs, _writeMs, _uploadKb;

    /// <summary>A model block's cell and orientation as found by the worker; resolved to a <see cref="ModelBlock"/>
    /// (which needs the GPU model) on the main thread.</summary>
    private readonly record struct ModelCell(byte X, byte Y, byte Z, BlockId Block, BlockOrientation Orientation);

    /// <summary>A meshed chunk: its vertices, indices and wireframe indices packed into one block (rented; the first
    /// <see cref="Bytes"/> are used), uploaded as one buffer.</summary>
    private sealed record Result(Entity Entity, byte[] Packed, int Bytes, int VertCount, int IdxCount, int WireCount,
                                 bool WideIndices, ModelCell[] Models, Exception? Error);

    /// <summary>Main-thread time spent uploading meshes per frame, at most (at least one goes each frame): results past
    /// it wait for the next frame, so a burst of finished jobs doesn't stall one.</summary>
    private const double UploadBudgetMs = 4.0;

    public ChunkMeshSystem(World ecsWorld, Renderer renderer, BlockModelLibrary blockModels)
    {
        _ecsWorld = ecsWorld;
        _dirtyChunks = ecsWorld.GetEntities().With<Chunk>().With<Transform>().With<NeedsRemeshFlag>().AsSet();
        _meshedChunks = ecsWorld.GetEntities().With<Chunk>().With<ChunkRenderData>().AsSet();
        _cameras = ecsWorld.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
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
        // Wireframe indices are only built while wireframe mode is on (they'd be a third of every chunk's upload):
        // turning it on remeshes everything loaded so it gets them.
        bool wireframe = _renderer.WireframeMode;
        if (wireframe && !_wireframe)
            foreach (var e in _meshedChunks.GetEntities().ToArray()) e.Set<NeedsRemeshFlag>();
        _wireframe = wireframe;

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        ApplyResults();
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Dispatch();
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        Cleanup();
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        _applyMs    += 0.05 * (Ms(t0, t1) - _applyMs);
        _dispatchMs += 0.05 * (Ms(t1, t2) - _dispatchMs);
        _cleanupMs  += 0.05 * (Ms(t2, t3) - _cleanupMs);
    }

    private static double Ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    private double _applyMs, _dispatchMs, _cleanupMs;

    private bool _wireframe;
    private readonly EntitySet _meshedChunks;

    /// <summary>Packs a chunk mesh for upload, off the main thread: the vertices as <see cref="ChunkVertex"/> (8 bytes,
    /// not a 48-byte <see cref="Vertex"/>), the indices, then (if <paramref name="wireframe"/>) the wireframe's line
    /// list. Indices are 16-bit unless the mesh has more vertices than that reaches. Returns the rented block, its used
    /// length, the wireframe index count and whether the indices are 32-bit.</summary>
    private static (byte[] Packed, int Bytes, int WireCount, bool Wide) PackMesh(ReadOnlySpan<Vertex> verts,
                                                                                ReadOnlySpan<uint> idxs, bool wireframe)
    {
        bool wide = verts.Length > ushort.MaxValue + 1;
        int size = wide ? 4 : 2;
        int wireCount = wireframe ? idxs.Length * 2 : 0;
        int vLen = verts.Length * (int)ChunkVertex.SizeBytes;
        int bytes = vLen + (idxs.Length + wireCount) * size; // a multiple of 4: quads have 6 indices, the wireframe 12
        var packed = ArrayPool<byte>.Shared.Rent(System.Math.Max(4, bytes));

        var cv = MemoryMarshal.Cast<byte, ChunkVertex>(packed.AsSpan(0, vLen));
        for (int k = 0; k < verts.Length; k++) cv[k] = ChunkVertex.Pack(verts[k]);

        int at = vLen;
        if (wide)
        {
            var dst = MemoryMarshal.Cast<byte, uint>(packed.AsSpan(at, (idxs.Length + wireCount) * 4));
            idxs.CopyTo(dst);
            if (wireframe) WriteWireframe(idxs, dst.Slice(idxs.Length));
        }
        else
        {
            var dst = MemoryMarshal.Cast<byte, ushort>(packed.AsSpan(at, (idxs.Length + wireCount) * 2));
            for (int k = 0; k < idxs.Length; k++) dst[k] = (ushort)idxs[k];
            if (wireframe) WriteWireframe(idxs, dst.Slice(idxs.Length));
        }
        return (packed, bytes, wireCount, wide);
    }

    /// <summary>A triangle list's edges as a line list (each triangle's three edges).</summary>
    private static void WriteWireframe<T>(ReadOnlySpan<uint> tris, Span<T> lines) where T : unmanaged
    {
        int li = 0;
        for (int i = 0; i < tris.Length; i += 3)
        {
            uint a = tris[i], b = tris[i + 1], c = tris[i + 2];
            lines[li++] = Index<T>(a); lines[li++] = Index<T>(b);
            lines[li++] = Index<T>(b); lines[li++] = Index<T>(c);
            lines[li++] = Index<T>(c); lines[li++] = Index<T>(a);
        }
    }

    private static T Index<T>(uint i) where T : unmanaged
    {
        if (typeof(T) == typeof(ushort)) { ushort u = (ushort)i; return System.Runtime.CompilerServices.Unsafe.As<ushort, T>(ref u); }
        return System.Runtime.CompilerServices.Unsafe.As<uint, T>(ref i);
    }

    private void Dispatch()
    {
        if (_inFlight >= MaxInFlight) return;

        // Closest to the camera first (see NearestChunks); a few spare picks for all-air chunks, which take no job.
        NearestChunks.Select(_dirtyChunks, _cameras, MaxInFlight - _inFlight + 4, _nearest);
        foreach (var e in _nearest)
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
            bool wireframe = _renderer.WireframeMode;
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try
                {
                    // The mesher's lists are per-thread scratch, so copy out before this thread meshes again.
                    var (verts, idxs) = _meshers.Value!.Mesh(data, nX, pX, nY, pY, nZ, pZ);
                    var (packed, bytes, wireCount, wide) = PackMesh(CollectionsMarshal.AsSpan(verts), CollectionsMarshal.AsSpan(idxs), wireframe);
                    _results.Enqueue(new Result(entry.Entity, packed, bytes, verts.Count, idxs.Count, wireCount, wide,
                                                FindModelBlocks(data), null));
                }
                catch (Exception e)
                {
                    _results.Enqueue(new Result(entry.Entity, Array.Empty<byte>(), 0, 0, 0, 0, false, Array.Empty<ModelCell>(), e));
                }
            }, null);

            if (_inFlight >= MaxInFlight) return;
        }
    }

    private void ApplyResults()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (Ms(start, System.Diagnostics.Stopwatch.GetTimestamp()) < UploadBudgetMs && _results.TryDequeue(out var r))
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
                    mesh = _renderer.UploadPackedMesh(r.Packed.AsSpan(0, r.Bytes), (ulong)r.VertCount * ChunkVertex.SizeBytes,
                                                      (uint)r.IdxCount, (uint)r.WireCount,
                                                      r.WideIndices ? IndexFormat.Uint32 : IndexFormat.Uint16);
                    _uploadMs += 0.05 * (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds - _uploadMs);
                    _createMs += 0.05 * (_renderer.LastCreateMs - _createMs);
                    _writeMs  += 0.05 * (_renderer.LastWriteMs - _writeMs);
                    _uploadKb += 0.05 * (r.Bytes / 1024.0 - _uploadKb);
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
                if (r.Packed.Length > 0) ArrayPool<byte>.Shared.Return(r.Packed);
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

    // Which block ids are static model blocks, so the per-voxel scan below is a table lookup. Entity blocks with a
    // model are left out: ModelRenderSystem draws those at their entity's Transform (see BlockModelSystem).
    private static readonly bool[] IsModelBlock = BuildModelBlockTable();

    private static bool[] BuildModelBlockTable()
    {
        var t = new bool[256];
        for (int i = 0; i < t.Length; i++)
        {
            var def = BlockRegistry.Get((BlockId)i);
            t[i] = def.Model != null && !def.IsEntityBlock;
        }
        return t;
    }

    /// <summary>Every static model block in <paramref name="data"/> (worker thread).</summary>
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
            (found ??= new()).Add(new ModelCell((byte)x, (byte)y, (byte)z, id, data.GetOrientation(x, y, z)));
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
                result.Add(new ModelBlock(model, c.Block, c.X, c.Y, c.Z, c.Orientation));
        return result.ToArray();
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Chunk Meshing";

    public void DrawDebugUi()
    {
        ImGui.Text($"Jobs in flight: {_inFlight} / {MaxInFlight}, chunks waiting to mesh: {_dirtyChunks.Count:N0}");
        ImGui.Text($"Main thread (smoothed): results {_applyMs:F2} ms, choosing + dispatch {_dispatchMs:F2} ms, " +
                   $"freeing meshes {_cleanupMs:F2} ms");
        ImGui.Text($"Upload (main thread, smoothed): {_uploadMs:F2} ms per chunk " +
                   $"(creating the buffer {_createMs:F2} ms, writing it {_writeMs:F2} ms, {_uploadKb:F0} KB)");
        ImGui.Text($"Chunks meshed (lifetime): {_totalMeshed}");
    }
}
