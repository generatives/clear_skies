using System.Numerics;
using BepuPhysics.Collidables;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Characters;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Tests;

/// <summary>A headless game world with the tick systems in their real order: no window, GPU or streaming.</summary>
public sealed class HeadlessScene : IDisposable
{
    public const float Dt = 1f / 60f;

    public readonly World World = new();
    public readonly PhysicsWorld Physics = new(new Vector3(0, -6, 0), Dt);
    public readonly Session Session;
    public readonly EntityRegistry Registry;
    public readonly GridSelection Selection;
    public readonly ChunkVolume WorldVolume;
    public readonly EntityPresenceSystem Presence;
    public readonly CommandSystem Commands;
    public readonly BlockEntities Blocks;
    public readonly EditLimits Limits = new();
    public readonly ManualTickClock Clock = new();
    private double _rateCredit;
    public uint TickNumber => Clock.Tick;
    public ClearSkies.Net.Session.SimulationParticipant? Net;
    public ClearSkies.Net.Sync.RemoteBodySystem? RemoteBodies;
    private readonly List<Engine.Core.ISystem> _tick = new();

    public readonly EntityIdAllocator Ids;

    public HeadlessScene(Session? session = null, uint firstFreeId = EntityRegistry.FirstFreeId)
    {
        Session = session ?? Session.SinglePlayer();
        Registry = new EntityRegistry(World);
        Ids = new EntityIdAllocator(firstFreeId);
        Registry.RequestBlock = Ids.NextBlock;
        Selection = new GridSelection(World);

        var root = World.CreateEntity();
        WorldVolume = new ChunkVolume(root, World) { ChunksOwnPresence = true };
        root.Set(new ChunkGrid { Volume = WorldVolume });
        root.Set(EntityRegistry.WorldVolume);
        root.Set<Rendered>();

        Presence = new EntityPresenceSystem(World, Session, WorldVolume, viewDistance: 500f);
        Commands = new CommandSystem(Session, Registry, () => Clock.Tick);
        Blocks = new BlockEntities(World, Registry);
        GameCommands.RegisterAll(Commands, World, Session, Blocks, Limits, Registry, Physics, Selection);
        var hierarchy = new HierarchyTransformSystem(World);
        _tick.Add(hierarchy);
        _tick.Add(new PhysicsBodySystem(World, Physics));
        _tick.Add(new PlayerMovementSystem(World, Commands));
        _tick.Add(Commands);
        _tick.Add(Presence);
        _tick.Add(Physics);
        _tick.Add(new PhysicsTransformSyncSystem(World, Physics));
        _tick.Add(hierarchy);
        _tick.Add(new SupportSystem(World, Physics));
        Interpolation = new TickInterpolationSystem(World, Time);
        _tick.Add(new LambdaSystem(() => Interpolation.Update(SystemStage.Simulation, Dt))); // records this tick's poses
    }

    /// <summary>Frame timing, as the game's: <see cref="Frame"/> sets its Alpha, which drawing interpolates by.</summary>
    public readonly Time Time = new();
    public readonly TickInterpolationSystem Interpolation;
    private readonly TickClock _frameClock = new();

    /// <summary>Puts a Participant in the tick: first, after <paramref name="first"/> (what carries messages to it over
    /// the network, and the Host, on the hosting machine), and body sync last. Entity IDs come from the Host from now
    /// on.</summary>
    public void AttachNet(ClearSkies.Net.Session.SimulationParticipant net, params Engine.Core.ISystem[] first)
    {
        Net = net;
        Registry.RequestBlock = null;
        _tick.Insert(0, net);
        _tick.InsertRange(0, first);
        _tick.Add(new ClearSkies.Net.Sync.BodySync(net, World, Physics));
        RemoteBodies = new ClearSkies.Net.Sync.RemoteBodySystem(World, Registry, Clock);
        _tick.Insert(_tick.FindIndex(s => s is PhysicsTransformSyncSystem) + 1, RemoteBodies);
        _tick.Insert(_tick.IndexOf(Physics), new ClearSkies.Net.Sync.RemoteBodyProxySystem(World, Physics, RemoteBodies));
        // A client predicts its own player, from the input a test puts on it, before movement (as the game's input
        // sample does).
        if (net is { IsAuthority: false } client)
            _tick.Insert(_tick.FindIndex(s => s is PlayerMovementSystem), new ClearSkies.Net.Sync.OwnPlayerPrediction(client, World, Registry));
    }

    /// <summary>Runs <paramref name="system"/> each tick just before the physics step (e.g. flight).</summary>
    public void AddBeforePhysics(Engine.Core.ISystem system) => _tick.Insert(_tick.IndexOf(Physics), system);

    public void Tick(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            // One frame of real time: usually one tick, but clock sync's slew (Rate) sometimes makes it none or two.
            _rateCredit += Clock.Rate;
            while (_rateCredit >= 1)
            {
                _rateCredit -= 1;
                Clock.Tick++;
                foreach (var s in _tick) s.Update(Dt);
            }
            Draw(Dt);
        }
    }

    /// <summary>One frame of <paramref name="seconds"/> as the game runs it: the ticks it's due (clock sync's Rate
    /// included), then drawing <see cref="ManualTickClock.Alpha"/> of the way between the last two ticks.</summary>
    public void Frame(double seconds)
    {
        _frameClock.Rate = Clock.Rate;
        uint before = Clock.Tick;
        int ticks = _frameClock.Advance(seconds);
        for (int i = 0; i < ticks; i++)
        {
            Clock.Tick++;
            foreach (var s in _tick) s.Update(Dt);
        }
        if (Clock.Tick != before + (uint)ticks) _frameClock.Snap(Clock.Tick); // clock sync snapped it, as the game's clock does
        Clock.Alpha = _frameClock.Alpha;
        Draw((float)seconds);
    }

    private void Draw(float dt)
    {
        Time.Alpha = Clock.Alpha;
        Interpolation.Update(SystemStage.Frame, dt);
    }

    /// <summary>Runs ticks until <paramref name="done"/> or <paramref name="max"/> ticks.</summary>
    public bool TickUntil(Func<bool> done, int max = 600)
    {
        for (int i = 0; i < max; i++)
        {
            if (done()) return true;
            Tick();
            Thread.Sleep(0);
        }
        return done();
    }

    /// <summary>A locked (kinematic) flat grid of stone, <paramref name="size"/> blocks square, centred at
    /// <paramref name="centre"/>, spawned through the command system.</summary>
    public Entity SpawnPlatform(Vector3 centre, int size = 8)
    {
        var voxels = new List<GridVoxel>();
        for (int x = 0; x < size; x++) for (int z = 0; z < size; z++) voxels.Add(new(x, 0, z, BlockId.Stone, BlockOrientation.Upright));
        return SpawnGrid(GridDescription.FromVoxels(centre, voxels));
    }

    /// <summary>Spawns a grid from a description now (outside a tick) and returns it.</summary>
    public Entity SpawnGrid(GridDescription description)
    {
        // Applied directly (as its event would be), so it works on a client scene too.
        var id = Registry.Allocate();
        var handler = (SpawnGridHandler)Commands.HandlerFor(CommandIds.SpawnGrid)!;
        handler.Apply(new Spawn<GridDescription> { Id = id, Owner = Session.LocalPeer, Description = description }, default);
        return Registry.Find(id) ?? throw new InvalidOperationException("The grid didn't spawn.");
    }

    /// <summary>The local player, walking unless <paramref name="freeFly"/>, with its capsule centre at
    /// <paramref name="position"/>.</summary>
    public Entity SpawnLocalPlayer(Vector3 position, bool freeFly = false)
    {
        var id = Registry.Allocate();
        var handler = (SpawnPlayerHandler)Commands.HandlerFor(CommandIds.SpawnPlayer)!;
        handler.Apply(new Spawn<PlayerDescription> { Id = id, Owner = Session.LocalPeer,
            Description = new PlayerDescription { Id = PlayerId.New(), Name = "test", FreeFly = freeFly, Position = position } }, default);
        return Registry.Find(id) ?? throw new InvalidOperationException("The player didn't spawn.");
    }

    public void Dispose()
    {
        Net?.Dispose();
        Registry.Dispose();
        Physics.Dispose();
        World.Dispose();
    }
}
