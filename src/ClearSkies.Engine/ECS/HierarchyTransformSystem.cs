using ClearSkies.Engine.Core;
using ClearSkies.Engine.Math;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Resolves each entity's <see cref="LocalTransform"/> into a world-space <see cref="Transform"/> by
/// composing it with its <see cref="Parent"/> chain, root down. Runs after every system that writes a
/// root's world <see cref="Transform"/> directly (<see cref="PhysicsTransformSyncSystem"/>,
/// <see cref="ChunkTransformSystem"/>, player input), so parents are current before their children are
/// resolved this tick.
///
/// Composition assumes uniform parent scale, like most engines' TRS hierarchies — a non-uniform
/// <see cref="LocalTransform.Scale"/> combined with a rotated parent will shear rather than resolve
/// exactly.
/// </summary>
public sealed class HierarchyTransformSystem : ISystem
{
    private readonly EntitySet _children;

    public HierarchyTransformSystem(World world)
    {
        _children = world.GetEntities().With<Parent>().With<LocalTransform>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _children.GetEntities())
            Resolve(e);
    }

    /// <summary>Resolves <paramref name="entity"/>'s world <see cref="Transform"/>, first resolving its
    /// parent if the parent is itself a child — so a parent is always current before it's read here,
    /// regardless of the EntitySet's iteration order.</summary>
    private static void Resolve(Entity entity)
    {
        var parent = entity.Get<Parent>().Value;
        if (!parent.IsAlive)
        {
            Hierarchy.RemoveParent(entity);
            return;
        }

        if (parent.Has<Parent>() && parent.Has<LocalTransform>())
            Resolve(parent);

        var parentWorld = parent.Has<Transform>() ? parent.Get<Transform>() : Transform.Identity;
        ref readonly var local = ref entity.Get<LocalTransform>();

        entity.Set(new Transform
        {
            Position = parentWorld.Position + Vec.Rotate(parentWorld.Rotation, parentWorld.Scale * local.Position),
            Rotation = parentWorld.Rotation * local.Rotation,
            Scale    = parentWorld.Scale * local.Scale,
        });
    }
}
