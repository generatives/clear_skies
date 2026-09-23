using ClearSkies.Engine.Core;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering.WebGpu;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>Draws every <see cref="HudRenderer"/> entity's NDC-space mesh. Runs in <see cref="SystemStage.RenderHud"/>,
/// after binding the HUD pipeline and identity camera.</summary>
public sealed class HudRenderSystem : ISystem
{
    private readonly RenderFrame _frame;
    private readonly EntitySet _huds;
    private readonly Renderer _renderer;

    public HudRenderSystem(RenderFrame frame, World world, Renderer renderer)
    {
        _frame    = frame;
        _renderer = renderer;
        _huds     = world.GetEntities().With<HudRenderer>().AsSet();
    }

    public void Update(float dt)
    {
        if (!_frame.IsOpen) return;
        _renderer.BeginHudPass();
        foreach (ref readonly Entity e in _huds.GetEntities())
        {
            ref readonly var hr = ref e.Get<HudRenderer>();
            _renderer.DrawHudMesh(hr.Mesh, Mat4.Identity);
        }
    }
}
