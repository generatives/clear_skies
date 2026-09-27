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
    /// <summary>A copy pulled towards its buffered pose by a force-limited servo, colliding only with grids (from N2).</summary>
    ServoFollower,
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

public enum TerrainInterestKind : byte
{
    /// <summary>Terrain data and colliders only, for simulating a body there.</summary>
    CollidersOnly,
    /// <summary>Everything, drawn: the local player's view.</summary>
    Full,
}

/// <summary>Terrain layer: terrain is streamed around this entity. Chunks within <see cref="EntityPresenceSystem.ColliderRange"/>
/// of any interest get colliders; a <see cref="TerrainInterestKind.Full"/> interest also streams the drawn world out to
/// <see cref="Radius"/>.</summary>
public struct TerrainInterest
{
    public float Radius;
    public TerrainInterestKind Kind;
}
