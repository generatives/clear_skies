using System.Collections.Concurrent;
using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>Terrain drawn beyond what's loaded in full detail, for the fog (see <see cref="FogSystem.Far"/>).</summary>
public interface IFarTerrain
{
    /// <summary>Whether it's drawing: the fog then follows it instead of the full-detail terrain.</summary>
    bool Active { get; }

    /// <summary>How far from <paramref name="centre"/> (horizontally) the terrain is ready to be seen.</summary>
    float ReadyRadius(Vector3D<float> centre);
}

/// <summary>
/// Distant terrain at half resolution (level 1 of the LOD plan): past <see cref="FullDetailRadius"/> the world is drawn
/// from coarse 32³ chunks whose cells are 2 blocks a side, out to <see cref="OuterRadius"/>, lit as open air (sun and
/// ambient, no voxel light). A coarse column covers a 2x2 group of full columns, and ChunkLoadSystem loads full detail
/// in whole groups (<see cref="ChunkLoadSystem.FullDetailRadius"/>), so each coarse column swaps for its four full ones
/// in one frame: it's drawn (and they're hidden, see <see cref="Covers"/>) until all four are loaded and meshed.
///
/// A coarse chunk is made by generating the eight full chunks under it (or loading the edited ones from the store) on a
/// worker, shrinking them (<see cref="Shrink"/>) and meshing the result; only its meshes are kept. Coarse columns are
/// kept within the outer radius, including under the full-detail terrain, so moving back out needs nothing loaded.
/// One under full detail whose chunks were edited meanwhile is made again when it's next drawn.
/// Off by default: a panel toggle (Distant Terrain) for comparing against full detail everywhere.
/// </summary>
public sealed class LodTerrainSystem : ISystem, IRenderSystem, IDebugUiSystem, IFarTerrain
{
    private const int S = ChunkData.Size;
    private const int Cell = 2;                  // blocks per coarse cell
    private const int ColumnBlocks = S * Cell;   // a coarse column's width
    private const int MaxInFlight = 16;
    private const double UploadBudgetMs = 2.0;

    private readonly World _world;
    private readonly Renderer _renderer;
    private readonly ChunkLoadSystem _load;
    private readonly ChunkVolume _volume;
    private readonly IChunkStore _store;
    private readonly ThreadLocal<IWorldGenerator> _generator;
    private readonly ThreadLocal<GreedyMesher> _meshers;
    private readonly EntitySet _interests;

    /// <summary>Whether distant terrain is drawn coarse (and full detail only loads to <see cref="FullDetailRadius"/>).</summary>
    public bool Enabled;

    /// <summary>How far the view's terrain loads in full detail (blocks).</summary>
    public float FullDetailRadius = 256f;

    /// <summary>How far coarse terrain is drawn (blocks).</summary>
    public float OuterRadius = 1024f;

    private sealed class CoarseChunk
    {
        public int Y;
        public GpuMesh? Opaque, Cutout, Transparent;
    }

    private sealed class CoarseColumn
    {
        public readonly List<CoarseChunk> Chunks = new();
        public int MinY, MaxY;
        public bool Drawn;       // this frame: not all four full columns are ready
        public bool ChildEdited; // a full chunk under it was edited while it was hidden
    }

    private readonly record struct Built(int Y, ChunkMeshSystem.Packed Opaque, ChunkMeshSystem.Packed Cutout,
                                         ChunkMeshSystem.Packed Transparent);
    private readonly record struct Result((int x, int z) Column, List<Built> Chunks, Exception? Error);

    private readonly Dictionary<(int x, int z), CoarseColumn> _columns = new();
    private readonly HashSet<(int x, int z)> _inFlight = new();
    private readonly List<(int x, int z)> _queue = new();
    private int _queueHead;
    private readonly ConcurrentQueue<Result> _results = new();
    private (int x, int z) _centre = (int.MinValue, int.MinValue);
    private int _outerColumns = -1;
    private bool _wasEnabled;
    private Vector3D<float> _view;

    private readonly record struct Draw(float DistSq, CoarseChunk Chunk, Mat4 Model, ChunkPosition Pos);
    private readonly List<Draw> _draws = new();
    private static readonly Comparison<Draw> NearestFirst = (a, b) => a.DistSq.CompareTo(b.DistSq);
    private int _built, _chunksDrawn;
    private long _quads;

    public LodTerrainSystem(World world, Renderer renderer, ChunkLoadSystem load, ChunkVolume staticVolume,
                            Func<IWorldGenerator> generatorFactory, IChunkStore store)
    {
        _world = world;
        _renderer = renderer;
        _load = load;
        _volume = staticVolume;
        _store = store;
        _generator = new ThreadLocal<IWorldGenerator>(generatorFactory);
        var atlas = renderer.Atlas;
        _meshers = new ThreadLocal<GreedyMesher>(() => new GreedyMesher(atlas));
        _interests = world.GetEntities().With<TerrainInterest>().With<Transform>().AsSet();
    }

    public bool Active => Enabled;

    /// <summary>Whether full column (x, z) is hidden because the coarse column over it is drawn instead.</summary>
    public bool Covers(int x, int z)
        => Enabled && _columns.TryGetValue((x >> 1, z >> 1), out var c) && c.Drawn;

    public void Update(float dt)
    {
        if (!Enabled)
        {
            if (_wasEnabled) { _load.FullDetailRadius = null; UnloadAll(); _wasEnabled = false; }
            return;
        }
        _wasEnabled = true;
        FullDetailRadius = MathF.Max(FullDetailRadius, 64f);
        OuterRadius = MathF.Max(OuterRadius, FullDetailRadius + ColumnBlocks);
        _load.FullDetailRadius = FullDetailRadius;
        if (!TryView(out _view)) return;

        var centre = ((int)MathF.Floor(_view.X / ColumnBlocks), (int)MathF.Floor(_view.Z / ColumnBlocks));
        int outer = (int)MathF.Ceiling(OuterRadius / ColumnBlocks);
        if (centre != _centre || outer != _outerColumns) Rebuild(centre, outer);

        ApplyResults();
        Dispatch();
        UpdateDrawn();
    }

    private bool TryView(out Vector3D<float> centre)
    {
        foreach (ref readonly Entity e in _interests.GetEntities())
        {
            if (e.Get<TerrainInterest>().DrawRadius <= 0) continue;
            centre = e.Get<Transform>().Position;
            return true;
        }
        centre = default;
        return false;
    }

    private bool Wanted((int x, int z) c)
        => Sq(c.x - _centre.x) + Sq(c.z - _centre.z) <= Sq(_outerColumns);

    private static long Sq(int v) => (long)v * v;

    /// <summary>The view moved to another coarse column (or the radius changed): drops what's out of range and queues
    /// what's missing, nearest first.</summary>
    private void Rebuild((int x, int z) centre, int outer)
    {
        _centre = centre;
        _outerColumns = outer;
        foreach (var key in _columns.Keys.Where(k => !Wanted(k)).ToList()) Unload(key);

        _queue.Clear();
        _queueHead = 0;
        for (int dz = -outer; dz <= outer; dz++)
        for (int dx = -outer; dx <= outer; dx++)
        {
            var c = (centre.x + dx, centre.z + dz);
            if (dx * dx + dz * dz > outer * outer || _columns.ContainsKey(c) || _inFlight.Contains(c)) continue;
            _queue.Add(c);
        }
        _queue.Sort((a, b) => (Sq(a.Item1 - centre.x) + Sq(a.Item2 - centre.z))
                              .CompareTo(Sq(b.Item1 - centre.x) + Sq(b.Item2 - centre.z)));
    }

    /// <summary>Hands queued coarse columns to workers; one whose edited chunks haven't arrived from the store waits
    /// (they're asked for) while the ones after it go.</summary>
    private void Dispatch()
    {
        int i = _queueHead, waiting = 0;
        while (_inFlight.Count < MaxInFlight && i < _queue.Count && waiting < MaxInFlight)
        {
            var col = _queue[i];
            if (_inFlight.Contains(col) || !Wanted(col)) { Dequeue(ref i); continue; }
            var work = Work(col);
            if (!Arrived(work)) { i++; waiting++; continue; }
            Dequeue(ref i);
            DispatchColumn(col, work);
        }
    }

    private void Dequeue(ref int i)
    {
        if (i == _queueHead) i = ++_queueHead;
        else _queue.RemoveAt(i);
    }

    /// <summary>The full chunks under coarse column <paramref name="col"/> that may hold something, with whether each
    /// is edited (loaded from the store).</summary>
    private List<(ChunkPosition Pos, bool Edited)> Work((int x, int z) col)
    {
        var work = new List<(ChunkPosition, bool)>();
        int minY = _load.MinLayer;
        for (int j = 0; j < 4; j++)
        {
            int x = col.x * 2 + (j & 1), z = col.z * 2 + (j >> 1);
            ulong bits = _generator.Value!.ColumnLayers(x, z, minY) | _load.BuiltLayers(x, z);
            for (; bits != 0; bits &= bits - 1)
            {
                var p = new ChunkPosition(x, minY + BitOperations.TrailingZeroCount(bits), z);
                work.Add((p, _load.IsEdited(p)));
            }
        }
        return work;
    }

    private bool Arrived(List<(ChunkPosition Pos, bool Edited)> work)
    {
        bool all = true;
        foreach (var (pos, edited) in work)
        {
            if (!edited || _store.IsReady(pos)) continue;
            _store.Request(pos);
            all = _store.IsReady(pos) && all;
        }
        return all;
    }

    private void DispatchColumn((int x, int z) col, List<(ChunkPosition Pos, bool Edited)> work)
    {
        _inFlight.Add(col);
        BackgroundWork.Queue(() =>
        {
            var built = new List<Built>();
            try
            {
                foreach (var group in work.GroupBy(w => w.Pos.Y >> 1))
                {
                    var coarse = new ChunkData();
                    foreach (var (pos, edited) in group)
                    {
                        var full = new ChunkData();
                        if (!(edited && _store.TryLoad(pos, full))) _generator.Value!.Generate(full, pos);
                        Shrink(full, coarse, (pos.X & 1) * 16, (pos.Y & 1) * 16, (pos.Z & 1) * 16);
                        full.Release();
                    }
                    if (coarse.HasAnyNonAir())
                    {
                        var mesh = _meshers.Value!.Mesh(coarse, null, null, null, null, null, null,
                                                        neighboursForTransparentOnly: true,
                                                        position: new ChunkPosition(col.x, group.Key, col.z));
                        var b = new Built(group.Key, ChunkMeshSystem.PackQuads(mesh.Opaque), ChunkMeshSystem.PackQuads(mesh.Cutout),
                                          ChunkMeshSystem.PackQuads(mesh.Translucent, twoSided: true));
                        if (b.Opaque.QuadCount + b.Cutout.QuadCount + b.Transparent.QuadCount > 0) built.Add(b);
                    }
                    coarse.Release();
                }
                _results.Enqueue(new Result(col, built, null));
            }
            catch (Exception e)
            {
                _results.Enqueue(new Result(col, built, e));
            }
        });
    }

    /// <summary>Shrinks full chunk <paramref name="full"/> into the 16³ octant of <paramref name="coarse"/> at
    /// (ox, oy, oz): each coarse cell takes the most common opaque cube among its 8 blocks, or if there's none, the most
    /// common see-through cube (leaves, glass, water). Plants and model blocks are left out, so a cell is solid if any
    /// of its blocks is a cube: thin edges and trees stay, a little thicker.</summary>
    internal static void Shrink(ChunkData full, ChunkData coarse, int ox, int oy, int oz)
    {
        if (full.IsUniform(out var only))
        {
            if (!IsCube(only)) return;
            for (int z = 0; z < 16; z++) for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
                coarse.Set(ox + x, oy + y, oz + z, only);
            return;
        }
        Span<BlockId> cell = stackalloc BlockId[8];
        for (int z = 0; z < 16; z++)
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
        {
            for (int k = 0; k < 8; k++)
                cell[k] = full.Get(2 * x + (k & 1), 2 * y + ((k >> 1) & 1), 2 * z + (k >> 2));
            var pick = MostCommon(cell, opaque: true);
            if (pick == BlockId.Air) pick = MostCommon(cell, opaque: false);
            if (pick != BlockId.Air) coarse.Set(ox + x, oy + y, oz + z, pick);
        }
    }

    private static bool IsCube(BlockId id) => id != BlockId.Air && BlockRegistry.Get(id).IsFullCube;

    private static BlockId MostCommon(ReadOnlySpan<BlockId> cell, bool opaque)
    {
        BlockId best = BlockId.Air;
        int bestCount = 0;
        for (int i = 0; i < cell.Length; i++)
        {
            var id = cell[i];
            if (!IsCube(id) || BlockRegistry.Get(id).Transparent == opaque) continue;
            int n = 0;
            for (int j = 0; j < cell.Length; j++) if (cell[j] == id) n++;
            if (n > bestCount) { best = id; bestCount = n; }
        }
        return best;
    }

    private void ApplyResults()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < UploadBudgetMs
               && _results.TryDequeue(out var r))
        {
            _inFlight.Remove(r.Column);
            try
            {
                if (r.Error != null) { Console.WriteLine($"[lod] coarse column failed: {r.Error}"); continue; }
                if (!Enabled || !Wanted(r.Column)) continue;
                var column = new CoarseColumn { MinY = int.MaxValue, MaxY = int.MinValue };
                foreach (var b in r.Chunks)
                {
                    var chunk = new CoarseChunk
                    {
                        Y = b.Y, Opaque = Upload(b.Opaque, LightmapKind.Opaque), Cutout = Upload(b.Cutout, LightmapKind.SeeThrough),
                        Transparent = Upload(b.Transparent, LightmapKind.SeeThrough | LightmapKind.TwoSided),
                    };
                    column.Chunks.Add(chunk);
                    column.MinY = System.Math.Min(column.MinY, b.Y);
                    column.MaxY = System.Math.Max(column.MaxY, b.Y);
                }
                Unload(r.Column); // a column made again replaces the one drawn meanwhile
                _columns[r.Column] = column;
                _built++;
            }
            finally
            {
                foreach (var b in r.Chunks) { Return(b.Opaque); Return(b.Cutout); Return(b.Transparent); }
            }
        }
    }

    private static void Return(in ChunkMeshSystem.Packed p)
    {
        if (p.Data.Length > 0) System.Buffers.ArrayPool<byte>.Shared.Return(p.Data);
    }

    /// <summary>Uploads a coarse mesh without a light map: it's lit as open air.</summary>
    private GpuMesh? Upload(in ChunkMeshSystem.Packed p, LightmapKind kind)
    {
        if (p.QuadCount == 0) return null;
        _quads += p.QuadCount;
        return _renderer.UploadChunkQuads(p.Data.AsSpan(0, p.Bytes), (uint)p.QuadCount, 0, kind);
    }

    private void Unload((int x, int z) key)
    {
        if (!_columns.Remove(key, out var column)) return;
        foreach (var c in column.Chunks)
        {
            _quads -= (c.Opaque?.QuadCount ?? 0) + (c.Cutout?.QuadCount ?? 0) + (c.Transparent?.QuadCount ?? 0);
            c.Opaque?.Dispose(); c.Cutout?.Dispose(); c.Transparent?.Dispose();
        }
    }

    private void UnloadAll()
    {
        foreach (var key in _columns.Keys.ToList()) Unload(key);
        _queue.Clear();
        _queueHead = 0;
        _centre = (int.MinValue, int.MinValue);
        _outerColumns = -1;
    }

    /// <summary>Which coarse columns are drawn this frame: those whose four full columns aren't all ready. One hidden
    /// under full detail notes any edit to the chunks there, and is made again when it's next drawn.</summary>
    private void UpdateDrawn()
    {
        foreach (var (key, column) in _columns)
        {
            bool ready = true;
            for (int j = 0; j < 4 && ready; j++) ready = _load.IsColumnReady(key.x * 2 + (j & 1), key.z * 2 + (j >> 1));
            if (ready)
            {
                if (!column.ChildEdited) column.ChildEdited = AnyEdited(key);
            }
            else if (!column.Drawn && column.ChildEdited && !_inFlight.Contains(key))
            {
                column.ChildEdited = false;
                _queue.Insert(_queueHead, key); // drawn as it was until it's made again
            }
            column.Drawn = !ready;
        }
    }

    private bool AnyEdited((int x, int z) key)
    {
        int minY = _load.MinLayer;
        for (int j = 0; j < 4; j++)
        for (int layer = 0; layer < 64; layer++)
        {
            var data = _volume.GetData(new ChunkPosition(key.x * 2 + (j & 1), minY + layer, key.z * 2 + (j >> 1)));
            if (data is { IsDirty: true }) return true;
        }
        return false;
    }

    public float ReadyRadius(Vector3D<float> centre)
    {
        float best = OuterRadius;
        foreach (var c in _inFlight)
            if (!_columns.ContainsKey(c)) best = MathF.Min(best, Distance(centre, c)); // one being made again is drawn meanwhile
        for (int i = _queueHead; i < _queue.Count; i++)
            if (!_columns.ContainsKey(_queue[i])) { best = MathF.Min(best, Distance(centre, _queue[i])); break; }
        return best;
    }

    /// <summary>Horizontal distance from <paramref name="centre"/> to the nearest point of coarse column <paramref name="c"/>.</summary>
    private static float Distance(Vector3D<float> centre, (int x, int z) c)
    {
        float dx = MathF.Max(0f, MathF.Max(c.x * ColumnBlocks - centre.X, centre.X - (c.x + 1) * ColumnBlocks));
        float dz = MathF.Max(0f, MathF.Max(c.z * ColumnBlocks - centre.Z, centre.Z - (c.z + 1) * ColumnBlocks));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public void Render(SystemStage stage, in RenderContext frame)
    {
        if (!Enabled) return;
        if (stage == SystemStage.RenderTransparent)
        {
            foreach (var d in _draws)
                if (d.Chunk.Transparent != null) _renderer.DrawTransparentChunkDepth(d.Chunk.Transparent, d.Model, Renderer.LodGrid, d.Pos);
            foreach (var d in _draws)
                if (d.Chunk.Transparent != null) _renderer.DrawTransparentChunkMesh(d.Chunk.Transparent, d.Model, Renderer.LodGrid, d.Pos);
            return;
        }

        _draws.Clear();
        var size = new Vector3D<float>(ColumnBlocks);
        var scale = Mat4.Scale(new Vector3D<float>(Cell));
        foreach (var ((cx, cz), column) in _columns)
        {
            if (!column.Drawn || column.Chunks.Count == 0) continue;
            var min = new Vector3D<float>(cx * ColumnBlocks, column.MinY * ColumnBlocks, cz * ColumnBlocks);
            var max = new Vector3D<float>((cx + 1) * ColumnBlocks, (column.MaxY + 1) * ColumnBlocks, (cz + 1) * ColumnBlocks);
            if (!frame.Frustum.Intersects(min, max)) continue;
            foreach (var c in column.Chunks)
            {
                var origin = new Vector3D<float>(cx * ColumnBlocks, c.Y * ColumnBlocks, cz * ColumnBlocks);
                if (!frame.Frustum.Intersects(origin, origin + size)) continue;
                float distSq = Vector3D.DistanceSquared(origin + size * 0.5f, frame.CameraPosition);
                _draws.Add(new Draw(distSq, c, Mat4.Multiply(Mat4.Translation(origin), scale), new ChunkPosition(cx, c.Y, cz)));
            }
        }
        _draws.Sort(NearestFirst);
        _chunksDrawn = _draws.Count;
        foreach (var d in _draws)
            if (d.Chunk.Opaque != null) _renderer.DrawChunkMesh(d.Chunk.Opaque, d.Model, Renderer.LodGrid, d.Pos);
        foreach (var d in _draws)
            if (d.Chunk.Cutout != null) _renderer.DrawCutoutChunkDepth(d.Chunk.Cutout, d.Model, Renderer.LodGrid, d.Pos);
        foreach (var d in _draws)
            if (d.Chunk.Cutout != null) _renderer.DrawCutoutChunkMesh(d.Chunk.Cutout, d.Model, Renderer.LodGrid, d.Pos);
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Distant Terrain";

    public void DrawDebugUi()
    {
        ImGui.Checkbox("Draw distant terrain coarse (LOD)", ref Enabled);
        ImGui.SliderFloat("Full detail out to (blocks)", ref FullDetailRadius, 64f, 1024f, "%.0f");
        ImGui.SliderFloat("Coarse terrain out to (blocks)", ref OuterRadius, 128f, 4096f, "%.0f");
        int drawnColumns = _columns.Values.Count(c => c.Drawn);
        int chunks = _columns.Values.Sum(c => c.Chunks.Count);
        ImGui.Text($"Coarse columns: {_columns.Count:N0} loaded ({drawnColumns:N0} drawn), {_inFlight.Count} being made, " +
                   $"{_queue.Count - _queueHead:N0} queued");
        ImGui.Text($"Coarse chunks: {chunks:N0} ({_chunksDrawn:N0} drawn this frame), {_quads:N0} quads " +
                   $"(~{_quads * ChunkQuad.SizeBytes / (1024.0 * 1024.0):F1} MB); made so far: {_built:N0}");
        ImGui.Text($"Full-detail chunks loaded: {_volume.LoadedCount:N0}");
    }
}
