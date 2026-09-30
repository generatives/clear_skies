using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Ui;
using ClearSkies.Game.Generation;

namespace ClearSkies.Game.Startup.Systems;

/// <summary>
/// Before drawing: what's on the GPU (lighting storage, light, chunk meshes, block models). Then drawing: the host opens
/// the frame, runs the render stages (systems in the order added within a stage), then closes it with ImGui and
/// presents. Each render system is handed this frame's camera and time.
/// </summary>
public sealed class RenderSystems : IDisposable
{
    private CloudRenderSystem? _clouds;
    private UiRenderSystem? _ui;

    public void Add(GameWorld w)
    {
        var host = w.Host;
        host.AddSystem(new GpuResidencySystem(host.World, w.StaticVolume, w.GridStore), SystemStage.PreRender);
        host.AddSystem(new GpuLightSystem(host.World, w.StaticVolume, host.Context, w.GridStore), SystemStage.PreRender);
        host.AddSystem(w.Meshes, SystemStage.PreRender);
        host.AddSystem(new BlockModelSystem(host.World, w.BlockModels), SystemStage.PreRender); // block entities -> RenderedModel

        _clouds = new CloudRenderSystem(host.Renderer, new HeartCloudDensity(w.Seed));
        host.AddSystem(new ChunkRenderSystem(host.World, host.Renderer, w.StaticVolume), SystemStage.RenderWorld);
        host.AddSystem(new ModelRenderSystem(host.World, host.Renderer, host.Time), SystemStage.RenderWorld);
        host.AddSystem(_clouds, SystemStage.RenderWorld);
        host.AddSystem(new SkyRenderSystem(host.Renderer), SystemStage.RenderSky);
        host.AddSystem(new WireframeRenderSystem(host.World, host.Renderer), SystemStage.RenderOverlay);
        host.AddSystem(new HudRenderSystem(host.World, host.Renderer), SystemStage.RenderHud);
        _ui = new UiRenderSystem(w.Ui, host.Renderer);
        host.AddSystem(_ui, SystemStage.RenderHud);
    }

    public void Dispose()
    {
        _ui?.Dispose();
        _clouds?.Dispose();
    }
}
