namespace ClearSkies.Engine.ECS;

// Presence layers: how this machine hosts an entity, as opposed to what the entity is (its description). Each layer
// is an optional component that EntityPresenceSystem works out locally from context; the systems for a layer attach
// their resources when its component is added and release them when it's removed. Layers are never saved or sent.

/// <summary>Tag: this entity positions itself, so <see cref="EntityPresenceSystem"/> decides its layers (grids,
/// players, terrain chunks). Everything else inherits its layers from the nearest ancestor with this tag, through
/// <see cref="Hierarchy"/>.</summary>
public struct OwnPresence
{
}

public enum PhysicsMode : byte
{
    /// <summary>A body this machine simulates.</summary>
    Simulated,
    /// <summary>A copy of a body simulated elsewhere, moved kinematically to its buffered pose (from N2).</summary>
    KinematicFollower,
    /// <summary>Another player's copy: a character body like theirs, walked towards its buffered pose, so it stands on
    /// and pushes ships as they do (from N2).</summary>
    CharacterFollower,
    /// <summary>A static terrain collider.</summary>
    Static,
}

/// <summary>Physics layer: a Bepu body in this mode (see PhysicsBodySystem).</summary>
public struct PhysicsPresence
{
    public PhysicsMode Mode;
}

/// <summary>Rendering layer: meshed, lit and drawn. Rendering systems only handle entities that have it.</summary>
public struct Rendered
{
}

/// <summary>Terrain layer: terrain is streamed around this entity, out to the larger of its two radii (horizontally,
/// <see cref="ChunkLoadSystem"/>). Loaded chunks within <see cref="ColliderRadius"/> of it get colliders, and those within
/// <see cref="DrawRadius"/> are drawn (<see cref="EntityPresenceSystem"/>). A body simulated here away from the view
/// (another player's character on the host, a ship) has only a collider radius; the local player's view has both.</summary>
public struct TerrainInterest
{
    /// <summary>Terrain colliders are built within this distance (dropped a little further out).</summary>
    public float ColliderRadius;

    /// <summary>Terrain is drawn within this horizontal distance; 0 for none.</summary>
    public float DrawRadius;

    /// <summary>How far terrain is loaded around it.</summary>
    public readonly float LoadRadius => MathF.Max(ColliderRadius, DrawRadius);

    public override readonly string ToString() => DrawRadius > 0 ? $"drawn to {DrawRadius:0}, colliders to {ColliderRadius:0}"
                                                                  : $"colliders to {ColliderRadius:0}";
}
