using System.Numerics;
using BepuPhysics.Collidables;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
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
    public uint TickNumber;
    private readonly List<Engine.Core.ISystem> _tick = new();
    private readonly TickInterpolationSystem _interpolation;

    public HeadlessScene(Session? session = null)
    {
        Session = session ?? Session.SinglePlayer();
        Registry = new EntityRegistry(World);
        var allocator = new EntityIdAllocator();
        Registry.RequestBlock = allocator.NextBlock;
        Selection = new GridSelection(World);

        var root = World.CreateEntity();
        WorldVolume = new ChunkVolume(root, World) { ChunksOwnPresence = true };
        root.Set(new ChunkGrid { Volume = WorldVolume });
        root.Set(EntityRegistry.WorldVolume);
        root.Set<Rendered>();

        Presence = new EntityPresenceSystem(World, Session, WorldVolume, viewDistance: 500f);
        Commands = new CommandSystem(Session, Registry, () => TickNumber);
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
        _interpolation = new TickInterpolationSystem(World, new Engine.Core.Time()); // last, as in the game
    }

    public void Tick(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            TickNumber++;
            foreach (var s in _tick) s.Update(Dt);
            _interpolation.Update(Engine.Core.SystemStage.Simulation, Dt);
        }
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
        handler.Apply(new SpawnGrid { Id = id, Owner = Session.LocalPeer, Grid = description }, default);
        return Registry.Find(id) ?? throw new InvalidOperationException("The grid didn't spawn.");
    }

    /// <summary>The local player, walking unless <paramref name="freeFly"/>, with its capsule centre at
    /// <paramref name="position"/>.</summary>
    public Entity SpawnLocalPlayer(Vector3 position, bool freeFly = false)
    {
        var id = Registry.Allocate();
        var handler = (SpawnPlayerHandler)Commands.HandlerFor(CommandIds.SpawnPlayer)!;
        handler.Apply(new SpawnPlayer { Id = id, Owner = Session.LocalPeer,
            Player = new PlayerDescription { Id = PlayerId.New(), Name = "test", FreeFly = freeFly, Position = position } }, default);
        return Registry.Find(id) ?? throw new InvalidOperationException("The player didn't spawn.");
    }

    public void Dispose()
    {
        Registry.Dispose();
        Physics.Dispose();
        World.Dispose();
    }
}
