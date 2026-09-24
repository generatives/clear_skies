using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;

namespace ClearSkies.Engine.ECS;

/// <summary>Draws the <see cref="CloudLayer"/> around the camera while <see cref="SkySettings.CloudsEnabled"/>.
/// Depth-tested real geometry, so it runs in <see cref="SystemStage.RenderWorld"/> (after the terrain, which occludes
/// more of it than it occludes of the terrain).</summary>
public sealed class CloudRenderSystem : IRenderSystem, IDisposable
{
    private readonly CloudLayer _clouds;

    public CloudRenderSystem(Renderer renderer)
    {
        _clouds = new CloudLayer(renderer);
    }

    public void Render(in RenderContext frame)
    {
        if (SkySettings.CloudsEnabled) _clouds.Draw(frame.CameraPosition, frame.TimeSeconds);
    }

    public void Dispose() => _clouds.Dispose();
}
