using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>Draws every <see cref="WireframeRenderer"/> entity as a full-bright wireframe at its
/// <see cref="Transform"/>. Runs in <see cref="SystemStage.RenderOverlay"/>.</summary>
public sealed class WireframeRenderSystem : IRenderSystem
{
    private readonly EntitySet _wireframes;
    private readonly Renderer _renderer;

    public WireframeRenderSystem(World world, Renderer renderer)
    {
        _renderer   = renderer;
        _wireframes = world.GetEntities().With<Transform>().With<WireframeRenderer>().AsSet();
    }

    public void Render(in RenderContext frame)
    {
        foreach (ref readonly Entity e in _wireframes.GetEntities())
        {
            ref readonly var t  = ref e.Get<Transform>();
            ref readonly var wr = ref e.Get<WireframeRenderer>();
            _renderer.DrawMeshWireframe(wr.Mesh, t.ToMatrix());
        }
    }
}
