using System.Numerics;
using BepuPhysics;
using ClearSkies.Engine.Physics;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Ties an entity to a BepuPhysics body. The body's pose is the source of truth for the entity's position and
/// rotation: <see cref="PhysicsTransformSyncSystem"/> copies it into the entity's <see cref="Transform"/> after
/// every physics step, so everything else (rendering, lighting, raycasts, camera follow) reads the
/// <see cref="Transform"/> and never needs to know a body exists.
///
/// The body needn't sit at the Transform: it sits <see cref="Offset"/> into the entity's own space. Bepu puts a
/// compound body's origin at its centre of mass, which moves as a grid is edited, while a grid's Transform is its
/// block space, which doesn't; so a grid's offset is its centre of mass in block space, kept up to date by
/// <see cref="PhysicsBodySystem"/>.
///
/// Present only while the body exists; <see cref="PhysicsBodySystem"/> adds it when it creates a body and
/// removes the body (and its shape) when the entity is disposed.
/// </summary>
public struct PhysicsBodyComponent
{
    public BodyHandle Body;

    /// <summary>Where the body's origin is in the entity's own space: a grid's centre of mass; zero otherwise.</summary>
    public Vector3D<float> Offset;

    /// <summary>Where the entity is (its Transform's position) with its body at <paramref name="position"/>,
    /// <paramref name="rotation"/>.</summary>
    public readonly Vector3D<float> EntityPosition(Vector3 position, Quaternion rotation)
        => PhysicsConv.ToSilk(position - Vector3.Transform(PhysicsConv.ToBepu(Offset), rotation));
}
