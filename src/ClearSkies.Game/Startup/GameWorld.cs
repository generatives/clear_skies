using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Ui;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;
using ClearSkies.Game.Startup.Systems;
using ClearSkies.Net.Session;
using ClearSkies.Net.Transport;
using Silk.NET.Maths;

namespace ClearSkies.Game.Startup;

/// <summary>
/// What every game has, whichever way it started (alone, hosting, joining): the engine host and its window, this
/// machine's session, the entity registry and command system, the static world streamed around the view, and the
/// systems that play and draw it. Built from a seed (the save's, or the host's) and where edited terrain chunks come
/// from; the game that built it adds its network session (and the save's systems, on a host) with
/// <see cref="AddSystems"/>, which puts everything in order.
/// </summary>
public sealed class GameWorld : IDisposable
{
    /// <summary>The islands stream from layer 0 (blocks -256..1792: HeartGrid's WorldBottom..WorldTop).</summary>
    private const int MinChunkY = 0;

    public GameWorld(EngineHost host, LaunchOptions options, Session session, ulong seed, IChunkStore chunkStore)
    {
        Host = host;
        Options = options;
        Session = session;
        Seed = seed;
        Registry = new EntityRegistry(host.World);
        Commands = new CommandSystem(session, Registry, () => host.Clock.Tick);

        host.Renderer.LoadTextureAtlas(
            Path.Combine(AppContext.BaseDirectory, "Resources", "spritesheet_tiles.png"),
            Path.Combine(AppContext.BaseDirectory, "Resources", "spritesheet_tiles.xml"));
        if (options.FrameRateCap is { } fps) host.CapFrameRate(fps);

        // The static world is a volume like any other, with an identity Transform (set by ChunkVolume), and a reserved
        // entity ID. Its chunks each decide their own presence layers (see EntityPresenceSystem).
        var staticEntity = host.World.CreateEntity();
        StaticVolume = new ChunkVolume(staticEntity, host.World) { MeshIgnoresNeighbours = true, ChunksOwnPresence = true };
        staticEntity.Set(new ChunkGrid { Volume = StaticVolume });
        staticEntity.Set(EntityRegistry.WorldVolume);
        staticEntity.Set(session.OwnerFor(PeerId.Host));
        staticEntity.Set<Rendered>();

        // Model blocks' glTF models (BlockDef.Model paths are relative to Resources/Models — see the csproj's link of the
        // blockbench folder), loaded on first use.
        BlockModels = new BlockModelLibrary(host.Renderer, Path.Combine(AppContext.BaseDirectory, "Resources", "Models"));
        Meshes = new ChunkMeshSystem(host.World, host.Renderer, BlockModels);
        Selection = new GridSelection(host.World);
        PlayerModel = new PlayerModel(host.Renderer);
        Blocks = new BlockEntities(host.World, Registry);
        GameCommands.RegisterAll(Commands, host.World, session, Blocks, EditLimits, Registry, host.Physics, Selection, PlayerModel);

        // Game UI (immediate mode, laid out by Clay).
        Ui = new UiContext(host.Window, host.Input);
        Ui.AddFont(UiFont.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts", "PixelifySans.ttf")));
        Ui.Atlas.LoadSprites(Path.Combine(AppContext.BaseDirectory, "Resources", "Ui")); // the HUD's; name.9.png is nine-sliced

        // Islands cut out of a continental terrain around island hearts (see HeartWorldGenerator), streamed around the
        // view into shared GPU voxel storage for lighting (world and ships).
        SkySettings.CloudAltitude = 1250f; // the islands are mostly low: clouds among the hills
        SkySettings.CloudSeaAltitude = HeartGrid.CloudSeaAltitude; // below its lowest islands
        GridStore = new GridStore(host.Context, (int)((long)options.LightBudgetMb * 1024 * 1024 / GridStore.SlotBytes),
                                  ChunkLoadSystem.WorldIndexDim(options.ViewDistance));
        ChunkLoad = new ChunkLoadSystem(host.World, StaticVolume, GridStore, () => new HeartWorldGenerator(seed),
                                        options.ViewDistance, MinChunkY, chunkStore);
        host.Renderer.AttachGridStore(GridStore);

        Simulation = new SimulationSystems(this);
    }

    public EngineHost Host { get; }
    public LaunchOptions Options { get; }
    public Session Session { get; }
    public ulong Seed { get; }
    public EntityRegistry Registry { get; }
    public CommandSystem Commands { get; }
    public ChunkVolume StaticVolume { get; }
    public BlockModelLibrary BlockModels { get; }
    public ChunkMeshSystem Meshes { get; }
    public GridSelection Selection { get; }
    public PlayerModel PlayerModel { get; }
    public BlockEntities Blocks { get; }
    public EditLimits EditLimits { get; } = new();
    public UiContext Ui { get; }
    public GridStore GridStore { get; }
    public ChunkLoadSystem ChunkLoad { get; }

    /// <summary>The gameplay systems the other groups share (the pilot, block actions, interpolation...).</summary>
    public SimulationSystems Simulation { get; }

    /// <summary>Whether the terrain around a point has loaded (joining waits on it, and so do grids' bodies).</summary>
    public bool TerrainLoaded(System.Numerics.Vector3 p) => ChunkLoad.IsTerrainLoaded(new Vector3D<float>(p.X, p.Y, p.Z), 64f);

    /// <summary>
    /// Adds every system, in order. Per frame, input first; then each tick: the network session (everything that arrived),
    /// the save's streaming and autosave (a host's), gameplay and physics, then body sync; then per frame, what's drawn
    /// and interacted with, and rendering.
    /// </summary>
    /// <param name="lag">The transport's artificial lag, for the network panel's sliders (none alone).</param>
    public void AddSystems(NetSession net, PersistenceSystems? persistence, LaggedTransport? lag)
    {
        InputSystems.Add(this);
        NetworkSystems.AddFirst(this, net);
        Simulation.AddInput();
        persistence?.Add(Host);
        Simulation.Add();
        var remoteBodies = NetworkSystems.AddLast(this, net, lag);
        FrameSystems.Add(this, remoteBodies);
        _render.Add(this);
    }

    /// <summary>Runs the game until the window closes, then stops background work (no chunk still loading or meshing
    /// while the store is freed).</summary>
    public void Run()
    {
        Host.Run();
        BackgroundWork.Stop(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        _render.Dispose();
        PlayerModel.Dispose();
        BlockModels.Dispose();
        Ui.Dispose();
        GridStore.Dispose();
    }

    private readonly RenderSystems _render = new();
}
