using BepuPhysics;
using ClearSkies.Engine.ECS;
using DefaultEcs;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// A dynamic voxel grid: a <see cref="ChunkVolume"/> of blocks authored in the grid's own local space,
/// backed by a single BepuPhysics dynamic body. Blocks never move relative to each other — only the
/// body pose changes — which keeps the grid's internal representation motion-invariant (the property
/// Tier-2 lighting relies on). Editing a block grows chunks on demand and flags the collision shape
/// for rebuild.
/// </summary>
public sealed class DynamicGrid : ChunkVolume
{
    /// <summary>Root entity tagged with <see cref="DynamicGridComponent"/>; distinct from the per-chunk entities.</summary>
    public Entity Root { get; }

    /// <summary>World position at which the grid's centre of mass is placed when its body is first created.</summary>
    public PhysVec SpawnPosition { get; }

    public BodyHandle Body { get; internal set; }
    public bool       BodyCreated { get; internal set; }

    /// <summary>Centre of mass in grid-local space, updated on every shape rebuild. Render offsets subtract this.</summary>
    public PhysVec CenterOfMass { get; internal set; }

    /// <summary>Set when block occupancy changes; consumed by GridShapeSystem to rebuild the body shape + inertia.</summary>
    public bool ShapeDirty { get; internal set; } = true;

    /// <summary>True while the grid is frozen in place (Phase 5.1 "lock"): its body is kinematic (zero
    /// inverse mass/inertia via <see cref="Physics.PhysicsWorld.SetBodyKinematic"/>), so Bepu's own
    /// integrator skips it entirely (no gravity added, ever). The dynamic/kinematic transition goes
    /// through <c>BodyReference.SetLocalInertia</c>, not a raw write to the <c>LocalInertia</c>
    /// property (confirmed via reflecting BepuPhysics.dll that the property is a ref-return onto the
    /// body's raw memory, bypassing whatever bookkeeping the transition needs — a raw write was why an
    /// unlocked grid previously stopped colliding with static terrain even after "unlocking"). Every
    /// grid spawns locked by default; unlocked via the End key.</summary>
    public bool Locked { get; internal set; } = true;

    /// <summary>Inertia last computed by GridShapeSystem from block occupancy; restored on unlock.</summary>
    public BodyInertia Inertia { get; internal set; }

    /// <summary>World-space force/torque AirshipControlSystem wants this tick. Transient — recomputed
    /// every tick and consumed the same tick by AirshipPropulsionSystem; meaningless between ticks.</summary>
    public PhysVec DesiredForce { get; internal set; }
    public PhysVec DesiredTorque { get; internal set; }

    /// <summary>Count of Buoyant voxels, cached by GridShapeSystem whenever the shape rebuilds (block
    /// occupancy is the only thing that changes it, so it doesn't need a per-tick scan). Read by
    /// AirshipControlSystem to feedforward-cancel Buoyant's constant lift alongside gravity, so the
    /// vertical hold converges to true zero instead of drifting against whichever one it didn't cancel.</summary>
    public int BuoyantBlockCount { get; internal set; }

    public DynamicGrid(World world, PhysVec spawnPosition) : base(world)
    {
        SpawnPosition = spawnPosition;
        Root = world.CreateEntity();
        Root.Set(new DynamicGridComponent { Grid = this });
    }

    public override void SetBlock(int x, int y, int z, BlockId id, Facing facing = Facing.Up)
    {
        var (cp, _, _, _) = Decompose(x, y, z);
        EnsureChunk(cp);          // grow on demand so edits outside existing chunks create new ones
        base.SetBlock(x, y, z, id, facing);
        ShapeDirty = true;
    }

    protected override void PlaceChunkEntity(Entity entity, ChunkPosition pos)
    {
        // Initial local placement; GridTransformSystem overwrites the world pose each frame.
        var t = Transform.Identity;
        t.Position = pos.WorldOrigin;
        entity.Set(t);
    }
}
