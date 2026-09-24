using ClearSkies.Engine.Core;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>Draws every <see cref="HudRenderer"/> entity's NDC-space mesh. Runs in <see cref="SystemStage.RenderHud"/>,
/// after binding the HUD pipeline and identity camera.</summary>
public sealed class HudRenderSystem : IRenderSystem
{
    private readonly EntitySet _huds;
    private readonly Renderer _renderer;

    public HudRenderSystem(World world, Renderer renderer)
    {
        _renderer = renderer;
        _huds     = world.GetEntities().With<HudRenderer>().AsSet();
    }

    public void Render(in RenderContext frame)
    {
        _renderer.BeginHudPass();
        foreach (ref readonly Entity e in _huds.GetEntities())
        {
            ref readonly var hr = ref e.Get<HudRenderer>();
            _renderer.DrawHudMesh(hr.Mesh, Mat4.Identity);
        }
    }
}
