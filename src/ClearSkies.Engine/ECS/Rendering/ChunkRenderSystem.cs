using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
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
/// outside the view is skipped in one test instead of one per chunk: looking at every chunk each frame cost ~6 ms.
/// Ships' chunks, which move, are tested one by one.
/// </summary>
public sealed class ChunkRenderSystem : IStagedRenderSystem, IDebugUiSystem
{
    private readonly Renderer _renderer;
    private readonly GridHandle _world;

    // The static world's chunks with render data, by column; every other chunk (ships') on its own.
    private sealed class WorldColumn
    {
        public readonly List<Entity> Chunks = new();
        public int MinY, MaxY; // chunk layers, inclusive
    }
    private readonly Dictionary<(int X, int Z), WorldColumn> _columns = new();
    private readonly Dictionary<Entity, (int X, int Z)> _columnOf = new();
    private readonly HashSet<Entity> _others = new();
    private int _columnsVisible;

    // One visible chunk, collected so they can be drawn nearest first.
    private readonly record struct ChunkDraw(float DistSq, GpuMesh? Mesh, GpuMesh? CutoutMesh, GpuMesh? TransparentMesh,
                                             ModelBlock[] Models, Mat4 Model, int Grid, ChunkPosition Chunk);
    private readonly List<ChunkDraw> _draws = new();
    private readonly List<ChunkDraw> _translucentDraws = new(); // the visible chunks with translucent faces
    private static readonly Comparison<ChunkDraw> NearestFirst = (a, b) => a.DistSq.CompareTo(b.DistSq);
    private int _modelBlocksDrawn;

    /// <summary>Cell-local placement per <see cref="BlockOrientation"/> (indexed by its byte): a model block's model is
    /// authored Blockbench-style (x/z centred on the origin, standing on y = 0, 1 unit = 1 block), so it is rotated
    /// about the cell centre to the orientation, standing on the cell face opposite its top.</summary>
    private static readonly Mat4[] OrientationPlacement = BuildOrientationPlacements();

    /// <param name="staticVolume">The static world, whose chunks are culled by column (it never moves).</param>
    public ChunkRenderSystem(World world, Renderer renderer, ChunkVolume staticVolume)
    {
        _renderer = renderer;
        _world    = staticVolume.Gpu;
        world.SubscribeComponentAdded<ChunkRenderData>(OnAdded);
        world.SubscribeComponentRemoved<ChunkRenderData>((in Entity e, in ChunkRenderData _) => Forget(e));
        world.SubscribeEntityDisposed((in Entity e) => Forget(e));
    }

    private void OnAdded(in Entity e, in ChunkRenderData rd)
    {
        if (rd.Grid != _world) { _others.Add(e); return; }
        var key = (rd.ChunkPos.X, rd.ChunkPos.Z);
        if (!_columns.TryGetValue(key, out var column)) _columns[key] = column = new WorldColumn();
        column.Chunks.Add(e);
        _columnOf[e] = key;
        UpdateRange(column);
    }

    private void Forget(Entity e)
    {
        _others.Remove(e);
        if (!_columnOf.Remove(e, out var key)) return;
        var column = _columns[key];
        column.Chunks.Remove(e);
        if (column.Chunks.Count == 0) _columns.Remove(key);
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

        // The static world sits at the origin unrotated (see ChunkVolume), so a chunk's box is its position's.
        foreach (var ((cx, cz), column) in _columns)
        {
            var min = new Vector3D<float>(cx * ChunkData.Size, column.MinY * ChunkData.Size, cz * ChunkData.Size);
            var max = new Vector3D<float>((cx + 1) * ChunkData.Size, (column.MaxY + 1) * ChunkData.Size, (cz + 1) * ChunkData.Size);
            if (!frame.Frustum.Intersects(min, max)) continue;
            _columnsVisible++;
            foreach (var e in column.Chunks)
            {
                if (!e.Has<Rendered>()) continue; // not in the rendering layer (see EntityPresenceSystem)
                ref readonly var rd = ref e.Get<ChunkRenderData>();
                if (rd.IsEmpty) continue; // buried stone: nothing to draw
                var origin = e.Get<Transform>().Position;
                if (!frame.Frustum.Intersects(origin, origin + size)) continue;
                var centre = origin + half;
                AddDraw(new ChunkDraw(Vector3D.DistanceSquared(centre, frame.CameraPosition), rd.Mesh, rd.CutoutMesh,
                                         rd.TransparentMesh, rd.Models, Mat4.Translation(origin), rd.Grid?.Index ?? -1, rd.ChunkPos));
            }
        }

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
            AddDraw(new ChunkDraw(distSq, rd.Mesh, rd.CutoutMesh, rd.TransparentMesh, rd.Models, model, rd.Grid?.Index ?? -1,
                                     rd.ChunkPos));
        }

        // Nearest first, so the depth test rejects hidden fragments before the (expensive) lighting shader runs on
        // them instead of shading them and overwriting them later.
        _draws.Sort(NearestFirst);
        foreach (var d in _draws)
            if (d.Mesh != null) _renderer.DrawChunkMesh(d.Mesh, d.Model, d.Grid, d.Chunk);
        // Cut-out faces after all the opaque ones: their shader discards texels, which costs the early depth test, so
        // the opaque world is drawn without it first.
        foreach (var d in _draws)
            if (d.CutoutMesh != null) _renderer.DrawCutoutChunkMesh(d.CutoutMesh, d.Model, d.Grid, d.Chunk);

        _modelBlocksDrawn = 0;
        foreach (var d in _draws)
        {
            foreach (var m in d.Models)
            {
                var cell  = Mat4.Translation(new Vector3D<float>(m.X, m.Y, m.Z));
                var world = Mat4.Multiply(d.Model, Mat4.Multiply(cell, OrientationPlacement[m.Orientation.ToByte()]));
                _renderer.DrawModel(m.Model, world, d.Grid, d.Chunk, new Vector3D<int>(m.X, m.Y, m.Z));
                _modelBlocksDrawn++;
            }
        }
    }

    private void AddDraw(in ChunkDraw d)
    {
        _draws.Add(d);
        if (d.TransparentMesh != null) _translucentDraws.Add(d);
    }

    /// <summary>The translucent meshes of the chunks <see cref="RenderWorld"/> found visible this frame: their depth,
    /// then their colour where they're the nearest translucent face, so each pixel shows one translucent layer whatever
    /// order the chunks (and their faces) are drawn in.</summary>
    private void RenderTranslucent()
    {
        foreach (var d in _translucentDraws) _renderer.DrawTransparentChunkDepth(d.TransparentMesh!, d.Model, d.Grid, d.Chunk);
        foreach (var d in _translucentDraws) _renderer.DrawTransparentChunkMesh(d.TransparentMesh!, d.Model, d.Grid, d.Chunk);
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
                   $"{_columnsVisible:N0} of {_columns.Count:N0}; ship chunks: {_others.Count:N0}");
        ImGui.Text($"Model blocks drawn: {_modelBlocksDrawn:N0}; chunks with translucent faces drawn: {_translucentDraws.Count:N0}");
    }
}
