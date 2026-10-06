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

/// <summary>On a terrain interest: how far around it (horizontally) every column it wants is known to
/// <see cref="ChunkLoadSystem"/>, as queued, loading or loaded. Infinite once all of them are.</summary>
public struct TerrainScanned
{
    public float Radius;
}

/// <summary>An edit changed a terrain chunk that wasn't here (not loaded, and not known to hold nothing), so it didn't
/// change it here: an entity of its own until <see cref="ChunkLoadSystem"/> sees it and loads that chunk with the edit
/// from then on.</summary>
public struct TerrainEditedElsewhere
{
    public Voxels.ChunkPosition Position;
}

/// <summary>A terrain column that <see cref="ChunkLoadSystem"/> is going to load, or is loading: an entity of its own
/// from when the column is queued until its chunks are added.</summary>
public struct TerrainColumnLoading
{
    public int X, Z;
}
