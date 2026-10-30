using System.Numerics;
using BepuPhysics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Characters;
using ClearSkies.Engine.Pipeline;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Owns every BepuPhysics body create-or-rebuild call for grids, and removes a disposed entity's
/// <see cref="PhysicsBodyComponent"/> body and shape. A grid's body is one compound of its chunks' boxes, made in the chunk
/// pipeline (see <see cref="ShipBoxesStage"/>), built again as they change. Terrain chunks' static colliders are
/// pipeline stages too (see <see cref="ColliderStages"/>).
/// </summary>
public sealed class PhysicsBodySystem : ISystem
{
    private readonly HashSet<Entity>   _grids = new();
    private readonly HashSet<Entity>   _boxesChanged = new();
    private readonly PhysicsWorld      _physics;
    private readonly ChunkPipeline     _pipeline;
    private readonly ShipBoxesStage    _boxes;
    private readonly List<(Vector3 center, Vector3 size, float mass)> _dynamicBoxes = new();
    private readonly List<BodyHandle> _removedBodies = new();

    private readonly EntitySet _gridsGainedPresence;
    private readonly EntitySet _gridsLostPresence;
    private readonly EntitySet _bodyOverrides;
    private readonly List<PlayerCharacter> _removedCharacters = new();

    /// <summary>Adds the ship boxes stage to <paramref name="chunks"/>' pipeline (before it wants any chunk).</summary>
    public PhysicsBodySystem(World world, PhysicsWorld physics, ChunkPipelineSystem chunks)
    {
        _physics = physics;
        _pipeline = chunks.Pipeline;
        _boxes = _pipeline.Add(new ShipBoxesStage(chunks.ShipBlocks, volume => _boxesChanged.Add(volume.Root)));
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

        // A ship whose chunks' boxes changed (edited, or come or gone).
        _grids.UnionWith(_boxesChanged);
        _boxesChanged.Clear();

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

        // Its chunks' boxes, in grid-local space, each of one block type with its weight (see ShipBoxesStage). Not till
        // every chunk with colliding blocks has its boxes: the body is never built from part of the ship. An edited
        // chunk's old boxes count until its new ones come.
        _dynamicBoxes.Clear();
        ref var grid = ref entity.Get<DynamicGrid>();
        foreach (var (_, entry) in entity.Get<ChunkGrid>().Volume.All)
        {
            var boxes = entry.Entity.Has<InPipeline>() ? _pipeline.Get(entry.Entity.Get<InPipeline>().Key)?.Output<ShipBoxes>(_boxes) : null;
            if (boxes == null)
            {
                if (entry.Data.HasAnyColliding()) return; // built once its boxes come
                continue;
            }
            _dynamicBoxes.AddRange(boxes.Boxes);
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
