using System.Collections.Generic;
using System.Linq;
using ClearSkies.Engine.Math;
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
/// <see cref="Hierarchy"/> — don't mutate the set directly or it will drift out of sync. A set rather than a
/// list because a parent can have thousands of children (the static world's chunks) that come and go.</summary>
public struct Children
{
    public HashSet<Entity> Entities;
}

/// <summary>An entity's transform relative to its <see cref="Parent"/>, instead of world space.
/// <see cref="HierarchyTransformSystem"/> composes this with the parent's world <see cref="Transform"/>
/// into the entity's own world-space <see cref="Transform"/>. Change it with <c>Entity.Set</c>, not a
/// <c>ref</c> write: the system only re-resolves a child whose parent moved or whose LocalTransform was
/// Set.</summary>
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
            parent.Set(new Children { Entities = new HashSet<Entity>() });
        parent.Get<Children>().Entities.Add(child);

        if (!child.Has<OwnPresence>()) InheritRendered(child, parent);
    }

    /// <summary>Gives <paramref name="child"/>, which has no <see cref="OwnPresence"/>, <paramref name="parent"/>'s
    /// <see cref="Rendered"/>, and carries it on down its own children. <see cref="Rendered"/> is the only presence
    /// layer that's inherited: nothing below a grid has a body of its own (its chunks and blocks are shapes in the grid's
    /// one compound body), so <see cref="PhysicsPresence"/> stays on the grid, and terrain chunks decide their own.</summary>
    private static void InheritRendered(Entity child, Entity parent)
    {
        bool rendered = parent.Has<Rendered>();
        if (rendered == child.Has<Rendered>()) return;
        if (rendered) child.Set<Rendered>(); else child.Remove<Rendered>();
        PropagateRendered(child);
    }

    /// <summary>Carries <paramref name="entity"/>'s <see cref="Rendered"/> down to every descendant that inherits it
    /// (stopping at descendants with their own <see cref="OwnPresence"/>). Called when it changes on it.</summary>
    public static void PropagateRendered(Entity entity)
    {
        if (!entity.Has<Children>()) return;
        foreach (var child in entity.Get<Children>().Entities)
            if (child.IsAlive && !child.Has<OwnPresence>()) InheritRendered(child, entity);
    }

    /// <summary>Attaches <paramref name="child"/> to <paramref name="parent"/> at <paramref name="local"/>, and
    /// resolves its world <see cref="Transform"/> straight away so it is placed correctly even before
    /// <see cref="HierarchyTransformSystem"/> next runs.</summary>
    public static void SetParent(Entity child, Entity parent, in LocalTransform local)
    {
        SetParent(child, parent);
        child.Set(local);
        child.Set(Compose(parent.Has<Transform>() ? parent.Get<Transform>() : Transform.Identity, local));
    }

    /// <summary>World transform of a child at <paramref name="local"/> under a parent at
    /// <paramref name="parentWorld"/>.</summary>
    public static Transform Compose(in Transform parentWorld, in LocalTransform local) => new()
    {
        Position = parentWorld.Position + Vec.Rotate(parentWorld.Rotation, parentWorld.Scale * local.Position),
        Rotation = parentWorld.Rotation * local.Rotation,
        Scale    = parentWorld.Scale * local.Scale,
    };

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
    /// <see cref="Children"/>, immediately. (Disposing a parent directly also takes its descendants with it,
    /// but only when <see cref="HierarchyTransformSystem"/> next runs.) A camera is only detached (see
    /// <see cref="OutlivesParent"/>).</summary>
    public static void DestroyRecursive(Entity entity)
    {
        if (!entity.IsAlive) return;

        if (entity.Has<Children>())
            foreach (var child in entity.Get<Children>().Entities.ToArray())
            {
                if (OutlivesParent(child)) RemoveParent(child);
                else DestroyRecursive(child);
            }

        RemoveParent(entity);
        entity.Dispose();
    }

    /// <summary>Whether <paramref name="child"/> is detached, not destroyed, with its parent: a camera only views from
    /// whatever it's attached to (a player's eye, a piloted ship), and whoever attached it puts it back.</summary>
    public static bool OutlivesParent(Entity child) => child.Has<CameraComponent>();
}
