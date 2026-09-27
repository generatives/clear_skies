using System;
using System.Diagnostics;
using BepuPhysics;
using BepuPhysics.Collidables;
using DefaultEcs;

namespace ClearSkies.Engine.Physics;

/// <summary>What a collider is, for code that only has its physics handle (contact callbacks, ray hits).</summary>
public enum ColliderKind : byte
{
    /// <summary>Never tagged (or already removed). Every collider must be tagged when it's created; reading this asserts.</summary>
    Untagged,
    /// <summary>Anything without a more specific kind (loose boxes, future non-voxel colliders).</summary>
    Other,
    /// <summary>A character capsule (see CharacterControllers); <see cref="ColliderInfo.Entity"/> is its entity, if any.</summary>
    Character,
    /// <summary>A static terrain chunk (one per chunk, no entity).</summary>
    VoxelTerrain,
    /// <summary>A grid's body (ship); <see cref="ColliderInfo.Entity"/> is the grid entity.</summary>
    VoxelGrid,
}

public readonly record struct ColliderInfo(ColliderKind Kind, Entity Entity = default)
{
    /// <summary>Built from unrotated axis-aligned boxes in the collider's local space.</summary>
    public bool IsVoxel => Kind is ColliderKind.VoxelTerrain or ColliderKind.VoxelGrid;
}

/// <summary>
/// Per-handle <see cref="ColliderInfo"/> for bodies and statics — this project's equivalent of user data on a collider
/// (Bepu has none). Every collider is tagged where it's created: PhysicsWorld's Add* methods take the tag as a required
/// argument and CharacterControllers tags character bodies. Bepu's own <see cref="CollidableProperty{T}"/> does the same
/// job but doesn't bounds-check reads in release builds; here a handle nothing tagged reads as
/// <see cref="ColliderKind.Untagged"/> (asserting in debug builds) instead of arbitrary memory.
/// Written on the main thread when colliders are added/removed; read from physics worker threads during a timestep.
/// Tags are cleared on removal so a reused handle never inherits a stale one.
/// </summary>
public sealed class ColliderTags
{
    private ColliderInfo[] _bodies = new ColliderInfo[64];
    private ColliderInfo[] _statics = new ColliderInfo[64];

    public void Set(BodyHandle handle, ColliderInfo info) => Set(ref _bodies, handle.Value, Checked(info));
    public void Set(StaticHandle handle, ColliderInfo info) => Set(ref _statics, handle.Value, Checked(info));
    public void Clear(BodyHandle handle) => Set(ref _bodies, handle.Value, default);
    public void Clear(StaticHandle handle) => Set(ref _statics, handle.Value, default);

    public ColliderInfo Get(BodyHandle handle) => Get(_bodies, handle.Value);
    public ColliderInfo Get(StaticHandle handle) => Get(_statics, handle.Value);
    public ColliderInfo Get(CollidableReference collidable) =>
        collidable.Mobility == CollidableMobility.Static ? Get(collidable.StaticHandle) : Get(collidable.BodyHandle);

    private static ColliderInfo Get(ColliderInfo[] table, int index)
    {
        var info = (uint)index < (uint)table.Length ? table[index] : default;
        Debug.Assert(info.Kind != ColliderKind.Untagged, "Collider was never tagged: create it through PhysicsWorld/CharacterControllers.");
        return info;
    }

    private static ColliderInfo Checked(ColliderInfo info)
    {
        if (info.Kind == ColliderKind.Untagged) throw new ArgumentException("Tag colliders with a real kind; use Clear to remove a tag.");
        return info;
    }

    private static void Set(ref ColliderInfo[] table, int index, ColliderInfo info)
    {
        if (index >= table.Length)
        {
            if (info == default) return;
            Array.Resize(ref table, System.Math.Max(index + 1, table.Length * 2));
        }
        table[index] = info;
    }
}
