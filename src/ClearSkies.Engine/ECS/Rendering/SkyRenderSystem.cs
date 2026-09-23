using ClearSkies.Engine.Rendering.WebGpu;

namespace ClearSkies.Engine.ECS;

/// <summary>Fills every pixel the world left uncovered with the sky gradient and sun (see
/// <see cref="Renderer.DrawSky"/>). Belongs in <see cref="RenderPass.Sky"/>.</summary>
public sealed class SkyRenderSystem : IRenderSystem
{
    private readonly Renderer _renderer;

    public SkyRenderSystem(Renderer renderer) => _renderer = renderer;

    public void Render(in RenderContext frame) => _renderer.DrawSky();
}
