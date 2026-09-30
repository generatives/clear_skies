using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Ui;
using ClearSkies.Game.Generation;

namespace ClearSkies.Game.Startup.Systems;

/// <summary>
/// Not headless. Before drawing: what's on the GPU (lighting storage, light, chunk meshes, block models). Then drawing: the host opens
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
        var renderer = host.Renderer!;
        var store = w.GridStore!;
        host.AddSystem(new GpuResidencySystem(host.World, w.StaticVolume, store), SystemStage.PreRender);
        host.AddSystem(new GpuLightSystem(host.World, w.StaticVolume, host.Context!, store), SystemStage.PreRender);
        host.AddSystem(w.Meshes!,SystemStage.PreRender);
        host.AddSystem(new BlockModelSystem(host.World, w.BlockModels!), SystemStage.PreRender); // block entities -> RenderedModel

        _clouds = new CloudRenderSystem(renderer, new HeartCloudDensity(w.Seed));
        host.AddSystem(new ChunkRenderSystem(host.World, renderer, w.StaticVolume), SystemStage.RenderWorld);
        host.AddSystem(new ModelRenderSystem(host.World, renderer, host.Time), SystemStage.RenderWorld);
        host.AddSystem(_clouds, SystemStage.RenderWorld);
        host.AddSystem(new SkyRenderSystem(renderer), SystemStage.RenderSky);
        host.AddSystem(new WireframeRenderSystem(host.World, renderer), SystemStage.RenderOverlay);
        host.AddSystem(new HudRenderSystem(host.World, renderer), SystemStage.RenderHud);
        _ui = new UiRenderSystem(w.Ui!, renderer);
        host.AddSystem(_ui, SystemStage.RenderHud);
    }

    public void Dispose()
    {
        _ui?.Dispose();
        _clouds?.Dispose();
    }
}
