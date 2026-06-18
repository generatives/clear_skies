using Silk.NET.Maths;

namespace ClearSkies.Engine.Math;

public static class Vec
{
    /// <summary>Rotate a vector by a quaternion (v' = v + 2q.w*(q.xyz×v) + 2*q.xyz×(q.xyz×v)).</summary>
    public static Vector3D<float> Rotate(Quaternion<float> q, Vector3D<float> v)
    {
        var u = new Vector3D<float>(q.X, q.Y, q.Z);
        var t = 2f * Vector3D.Cross(u, v);
        return v + q.W * t + Vector3D.Cross(u, t);
    }
}
