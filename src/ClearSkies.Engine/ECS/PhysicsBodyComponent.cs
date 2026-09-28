using System.Numerics;
using BepuPhysics;
using ClearSkies.Engine.Physics;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Ties an entity to a BepuPhysics body. The body's pose is the source of truth for the entity's position and
/// rotation: <see cref="PhysicsTransformSyncSystem"/> copies it into the entity's <see cref="Transform"/> after
/// every physics step, so everything else (rendering, lighting, raycasts, camera follow) reads the
/// <see cref="Transform"/> and never needs to know a body exists.
///
/// The body needn't sit at the Transform: Bepu puts a compound body's origin at its centre of mass, which moves as a
/// grid is edited, while a grid's Transform is its block space, which doesn't. The body sits
/// <see cref="BodyFrame.Offset"/> into the entity's own space (see <see cref="BodyFrame"/>).
///
/// Present only while the body exists; <see cref="PhysicsBodySystem"/> adds it when it creates a body and
/// removes the body (and its shape) when the entity is disposed.
/// </summary>
public struct PhysicsBodyComponent
{
    public BodyHandle Body;
}

/// <summary>
/// Converts between an entity's <see cref="Transform"/> and its body's pose. The body sits <see cref="Offset"/> into
/// the entity's own space: a grid's centre of mass (its volume's <see cref="Voxels.ChunkVolume.Pivot"/>), zero for
/// anything else.
/// </summary>
public static class BodyFrame
{
    /// <summary>Where the body's origin is in the entity's own space.</summary>
    public static Vector3D<float> Offset(Entity e) => e.Has<ChunkGrid>() ? e.Get<ChunkGrid>().Volume.Pivot : default;

    /// <summary>Where the entity is (its Transform's position) with its body at <paramref name="position"/>,
    /// <paramref name="rotation"/>.</summary>
    public static Vector3D<float> EntityPosition(Entity e, Vector3 position, Quaternion rotation)
        => PhysicsConv.ToSilk(position - Vector3.Transform(PhysicsConv.ToBepu(Offset(e)), rotation));
}
