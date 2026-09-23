using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>Draws every frustum-visible standalone <see cref="ModelRenderer"/> entity at its <see cref="Transform"/>
/// (model blocks are drawn by <see cref="ChunkRenderSystem"/> instead). Runs in <see cref="SystemStage.RenderWorld"/>.</summary>
public sealed class ModelRenderSystem : ISystem
{
    private readonly RenderFrame _frame;
    private readonly EntitySet _models;
    private readonly Renderer _renderer;

    public ModelRenderSystem(RenderFrame frame, World world, Renderer renderer)
    {
        _frame    = frame;
        _renderer = renderer;
        _models   = world.GetEntities().With<Transform>().With<ModelRenderer>().AsSet();
    }

    public void Update(float dt)
    {
        if (!_frame.IsOpen) return;
        var frame = _frame.Context;
        foreach (ref readonly Entity e in _models.GetEntities())
        {
            ref readonly var mr = ref e.Get<ModelRenderer>();
            var model = e.Get<Transform>().ToMatrix();
            if (!frame.Frustum.Intersects(model, mr.Model.BoundsMin, mr.Model.BoundsMax)) continue;
            _renderer.DrawModel(mr.Model, model);
        }
    }
}
