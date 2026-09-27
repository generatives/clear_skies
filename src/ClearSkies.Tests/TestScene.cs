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
    private readonly GridNetworking _gridNetworking;
    private readonly List<Engine.Core.ISystem> _tick = new();

    public HeadlessScene(Session? session = null)
    {
        Session = session ?? Session.SinglePlayer();
        Registry = new NetRegistry(World);
        var allocator = new NetIdAllocator();
        Registry.RequestBlock = allocator.NextBlock;
        _gridNetworking = new GridNetworking(World, Registry, Session);
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
    /// <paramref name="centre"/>.</summary>
    public Entity SpawnPlatform(Vector3 centre, int size = 8)
    {
        var voxels = new List<(int, int, int, BlockId, BlockOrientation)>();
        for (int x = 0; x < size; x++) for (int z = 0; z < size; z++) voxels.Add((x, 0, z, BlockId.Stone, BlockOrientation.Upright));
        DynamicGridFactory.SpawnFromVoxels(World, Selection, centre, voxels);
        return World.GetEntities().With<SelectedGridComponent>().AsEnumerable().First();
    }

    /// <summary>The local player, walking, with its capsule centre at <paramref name="position"/>.</summary>
    public Entity SpawnLocalPlayer(Vector3 position, bool freeFly = false)
    {
        var player = World.CreateEntity();
        player.Set(new Transform { Position = new Vector3D<float>(position.X, position.Y, position.Z), Rotation = Quaternion<float>.Identity, Scale = Vector3D<float>.One });
        player.Set(new MouseLookComponent { LookSensitivity = 0.0025f });
        player.Set(new FreeFlyController { MoveSpeed = 10f });
        var character = new PlayerCharacter(Physics.Characters, position, new Capsule(0.3f, 1f), 0.01f, 2f, 100f, 70f, 6f, 5f, entity: player);
        player.Set(new CharacterControllerComponent { Character = character, EyeHeight = 0.7f });
        player.Set(new CharacterModeComponent { FreeFly = freeFly });
        player.Set(new PlayerInput());
        player.Set(new Support());
        player.Set(new Player { Id = PlayerId.New(), Name = "test", IsLocal = true });
        player.Set<LocalPlayer>();
        player.Set(new NetId { Value = Registry.Allocate() });
        player.Set(Session.LocalOwner());
        player.Set<OwnPresence>();
        return player;
    }

    public void Dispose()
    {
        _gridNetworking.Dispose();
        Registry.Dispose();
        Physics.Dispose();
        World.Dispose();
    }
}
