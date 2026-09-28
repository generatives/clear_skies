using ClearSkies.Engine.Core;
using ClearSkies.Engine.Physics;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Post-physics: copies each <see cref="PhysicsBodyComponent"/> body's pose into its entity's
/// <see cref="Transform"/> (position and rotation; scale is left alone), and each walking character's capsule centre
/// into its position (the rotation is the player's look, set by mouse-look). Runs right after
/// <see cref="PhysicsWorld"/> steps and before anything that reads those Transforms this tick
/// (<see cref="HierarchyTransformSystem"/>, which carries children such as a grid's chunks along; support; rendering).
/// </summary>
public sealed class PhysicsTransformSyncSystem : ISystem
{
    private readonly EntitySet    _bodies;
    private readonly EntitySet    _characters;
    private readonly PhysicsWorld _physics;

    public PhysicsTransformSyncSystem(World world, PhysicsWorld physics)
    {
        _physics = physics;
        _bodies  = world.GetEntities().With<PhysicsBodyComponent>().With<Transform>().AsSet();
        _characters = world.GetEntities().With<CharacterControllerComponent>().With<Transform>().Without<FreeFlying>().AsSet();
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

        foreach (ref readonly Entity e in _characters.GetEntities()) // a free-flying player has no capsule in the simulation
            e.Get<Transform>().Position = PhysicsConv.ToSilk(e.Get<CharacterControllerComponent>().Character.Position);
    }
}
