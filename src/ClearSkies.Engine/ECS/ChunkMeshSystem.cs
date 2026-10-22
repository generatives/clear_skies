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
/// <see cref="ChunkRenderData"/>: the greedy-meshed cubes (one mesh per <see cref="RenderLayer"/>) plus the chunk's model blocks (found in the same worker
/// job, resolved to their shared models through <see cref="BlockModelLibrary"/>). The greedy mesh itself
/// (~0.65ms per non-empty chunk, see StreamingBenchmark) runs on thread-pool workers, one
/// <see cref="GreedyMesher"/> per thread; only the GPU upload and the hand-back to the owning volume happen
/// here on the main thread.
///
/// A chunk has at most one job in flight. Workers read <see cref="ChunkData"/> while the main thread may edit
/// it; a torn read is harmless because any edit sets <see cref="ChunkEntry.NeedsRemesh"/> again, so the chunk
/// is re-dispatched once its current job lands (whose result is still applied — it's newer than the mesh on
/// screen). A result is dropped if its chunk was unloaded or its volume unregistered meanwhile.
///
/// Streamed terrain is meshed as it loads instead, by the loader's <see cref="Stage"/>; its meshes are only uploaded
/// here. The jobs are for everything else: edits, ships, and a chunk whose neighbour changed.
/// </summary>
public sealed partial class ChunkMeshSystem : ISystem, IDebugUiSystem
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
    // Of those, the one kept for the coarse levels of terrain while they wait (0 or 1): without it they waited behind
    // every full-detail chunk (they rank no nearer than where full detail ends, see NearestChunks), and while the view
    // moves full detail never runs out, so a coarse chunk could hold the fog waiting for its mesh.
    private int _coarseInFlight = 0;
    private readonly EntitySet _dirtyCoarse;
    private int _meshes, _remeshes; // jobs started, and those for a chunk that already had a mesh
    private readonly ConcurrentQueue<Result> _results = new();
    private readonly List<GpuMesh> _removed = new();

    private int _totalMeshed;
    private double _uploadMs, _createMs, _writeMs, _uploadKb;

    /// <summary>A model block's cell and orientation as found by the worker; resolved to a <see cref="ModelBlock"/>
    /// (which needs the GPU model) on the main thread.</summary>
    internal readonly record struct ModelCell(byte X, byte Y, byte Z, BlockId Block, BlockOrientation Orientation);

    /// <summary>One packed mesh: its quads as <see cref="ChunkQuad"/>s (rented; the first <see cref="Bytes"/> are used),
    /// uploaded as one buffer, and the words its light map takes (see <see cref="ChunkQuad.AssignLightOffsets"/>).</summary>
    internal readonly record struct Packed(byte[] Data, int Bytes, int QuadCount, uint LightWords)
    {
        public static readonly Packed None = new(Array.Empty<byte>(), 0, 0, 0);
    }

    /// <summary>A meshed chunk: its opaque, cut-out and translucent meshes, and its model blocks.</summary>
    private sealed record Result(Entity Entity, Packed Opaque, Packed Cutout, Packed Transparent, ModelCell[] Models,
                                 Exception? Error, bool Coarse);

    /// <summary>Main-thread time spent uploading meshes per frame, at most (at least one goes each frame): results past
    /// it wait for the next frame, so a burst of finished jobs doesn't stall one.</summary>
    private const double UploadBudgetMs = 4.0;

    public ChunkMeshSystem(World ecsWorld, Renderer renderer, BlockModelLibrary blockModels)
    {
        _ecsWorld = ecsWorld;
        _dirtyChunks = ecsWorld.GetEntities().With<Chunk>().With<Transform>().With<NeedsRemeshFlag>().With<Rendered>().AsSet();
        _dirtyCoarse = ecsWorld.GetEntities().With<Chunk>(IsCoarse).With<Transform>().With<NeedsRemeshFlag>().With<Rendered>().AsSet();
        _meshedChunks = ecsWorld.GetEntities().With<Chunk>().With<ChunkRenderData>().AsSet();
        _unrendered = ecsWorld.GetEntities().With<Chunk>().WhenRemoved<Rendered>().AsSet();
        _cameras = ecsWorld.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _renderer = renderer;
        _blockModels = blockModels;
        var atlas = renderer.Atlas;
        _meshers  = new ThreadLocal<GreedyMesher>(() => new GreedyMesher(atlas));

        _ecsWorld.SubscribeEntityDisposed(EntityDisposed);
        _ecsWorld.SubscribeComponentAdded((in Entity e, in PreparedMesh _) => _prepared.Enqueue(e));
    }

    // Chunks meshed as they loaded, in the order they loaded (nearest first), to upload; those not drawn yet wait in
    // _notDrawnYet for a frame.
    private readonly Queue<Entity> _prepared = new();
    private readonly List<Entity> _notDrawnYet = new();
    private int _totalPrepared, _preparedStale, _preparedBuried;

    /// <summary>Drops the mesh <paramref name="e"/> was given as it loaded, if it still has it (a newer one is coming, or
    /// it isn't wanted).</summary>
    private static void DropPrepared(Entity e)
    {
        if (!e.Has<PreparedMesh>()) return;
        e.Get<PreparedMesh>().Release();
        e.Remove<PreparedMesh>();
    }

    private readonly EntitySet _unrendered;

    /// <summary>Chunks no longer drawn (their grid or chunk lost <see cref="Rendered"/>) give up their meshes. Without a
    /// mesh they're out of date, so they're flagged: that's what gets them meshed again if they're drawn again. Nothing
    /// is meshed meanwhile, because only drawn chunks are (<c>_dirtyChunks</c> requires Rendered).</summary>
    private void ReleaseUnrendered()
    {
        foreach (var e in _unrendered.GetEntities().ToArray())
        {
            if (e.Has<Rendered>()) continue; // back again already
            DropPrepared(e);
            if (e.Has<ChunkRenderData>())
            {
                _removed.AddRange(e.Get<ChunkRenderData>().Meshes());
                e.Remove<ChunkRenderData>();
            }
            e.Remove<ChunkMeshedFlag>();
            e.Set<NeedsRemeshFlag>();
        }
        _unrendered.Complete();
    }

    private void EntityDisposed(in Entity e)
    {
        if (e.Has<PreparedMesh>()) e.Get<PreparedMesh>().Release();
        if (!e.Has<ChunkRenderData>()) return;
        _removed.AddRange(e.Get<ChunkRenderData>().Meshes());
    }

    public void Update(float dt)
    {
        ReleaseUnrendered();
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
    /// indices only ever join them as two triangles) as one 8-byte <see cref="ChunkQuad"/>, with its light map laid out
    /// (both sides of each quad for a <paramref name="twoSided"/> mesh).</summary>
    private static Packed PackQuads(LayerMesh mesh, bool twoSided = false)
    {
        ReadOnlySpan<Vertex> verts = CollectionsMarshal.AsSpan(mesh.Vertices);
        int quads = verts.Length / 4;
        if (quads == 0) return Packed.None;
        int bytes = quads * (int)ChunkQuad.SizeBytes;
        var packed = ArrayPool<byte>.Shared.Rent(bytes);
        var dst = MemoryMarshal.Cast<byte, ChunkQuad>(packed.AsSpan(0, bytes));
        for (int q = 0; q < quads; q++) dst[q] = ChunkQuad.Pack(verts.Slice(4 * q, 4), mesh.Blocks[q]);
        uint lightWords = ChunkQuad.AssignLightOffsets(dst, twoSided);
        return new Packed(packed, bytes, quads, lightWords);
    }

    private static bool IsCoarse(in Chunk chunk) => chunk.Entry.Volume.IsLowResolutionCopy;

    private void Dispatch()
    {
        // The coarse levels' job: the nearest coarse chunk waiting, whatever full detail is waiting.
        if (_coarseInFlight == 0 && _inFlight < MaxInFlight)
        {
            NearestChunks.Select(_dirtyCoarse, _cameras, 1 + 4, _nearest);
            foreach (var e in _nearest)
                if (Start(e, coarse: true)) break;
        }

        // The rest: closest to the camera first (see NearestChunks); a few spare picks for all-air and buried chunks,
        // which take no job.
        // While coarse chunks wait, one job is left for them (taken next frame if full detail has them all now).
        int free = MaxInFlight - _inFlight - (_coarseInFlight == 0 && _dirtyCoarse.Count > 0 ? 1 : 0);
        if (free <= 0) return;
        NearestChunks.Select(_dirtyChunks, _cameras, free + 4, _nearest);
        foreach (var e in _nearest)
            if (Start(e, coarse: false) && --free == 0) return;
    }

    /// <summary>Meshes a chunk: on a worker, or at once for an all-air or buried one. Whether it took a job.</summary>
    private bool Start(Entity e, bool coarse)
    {
        var chunk = e.Get<Chunk>();
        var entry = chunk.Entry;
        var pos = entry.Position;
        var volume = entry.Volume;
        DropPrepared(e); // this is newer

        // Fast path: pure air chunk, or one buried in opaque chunks (see ChunkVolume.IsBuried), which shows nothing.
        if (!entry.Data.HasAnyNonAir() || volume.IsBuried(pos))
        {
            ClearMesh(entry);
            return false;
        }

        entry.Entity.Remove<NeedsRemeshFlag>();

        _inFlight += 1;
        if (coarse) _coarseInFlight += 1;

        var data = entry.Data;
        _meshes++;
        if (e.Has<ChunkRenderData>()) _remeshes++;
        // A volume meshed without its neighbours (the streamed world) uses them only to cull transparent faces
        // (see ChunkVolume.MeshIgnoresNeighbours): its other border faces are all drawn.
        bool alone = volume.MeshIgnoresNeighbours;
        var around = new ChunkData?[6];
        for (int f = 0; f < 6; f++) around[f] = volume.GetData(pos.Offset(Offsets[f].X, Offsets[f].Y, Offsets[f].Z));
        // Held until the job is done, so a chunk unloaded meanwhile doesn't hand its arrays to another chunk.
        data.Retain();
        foreach (var n in around) n?.Retain();
        BackgroundWork.Soon(() =>
        {
            try
            {
                var (opaque, cutout, transparent) = MeshAndPack(_meshers.Value!, data, around, alone, pos);
                _results.Enqueue(new Result(entry.Entity, opaque, cutout, transparent, FindModelBlocks(data), null, coarse));
            }
            catch (Exception e)
            {
                _results.Enqueue(new Result(entry.Entity, Packed.None, Packed.None, Packed.None, Array.Empty<ModelCell>(), e, coarse));
            }
            finally
            {
                data.Unretain();
                foreach (var n in around) n?.Unretain();
            }
        });
        return true;
    }

    /// <summary>Meshes and packs a chunk (worker thread), with its neighbours in <see cref="Offsets"/>' order (null for
    /// none); <paramref name="alone"/> as <see cref="ChunkVolume.MeshIgnoresNeighbours"/>.</summary>
    private static (Packed Opaque, Packed Cutout, Packed Transparent) MeshAndPack(GreedyMesher mesher, ChunkData data,
                                                                                ChunkData?[] around, bool alone,
                                                                                ChunkPosition pos)
    {
        // The mesher's lists are per-thread scratch, so copy out before this thread meshes again.
        var mesh = mesher.Mesh(data, around[0], around[1], around[2], around[3], around[4], around[5],
                               neighboursForTransparentOnly: alone, position: pos);
        return (PackQuads(mesh.Opaque), PackQuads(mesh.Cutout),
                PackQuads(mesh.Translucent, twoSided: true)); // water is seen from below too
    }

    private void ApplyResults()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        bool InBudget() => Ms(start, System.Diagnostics.Stopwatch.GetTimestamp()) < UploadBudgetMs;
        while (InBudget() && _results.TryDequeue(out var r))
        {
            _inFlight -= 1;
            if (r.Coarse) _coarseInFlight -= 1;
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
                if (!entity.Has<Rendered>()) { entity.Set<NeedsRemeshFlag>(); continue; } // no longer drawn
                Apply(entity, r.Opaque, r.Cutout, r.Transparent, r.Models);
            }
            finally
            {
                if (r.Opaque.Data.Length > 0) ArrayPool<byte>.Shared.Return(r.Opaque.Data);
                if (r.Cutout.Data.Length > 0) ArrayPool<byte>.Shared.Return(r.Cutout.Data);
                if (r.Transparent.Data.Length > 0) ArrayPool<byte>.Shared.Return(r.Transparent.Data);
            }
        }

        // Then the meshes made as chunks loaded.
        while (InBudget() && _prepared.TryDequeue(out var e))
        {
            if (!e.IsAlive || !e.Has<PreparedMesh>()) continue; // unloaded, or meshed again since
            if (!e.Has<Rendered>()) { _notDrawnYet.Add(e); continue; } // its presence is decided on the next tick
            var prepared = e.Get<PreparedMesh>();
            e.Remove<PreparedMesh>();
            try
            {
                var entry = e.Get<Chunk>().Entry;
                if (!prepared.Meshed)
                {
                    if (entry.Volume.IsBuried(entry.Position)) { ClearMesh(entry); _preparedBuried++; }
                    else e.Set<NeedsRemeshFlag>();
                    continue;
                }
                // A see-through face's neighbour came or went since it was made: shown as it is until it's remeshed.
                if (!prepared.CurrentIn(entry.Volume, entry.Position))
                {
                    e.Set<NeedsRemeshFlag>();
                    _preparedStale++;
                }
                Apply(e, prepared.Opaque, prepared.Cutout, prepared.Transparent, prepared.Models);
                _totalPrepared++;
            }
            finally
            {
                prepared.Release();
            }
        }
        foreach (var e in _notDrawnYet) _prepared.Enqueue(e);
        _notDrawnYet.Clear();
    }

    /// <summary>Gives a drawn chunk its new mesh (main thread).</summary>
    private void Apply(Entity entity, in Packed opaque, in Packed cutout, in Packed transparent, ModelCell[] modelCells)
    {
        var chunk = entity.Get<Chunk>();
        var entry = chunk.Entry;
        var volume = entry.Volume;

        var models = ResolveModels(modelCells);
        // Nothing to draw, or buried since the job started (a neighbour loaded meanwhile).
        if (opaque.QuadCount == 0 && cutout.QuadCount == 0 && transparent.QuadCount == 0 && models.Length == 0
            || volume.IsBuried(entry.Position))
        {
            bool redirtied = entity.Has<NeedsRemeshFlag>();
            ClearMesh(entry);
            if (redirtied) entity.Set<NeedsRemeshFlag>();
            return;
        }

        var mesh = Upload(opaque, LightmapKind.Opaque);
        var cutoutMesh = Upload(cutout, LightmapKind.SeeThrough);
        var transparentMesh = Upload(transparent, LightmapKind.SeeThrough | LightmapKind.TwoSided);

        // The chunk's voxel base and the volume dims are derived live at draw time from the volume's
        // GPU resources (see ChunkRenderSystem), so a volume reallocation needs no remesh here. SetMesh clears
        // NeedsRemesh, so preserve a re-dirty that arrived while this job was in flight.
        bool redirty = entity.Has<NeedsRemeshFlag>();
        if (entity.Has<ChunkRenderData>())
        {
            foreach (var old in entry.Entity.Get<ChunkRenderData>().Meshes()) old.Dispose();
        }
        entry.Entity.Remove<NeedsRemeshFlag>();
        entry.Entity.Set<ChunkMeshedFlag>();
        entry.Entity.Set(new ChunkRenderData
        {
            Mesh     = mesh,
            CutoutMesh = cutoutMesh,
            TransparentMesh = transparentMesh,
            Models   = models,
            Grid     = volume.Gpu,
            ChunkPos = entry.Position,
        });

        if (redirty) entity.Set<NeedsRemeshFlag>();
        _totalMeshed++;
    }

    /// <summary>Uploads one packed mesh (main thread), or returns null for an empty one.</summary>
    private GpuMesh? Upload(in Packed p, LightmapKind kind)
    {
        if (p.QuadCount == 0) return null;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var mesh = _renderer.UploadChunkQuads(p.Data.AsSpan(0, p.Bytes), (uint)p.QuadCount, p.LightWords, kind);
        _uploadMs += 0.05 * (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds - _uploadMs);
        _createMs += 0.05 * (_renderer.LastCreateMs - _createMs);
        _writeMs  += 0.05 * (_renderer.LastWriteMs - _writeMs);
        _uploadKb += 0.05 * (p.Bytes / 1024.0 - _uploadKb);
        return mesh;
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
        entry.Entity.Set<ChunkMeshedFlag>();
        if (entry.Entity.Has<ChunkRenderData>())
            foreach (var mesh in entry.Entity.Get<ChunkRenderData>().Meshes()) mesh.Dispose();
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
        ImGui.Text($"Jobs in flight: {_inFlight} / {MaxInFlight} ({_coarseInFlight} kept for coarse terrain), " +
                   $"chunks waiting to mesh: {_dirtyChunks.Count:N0} ({_dirtyCoarse.Count:N0} coarse)");
        ImGui.Text($"Main thread (smoothed): results {_applyMs:F2} ms, choosing + dispatch {_dispatchMs:F2} ms, " +
                   $"freeing meshes {_cleanupMs:F2} ms");
        ImGui.Text($"Upload (main thread, smoothed): {_uploadMs:F2} ms per chunk " +
                   $"(creating the buffer {_createMs:F2} ms, writing it {_writeMs:F2} ms, {_uploadKb:F0} KB)");
        ImGui.Text($"Chunks meshed (lifetime): {_totalMeshed}; of the jobs, {_remeshes:N0} of {_meshes:N0} were remeshes");
        ImGui.Text($"Meshed as they loaded: {_totalPrepared:N0} uploaded, {_prepared.Count:N0} waiting; " +
                   $"{_preparedStale:N0} remeshed after for a see-through face's neighbour, " +
                   $"{_preparedBuried:N0} solid ones left unmeshed and found buried");
    }
}
