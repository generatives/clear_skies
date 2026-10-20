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
    public GameView(WindowedEngineHost host, LaunchOptions options)
    {
        Host = host;
        _lightingPreset = options.LightingPreset;
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
        _checkerBounce = options.CheckerBounce;
        _lod = options.Lod;
        _lodDistance = options.LodDistance;
        _lodLevels = options.LodLevels;

        InputSample = new InputSampleSystem(host.World, host.Input, host.Time);
    }

    private readonly int? _checkerBounce;
    private readonly bool _lod;
    private readonly float? _lodDistance;
    private readonly int _lodLevels;

    public WindowedEngineHost Host { get; }
    public BlockModelLibrary BlockModels { get; }
    public ChunkMeshSystem Meshes { get; }
    public PlayerModel PlayerModel { get; }
    public UiContext Ui { get; }
    public GridStore GridStore { get; }

    /// <summary>Distant terrain drawn coarse (made in <see cref="AddRender"/>): level 1, past full detail.</summary>
    public LodTerrainSystem? Lod { get; private set; }

    /// <summary>The coarsest level of distant terrain, which the fog follows while it's on (see LodTerrainSystem.ReadyRadius).</summary>
    public LodTerrainSystem? FarthestLod { get; private set; }

    /// <summary>Levels of distant terrain made (each drawn past the one before when ticked in its panel).</summary>
    private const int LodLevelsMade = 2;

    /// <summary>How much of the world loads: as much as the light storage holds.</summary>
    public IChunkBudget Budget { get; }

    /// <summary>Packs each loaded chunk for the light storage as it loads.</summary>
    public IChunkPreparer ChunkPreparer { get; } = new LightPacking();

    /// <summary>Latches each frame's input (in the Input stage), and hands it to the tick as the local player's
    /// PlayerInput (early in the tick).</summary>
    public InputSampleSystem InputSample { get; }

    private readonly string? _lightingPreset;
    private CloudRenderSystem? _clouds;
    private GrappleRopeSystem? _ropes;
    private ParticleRenderer? _particles;
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
        var light = new GpuLightSystem(host.World, volume, host.Context, GridStore);
        if (_lightingPreset != null && !light.ApplyPreset(_lightingPreset))
            Console.WriteLine($"Unknown lighting preset '{_lightingPreset}' (minimal, low, medium or high); using medium.");
        if (_checkerBounce is { } spread) light.BounceSpread = spread;
        host.AddSystem(light, SystemStage.PreRender);
        host.AddSystem(Meshes, SystemStage.PreRender);
        host.AddSystem(new BlockModelSystem(host.World, BlockModels), SystemStage.PreRender); // block entities -> RenderedModel

        SkySettings.CloudAltitude = 1550f; // the islands are mostly low: clouds among the hills
        SkySettings.CloudSeaAltitude = HeartGrid.CloudSeaAltitude; // below its lowest islands
        _clouds = new CloudRenderSystem(renderer, new HeartCloudDensity(world.Seed));
        _uiRenderer = new UiRenderSystem(Ui, renderer);
        _particles = new ParticleRenderer(host.World, renderer);
        host.AddSystem(new ThrusterFlameSystem(host.World), SystemStage.Frame); // aims each Fan's flames
        host.AddSystem((ISystem)_particles, SystemStage.Frame); // moves every emitter's particles
        var chunks = new ChunkRenderSystem(host.World, renderer, volume);
        host.AddSystem(chunks, SystemStage.RenderWorld);
        // Distant terrain drawn coarse past the full-detail terrain, after it, in levels each past the one before (level
        // 1 on unless --no-lod; the rest with --lod-levels).
        var levels = new List<LodTerrainSystem>();
        for (int i = 0; i < LodLevelsMade; i++)
        {
            var level = new LodTerrainSystem(host.World, renderer, world.ChunkLoad, volume, () => new HeartWorldGenerator(world.Seed),
                                             world.ChunkStore, GridStore, levels.LastOrDefault())
                        { Enabled = _lod && i < _lodLevels };
            light.AddStaticGrid(level.Grid);     // lit like the world
            level.IsLit = light.IsLit;           // swaps wait for the incoming terrain's light
            level.Hurry = light.Hurry;           // which, where it holds the fog, goes ahead of the light queues
            // Each level reaches four times as far as the one before, from 1024 blocks for level 1 (2048 alone).
            level.OuterRadius = i == 0 && _lodLevels <= 1 ? 2048f : 1024f * (1 << (2 * i));
            levels.Add(level);
            host.AddSystem((ISystem)level, SystemStage.PreRender);
            host.AddSystem((IRenderSystem)level, SystemStage.RenderWorld);
        }
        host.AddSystem(new DistantTerrainPanel(levels), SystemStage.Frame); // one window for every level
        Lod = levels[0];
        FarthestLod = levels[^1];
        if (_lodDistance is { } lodDistance) levels[System.Math.Clamp(_lodLevels, 1, levels.Count) - 1].OuterRadius = lodDistance;
        chunks.Cover = Lod.CoverOf;
        world.ChunkLoad.HeldBy = Lod.HoldsFullChunk; // full detail leaving stays until the coarse terrain is in over it
        host.AddSystem(new ModelRenderSystem(host.World, renderer, host.Time), SystemStage.RenderWorld);
        host.AddSystem(_clouds, SystemStage.RenderWorld);
        host.AddSystem((IRenderSystem)_particles, SystemStage.RenderWorld);
        host.AddSystem(new SkyRenderSystem(renderer), SystemStage.RenderSky);
        host.AddSystem(new WireframeRenderSystem(host.World, renderer), SystemStage.RenderOverlay);
        host.AddSystem(_ropes = new GrappleRopeSystem(host.World, renderer, world.Registry), SystemStage.RenderOverlay); // grapple ropes, ours and others'
        host.AddSystem(chunks, SystemStage.RenderTransparent);
        foreach (var level in levels) host.AddSystem((IRenderSystem)level, SystemStage.RenderTransparent);
        host.AddSystem(new HudRenderSystem(host.World, renderer), SystemStage.RenderHud);
        host.AddSystem(_uiRenderer, SystemStage.RenderHud);
    }

    public void Dispose()
    {
        _uiRenderer?.Dispose();
        _clouds?.Dispose();
        _ropes?.Dispose();
        _particles?.Dispose();
        PlayerModel.Dispose();
        BlockModels.Dispose();
        Ui.Dispose();
        GridStore.Dispose();
    }
}
