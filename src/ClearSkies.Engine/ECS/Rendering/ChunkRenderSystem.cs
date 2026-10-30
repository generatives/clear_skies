using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using System.Runtime.InteropServices;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Draws every loaded chunk's <see cref="ChunkRenderData"/>: frustum-culls the chunks, draws their greedy-meshed
/// cubes nearest first (opaque, then cut out), then each visible chunk's model blocks — placed at their cell, turned to
/// their stored <see cref="BlockOrientation"/> and lit from that cell's voxel light, in
/// <see cref="SystemStage.RenderWorld"/>; then, in <see cref="SystemStage.RenderTransparent"/>, the same chunks'
/// translucent meshes (water). Schedule it in both stages.
///
/// The static world's chunks (tens of thousands) are kept by column, with each column's height range, so a column
/// outside the view is skipped in one test instead of one per chunk: looking at every chunk each frame cost ~6 ms. So are
/// a low-resolution copy of the world's (see <see cref="LowResolutionCopy"/>), drawn at their scale. Ships' chunks, which move, are
/// tested one by one.
///
/// A static volume's chunk is drawn while it's shown (<see cref="Shown"/>), dissolving in and out. A scaled
/// volume's light maps are baked nearest first up to <see cref="ScaledBakeQuadBudget"/> quads a frame; then, with what's
/// left of that, those of chunks waiting for their first bake (<see cref="BakeFirst"/>), in view or not.
/// </summary>
public sealed class ChunkRenderSystem : IRenderSystem, IDebugUiSystem
{
    private readonly Renderer _renderer;
    private readonly ChunkVolume _world;

    // A static volume's chunks with render data, by column (the world's, and each low-resolution copy's); every other chunk
    // (ships') on its own.
    private sealed class WorldColumn
    {
        public readonly List<Entity> Chunks = new();
        public int MinY, MaxY; // chunk layers, inclusive
    }
    private sealed class StaticVolume(ChunkVolume volume)
    {
        public readonly ChunkVolume Volume = volume;
        public readonly Dictionary<(int X, int Z), WorldColumn> Columns = new();
    }
    private readonly Dictionary<ChunkVolume, StaticVolume> _static = new();
    private readonly Dictionary<Entity, (StaticVolume Volume, (int X, int Z) Key)> _columnOf = new();
    private readonly HashSet<Entity> _others = new();
    private int _columnsVisible, _columnCount;

    // One visible chunk, collected so they can be drawn nearest first. Budgeted: a scaled volume's, whose light maps are
    // baked to a budget (see ScaledBakeQuadBudget).
    private readonly record struct ChunkDraw(float DistSq, GpuMesh? Mesh, GpuMesh? CutoutMesh, GpuMesh? TransparentMesh,
                                             ModelBlock[] Models, Mat4 Model, int Grid, ChunkPosition Chunk, GridHandle? Handle,
                                             float Dissolve = 0f, bool Budgeted = false,
                                             int Level = -1);
    private List<ChunkDraw> _draws = new(), _sorted = new();
    private readonly List<ChunkDraw> _translucentDraws = new(); // the visible chunks with translucent faces
    // The sort's keys: each draw's distance and place in _draws (sorting the draws themselves moved ~150 bytes a swap).
    private float[] _sortKeys = Array.Empty<float>();
    private int[] _sortOrder = Array.Empty<int>();
    private int _modelBlocksDrawn;
    // Per level of detail (0 full detail, k a low-resolution copy at 2^k; ships last), this frame's chunks drawn and their
    // opaque and cut-out quads (all of them, before any are skipped as facing away), for the panel.
    private const int LevelRows = 8;
    private readonly (int Chunks, long Opaque, long Cutout)[] _levelCounts = new (int, long, long)[LevelRows + 1];
    private double _bakedMeshesEma, _bakedGroupsEma;

    // Where RenderWorld's time goes (smoothed ms): choosing the chunks in view (less the draw-state calls, timed on
    // their own), sorting, the light-map checks and the bake's encoding, and the draw calls; RenderTranslucent's draws.
    private double _cullMs, _sortMs, _bakeCheckMs, _bakeEncodeMs, _submitMs, _modelsMs, _translucentMs;
    private static double Ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    private static void Smooth(ref double ema, double ms) => ema += 0.05 * (ms - ema);

    /// <summary>The most light-map quads of scaled volumes' chunks baked a frame (always at least one chunk's): a level
    /// of distant terrain coming into view has far more than the world's chunks do, and baking them all at once was a
    /// spike of several ms. A chunk past the budget keeps the maps it last baked, and one never baked waits.</summary>
    public const int ScaledBakeQuadBudget = 65536;
    private int _bakedQuads;

    // Chunks waiting for their first bake (see FirstBakeStage), and how many of the nearest are looked at a frame.
    private readonly EntitySet _bakeFirst, _cameras;
    private readonly List<Entity> _nearestToBake = new();
    private const int FirstBakesLooked = 64;
    // For the panel: every chunk marked to be baked first (some may be left out of _bakeFirst), and of last frame's
    // nearest looked at, how many were listed, had nothing to bake, or weren't in the light store.
    private readonly EntitySet _markedBakeFirst;
    private int _firstListed, _firstNothing, _firstNoStore;

    /// <summary>Cell-local placement per <see cref="BlockOrientation"/> (indexed by its byte): a model block's model is
    /// authored Blockbench-style (x/z centred on the origin, standing on y = 0, 1 unit = 1 block), so it is rotated
    /// about the cell centre to the orientation, standing on the cell face opposite its top.</summary>
    private static readonly Mat4[] OrientationPlacement = BuildOrientationPlacements();

    /// <param name="staticVolume">The static world, whose chunks are culled by column (it never moves).</param>
    public ChunkRenderSystem(World world, Renderer renderer, ChunkVolume staticVolume)
    {
        _renderer = renderer;
        _world    = staticVolume;
        world.SubscribeComponentAdded<ChunkRenderData>(OnAdded);
        world.SubscribeComponentRemoved<ChunkRenderData>((in Entity e, in ChunkRenderData _) => Forget(e));
        world.SubscribeEntityDisposed((in Entity e) => Forget(e));
        _bakeFirst = world.GetEntities().With<BakeFirst>().With<ChunkRenderData>().With<Chunk>().With<Rendered>().AsSet();
        _cameras = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _markedBakeFirst = world.GetEntities().With<BakeFirst>().AsSet();
    }

    private StaticVolume StaticOf(ChunkVolume volume)
    {
        if (!_static.TryGetValue(volume, out var sv)) _static[volume] = sv = new StaticVolume(volume);
        return sv;
    }

    private void OnAdded(in Entity e, in ChunkRenderData rd)
    {
        var volume = e.Has<Chunk>() ? e.Get<Chunk>().Entry.Volume : null;
        if (volume == null || (volume != _world && !volume.IsLowResolutionCopy)) { _others.Add(e); return; }
        var sv = StaticOf(volume);
        var key = (rd.ChunkPos.X, rd.ChunkPos.Z);
        if (!sv.Columns.TryGetValue(key, out var column)) { sv.Columns[key] = column = new WorldColumn(); _columnCount++; }
        column.Chunks.Add(e);
        _columnOf[e] = (sv, key);
        UpdateRange(column);
    }

    private void Forget(Entity e)
    {
        _others.Remove(e);
        if (!_columnOf.Remove(e, out var at)) return;
        var column = at.Volume.Columns[at.Key];
        column.Chunks.Remove(e);
        if (column.Chunks.Count == 0)
        {
            at.Volume.Columns.Remove(at.Key);
            _columnCount--;
            if (at.Volume.Columns.Count == 0) _static.Remove(at.Volume.Volume); // a volume gone, or emptied
        }
        else UpdateRange(column);
    }

    private static void UpdateRange(WorldColumn column)
    {
        column.MinY = int.MaxValue;
        column.MaxY = int.MinValue;
        foreach (var e in column.Chunks)
        {
            int y = e.Get<ChunkRenderData>().ChunkPos.Y;
            column.MinY = System.Math.Min(column.MinY, y);
            column.MaxY = System.Math.Max(column.MaxY, y);
        }
    }

    public void Render(SystemStage stage, in RenderContext frame)
    {
        switch (stage)
        {
            case SystemStage.RenderWorld:       RenderWorld(frame); break;
            case SystemStage.RenderTransparent: RenderTranslucent(); break;
            default: throw new InvalidOperationException($"{nameof(ChunkRenderSystem)} doesn't draw in {stage}.");
        }
    }

    private void RenderWorld(in RenderContext frame)
    {
        // Every chunk's box is exactly ChunkData.Size local units on a side (GreedyMesher's local space); its model
        // blocks sit in its cells, so the same box culls them too.
        _draws.Clear();
        _translucentDraws.Clear();
        _columnsVisible = 0;
        var size = new Vector3D<float>(ChunkData.Size);
        var half = size * 0.5f;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        foreach (var sv in _static.Values) DrawStatic(sv, frame);

        foreach (var e in _others)
        {
            if (!e.Has<Transform>() || !e.Has<Rendered>()) continue;
            ref readonly var rd = ref e.Get<ChunkRenderData>();
            if (rd.IsEmpty) continue;

            var t = e.DrawnPose(); // ships are drawn where they're drawn, between ticks
            Mat4 model;
            if (t.Rotation == Quaternion<float>.Identity && t.Scale == Vector3D<float>.One)
            {
                if (!frame.Frustum.Intersects(t.Position, t.Position + size)) continue;
                model = Mat4.Translation(t.Position);
            }
            else
            {
                model = t.ToMatrix();
                if (!frame.Frustum.Intersects(model, Vector3D<float>.Zero, size)) continue;
            }

            float distSq = Vector3D.DistanceSquared(model.TransformPoint(half), frame.CameraPosition);
            _draws.Add(new ChunkDraw(distSq, rd.Mesh, rd.CutoutMesh, rd.TransparentMesh, rd.Models, model, rd.Grid?.Index ?? -1,
                                     rd.ChunkPos, rd.Grid));
        }

        // Nearest first, so the depth test rejects hidden fragments before the (expensive) lighting shader runs on
        // them instead of shading them and overwriting them later (and the nearest are baked first).
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        SortNearestFirst();
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();

        // Light maps whose chunk's light changed since they were baked (or never baked) are baked before anything draws:
        // only the chunks in view, so one out of view waits until it's seen. A scaled volume's go to a budget, but for
        // new meshes.
        _bakedQuads = 0;
        int kept = 0;
        var draws = CollectionsMarshal.AsSpan(_draws);
        for (int i = 0; i < draws.Length; i++)
        {
            ref readonly var d = ref draws[i];
            if (!Bake(in d)) continue;
            if (d.TransparentMesh != null) _translucentDraws.Add(d);
            if (kept != i) draws[kept] = d;
            kept++;
        }
        _draws.RemoveRange(kept, _draws.Count - kept);
        // Then chunks waiting for their first bake, in view or not, nearest first, with what's left of the budget. One
        // listed (or with nothing to bake) is let go of at once: it waits only for its stage to be asked again, and kept,
        // the nearest would be looked at each frame with nothing to bake while those behind them waited.
        NearestChunks.Select(_bakeFirst, _cameras, FirstBakesLooked, _nearestToBake);
        _firstListed = _firstNothing = _firstNoStore = 0;
        foreach (var e in _nearestToBake)
        {
            if (!BakeFirstTime(in e.Get<ChunkRenderData>())) break;
            e.Remove<BakeFirst>();
        }
        Array.Clear(_levelCounts);
        foreach (ref readonly var d in CollectionsMarshal.AsSpan(_draws))
        {
            ref var c = ref _levelCounts[d.Level is >= 0 and < LevelRows ? d.Level : LevelRows];
            c.Chunks++;
            c.Opaque += d.Mesh?.QuadCount ?? 0;
            c.Cutout += d.CutoutMesh?.QuadCount ?? 0;
        }
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        _renderer.BakeLightmaps();
        long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
        var (bakedMeshes, bakedGroups) = _renderer.LastLightmapBake;
        _bakedMeshesEma += 0.05 * (bakedMeshes - _bakedMeshesEma);
        _bakedGroupsEma += 0.05 * (bakedGroups - _bakedGroupsEma);

        draws = CollectionsMarshal.AsSpan(_draws);
        _renderer.Section("Render: opaque chunks");
        foreach (ref readonly var d in draws)
            if (d.Mesh != null) _renderer.DrawChunkMesh(d.Mesh, d.Model, d.Grid, d.Chunk, d.Dissolve);
        // Cut-out faces after all the opaque ones: discarding texels costs the early depth test, so the opaque world is
        // drawn without it first. Their depth goes down first, so each pixel's nearest cut-out face is lit once rather
        // than every leaf behind it too (see Renderer.DrawCutoutChunkDepth).
        _renderer.Section("Render: cut-out depth (leaves, grass)");
        foreach (ref readonly var d in draws)
            if (d.CutoutMesh != null) _renderer.DrawCutoutChunkDepth(d.CutoutMesh, d.Model, d.Grid, d.Chunk, d.Dissolve);
        _renderer.Section("Render: cut-out shaded");
        foreach (ref readonly var d in draws)
            if (d.CutoutMesh != null) _renderer.DrawCutoutChunkMesh(d.CutoutMesh, d.Model, d.Grid, d.Chunk, d.Dissolve);

        long t5 = System.Diagnostics.Stopwatch.GetTimestamp();
        _renderer.Section("Render: model blocks");
        _modelBlocksDrawn = 0;
        foreach (ref readonly var d in draws)
        {
            foreach (var m in d.Models)
            {
                var cell  = Mat4.Translation(new Vector3D<float>(m.X, m.Y, m.Z));
                var world = Mat4.Multiply(d.Model, Mat4.Multiply(cell, OrientationPlacement[m.Orientation.ToByte()]));
                _renderer.DrawModel(m.Model, world, d.Grid, d.Chunk, new Vector3D<int>(m.X, m.Y, m.Z));
                _modelBlocksDrawn++;
            }
        }
        _renderer.Section("Render: after the world (sky, ...)");
        long t6 = System.Diagnostics.Stopwatch.GetTimestamp();

        Smooth(ref _cullMs, Ms(t0, t1));
        Smooth(ref _sortMs, Ms(t1, t2));
        Smooth(ref _bakeCheckMs, Ms(t2, t3));
        Smooth(ref _bakeEncodeMs, Ms(t3, t4));
        Smooth(ref _submitMs, Ms(t4, t5));
        Smooth(ref _modelsMs, Ms(t5, t6));
    }

    /// <summary>Puts <see cref="_draws"/> nearest first, by sorting their distances and then moving each draw once.</summary>
    private void SortNearestFirst()
    {
        int n = _draws.Count;
        if (_sortKeys.Length < n) { _sortKeys = new float[n * 2]; _sortOrder = new int[n * 2]; }
        var draws = CollectionsMarshal.AsSpan(_draws);
        for (int i = 0; i < n; i++) { _sortKeys[i] = draws[i].DistSq; _sortOrder[i] = i; }
        Array.Sort(_sortKeys, _sortOrder, 0, n);
        _sorted.Clear();
        for (int i = 0; i < n; i++) _sorted.Add(draws[_sortOrder[i]]);
        (_draws, _sorted) = (_sorted, _draws);
    }

    /// <summary>Lists one static volume's chunks in view. It sits unrotated at its scale (see ChunkVolume), so a chunk's
    /// box is its position's times the chunk size and scale.</summary>
    private void DrawStatic(StaticVolume sv, in RenderContext frame)
    {
        int k = sv.Volume.Scale, blocks = ChunkData.Size * k;
        var size = new Vector3D<float>(blocks);
        var scale = Mat4.Scale(new Vector3D<float>(k));
        int level = System.Numerics.BitOperations.Log2((uint)k);
        bool copy = sv.Volume.IsLowResolutionCopy;
        foreach (var ((cx, cz), column) in sv.Columns)
        {
            var min = new Vector3D<float>(cx * blocks, column.MinY * blocks, cz * blocks);
            var max = new Vector3D<float>((cx + 1) * blocks, (column.MaxY + 1) * blocks, (cz + 1) * blocks);
            if (!frame.Frustum.Intersects(min, max)) continue;
            _columnsVisible++;
            foreach (var e in column.Chunks)
            {
                if (!e.Has<Rendered>() || !e.Has<Shown>()) continue; // not in the rendering layer, or not ready yet
                ref readonly var rd = ref e.Get<ChunkRenderData>();
                if (rd.IsEmpty) continue; // buried stone: nothing to draw
                var origin = e.Get<Transform>().Position;
                if (!frame.Frustum.Intersects(origin, origin + size)) continue;
                float dissolve = e.Get<Shown>().Dissolve(frame.TimeSeconds);
                if (dissolve >= 1f) continue;
                var model = Mat4.Multiply(Mat4.Translation(origin), scale);
                // A scaled volume not in the light store has no light to show: lit as open air.
                int grid = rd.Grid?.Index ?? -1;
                if (copy && grid < 0) grid = Renderer.OpenAirGrid;
                var centre = origin + size * 0.5f;
                _draws.Add(new ChunkDraw(Vector3D.DistanceSquared(centre, frame.CameraPosition), rd.Mesh, rd.CutoutMesh,
                                         rd.TransparentMesh, rd.Models, model, grid, rd.ChunkPos, rd.Grid, dissolve,
                                         Budgeted: copy, Level: level));
            }
        }
    }

    /// <summary>Lists a draw's stale light maps for baking, if it's lit from them. A scaled volume's go while the frame's
    /// budget lasts (always the first), except maps never baked: a chunk shown that's meshed again (an edit, or a
    /// neighbour coming) has new meshes, baked at once rather than leaving it out (and popping back in) till there's
    /// room. Returns whether it can be drawn.</summary>
    private bool Bake(in ChunkDraw d)
    {
        if (d.Grid == Renderer.OpenAirGrid) return true;
        int stamp = _renderer.LightStampOf(d.Handle, d.Chunk);
        if (d.Budgeted)
        {
            int quads = Stale(d.Mesh, d.Handle, stamp) + Stale(d.CutoutMesh, d.Handle, stamp)
                      + Stale(d.TransparentMesh, d.Handle, stamp);
            if (quads == 0) return true;
            if (_bakedQuads > 0 && _bakedQuads + quads > ScaledBakeQuadBudget && AllBaked(in d)) return true;
            _bakedQuads += quads;
        }
        _renderer.PrepareLightmap(d.Mesh, d.Handle, d.Chunk, stamp);
        _renderer.PrepareLightmap(d.CutoutMesh, d.Handle, d.Chunk, stamp);
        _renderer.PrepareLightmap(d.TransparentMesh, d.Handle, d.Chunk, stamp);
        return true;
    }

    /// <summary>Lists a chunk's stale light maps for baking while the frame's budget lasts; false once it's spent.</summary>
    private bool BakeFirstTime(in ChunkRenderData rd)
    {
        if (rd.Grid is not { Index: >= 0 } grid) { _firstNoStore++; return true; } // not in the light store (any more)
        int stamp = _renderer.LightStampOf(grid, rd.ChunkPos);
        int quads = Stale(rd.Mesh, grid, stamp) + Stale(rd.CutoutMesh, grid, stamp) + Stale(rd.TransparentMesh, grid, stamp);
        if (quads == 0) { _firstNothing++; return true; }
        if (_bakedQuads > 0 && _bakedQuads + quads > ScaledBakeQuadBudget) return false;
        _bakedQuads += quads;
        _firstListed++;
        _renderer.PrepareLightmap(rd.Mesh, grid, rd.ChunkPos, stamp);
        _renderer.PrepareLightmap(rd.CutoutMesh, grid, rd.ChunkPos, stamp);
        _renderer.PrepareLightmap(rd.TransparentMesh, grid, rd.ChunkPos, stamp);
        return true;
    }

    private int Stale(GpuMesh? mesh, GridHandle? grid, int stamp)
        => _renderer.LightmapStale(mesh, grid, stamp) ? (int)mesh!.QuadCount : 0;

    private bool AllBaked(in ChunkDraw d)
        => _renderer.LightmapEverBaked(d.Mesh) && _renderer.LightmapEverBaked(d.CutoutMesh)
           && _renderer.LightmapEverBaked(d.TransparentMesh);

    /// <summary>The translucent meshes of the chunks <see cref="RenderWorld"/> found visible this frame: their depth,
    /// then their colour where they're the nearest translucent face, so each pixel shows one translucent layer whatever
    /// order the chunks (and their faces) are drawn in.</summary>
    private void RenderTranslucent()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _renderer.Section("Render: translucent depth");
        foreach (var d in _translucentDraws) _renderer.DrawTransparentChunkDepth(d.TransparentMesh!, d.Model, d.Grid, d.Chunk, d.Dissolve);
        _renderer.Section("Render: translucent");
        foreach (var d in _translucentDraws) _renderer.DrawTransparentChunkMesh(d.TransparentMesh!, d.Model, d.Grid, d.Chunk, d.Dissolve);
        _renderer.Section("Render: after translucent (HUD, ImGui)");
        Smooth(ref _translucentMs, Ms(t0, System.Diagnostics.Stopwatch.GetTimestamp()));
    }

    private static Mat4[] BuildOrientationPlacements()
    {
        var toCentre   = Mat4.Translation(new Vector3D<float>(0.5f));
        var fromCentre = Mat4.Translation(new Vector3D<float>(0f, -0.5f, 0f));
        var placements = new Mat4[BlockOrientation.Count];
        for (int i = 0; i < placements.Length; i++)
        {
            var rotation = Mat4.FromQuaternion(BlockOrientation.FromByte((byte)i).Rotation);
            placements[i] = Mat4.Multiply(toCentre, Mat4.Multiply(rotation, fromCentre));
        }
        return placements;
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Chunk Rendering";

    public void DrawDebugUi()
    {
        ImGui.Text($"Chunks visible: {_draws.Count:N0} of {_columnOf.Count + _others.Count:N0}; world columns visible: " +
                   $"{_columnsVisible:N0} of {_columnCount:N0}; ship chunks: {_others.Count:N0}");
        ImGui.Text($"Model blocks drawn: {_modelBlocksDrawn:N0}; chunks with translucent faces drawn: {_translucentDraws.Count:N0}");
        ImGui.Text($"Main thread (smoothed): choosing {_cullMs:F2} ms, sort {_sortMs:F2} ms, " +
                   $"light-map checks {_bakeCheckMs:F2} ms + bake encode {_bakeEncodeMs:F2} ms");
        for (int i = 0; i <= LevelRows; i++)
        {
            var (chunks, opaque, cutout) = _levelCounts[i];
            if (chunks == 0) continue;
            string name = i == LevelRows ? "Ships" : i == 0 ? "Full detail" : $"Level {i}";
            ImGui.Text($"{name}: {chunks:N0} chunks drawn, {opaque:N0} opaque quads, {cutout:N0} cut-out quads");
        }
        ImGui.Text($"Draw calls (smoothed): chunks {_submitMs:F2} ms, model blocks {_modelsMs:F2} ms, translucent {_translucentMs:F2} ms");
        ImGui.Text($"First bakes: {_markedBakeFirst.Count:N0} marked, {_bakeFirst.Count:N0} of them meshed and Rendered; of the " +
                   $"{_nearestToBake.Count} nearest looked at, {_firstListed} listed, {_firstNothing} with nothing to bake, " +
                   $"{_firstNoStore} not in the light store; {_bakedQuads:N0} quads listed this frame");
        var (used, capacity) = _renderer.LightmapArenaUse;
        ImGui.Text($"Light maps: {used * 4 / 1048576.0:F1} of {capacity * 4 / 1048576.0:F1} MB; baked per frame (smoothed): " +
                   $"{_bakedMeshesEma:F1} meshes, {_bakedGroupsEma * Renderer.LightmapQuadsPerGroup:N0} quads at most");
    }
}
