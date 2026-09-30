using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;
using ClearSkies.Game.Startup.Systems;
using ClearSkies.Net.Session;
using Silk.NET.Maths;

namespace ClearSkies.Game.Startup;

/// <summary>
/// What every game runs, whichever way it started: this machine's session, the entity registry and command system,
/// the static world streamed around the view, and the systems that play it. Built from a seed (the save's, or the
/// host's), where edited terrain chunks come from, and what limits how much of the world loads. Nothing here draws or
/// reads input: that's the <see cref="GameView"/>'s, in a game that has one.
/// <para>Its systems go in three places, which the game that built it puts in order around its others (the view's, the
/// save's): <see cref="AddTickStart"/>, <see cref="AddTick"/> and <see cref="AddFrame"/>.</para>
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
        StaticVolume = new ChunkVolume(staticEntity, host.World) { MeshIgnoresNeighbours = true, ChunksOwnPresence = true };
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

        Simulation = new SimulationSystems(this);
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

    /// <summary>The gameplay systems others share (block actions, interpolation, remote bodies...).</summary>
    public SimulationSystems Simulation { get; }

    /// <summary>Whether the terrain around a point has loaded (joining waits on it, and so do grids' bodies).</summary>
    public bool TerrainLoaded(System.Numerics.Vector3 p) => ChunkLoad.IsTerrainLoaded(new Vector3D<float>(p.X, p.Y, p.Z), 64f);

    /// <summary>First in each tick: the network session (everything that arrived: commands, events, snapshots,
    /// session messages), then the hierarchy. Next come what hands the tick the local player's input (the view's) and
    /// the save's streaming (a host's).</summary>
    public void AddTickStart(NetSession net)
    {
        Host.AddSystem(net, SystemStage.Simulation);
        Simulation.AddStart();
    }

    /// <summary>The rest of the tick: gameplay and physics, then body sync, last.</summary>
    public void AddTick(NetSession net)
    {
        Simulation.Add();
        Host.AddSystem(new Net.Sync.BodySync(net, Host.World, Host.Physics), SystemStage.Simulation); // owned bodies, every second tick
    }

    /// <summary>Once each frame, after the ticks: see <see cref="FrameSystems"/>. Before it go what moves the camera
    /// itself (the view's flying); after it, what the player points at and uses.</summary>
    public void AddFrame() => FrameSystems.Add(this);

    /// <summary>Runs the game until it quits, then stops background work (no chunk still loading while what it loads
    /// into is freed).</summary>
    public void Run()
    {
        Host.Run();
        BackgroundWork.Stop(TimeSpan.FromSeconds(5));
    }
}
