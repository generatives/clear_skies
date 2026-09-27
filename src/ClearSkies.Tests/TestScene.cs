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
    public readonly NetRegistry Registry;
    public readonly GridSelection Selection;
    public readonly ChunkVolume WorldVolume;
    public readonly EntityPresenceSystem Presence;
    public readonly CommandSystem Commands;
    public readonly BlockEntities Blocks;
    public readonly EditLimits Limits = new();
    public uint TickNumber;
    private readonly List<Engine.Core.ISystem> _tick = new();

    public HeadlessScene(Session? session = null)
    {
        Session = session ?? Session.SinglePlayer();
        Registry = new NetRegistry(World);
        var allocator = new NetIdAllocator();
        Registry.RequestBlock = allocator.NextBlock;
        Selection = new GridSelection(World);

        var root = World.CreateEntity();
        WorldVolume = new ChunkVolume(root, World) { ChunksOwnPresence = true };
        root.Set(new ChunkGrid { Volume = WorldVolume });
        root.Set(new NetId { Value = NetRegistry.WorldVolume });
        root.Set<Rendered>();

        Presence = new EntityPresenceSystem(World, Session, WorldVolume, viewDistance: 500f);
        Commands = new CommandSystem(Session, Registry, () => TickNumber);
        Blocks = new BlockEntities(World, Registry);
        Commands.Register(new EditVoxelsHandler(Blocks, Limits));
        Commands.Register(new SetLeverHandler(Blocks));
        Commands.Register(new SetWheelHandler(Blocks));
        Commands.Register(new SetGridLockedHandler(Registry, Physics));
        Commands.Register(new RightGridHandler(Registry, Physics));
        Commands.Register(new SetMoveModeHandler(Registry));
        Commands.Register(new SpawnGridHandler(World, Registry, Session, Physics, Selection));
        Commands.Register(new SpawnPlayerHandler(World, Registry, Session, Physics));
        Commands.Register(new DespawnEntityHandler(Registry));
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
    }

    public void Tick(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            TickNumber++;
            foreach (var s in _tick) s.Update(Dt);
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
        uint id = Registry.Allocate();
        var handler = (SpawnGridHandler)Commands.HandlerFor(CommandIds.SpawnGrid)!;
        handler.Apply(new SpawnGrid { Id = id, Owner = Session.LocalPeer, Grid = description }, default);
        return Registry.Find(id) ?? throw new InvalidOperationException("The grid didn't spawn.");
    }

    /// <summary>The local player, walking unless <paramref name="freeFly"/>, with its capsule centre at
    /// <paramref name="position"/>.</summary>
    public Entity SpawnLocalPlayer(Vector3 position, bool freeFly = false)
    {
        uint id = Registry.Allocate();
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
