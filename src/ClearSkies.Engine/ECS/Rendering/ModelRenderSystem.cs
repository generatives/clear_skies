using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Draws every frustum-visible <see cref="RenderedModel"/> entity at its <see cref="Transform"/>: standalone props
/// and block entities alike (static model blocks are drawn by <see cref="ChunkRenderSystem"/> instead). Each is drawn
/// in its own pose, computed here from its node rotations (so only for entities that are actually visible); one
/// with <see cref="VoxelLit"/> is lit from that voxel's light. Runs in
/// <see cref="SystemStage.RenderWorld"/>.
/// </summary>
public sealed class ModelRenderSystem : IRenderSystem, IDebugUiSystem
{
    private readonly EntitySet _models;
    private readonly Renderer _renderer;
    private int _drawn;

    public ModelRenderSystem(World world, Renderer renderer)
    {
        _renderer = renderer;
        _models   = world.GetEntities().With<Transform>().With<RenderedModel>().AsSet();
    }

    public void Render(in RenderContext frame)
    {
        _drawn = 0;
        foreach (ref readonly Entity e in _models.GetEntities())
        {
            ref readonly var rm = ref e.Get<RenderedModel>();
            var gpuModel = rm.Model;
            if (gpuModel is null) continue; // default-constructed: nothing to draw
            var model = e.Get<Transform>().ToMatrix();
            if (!frame.Frustum.Intersects(model, gpuModel.BoundsMin, gpuModel.BoundsMax)) continue;

            var pose = rm.ComputePose();

            if (e.Has<VoxelLit>())
            {
                ref readonly var lit = ref e.Get<VoxelLit>();
                _renderer.DrawModel(gpuModel, model, lit.Grid.Index, lit.Chunk, lit.Cell, pose);
            }
            else
            {
                _renderer.DrawModel(gpuModel, model, pose: pose);
            }
            _drawn++;
        }
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Model Rendering";

    public void DrawDebugUi() => ImGui.Text($"Model entities: {_models.Count:N0}, drawn this frame: {_drawn:N0}");
}
