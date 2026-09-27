using DefaultEcs;

namespace ClearSkies.Engine.Physics;

/// <summary>What a collider is, for code that only has its physics handle (contact callbacks, ray hits). Stored per
/// body/static in <see cref="PhysicsWorld.Colliders"/>; every collider is tagged where it's created.</summary>
public enum ColliderKind : byte
{
    /// <summary>Anything without a more specific kind (loose boxes, future non-voxel colliders). The zero value, so
    /// zeroed memory reads as "not voxel".</summary>
    Other,
    /// <summary>A character capsule (see CharacterControllers); <see cref="ColliderInfo.Entity"/> is its entity, if any.</summary>
    Character,
    /// <summary>A static terrain chunk (one per chunk, no entity).</summary>
    VoxelTerrain,
    /// <summary>A grid's body (ship); <see cref="ColliderInfo.Entity"/> is the grid entity.</summary>
    VoxelGrid,
    /// <summary>Another player's servo copy (see FollowerSystem): collides with grids only, never terrain or other
    /// characters, since its owner already keeps it out of those.</summary>
    Follower,
}

public readonly record struct ColliderInfo(ColliderKind Kind, Entity Entity = default)
{
    /// <summary>Built from unrotated axis-aligned boxes in the collider's local space.</summary>
    public bool IsVoxel => Kind is ColliderKind.VoxelTerrain or ColliderKind.VoxelGrid;
}
