using ClearSkies.Engine.Core;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Resolves each entity's <see cref="LocalTransform"/> into a world-space <see cref="Transform"/> by
/// composing it with its <see cref="Parent"/> chain, root down. Runs after every system that writes a
/// root's world <see cref="Transform"/> directly (<see cref="PhysicsTransformSyncSystem"/>, player input),
/// so parents are current before their children are resolved this tick.
///
/// Only work that can have changed is redone, since a parent can have thousands of children (the static
/// world's chunks): a parent's children are re-resolved when its world Transform differs from the one they
/// were last resolved against, and a single child when its <see cref="Parent"/> or
/// <see cref="LocalTransform"/> is Set. Either way the change carries on down that child's own subtree.
///
/// Also cascades destruction: when a parent is disposed, its descendants are disposed here on the next run
/// (use <see cref="Hierarchy.DestroyRecursive"/> to take them with it immediately).
///
/// Composition assumes uniform parent scale, like most engines' TRS hierarchies — a non-uniform
/// <see cref="LocalTransform.Scale"/> combined with a rotated parent will shear rather than resolve
/// exactly.
/// </summary>
public sealed class HierarchyTransformSystem : ISystem
{
    /// <summary>The world Transform a parent's children were last resolved against.</summary>
    private struct ChildrenResolvedAgainst
    {
        public Transform Value;
    }

    private readonly EntitySet _roots;
    private readonly EntitySet _changedChildren;
    private readonly List<Entity> _orphans = new();

    public HierarchyTransformSystem(World world)
    {
        _roots = world.GetEntities().With<Children>().Without<Parent>().AsSet();
        _changedChildren = world.GetEntities().With<Parent>().With<LocalTransform>()
            .WhenAdded<Parent>().WhenChanged<Parent>()
            .WhenAdded<LocalTransform>().WhenChanged<LocalTransform>()
            .AsSet();
        world.SubscribeEntityDisposed(OnEntityDisposed);
    }

    private void OnEntityDisposed(in Entity entity)
    {
        if (entity.Has<Parent>())
        {
            var parent = entity.Get<Parent>().Value;
            if (parent.IsAlive && parent.Has<Children>())
                parent.Get<Children>().Entities.Remove(entity);
        }

        if (entity.Has<Children>())
            _orphans.AddRange(entity.Get<Children>().Entities);
    }

    public void Update(float dt)
    {
        foreach (var orphan in _orphans)
        {
            if (!orphan.IsAlive) continue;
            orphan.Remove<Parent>(); // its parent is gone; don't let DestroyRecursive look for it
            Hierarchy.DestroyRecursive(orphan);
        }
        _orphans.Clear();

        foreach (ref readonly Entity root in _roots.GetEntities())
            ResolveChildrenIfMoved(root);

        foreach (ref readonly Entity child in _changedChildren.GetEntities())
        {
            var parent = child.Get<Parent>().Value;
            if (!parent.IsAlive) continue; // disposed this tick; destroyed with the orphans next run
            child.Set(Hierarchy.Compose(WorldOf(parent), child.Get<LocalTransform>()));
            ResolveChildrenIfMoved(child);
        }
        _changedChildren.Complete();
    }

    /// <summary>Re-resolves <paramref name="parent"/>'s children if its world Transform has moved since they
    /// were last resolved, recursing into each of them in turn.</summary>
    private static void ResolveChildrenIfMoved(Entity parent)
    {
        if (!parent.Has<Children>()) return;

        var world = WorldOf(parent);
        if (parent.Has<ChildrenResolvedAgainst>() && Same(parent.Get<ChildrenResolvedAgainst>().Value, world)) return;
        parent.Set(new ChildrenResolvedAgainst { Value = world });

        foreach (var child in parent.Get<Children>().Entities)
        {
            if (!child.Has<LocalTransform>()) continue;
            child.Set(Hierarchy.Compose(world, child.Get<LocalTransform>()));
            ResolveChildrenIfMoved(child);
        }
    }

    private static Transform WorldOf(Entity e) => e.Has<Transform>() ? e.Get<Transform>() : Transform.Identity;

    private static bool Same(in Transform a, in Transform b)
        => a.Position == b.Position && a.Rotation == b.Rotation && a.Scale == b.Scale;
}
