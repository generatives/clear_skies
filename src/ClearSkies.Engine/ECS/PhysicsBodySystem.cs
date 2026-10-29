using System.Numerics;
using BepuPhysics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Characters;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Owns every BepuPhysics body create-or-rebuild call for grids (reacting to <see cref="NeedsRecollideFlag"/> on their
/// chunks), and removes a disposed entity's <see cref="PhysicsBodyComponent"/> body and shape. A grid's body is one
/// compound of its chunks' boxes, mass from each block's weight. Terrain chunks' static colliders are pipeline stages
/// (see <see cref="ColliderStages"/>).
/// </summary>
public sealed class PhysicsBodySystem : ISystem
{
    private readonly EntitySet         _dirtyChunks;
    private readonly HashSet<Entity>   _grids = new();
    private readonly PhysicsWorld      _physics;
    private readonly VoxelBoxDecomposer _decomposer = new();
    private readonly List<(Vector3 center, Vector3 size, float mass)> _dynamicBoxes = new();
    private readonly List<BodyHandle> _removedBodies = new();
    private readonly List<Entity> _clean = new();

    private readonly EntitySet _gridsGainedPresence;
    private readonly EntitySet _gridsLostPresence;
    private readonly EntitySet _bodyOverrides;
    private readonly List<PlayerCharacter> _removedCharacters = new();

    public PhysicsBodySystem(World world, PhysicsWorld physics)
    {
        _physics = physics;
        _dirtyChunks = world.GetEntities().With<Chunk>().With<NeedsRecollideFlag>().AsSet();
        _gridsGainedPresence = world.GetEntities().With<DynamicGrid>().With<ChunkGrid>().WhenAdded<PhysicsPresence>().WhenChanged<PhysicsPresence>().AsSet();
        _gridsLostPresence = world.GetEntities().With<DynamicGrid>().With<PhysicsBodyComponent>().WhenRemoved<PhysicsPresence>().AsSet();
        _bodyOverrides = world.GetEntities().With<BodyStateOverride>().With<PhysicsBodyComponent>().With<Transform>().AsSet();
        world.SubscribeEntityDisposed(OnEntityDisposed);
    }

    private void OnEntityDisposed(in Entity entity)
    {
        if (entity.Has<PhysicsBodyComponent>())
            _removedBodies.Add(entity.Get<PhysicsBodyComponent>().Body);

        if (entity.Has<CharacterControllerComponent>())
            _removedCharacters.Add(entity.Get<CharacterControllerComponent>().Character);
    }

    public void Update(float dt)
    {
        UpdateColliders();
        Cleanup();
    }

    public void UpdateColliders()
    {
        _grids.Clear();

        // Grids: a body while simulated here, none otherwise.
        foreach (ref readonly Entity entity in _gridsGainedPresence.GetEntities()) _grids.Add(entity);
        _gridsGainedPresence.Complete();
        foreach (var entity in _gridsLostPresence.GetEntities().ToArray())
        {
            _removedBodies.Add(entity.Get<PhysicsBodyComponent>().Body);
            entity.Remove<PhysicsBodyComponent>();
        }
        _gridsLostPresence.Complete();

        // A ship's chunks are rebuilt with their grid; other chunks' flags just go (terrain's colliders follow its blocks
        // in the pipeline).
        _clean.Clear();
        foreach (ref readonly Entity entity in _dirtyChunks.GetEntities())
        {
            var root = entity.Get<Chunk>().Entry.Volume.Root;
            if (root.Has<DynamicGrid>()) _grids.Add(root);
            _clean.Add(entity);
        }
        foreach (var entity in _clean) entity.Remove<NeedsRecollideFlag>();

        // A grid's body: simulated here, or a kinematic copy following its owner's snapshots. A grid with neither has no
        // body here: its Transform alone places it.
        foreach (var entity in _grids)
        {
            if (!entity.IsAlive) continue;
            var mode = entity.Has<PhysicsPresence>() ? entity.Get<PhysicsPresence>().Mode : (PhysicsMode?)null;
            UpdateDynamicGrid(entity, withBody: mode is PhysicsMode.Simulated or PhysicsMode.KinematicFollower,
                              kinematic: mode == PhysicsMode.KinematicFollower);
        }

        // Spawned or overwritten from a description: the body takes the described pose and velocities.
        foreach (var entity in _bodyOverrides.GetEntities().ToArray())
        {
            ref readonly var pb = ref entity.Get<PhysicsBodyComponent>();
            var body = pb.Body;
            ref readonly var t = ref entity.Get<Transform>();
            var o = entity.Get<BodyStateOverride>();
            _physics.SetBodyPose(body, pb.BodyPosition(t), PhysicsConv.ToBepu(t.Rotation));
            // A copy following its owner's snapshots moves too (kinematically) until the follower takes over from its first
            // snapshot, so whoever spawns aboard doesn't ride a ship that stands still and then lurches off. A locked grid
            // simulated here stays put.
            bool following = entity.Has<PhysicsPresence>() && entity.Get<PhysicsPresence>().Mode == PhysicsMode.KinematicFollower;
            if (_physics.GetBodyMass(body) > 0 || following)
            {
                _physics.SetBodyLinearVelocity(body, o.LinearVelocity);
                _physics.SetBodyAngularVelocity(body, o.AngularVelocity);
            }
            entity.Remove<BodyStateOverride>();
        }
    }

    // ── dynamic grid bodies (moved from GridShapeSystem) ────────────────────────
    private void UpdateDynamicGrid(Entity entity, bool withBody, bool kinematic)
    {
        if (!withBody)
        {
            // No body here; the grid is placed by its Transform alone.
            if (entity.Has<PhysicsBodyComponent>())
            {
                _removedBodies.Add(entity.Get<PhysicsBodyComponent>().Body);
                entity.Remove<PhysicsBodyComponent>();
            }
            return;
        }

        // Gather merged boxes across all chunks, expressed in grid-local space. Each box is
        // homogeneous in BlockId (see VoxelBoxDecomposer), so its mass is volume * that block's
        // Weight — real per-block-type density instead of uniform volume.
        _dynamicBoxes.Clear();

        var chunkVolume = entity.Get<ChunkGrid>().Volume;
        ref var grid = ref entity.Get<DynamicGrid>();

        foreach (var (pos, entry) in chunkVolume.All)
        {
            if (!entry.Data.HasAnyColliding()) continue;
            var o = pos.WorldOrigin;
            foreach (var (c, s, id) in _decomposer.Decompose(entry.Data))
            {
                float volume = s.X * s.Y * s.Z;
                _dynamicBoxes.Add((new Vector3(o.X + c.X, o.Y + c.Y, o.Z + c.Z), s, volume * BlockRegistry.Get(id).Weight));
            }
        }

        if (_dynamicBoxes.Count == 0)
        {
            return;
        }

        var (shape, inertia, com) = _physics.BuildDynamicCompound(_dynamicBoxes);
        grid.Inertia = inertia;
        bool zeroInertia = grid.Locked || kinematic;

        // Bepu recentres the compound on its centre of mass, so the body origin is the CoM, which sits at the body's
        // Offset inside the grid's block space. The grid's Transform is that block space and must not move as blocks
        // change: only the body moves, to wherever the new CoM is.
        if (!entity.Has<PhysicsBodyComponent>())
        {
            // First solid block: place the body at the CoM within the grid's current Transform (where it was spawned).
            // Grids default to Locked (see DynamicGrid.Locked) so they don't immediately fall under
            // gravity when spawned; the body is created kinematic (zero inertia) in that case, same
            // as the rebuild branch below.
            ref readonly var t = ref entity.Get<Transform>();
            var orient = PhysicsConv.ToBepu(t.Rotation);
            var pos    = PhysicsConv.ToBepu(t.Position) + Vector3.Transform(com, orient);
            var body   = _physics.AddDynamicBody(shape, zeroInertia ? default : inertia, pos, orient,
                                                 new ColliderInfo(ColliderKind.VoxelGrid, entity));
            entity.Set(new PhysicsBodyComponent { Body = body, Offset = PhysicsConv.ToSilk(com) });
        }
        else
        {
            // The body pose, not the Transform: something may have set it since the last sync. The block space stays
            // put, so the body moves by however far the CoM moved within it.
            ref var pb = ref entity.Get<PhysicsBodyComponent>();
            var body = pb.Body;
            var (pos, orient) = _physics.GetBodyPose(body);
            var worldShift = Vector3.Transform(com - PhysicsConv.ToBepu(pb.Offset), orient);
            pb.Offset = PhysicsConv.ToSilk(com);
            var oldShape = _physics.GetBodyShape(body);

            // While locked, keep the body's actual physics inertia zeroed (kinematic) even though
            // the shape/geometry updates — grid.Inertia (above) still tracks the real value for
            // GridPilotSystem to restore on unlock.
            _physics.SetBodyShape(body, shape, zeroInertia ? default : inertia);
            _physics.SetBodyPose(body, pos + worldShift, orient);
            _physics.RemoveCompound(oldShape);
        }
    }

    private void Cleanup()
    {
        foreach (var body in _removedBodies)
        {
            var shape = _physics.GetBodyShape(body);
            _physics.RemoveBody(body);
            _physics.RemoveCompound(shape);
        }
        _removedBodies.Clear();

        foreach (var character in _removedCharacters) character.Dispose();
        _removedCharacters.Clear();
    }
}
