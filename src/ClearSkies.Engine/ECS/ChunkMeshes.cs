using System.Buffers;
using System.Runtime.InteropServices;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// How chunks are meshed and their meshes put on screen, for the chunk pipeline's Mesh and Upload stages (see
/// <see cref="MeshStage"/>): the greedy mesh (one per <see cref="RenderLayer"/>) plus the chunk's model blocks, found
/// on a worker with one <see cref="GreedyMesher"/> a thread (~0.65ms per non-empty chunk, see StreamingBenchmark), then
/// uploaded on the main thread into the chunk's <see cref="ChunkRenderData"/>. A chunk's meshes go with it.
/// </summary>
public sealed class ChunkMeshes : IDebugUiSystem
{
    private readonly Renderer _renderer;
    private readonly BlockModelLibrary _blockModels;
    private readonly ThreadLocal<GreedyMesher> _meshers;

    private int _totalMeshed;
    private double _uploadMs, _createMs, _writeMs, _uploadKb;

    public ChunkMeshes(World ecsWorld, Renderer renderer, BlockModelLibrary blockModels)
    {
        _renderer = renderer;
        _blockModels = blockModels;
        var atlas = renderer.Atlas;
        _meshers  = new ThreadLocal<GreedyMesher>(() => new GreedyMesher(atlas));
        ecsWorld.SubscribeEntityDisposed(EntityDisposed);
    }

    private static void EntityDisposed(in Entity e)
    {
        if (!e.Has<ChunkRenderData>()) return;
        foreach (var mesh in e.Get<ChunkRenderData>().Meshes()) mesh.Dispose();
    }

    /// <summary>Packs a chunk mesh for upload, off the main thread: each of the mesher's quads (four vertices; the
    /// indices only ever join them as two triangles) as one 8-byte <see cref="ChunkQuad"/>, with its light map laid out
    /// (both sides of each quad for a <paramref name="twoSided"/> mesh). The quads are grouped by the way they face (+X,
    /// -X, +Y, -Y, +Z, -Z, then cross blocks'), so a draw can skip the groups facing away from the camera.</summary>
    private static PackedChunkMesh.Packed PackQuads(LayerMesh mesh, bool twoSided = false)
    {
        ReadOnlySpan<Vertex> verts = CollectionsMarshal.AsSpan(mesh.Vertices);
        int quads = verts.Length / 4;
        if (quads == 0) return PackedChunkMesh.Packed.None;
        int bytes = quads * (int)ChunkQuad.SizeBytes;
        var packed = ArrayPool<byte>.Shared.Rent(bytes);
        var dst = MemoryMarshal.Cast<byte, ChunkQuad>(packed.AsSpan(0, bytes));
        var ends = new int[GpuMesh.FaceGroups];
        Span<int> at = stackalloc int[GpuMesh.FaceGroups];
        for (int q = 0; q < quads; q++) ends[GpuMesh.FaceGroupOf(ChunkQuad.Pack(verts.Slice(4 * q, 4), mesh.Blocks[q]))]++;
        for (int g = 0, sum = 0; g < ends.Length; g++) { at[g] = sum; sum += ends[g]; ends[g] = sum; }
        for (int q = 0; q < quads; q++)
        {
            var quad = ChunkQuad.Pack(verts.Slice(4 * q, 4), mesh.Blocks[q]);
            dst[at[GpuMesh.FaceGroupOf(quad)]++] = quad;
        }
        uint lightWords = ChunkQuad.AssignLightOffsets(dst, twoSided);
        return new PackedChunkMesh.Packed(packed, bytes, quads, lightWords, ends);
    }


    /// <summary>Meshes and packs a chunk against its neighbours (null for one not loaded), on a worker; failures are
    /// returned, not thrown. A volume meshed without its neighbours (<paramref name="alone"/>) uses them only to cull
    /// transparent faces.</summary>
    internal PackedChunkMesh MeshOnWorker(ChunkData data, ChunkData? nX, ChunkData? pX, ChunkData? nY, ChunkData? pY,
                                          ChunkData? nZ, ChunkData? pZ, bool alone, ChunkPosition pos)
    {
        try
        {
            // The mesher's lists are per-thread scratch, so copy out before this thread meshes again.
            var mesher = _meshers.Value!;
            var mesh = mesher.Mesh(data, nX, pX, nY, pY, nZ, pZ, neighboursForTransparentOnly: alone, position: pos);
            var opaque = PackQuads(mesh.Opaque);
            var cutout = PackQuads(mesh.Cutout);
            var transparent = PackQuads(mesh.Translucent, twoSided: true); // water is seen from below too
            return new PackedChunkMesh(opaque, cutout, transparent, FindModelBlocks(data), null);
        }
        catch (Exception e)
        {
            return new PackedChunkMesh(PackedChunkMesh.Packed.None, PackedChunkMesh.Packed.None, PackedChunkMesh.Packed.None,
                                       Array.Empty<PackedChunkMesh.ModelCell>(), e);
        }
    }


    /// <summary>Puts a chunk's new mesh in place of the one it had (main thread): uploads it, or clears the chunk's mesh
    /// if there's nothing to draw or the chunk was buried meanwhile (a neighbour loaded). Returns the packed buffers.
    /// Clears the chunk's <see cref="NeedsRemeshFlag"/>.</summary>
    internal void Show(ChunkEntry entry, PackedChunkMesh packed)
    {
        try
        {
            if (packed.Error is not null)
            {
                Console.WriteLine($"[mesh] chunk failed: {packed.Error}");
                return;
            }
            var models = ResolveModels(packed.Models);
            if (packed.IsEmpty && models.Length == 0 || entry.Volume.IsBuried(entry.Position))
            {
                ClearMesh(entry);
                return;
            }

            var mesh = Upload(packed.Opaque, LightmapKind.Opaque);
            var cutoutMesh = Upload(packed.Cutout, LightmapKind.SeeThrough);
            var transparentMesh = Upload(packed.Transparent, LightmapKind.SeeThrough | LightmapKind.TwoSided);

            // The chunk's voxel base and the volume dims are derived live at draw time from the volume's GPU resources
            // (see ChunkRenderSystem), so a volume reallocation needs no remesh here.
            var entity = entry.Entity;
            if (entity.Has<ChunkRenderData>())
                foreach (var old in entity.Get<ChunkRenderData>().Meshes()) old.Dispose();
            entity.Remove<NeedsRemeshFlag>();
            entity.Set<ChunkMeshedFlag>();
            entity.Set(new ChunkRenderData
            {
                Mesh            = mesh,
                CutoutMesh      = cutoutMesh,
                TransparentMesh = transparentMesh,
                Models          = models,
                Grid            = entry.Volume.Gpu,
                ChunkPos        = entry.Position,
            });
            _totalMeshed++;
        }
        finally
        {
            packed.ReturnBuffers();
        }
    }

    /// <summary>Uploads one packed mesh (main thread), or returns null for an empty one.</summary>
    private GpuMesh? Upload(in PackedChunkMesh.Packed p, LightmapKind kind)
    {
        if (p.QuadCount == 0) return null;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var mesh = _renderer.UploadChunkQuads(p.Data.AsSpan(0, p.Bytes), (uint)p.QuadCount, p.LightWords, kind, p.FaceEnds);
        _uploadMs += 0.05 * (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds - _uploadMs);
        _createMs += 0.05 * (_renderer.LastCreateMs - _createMs);
        _writeMs  += 0.05 * (_renderer.LastWriteMs - _writeMs);
        _uploadKb += 0.05 * (p.Bytes / 1024.0 - _uploadKb);
        return mesh;
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
    private static PackedChunkMesh.ModelCell[] FindModelBlocks(ChunkData data)
    {
        List<PackedChunkMesh.ModelCell>? found = null;
        int s = ChunkData.Size;
        for (int z = 0; z < s; z++)
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            var id = data.Get(x, y, z);
            if (!IsModelBlock[(byte)id]) continue;
            (found ??= new()).Add(new PackedChunkMesh.ModelCell((byte)x, (byte)y, (byte)z, id, data.GetOrientation(x, y, z)));
        }
        return found?.ToArray() ?? Array.Empty<PackedChunkMesh.ModelCell>();
    }

    /// <summary>Attaches each found model block's GPU model (main thread: may load it); blocks whose model is
    /// unavailable are dropped.</summary>
    private ModelBlock[] ResolveModels(PackedChunkMesh.ModelCell[] cells)
    {
        if (cells.Length == 0) return Array.Empty<ModelBlock>();
        var result = new List<ModelBlock>(cells.Length);
        foreach (var c in cells)
            if (_blockModels.Get(c.Block) is { } model)
                result.Add(new ModelBlock(model, c.Block, c.X, c.Y, c.Z, c.Orientation));
        return result.ToArray();
    }

    // ── debug UI ─────────────────────────────────────────────────────────────

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Chunk Meshing";

    public void DrawDebugUi()
    {
        ImGui.Text($"Upload (main thread, smoothed): {_uploadMs:F2} ms per chunk " +
                   $"(creating the buffer {_createMs:F2} ms, writing it {_writeMs:F2} ms, {_uploadKb:F0} KB)");
        ImGui.Text($"Chunks meshed (lifetime): {_totalMeshed:N0}");
    }
}

/// <summary>A chunk meshed and packed for upload (see <see cref="ChunkMeshes.MeshOnWorker"/>): its opaque, cut-out
/// and translucent meshes, its model blocks, or what went wrong. Its buffers are rented until it's shown or let go of
/// (<see cref="ReturnBuffers"/>).</summary>
public sealed class PackedChunkMesh
{
    /// <summary>A model block's cell and orientation as found by the worker; resolved to a <see cref="ModelBlock"/>
    /// (which needs the GPU model) on the main thread.</summary>
    internal readonly record struct ModelCell(byte X, byte Y, byte Z, BlockId Block, BlockOrientation Orientation);

    /// <summary>One packed mesh: its quads as <see cref="ChunkQuad"/>s (rented; the first <see cref="Bytes"/> are used),
    /// uploaded as one buffer, and the words its light map takes (see <see cref="ChunkQuad.AssignLightOffsets"/>); its
    /// quads grouped by the way they face, with where each group ends (see <see cref="GpuMesh.FaceEnds"/>).</summary>
    internal readonly record struct Packed(byte[] Data, int Bytes, int QuadCount, uint LightWords, int[]? FaceEnds)
    {
        public static readonly Packed None = new(Array.Empty<byte>(), 0, 0, 0, null);
    }

    internal PackedChunkMesh(Packed opaque, Packed cutout, Packed transparent, ModelCell[] models, Exception? error)
    {
        Opaque = opaque;
        Cutout = cutout;
        Transparent = transparent;
        Models = models;
        Error = error;
    }

    /// <summary>Nothing to draw.</summary>
    internal static PackedChunkMesh Nothing()
        => new(Packed.None, Packed.None, Packed.None, Array.Empty<ModelCell>(), null);

    /// <summary>Not meshed: whatever mesh the chunk has stays.</summary>
    internal static readonly PackedChunkMesh Unchanged = Nothing();

    internal Packed Opaque { get; private set; }
    internal Packed Cutout { get; private set; }
    internal Packed Transparent { get; private set; }
    internal ModelCell[] Models { get; }
    public Exception? Error { get; }

    /// <summary>No quads to draw (it may still have model blocks).</summary>
    public bool IsEmpty => Opaque.QuadCount == 0 && Cutout.QuadCount == 0 && Transparent.QuadCount == 0;

    /// <summary>Gives the rented buffers back (once uploaded, or not wanted): safe to call more than once.</summary>
    public void ReturnBuffers()
    {
        if (Opaque.Data.Length > 0) ArrayPool<byte>.Shared.Return(Opaque.Data);
        if (Cutout.Data.Length > 0) ArrayPool<byte>.Shared.Return(Cutout.Data);
        if (Transparent.Data.Length > 0) ArrayPool<byte>.Shared.Return(Transparent.Data);
        Opaque = Cutout = Transparent = Packed.None;
    }
}
