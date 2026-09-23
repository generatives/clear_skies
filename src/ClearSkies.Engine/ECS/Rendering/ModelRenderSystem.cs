using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>Draws every frustum-visible standalone <see cref="ModelRenderer"/> entity at its <see cref="Transform"/>
/// (model blocks are drawn by <see cref="ChunkRenderSystem"/> instead). Belongs in <see cref="RenderPass.World"/>.</summary>
public sealed class ModelRenderSystem : IRenderSystem
{
    private readonly EntitySet _models;
    private readonly Renderer _renderer;

    public ModelRenderSystem(World world, Renderer renderer)
    {
        _renderer = renderer;
        _models   = world.GetEntities().With<Transform>().With<ModelRenderer>().AsSet();
    }

    public void Render(in RenderContext frame)
    {
        foreach (ref readonly Entity e in _models.GetEntities())
        {
            ref readonly var mr = ref e.Get<ModelRenderer>();
            var model = e.Get<Transform>().ToMatrix();
            if (!frame.Frustum.Intersects(model, mr.Model.BoundsMin, mr.Model.BoundsMax)) continue;
            _renderer.DrawModel(mr.Model, model);
        }
    }
}
