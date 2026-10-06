using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;
using ClearSkies.Net.Session;
using ClearSkies.Net.Sync;
using Silk.NET.Maths;

namespace ClearSkies.Game.Startup;

/// <summary>
/// What every game runs, whichever way it started: this machine's session, the entity registry and command system,
/// the static world streamed around the view, and the systems several places in a game's schedule share (the
/// hierarchy and interpolation run in the tick and the frame; block actions, flight and remote bodies have debug panels
/// and UI reading them). Built from a seed (the save's, or the host's), where edited terrain chunks come from (the Host), and
/// what limits how much of the world loads. Nothing here draws or reads input: that's the <see cref="GameView"/>'s, in
/// a game that has one. Each game schedules the systems itself, in order.
/// </summary>
public sealed class GameWorld
{
    /// <summary>The islands stream from layer 0 (blocks -256..1792: HeartGrid's WorldBottom..WorldTop).</summary>
    private const int MinChunkY = 0;

    /// <param name="budget">What limits how much of the world loads.</param>
    /// <param name="chunkPreparer">Work on each loaded chunk as it loads (the view's packing for the GPU).</param>
    /// <param name="playerModel">What other players are drawn as, in a game that draws them.</param>
    public GameWorld(EngineHost host, LaunchOptions options, Session session, ulong seed, IChunkStore chunkStore,
                     IChunkBudget budget, IChunkPreparer? chunkPreparer = null, PlayerModel? playerModel = null)
    {
        Host = host;
        Options = options;
        Session = session;
        Seed = seed;
        Registry = new EntityRegistry(host.World);
        Commands = new CommandSystem(session, Registry, () => host.Clock.Tick);

        // The static world is a volume like any other, with an identity Transform (set by ChunkVolume), and a reserved
        // entity ID. Its chunks each decide their own presence layers (see EntityPresenceSystem).
        var staticEntity = host.World.CreateEntity();
        StaticVolume = new ChunkVolume(staticEntity, host.World) { MeshIgnoresNeighbours = true, ChunksOwnPresence = true, PoolMeshes = true };
        staticEntity.Set(new ChunkGrid { Volume = StaticVolume });
        staticEntity.Set(EntityRegistry.WorldVolume);
        staticEntity.Set(session.OwnerFor(PeerId.Host));
        staticEntity.Set<Rendered>();

        Selection = new GridSelection(host.World);
        Blocks = new BlockEntities(host.World, Registry);
        GameCommands.RegisterAll(Commands, host.World, session, Blocks, EditLimits, Registry, host.Physics, Selection, playerModel);

        // Islands cut out of a continental terrain around island hearts (see HeartWorldGenerator), streamed around the
        // view as far as the budget reaches.
        ChunkLoad = new ChunkLoadSystem(host.World, StaticVolume, budget, () => new HeartWorldGenerator(seed),
                                        options.ViewDistance, MinChunkY, chunkStore, chunkPreparer);
        ((EditVoxelsHandler)Commands.HandlerFor(CommandIds.EditVoxels)!).Terrain = ChunkLoad; // edits to the world ask it what's there

        Interpolation = new TickInterpolationSystem(host.World, host.Time);
        Hierarchy = new HierarchyTransformSystem(host.World);
        PhysicsBody = new PhysicsBodySystem(host.World, host.Physics);
        BlockActions = new BlockActionSystem(host.World, Commands, EditLimits, Selection);
        Flight = new AirshipFlightSystem(host.World, host.Physics);
        RemoteBodies = new RemoteBodySystem(host.World, Registry, host.Clock);
    }

    public EngineHost Host { get; }
    public LaunchOptions Options { get; }
    public Session Session { get; }
    public ulong Seed { get; }
    public EntityRegistry Registry { get; }
    public CommandSystem Commands { get; }
    public ChunkVolume StaticVolume { get; }
    public GridSelection Selection { get; }
    public BlockEntities Blocks { get; }
    public EditLimits EditLimits { get; } = new();
    public ChunkLoadSystem ChunkLoad { get; }

    /// <summary>Records each tick's poses (in the tick), and draws between the last two (in the frame).</summary>
    public TickInterpolationSystem Interpolation { get; }

    /// <summary>Parents' Transforms to their children's: after the network, after the physics step, and in the frame.</summary>
    public HierarchyTransformSystem Hierarchy { get; }

    public PhysicsBodySystem PhysicsBody { get; }
    public BlockActionSystem BlockActions { get; }
    public AirshipFlightSystem Flight { get; }

    /// <summary>Bodies owned elsewhere, placed from their snapshots about 100 ms behind.</summary>
    public RemoteBodySystem RemoteBodies { get; }

    /// <summary>Whether the terrain around a point has loaded with colliders, so a body there won't fall through it
    /// (each spawn simulated here waits on it, see SpawnQueue).</summary>
    public bool TerrainReadyFor(System.Numerics.Vector3 p) =>
        ChunkLoad.IsTerrainLoaded(new Vector3D<float>(p.X, p.Y, p.Z), 64f) && PhysicsBody.CollidersReady(StaticVolume, p, 64f);

    /// <summary>Presence layers (bodies, drawing, terrain interest and colliders). Entities are drawn as far as the
    /// terrain, but no further than the View Volume, past which the Host sends none.</summary>
    public EntityPresenceSystem CreatePresence() =>
        new(Host.World, Session, StaticVolume, Options.ViewDistance) { RenderDistanceLimit = ClearSkies.Net.Session.Host.ViewRadius };

    /// <summary>Runs the game until it quits, then stops background work (no chunk still loading while what it loads
    /// into is freed).</summary>
    public void Run()
    {
        Host.Run();
        BackgroundWork.Stop(TimeSpan.FromSeconds(5));
    }
}
