using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Ui;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;

namespace ClearSkies.Game.Startup;

/// <summary>
/// What a game shown in a window draws with: the texture atlas, block models and chunk meshes, other players' model,
/// the game UI, and the GPU light storage the world streams into, which is what limits how much of it loads
/// (<see cref="Budget"/>). Made before the <see cref="GameWorld"/>, which is given the parts it needs. Each game
/// schedules the view's input and gameplay systems itself, among the world's; what's on the GPU and drawing come last,
/// with <see cref="AddRender"/>.
/// </summary>
public sealed class GameView : IDisposable
{
    public GameView(EngineHost host, LaunchOptions options)
    {
        Host = host;
        host.Renderer.LoadTextureAtlas(
            Path.Combine(AppContext.BaseDirectory, "Resources", "spritesheet_tiles.png"),
            Path.Combine(AppContext.BaseDirectory, "Resources", "spritesheet_tiles.xml"));
        if (options.FrameRateCap is { } fps) host.CapFrameRate(fps);
        host.Input.CursorCaptured = false; // the F1 debug menu starts open, and F1 frees the cursor with it

        // Model blocks' glTF models (BlockDef.Model paths are relative to Resources/Models — see the csproj's link of the
        // blockbench folder), loaded on first use.
        BlockModels = new BlockModelLibrary(host.Renderer, Path.Combine(AppContext.BaseDirectory, "Resources", "Models"));
        Meshes = new ChunkMeshSystem(host.World, host.Renderer, BlockModels);
        PlayerModel = new PlayerModel(host.Renderer);

        // Game UI (immediate mode, laid out by Clay).
        Ui = new UiContext(host.Window, host.Input);
        Ui.AddFont(UiFont.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts", "PixelifySans.ttf")));
        Ui.Atlas.LoadSprites(Path.Combine(AppContext.BaseDirectory, "Resources", "Ui")); // the HUD's; name.9.png is nine-sliced

        // Shared GPU voxel storage for lighting (world and ships). Its light budget limits how much of the world loads.
        GridStore = new GridStore(host.Context, (int)((long)options.LightBudgetMb * 1024 * 1024 / GridStore.SlotBytes),
                                  LightBudget.WorldIndexDim(options.ViewDistance));
        host.Renderer.AttachGridStore(GridStore);
        Budget = new LightBudget(GridStore);

        InputSample = new InputSampleSystem(host.World, host.Input, host.Time);
    }

    public EngineHost Host { get; }
    public BlockModelLibrary BlockModels { get; }
    public ChunkMeshSystem Meshes { get; }
    public PlayerModel PlayerModel { get; }
    public UiContext Ui { get; }
    public GridStore GridStore { get; }

    /// <summary>How much of the world loads: as much as the light storage holds.</summary>
    public IChunkBudget Budget { get; }

    /// <summary>Packs each loaded chunk for the light storage as it loads.</summary>
    public IChunkPreparer ChunkPreparer { get; } = new LightPacking();

    /// <summary>Latches each frame's input (in the Input stage), and hands it to the tick as the local player's
    /// PlayerInput (early in the tick).</summary>
    public InputSampleSystem InputSample { get; }

    private CloudRenderSystem? _clouds;
    private UiRenderSystem? _uiRenderer;

    /// <summary>
    /// Last: what's on the GPU (lighting storage, light, chunk meshes, block models), then drawing. The host opens the
    /// frame, runs the render stages (systems in the order added within a stage), then closes it with ImGui and presents.
    /// Each render system is handed this frame's camera and time.
    /// </summary>
    public void AddRender(GameWorld world)
    {
        var host = Host;
        var renderer = host.Renderer;
        var volume = world.StaticVolume;
        host.AddSystem(new GpuResidencySystem(host.World, volume, GridStore), SystemStage.PreRender);
        host.AddSystem(new GpuLightSystem(host.World, volume, host.Context, GridStore), SystemStage.PreRender);
        host.AddSystem(Meshes, SystemStage.PreRender);
        host.AddSystem(new BlockModelSystem(host.World, BlockModels), SystemStage.PreRender); // block entities -> RenderedModel

        SkySettings.CloudAltitude = 1250f; // the islands are mostly low: clouds among the hills
        SkySettings.CloudSeaAltitude = HeartGrid.CloudSeaAltitude; // below its lowest islands
        _clouds = new CloudRenderSystem(renderer, new HeartCloudDensity(world.Seed));
        _uiRenderer = new UiRenderSystem(Ui, renderer);
        host.AddSystem(new ChunkRenderSystem(host.World, renderer, volume), SystemStage.RenderWorld);
        host.AddSystem(new ModelRenderSystem(host.World, renderer, host.Time), SystemStage.RenderWorld);
        host.AddSystem(_clouds, SystemStage.RenderWorld);
        host.AddSystem(new SkyRenderSystem(renderer), SystemStage.RenderSky);
        host.AddSystem(new WireframeRenderSystem(host.World, renderer), SystemStage.RenderOverlay);
        host.AddSystem(new HudRenderSystem(host.World, renderer), SystemStage.RenderHud);
        host.AddSystem(_uiRenderer, SystemStage.RenderHud);
    }

    public void Dispose()
    {
        _uiRenderer?.Dispose();
        _clouds?.Dispose();
        PlayerModel.Dispose();
        BlockModels.Dispose();
        Ui.Dispose();
        GridStore.Dispose();
    }
}
