using ClearSkies.Engine.Core;
using ClearSkies.Engine.Physics;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Post-physics: copies each <see cref="PhysicsBodyComponent"/> body's pose into its entity's
/// <see cref="Transform"/> (position and rotation; scale is left alone). Runs right after
/// <see cref="PhysicsWorld"/> steps and before anything that reads those Transforms this tick
/// (<see cref="ChunkTransformSystem"/>, <see cref="HierarchyTransformSystem"/>, camera follow, rendering).
/// </summary>
public sealed class PhysicsTransformSyncSystem : ISystem
{
    private readonly EntitySet    _bodies;
    private readonly PhysicsWorld _physics;

    public PhysicsTransformSyncSystem(World world, PhysicsWorld physics)
    {
        _physics = physics;
        _bodies  = world.GetEntities().With<PhysicsBodyComponent>().With<Transform>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _bodies.GetEntities())
        {
            var (p, q) = _physics.GetBodyPose(e.Get<PhysicsBodyComponent>().Body);
            ref var t = ref e.Get<Transform>();
            t.Position = PhysicsConv.ToSilk(p);
            t.Rotation = PhysicsConv.ToSilk(q);
        }
    }
}
