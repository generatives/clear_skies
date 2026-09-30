using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering;
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
    private readonly GameWorld _world;
    private readonly GameView _view;
    private readonly CloudRenderSystem _clouds;
    private readonly UiRenderSystem _ui;

    public RenderSystems(GameWorld world, GameView view)
    {
        _world = world;
        _view = view;
        SkySettings.CloudAltitude = 1250f; // the islands are mostly low: clouds among the hills
        SkySettings.CloudSeaAltitude = HeartGrid.CloudSeaAltitude; // below its lowest islands
        _clouds = new CloudRenderSystem(view.Host.Renderer, new HeartCloudDensity(world.Seed));
        _ui = new UiRenderSystem(view.Ui, view.Host.Renderer);
    }

    public void Add()
    {
        var host = _view.Host;
        var renderer = host.Renderer;
        var volume = _world.StaticVolume;
        host.AddSystem(new GpuResidencySystem(host.World, volume, _view.GridStore), SystemStage.PreRender);
        host.AddSystem(new GpuLightSystem(host.World, volume, host.Context, _view.GridStore), SystemStage.PreRender);
        host.AddSystem(_view.Meshes, SystemStage.PreRender);
        host.AddSystem(new BlockModelSystem(host.World, _view.BlockModels), SystemStage.PreRender); // block entities -> RenderedModel

        host.AddSystem(new ChunkRenderSystem(host.World, renderer, volume), SystemStage.RenderWorld);
        host.AddSystem(new ModelRenderSystem(host.World, renderer, host.Time), SystemStage.RenderWorld);
        host.AddSystem(_clouds, SystemStage.RenderWorld);
        host.AddSystem(new SkyRenderSystem(renderer), SystemStage.RenderSky);
        host.AddSystem(new WireframeRenderSystem(host.World, renderer), SystemStage.RenderOverlay);
        host.AddSystem(new HudRenderSystem(host.World, renderer), SystemStage.RenderHud);
        host.AddSystem(_ui, SystemStage.RenderHud);
    }

    public void Dispose()
    {
        _ui.Dispose();
        _clouds.Dispose();
    }
}
