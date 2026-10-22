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
/// Whoever manages a static volume can say how each of its chunks is drawn each frame (see <see cref="ChunkDrawStates"/>):
/// left out, dissolving in or out, and lit from its light maps, from the maps it last baked, or as open air. Static
/// volumes' light maps (the world's and every level of detail's) are baked nearest first up to
/// <see cref="BakeQuadBudget"/> quads a frame; what the budget leaves goes to their chunks out of view never baked, so
/// each is baked once in the end, and gets <see cref="LightmapsBakedFlag"/>: what waits for it to be drawable can tell.
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
        public ChunkDrawStateOf? State;
    }
    private readonly Dictionary<ChunkVolume, StaticVolume> _static = new();
    private readonly Dictionary<Entity, (StaticVolume Volume, (int X, int Z) Key)> _columnOf = new();
    private readonly HashSet<Entity> _others = new();
    private int _columnsVisible, _columnCount;

    // One visible chunk, collected so they can be drawn nearest first. Budgeted: a static volume's, whose light maps are
    // baked to a budget (see BakeQuadBudget); Light says how it's lit (see ChunkDrawState).
    private readonly record struct ChunkDraw(float DistSq, GpuMesh? Mesh, GpuMesh? CutoutMesh, GpuMesh? TransparentMesh,
                                             ModelBlock[] Models, Mat4 Model, int Grid, ChunkPosition Chunk, GridHandle? Handle,
                                             float Dissolve = 0f, ChunkLight Light = ChunkLight.Baked, bool Budgeted = false,
                                             int Level = -1, Entity Entity = default);
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
    private double _cullMs, _stateMs, _sortMs, _bakeCheckMs, _bakeEncodeMs, _submitMs, _modelsMs, _translucentMs;
    private long _stateTicks;
    private static double Ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    private static void Smooth(ref double ema, double ms) => ema += 0.05 * (ms - ema);

    /// <summary>The most light-map quads of static volumes' chunks baked a frame (always at least one chunk's): terrain
    /// coming into view, a level of distant terrain above all, can have far more, and baking them all at once was a spike
    /// of several ms. A chunk past the budget keeps the maps it last baked, and one never baked waits.</summary>
    public const int BakeQuadBudget = 65536;
    private int _waitingFirstBake; // static volumes' chunks in view left out this frame: past the budget, never baked
    // Static volumes' chunks with something to draw whose light maps were never baked, in the order they were meshed.
    // By the volume's scale, finest first: a coarser level's chunk is often only standing in for the finer one's, which
    // the fog waits on.
    private readonly SortedDictionary<int, List<Entity>> _unbaked = new();
    private int UnbakedCount => _unbaked.Values.Sum(l => l.Count);
    private int _bakedQuads;

    /// <summary>How long a static volume's chunk takes to fade in from when it's first drawn: terrain arriving shows by
    /// fading in, not popping, the fog staying where it is (see FogSystem). Remeshing it doesn't fade it again.</summary>
    public const double ArrivalFadeSeconds = 1.0;
    private readonly Dictionary<Entity, double> _firstDrawn = new(); // when each loaded chunk was first drawn (seconds)
    private double _now; // this frame's time (seconds)

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
        world.SubscribeComponentRemoved<ChunkRenderData>((in Entity e, in ChunkRenderData _) =>
        {
            Forget(e);
            if (e.Has<LightmapsBakedFlag>()) e.Remove<LightmapsBakedFlag>(); // meshed again, baked again
        });
        world.SubscribeEntityDisposed((in Entity e) => { Forget(e); _firstDrawn.Remove(e); });
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
        if (!rd.IsEmpty && !e.Has<LightmapsBakedFlag>())
        {
            if (!_unbaked.TryGetValue(volume.Scale, out var list)) _unbaked[volume.Scale] = list = new();
            list.Add(e);
        }
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
        _now = t0 / (double)System.Diagnostics.Stopwatch.Frequency;
        _stateTicks = 0;

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
        // only the chunks in view, so one out of view waits until it's seen. A static volume's go to a budget, and a
        // chunk of one never baked isn't drawn until it is.
        _bakedQuads = 0;
        _waitingFirstBake = 0;
        int kept = 0;
        var draws = CollectionsMarshal.AsSpan(_draws);
        for (int i = 0; i < draws.Length; i++)
        {
            ref readonly var d = ref draws[i];
            if (!Bake(in d)) continue;
            if (kept != i) draws[kept] = d;
            kept++;
        }
        _draws.RemoveRange(kept, _draws.Count - kept);
        BakeFirstTimes();
        // A chunk fades in over ArrivalFadeSeconds from when it's first drawn, unless something else is fading it (a
        // swap between levels of detail, whose first draw this then is).
        draws = CollectionsMarshal.AsSpan(_draws);
        for (int i = 0; i < draws.Length; i++)
        {
            ref var d = ref draws[i];
            if (d.Entity != default)
            {
                if (!_firstDrawn.TryGetValue(d.Entity, out var at)) _firstDrawn[d.Entity] = at = d.Dissolve == 0f ? _now : 0;
                double t = (_now - at) / ArrivalFadeSeconds;
                if (t < 1.0 && d.Dissolve == 0f) d = d with { Dissolve = (float)(1.0 - t) };
            }
            if (d.TransparentMesh != null) _translucentDraws.Add(d);
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

        double stateMs = _stateTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Smooth(ref _cullMs, Ms(t0, t1) - stateMs);
        Smooth(ref _stateMs, stateMs);
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
        var root = sv.Volume.Root;
        var state = root.Has<ChunkDrawStates>() ? root.Get<ChunkDrawStates>().Of : null;
        bool copy = sv.Volume.IsLowResolutionCopy;
        foreach (var ((cx, cz), column) in sv.Columns)
        {
            var min = new Vector3D<float>(cx * blocks, column.MinY * blocks, cz * blocks);
            var max = new Vector3D<float>((cx + 1) * blocks, (column.MaxY + 1) * blocks, (cz + 1) * blocks);
            if (!frame.Frustum.Intersects(min, max)) continue;
            _columnsVisible++;
            foreach (var e in column.Chunks)
            {
                if (!e.Has<Rendered>()) continue; // not in the rendering layer (see EntityPresenceSystem)
                ref readonly var rd = ref e.Get<ChunkRenderData>();
                if (rd.IsEmpty) continue; // buried stone: nothing to draw
                var origin = e.Get<Transform>().Position;
                if (!frame.Frustum.Intersects(origin, origin + size)) continue;
                long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var how = state?.Invoke(rd.ChunkPos) ?? ChunkDrawState.Whole;
                _stateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - s0;
                if (how.Dissolve >= 1f) continue;
                var model = Mat4.Multiply(Mat4.Translation(origin), scale);
                // A scaled volume not in the light store yet has no light to show: lit as open air meanwhile.
                int grid = rd.Grid?.Index ?? -1;
                if (copy && (how.Light == ChunkLight.OpenAir || grid < 0)) { grid = Renderer.OpenAirGrid; how = how with { Light = ChunkLight.OpenAir }; }
                var centre = origin + size * 0.5f;
                _draws.Add(new ChunkDraw(Vector3D.DistanceSquared(centre, frame.CameraPosition), rd.Mesh, rd.CutoutMesh,
                                         rd.TransparentMesh, rd.Models, model, grid, rd.ChunkPos, rd.Grid, how.Dissolve,
                                         how.Light, Budgeted: true, Level: level, Entity: e));
            }
        }
    }

    /// <summary>Flags the static volumes' chunks whose light maps are now baked (<see cref="LightmapsBakedFlag"/>), and
    /// lists the next never baked, out of view or held back, for what's left of the frame's budget. A chunk not in the
    /// light store yet waits for its place there.</summary>
    private void BakeFirstTimes()
    {
        foreach (var unbaked in _unbaked.Values) BakeFirstTimes(unbaked);
    }

    private void BakeFirstTimes(List<Entity> unbaked)
    {
        int kept = 0;
        for (int i = 0; i < unbaked.Count; i++)
        {
            var e = unbaked[i];
            if (!e.IsAlive || !e.Has<ChunkRenderData>()) continue;
            ref readonly var rd = ref e.Get<ChunkRenderData>();
            if (!Baked(rd) && e.Has<ChunkUploadedFlag>() && _bakedQuads < BakeQuadBudget)
            {
                int stamp = _renderer.LightStampOf(rd.Grid, rd.ChunkPos);
                foreach (var mesh in rd.Meshes())
                {
                    if (_renderer.LightmapEverBaked(mesh)) continue;
                    _bakedQuads += (int)mesh.QuadCount;
                    _renderer.PrepareLightmap(mesh, rd.Grid, rd.ChunkPos, stamp);
                }
            }
            if (Baked(rd)) e.Set<LightmapsBakedFlag>();
            else unbaked[kept++] = e;
        }
        unbaked.RemoveRange(kept, unbaked.Count - kept);

        bool Baked(in ChunkRenderData rd)
            => _renderer.LightmapEverBaked(rd.Mesh) && _renderer.LightmapEverBaked(rd.CutoutMesh)
               && _renderer.LightmapEverBaked(rd.TransparentMesh);
    }

    /// <summary>Lists a draw's stale light maps for baking, if it's lit from them. A static volume's go while the frame's
    /// budget lasts (always the first). Returns whether it can be drawn: its maps are baked, or were once.</summary>
    private bool Bake(in ChunkDraw d)
    {
        if (d.Light == ChunkLight.OpenAir) return true;
        if (d.Light == ChunkLight.Kept) return AllBaked(in d);
        int stamp = _renderer.LightStampOf(d.Handle, d.Chunk);
        if (d.Budgeted)
        {
            int quads = Stale(d.Mesh, d.Handle, stamp) + Stale(d.CutoutMesh, d.Handle, stamp)
                      + Stale(d.TransparentMesh, d.Handle, stamp);
            if (quads == 0) return true;
            if (_bakedQuads > 0 && _bakedQuads + quads > BakeQuadBudget)
            {
                bool drawn = AllBaked(in d);
                if (!drawn) _waitingFirstBake++;
                return drawn;
            }
            _bakedQuads += quads;
        }
        _renderer.PrepareLightmap(d.Mesh, d.Handle, d.Chunk, stamp);
        _renderer.PrepareLightmap(d.CutoutMesh, d.Handle, d.Chunk, stamp);
        _renderer.PrepareLightmap(d.TransparentMesh, d.Handle, d.Chunk, stamp);
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
        ImGui.Text($"Main thread (smoothed): choosing {_cullMs:F2} ms + draw states {_stateMs:F2} ms, sort {_sortMs:F2} ms, " +
                   $"light-map checks {_bakeCheckMs:F2} ms + bake encode {_bakeEncodeMs:F2} ms");
        for (int i = 0; i <= LevelRows; i++)
        {
            var (chunks, opaque, cutout) = _levelCounts[i];
            if (chunks == 0) continue;
            string name = i == LevelRows ? "Ships" : i == 0 ? "Full detail" : $"Level {i}";
            ImGui.Text($"{name}: {chunks:N0} chunks drawn, {opaque:N0} opaque quads, {cutout:N0} cut-out quads");
        }
        ImGui.Text($"Draw calls (smoothed): chunks {_submitMs:F2} ms, model blocks {_modelsMs:F2} ms, translucent {_translucentMs:F2} ms");
        var (used, capacity) = _renderer.LightmapArenaUse;
        ImGui.Text($"Light maps: {used * 4 / 1048576.0:F1} of {capacity * 4 / 1048576.0:F1} MB; baked per frame (smoothed): " +
                   $"{_bakedMeshesEma:F1} meshes, {_bakedGroupsEma * Renderer.LightmapQuadsPerGroup:N0} quads at most");
        ImGui.Text($"Chunks in view not drawn yet, waiting for their first bake (past {BakeQuadBudget:N0} " +
                   $"quads this frame): {_waitingFirstBake:N0}; never baked in all: {UnbakedCount:N0}");
    }
}

/// <summary>How a static volume's chunk is lit (see <see cref="ChunkDrawState"/>).</summary>
public enum ChunkLight
{
    /// <summary>From its light maps, baked again whenever its light changes.</summary>
    Baked,
    /// <summary>From the light maps it last baked, not baked again (left out until it has some).</summary>
    Kept,
    /// <summary>As open air: ambient and full sun, with no light maps.</summary>
    OpenAir,
}

/// <summary>How a static volume's chunk is drawn this frame (see <see cref="ChunkDrawStates"/>).
/// <see cref="Dissolve"/>: 0 draws it whole; 1 leaves it out; d between 0 and 1 leaves out that share of its pixels in an
/// even screen dither (going away); -d draws only that share, the very pixels d leaves out (coming in over something
/// going away).</summary>
public readonly record struct ChunkDrawState(float Dissolve, ChunkLight Light = ChunkLight.Baked)
{
    public static readonly ChunkDrawState Whole = new(0f);
    public static readonly ChunkDrawState Hidden = new(1f);
}

/// <summary>How the chunk at <paramref name="pos"/> is drawn this frame.</summary>
public delegate ChunkDrawState ChunkDrawStateOf(ChunkPosition pos);

/// <summary>On a static volume's root (the world's, or a low-resolution copy's): how each of its chunks is drawn, asked for each
/// one in view every frame. Without it, they're all drawn whole, lit from their light maps.</summary>
public struct ChunkDrawStates
{
    public ChunkDrawStateOf Of;
}
