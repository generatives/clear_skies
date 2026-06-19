using ClearSkies.Engine.Core;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Physics;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Syncs each dynamic grid's chunk entities to its rigid-body pose. Runs after <see cref="PhysicsSystem"/>.
/// Every chunk is carried rigidly by the grid pose: world = bodyPos + R·(chunkLocalOrigin − centreOfMass),
/// with the same centre-of-mass offset the physics compound was recentered by, so meshes and colliders
/// stay aligned.
///
/// Lighting note (Tier 2 motion invariance): this system must NEVER enqueue a relight. The grid's baked
/// light field lives in grid-local coordinates and is carried rigidly by the mesh transform, so movement
/// and rotation re-shade for free (the shader's per-frame N·L term) without recomputing the flood-fill.
/// </summary>
public sealed class GridTransformSystem : ISystem
{
    private readonly EntitySet    _grids;
    private readonly PhysicsWorld _physics;

    public GridTransformSystem(World world, PhysicsWorld physics)
    {
        _physics = physics;
        _grids   = world.GetEntities().With<DynamicGridComponent>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _grids.GetEntities())
        {
            var grid = e.Get<DynamicGridComponent>().Grid;
            if (!grid.BodyCreated) continue;

            var (p, q) = _physics.GetBodyPose(grid.Body);
            var gridPos = PhysicsConv.ToSilk(p);
            var gridRot = PhysicsConv.ToSilk(q);
            var com     = PhysicsConv.ToSilk(grid.CenterOfMass);

            foreach (var (pos, entry) in grid.All)
            {
                if (!entry.Entity.IsAlive) continue;
                ref var t = ref entry.Entity.Get<Transform>();
                t.Rotation = gridRot;
                t.Position = gridPos + Vec.Rotate(gridRot, pos.WorldOrigin - com);
            }
        }
    }
}
