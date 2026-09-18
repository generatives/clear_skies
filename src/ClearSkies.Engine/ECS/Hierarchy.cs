using System.Collections.Generic;
using System.Linq;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>Points a child entity at its parent. Kept in sync with the parent's <see cref="Children"/>
/// list by <see cref="Hierarchy"/> — set it via <see cref="Hierarchy.SetParent"/> rather than directly.</summary>
public struct Parent
{
    public Entity Value;
}

/// <summary>The child entities attached to this entity via <see cref="Parent"/>. Maintained by
/// <see cref="Hierarchy"/> — don't mutate the list directly or it will drift out of sync.</summary>
public struct Children
{
    public List<Entity> Entities;
}

/// <summary>An entity's transform relative to its <see cref="Parent"/>, instead of world space.
/// <see cref="HierarchyTransformSystem"/> composes this with the parent's world <see cref="Transform"/>
/// into the entity's own world-space <see cref="Transform"/> each frame.</summary>
public struct LocalTransform
{
    public Vector3D<float> Position;
    public Quaternion<float> Rotation;
    public Vector3D<float> Scale;

    public static LocalTransform Identity => new()
    {
        Position = Vector3D<float>.Zero,
        Rotation = Quaternion<float>.Identity,
        Scale = Vector3D<float>.One,
    };
}

/// <summary>Mutators that keep <see cref="Parent"/> and <see cref="Children"/> consistent with each
/// other, and cascade entity destruction down a hierarchy — DefaultEcs has no notion of parent/child
/// itself, so nothing else removes descendants when a parent entity is disposed.</summary>
public static class Hierarchy
{
    /// <summary>Attaches <paramref name="child"/> to <paramref name="parent"/>, detaching it from any
    /// previous parent first. Doesn't touch <paramref name="child"/>'s <see cref="LocalTransform"/> —
    /// set that separately to position it relative to its new parent.</summary>
    public static void SetParent(Entity child, Entity parent)
    {
        RemoveParent(child);

        child.Set(new Parent { Value = parent });

        if (!parent.Has<Children>())
            parent.Set(new Children { Entities = new List<Entity>() });
        parent.Get<Children>().Entities.Add(child);
    }

    /// <summary>Detaches <paramref name="child"/> from its parent, if any. The child keeps whatever
    /// world-space <see cref="Transform"/> it last resolved to.</summary>
    public static void RemoveParent(Entity child)
    {
        if (!child.Has<Parent>()) return;

        var parent = child.Get<Parent>().Value;
        if (parent.IsAlive && parent.Has<Children>())
            parent.Get<Children>().Entities.Remove(child);

        child.Remove<Parent>();
    }

    /// <summary>Disposes <paramref name="entity"/> and every descendant reachable through
    /// <see cref="Children"/>.</summary>
    public static void DestroyRecursive(Entity entity)
    {
        if (!entity.IsAlive) return;

        if (entity.Has<Children>())
            foreach (var child in entity.Get<Children>().Entities.ToArray())
                DestroyRecursive(child);

        RemoveParent(entity);
        entity.Dispose();
    }
}
