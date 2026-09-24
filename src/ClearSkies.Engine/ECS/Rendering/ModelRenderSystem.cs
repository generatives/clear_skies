using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Draws every frustum-visible <see cref="ModelRenderer"/> entity at its <see cref="Transform"/>: standalone props
/// and block entities alike (static model blocks are drawn by <see cref="ChunkRenderSystem"/> instead). An entity
/// with an <see cref="AnimatedModel"/> is drawn in its own pose, computed here from its node rotations (so only for
/// entities that are actually visible); one with <see cref="VoxelLit"/> is lit from that voxel's light. Runs in
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
        _models   = world.GetEntities().With<Transform>().With<ModelRenderer>().AsSet();
    }

    public void Render(in RenderContext frame)
    {
        _drawn = 0;
        foreach (ref readonly Entity e in _models.GetEntities())
        {
            var gpuModel = e.Get<ModelRenderer>().Model;
            var model = e.Get<Transform>().ToMatrix();
            if (!frame.Frustum.Intersects(model, gpuModel.BoundsMin, gpuModel.BoundsMax)) continue;

            ReadOnlySpan<Mat4> pose = default;
            if (e.Has<AnimatedModel>())
            {
                ref readonly var anim = ref e.Get<AnimatedModel>();
                gpuModel.ComputePose(anim.Pose, anim.NodeRotations);
                pose = anim.Pose;
            }

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
