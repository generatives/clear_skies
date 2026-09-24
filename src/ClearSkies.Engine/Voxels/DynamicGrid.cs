using BepuPhysics;
using ClearSkies.Engine.ECS;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// A dynamic voxel grid: a <see cref="ChunkVolume"/> of blocks authored in the grid's own local space,
/// backed by a single BepuPhysics dynamic body. Blocks never move relative to each other — only the
/// body pose changes — which keeps the grid's internal representation motion-invariant (the property
/// Tier-2 lighting relies on). Editing a block grows chunks on demand and flags the collision shape
/// for rebuild.
///
/// The body itself is a <see cref="PhysicsBodyComponent"/> on the same entity, added by PhysicsBodySystem once
/// the grid has a solid block. Like any body its pose is synced into the entity's <see cref="Transform"/>, which
/// therefore sits at the grid's centre of mass; the volume's <see cref="ChunkVolume.Pivot"/> records where that is
/// in grid-local space. Before the body exists the Transform is simply where the grid was spawned.
/// </summary>
public struct DynamicGrid
{
    /// <summary>True while the grid is frozen in place (Phase 5.1 "lock"): its body is kinematic (zero
    /// inverse mass/inertia via <see cref="Physics.PhysicsWorld.SetBodyKinematic"/>), so Bepu's own
    /// integrator skips it entirely (no gravity added, ever). The dynamic/kinematic transition goes
    /// through <c>BodyReference.SetLocalInertia</c>, not a raw write to the <c>LocalInertia</c>
    /// property (confirmed via reflecting BepuPhysics.dll that the property is a ref-return onto the
    /// body's raw memory, bypassing whatever bookkeeping the transition needs — a raw write was why an
    /// unlocked grid previously stopped colliding with static terrain even after "unlocking"). Every
    /// grid spawns locked by default; unlocked via the End key.</summary>
    public bool Locked { get; internal set; } = true;

    /// <summary>Inertia last computed by PhysicsBodySystem from block occupancy; restored on unlock.</summary>
    public BodyInertia Inertia { get; internal set; }

    /// <summary>Count of Buoyant voxels, cached by PhysicsBodySystem whenever the shape rebuilds (block
    /// occupancy is the only thing that changes it, so it doesn't need a per-tick scan). Read by
    /// AirshipFlightSystem to feedforward-cancel Buoyant's constant lift alongside gravity, so the
    /// vertical hold converges to true zero instead of drifting against whichever one it didn't cancel.</summary>
    public int BuoyantBlockCount { get; internal set; }

    public DynamicGrid() { }
}
