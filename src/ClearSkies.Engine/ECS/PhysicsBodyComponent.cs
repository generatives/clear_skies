using BepuPhysics;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Ties an entity to a BepuPhysics body. The body's pose is the source of truth for the entity's position and
/// rotation: <see cref="PhysicsTransformSyncSystem"/> copies it into the entity's <see cref="Transform"/> after
/// every physics step, so everything else (rendering, lighting, raycasts, camera follow) reads the
/// <see cref="Transform"/> and never needs to know a body exists. The Transform sits at the body's origin, which
/// for a compound body is its centre of mass.
///
/// Present only while the body exists; <see cref="PhysicsBodySystem"/> adds it when it creates a body and
/// removes the body (and its shape) when the entity is disposed.
/// </summary>
public struct PhysicsBodyComponent
{
    public BodyHandle Body;
}
