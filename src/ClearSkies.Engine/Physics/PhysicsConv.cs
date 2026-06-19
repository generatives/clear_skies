using System.Numerics;
using SilkVec = Silk.NET.Maths.Vector3D<float>;
using SilkQuat = Silk.NET.Maths.Quaternion<float>;

namespace ClearSkies.Engine.Physics;

/// <summary>
/// Converts between the engine's Silk.NET.Maths math types and BepuPhysics's System.Numerics types.
/// Used at every physics↔ECS boundary; never pass raw types across.
/// </summary>
internal static class PhysicsConv
{
    public static Vector3 ToBepu(SilkVec v) => new(v.X, v.Y, v.Z);
    public static SilkVec ToSilk(Vector3 v) => new(v.X, v.Y, v.Z);

    public static Quaternion ToBepu(SilkQuat q) => new(q.X, q.Y, q.Z, q.W);
    public static SilkQuat   ToSilk(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
}
