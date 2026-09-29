using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;
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
    private int _meshes, _remeshes; // jobs started, and those for a chunk that already had a mesh
    private readonly ConcurrentQueue<Result> _results = new();
    private readonly List<GpuMesh> _removed = new();

    private int _totalMeshed;
    private double _uploadMs, _createMs, _writeMs, _uploadKb;

    /// <summary>A model block's cell and orientation as found by the worker; resolved to a <see cref="ModelBlock"/>
    /// (which needs the GPU model) on the main thread.</summary>
    private readonly record struct ModelCell(byte X, byte Y, byte Z, BlockId Block, BlockOrientation Orientation);

    /// <summary>A meshed chunk: its quads packed as <see cref="ChunkQuad"/>s (rented; the first <see cref="Bytes"/> are
    /// used), uploaded as one buffer.</summary>
    private sealed record Result(Entity Entity, byte[] Packed, int Bytes, int QuadCount, int[] FaceEnds, ModelCell[] Models,
                                 Exception? Error);

    /// <summary>Main-thread time spent uploading meshes per frame, at most (at least one goes each frame): results past
    /// it wait for the next frame, so a burst of finished jobs doesn't stall one.</summary>
    private const double UploadBudgetMs = 4.0;

    public ChunkMeshSystem(World ecsWorld, Renderer renderer, BlockModelLibrary blockModels)
    {
        _ecsWorld = ecsWorld;
        _dirtyChunks = ecsWorld.GetEntities().With<Chunk>().With<Transform>().With<NeedsRemeshFlag>().With<Rendered>().AsSet();
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

    private readonly EntitySet _meshedChunks;

    /// <summary>Packs a chunk mesh for upload, off the main thread: each of the mesher's quads (four vertices; the
    /// indices only ever join them as two triangles) as one 8-byte <see cref="ChunkQuad"/>, grouped by the way they
    /// face (+X, -X, +Y, -Y, +Z, -Z), so the world's culling pass can skip the groups facing away from the camera.
    /// Returns the rented block, its used length, the quad count and where each face's group ends.</summary>
    private static (byte[] Packed, int Bytes, int Quads, int[] FaceEnds) PackQuads(ReadOnlySpan<Vertex> verts)
    {
        int quads = verts.Length / 4;
        int bytes = quads * (int)ChunkQuad.SizeBytes;
        var packed = ArrayPool<byte>.Shared.Rent(System.Math.Max(8, bytes));
        var dst = MemoryMarshal.Cast<byte, ChunkQuad>(packed.AsSpan(0, bytes));
        var ends = new int[6];
        Span<int> at = stackalloc int[6];
        for (int q = 0; q < quads; q++) ends[FaceOf(verts[4 * q].Normal)]++;
        for (int f = 0, sum = 0; f < 6; f++) { at[f] = sum; sum += ends[f]; ends[f] = sum; }
        for (int q = 0; q < quads; q++)
        {
            var quad = ChunkQuad.Pack(verts.Slice(4 * q, 4));
            dst[at[quad.Face]++] = quad;
        }
        return (packed, bytes, quads, ends);
    }

    private static int FaceOf(Vector3D<float> n)
        => n.X > 0.5f ? 0 : n.X < -0.5f ? 1 : n.Y > 0.5f ? 2 : n.Y < -0.5f ? 3 : n.Z > 0.5f ? 4 : 5;

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
            _meshes++;
            if (e.Has<ChunkRenderData>()) _remeshes++;
            // A volume meshed without its neighbours (the streamed world) passes none: its border faces are all drawn.
            bool alone = volume.MeshIgnoresNeighbours;
            var nX = alone ? null : volume.GetData(pos.Offset(-1, 0, 0)); var pX = alone ? null : volume.GetData(pos.Offset(1, 0, 0));
            var nY = alone ? null : volume.GetData(pos.Offset(0, -1, 0)); var pY = alone ? null : volume.GetData(pos.Offset(0, 1, 0));
            var nZ = alone ? null : volume.GetData(pos.Offset(0, 0, -1)); var pZ = alone ? null : volume.GetData(pos.Offset(0, 0, 1));
            var vol = volume;
            // Held until the job is done, so a chunk unloaded meanwhile doesn't hand its arrays to another chunk.
            data.Retain(); nX?.Retain(); pX?.Retain(); nY?.Retain(); pY?.Retain(); nZ?.Retain(); pZ?.Retain();
            BackgroundWork.Soon(() =>
            {
                try
                {
                    // The mesher's lists are per-thread scratch, so copy out before this thread meshes again.
                    var (verts, _) = _meshers.Value!.Mesh(data, nX, pX, nY, pY, nZ, pZ);
                    var (packed, bytes, quads, faceEnds) = PackQuads(CollectionsMarshal.AsSpan(verts));
                    _results.Enqueue(new Result(entry.Entity, packed, bytes, quads, faceEnds, FindModelBlocks(data), null));
                }
                catch (Exception e)
                {
                    _results.Enqueue(new Result(entry.Entity, Array.Empty<byte>(), 0, 0, Array.Empty<int>(), Array.Empty<ModelCell>(), e));
                }
                finally
                {
                    data.Unretain(); nX?.Unretain(); pX?.Unretain(); nY?.Unretain(); pY?.Unretain(); nZ?.Unretain(); pZ?.Unretain();
                }
            });

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
                if (r.QuadCount == 0 && models.Length == 0)
                {
                    bool redirtied = entity.Has<NeedsRemeshFlag>();
                    ClearMesh(entry);
                    if (redirtied) entity.Set<NeedsRemeshFlag>();
                    continue;
                }

                GpuMesh? mesh = null;
                if (r.QuadCount > 0)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var quads = r.Packed.AsSpan(0, r.Bytes);
                    mesh = volume.PoolMeshes && volume.Gpu.Index >= 0
                        ? _renderer.UploadWorldChunkQuads(quads, (uint)r.QuadCount, r.FaceEnds, volume.Gpu.Index, entry.Position)
                        : _renderer.UploadChunkQuads(quads, (uint)r.QuadCount);
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
        ImGui.Text($"Chunks meshed (lifetime): {_totalMeshed}; of the jobs, {_remeshes:N0} of {_meshes:N0} were remeshes");
    }
}
