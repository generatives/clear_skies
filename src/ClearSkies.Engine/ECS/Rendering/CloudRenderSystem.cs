using ClearSkies.Engine.Core;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;

namespace ClearSkies.Engine.ECS;

/// <summary>Draws the <see cref="CloudLayer"/> around the camera while <see cref="SkySettings.CloudsEnabled"/>.
/// Depth-tested real geometry, so it runs in <see cref="SystemStage.RenderWorld"/> (after the terrain, which occludes
/// more of it than it occludes of the terrain).</summary>
public sealed class CloudRenderSystem : ISystem, IDisposable
{
    private readonly RenderFrame _frame;
    private readonly CloudLayer _clouds;

    public CloudRenderSystem(RenderFrame frame, Renderer renderer)
    {
        _frame  = frame;
        _clouds = new CloudLayer(renderer);
    }

    public void Update(float dt)
    {
        if (!_frame.IsOpen) return;
        var frame = _frame.Context;
        if (SkySettings.CloudsEnabled) _clouds.Draw(frame.CameraPosition, frame.TimeSeconds);
    }

    public void Dispose() => _clouds.Dispose();
}
