using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering.WebGpu;

namespace ClearSkies.Engine.ECS;

/// <summary>Fills every pixel the world left uncovered with the sky gradient and sun (see
/// <see cref="Renderer.DrawSky"/>). Runs in <see cref="SystemStage.RenderSky"/>.</summary>
public sealed class SkyRenderSystem : ISystem
{
    private readonly RenderFrame _frame;
    private readonly Renderer _renderer;

    public SkyRenderSystem(RenderFrame frame, Renderer renderer)
    {
        _frame    = frame;
        _renderer = renderer;
    }

    public void Update(float dt)
    {
        if (_frame.IsOpen) _renderer.DrawSky();
    }
}
