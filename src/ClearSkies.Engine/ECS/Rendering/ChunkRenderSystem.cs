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
/// cubes nearest first, then each visible chunk's model blocks — placed at their cell, turned to their stored
/// <see cref="BlockOrientation"/> and lit from that cell's voxel light. Runs in <see cref="SystemStage.RenderWorld"/>.
/// </summary>
public sealed class ChunkRenderSystem : IRenderSystem, IDebugUiSystem
{
    private readonly EntitySet _chunks;
    private readonly Renderer _renderer;

    // One visible chunk, collected so they can be drawn nearest first.
    private readonly record struct ChunkDraw(float DistSq, GpuMesh? Mesh, ModelBlock[] Models, Mat4 Model, int Grid,
                                             ChunkPosition Chunk);
    private readonly List<ChunkDraw> _draws = new();
    private static readonly Comparison<ChunkDraw> NearestFirst = (a, b) => a.DistSq.CompareTo(b.DistSq);
    private int _modelBlocksDrawn;

    /// <summary>Cell-local placement per <see cref="BlockOrientation"/> (indexed by its byte): a model block's model is
    /// authored Blockbench-style (x/z centred on the origin, standing on y = 0, 1 unit = 1 block), so it is rotated
    /// about the cell centre to the orientation, standing on the cell face opposite its top.</summary>
    private static readonly Mat4[] OrientationPlacement = BuildOrientationPlacements();

    public ChunkRenderSystem(World world, Renderer renderer)
    {
        _renderer = renderer;
        _chunks   = world.GetEntities().With<Transform>().With<ChunkRenderData>().AsSet();
    }

    public void Render(in RenderContext frame)
    {
        // Every chunk's box is exactly ChunkData.Size local units on a side (GreedyMesher's local space); its model
        // blocks sit in its cells, so the same box culls them too.
        _draws.Clear();
        var size = new Vector3D<float>(ChunkData.Size);
        var half = size * 0.5f;
        foreach (ref readonly Entity e in _chunks.GetEntities())
        {
            ref readonly var rd = ref e.Get<ChunkRenderData>();
            var model = e.Get<Transform>().ToMatrix();
            if (!frame.Frustum.Intersects(model, Vector3D<float>.Zero, size)) continue;

            float distSq = Vector3D.DistanceSquared(model.TransformPoint(half), frame.CameraPosition);
            _draws.Add(new ChunkDraw(distSq, rd.Mesh, rd.Models, model, rd.Grid?.Index ?? -1, rd.ChunkPos));
        }

        // Nearest first, so the depth test rejects hidden fragments before the (expensive) lighting shader runs on
        // them instead of shading them and overwriting them later.
        _draws.Sort(NearestFirst);
        foreach (var d in _draws)
            if (d.Mesh != null) _renderer.DrawChunkMesh(d.Mesh, d.Model, d.Grid, d.Chunk);

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
        ImGui.Text($"Chunks visible: {_draws.Count:N0} of {_chunks.Count:N0}");
        ImGui.Text($"Model blocks drawn: {_modelBlocksDrawn:N0}");
    }
}
