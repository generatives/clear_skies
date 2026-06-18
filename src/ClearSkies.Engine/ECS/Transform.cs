using ClearSkies.Engine.Math;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

public struct Transform
{
    public Vector3D<float> Position;
    public Quaternion<float> Rotation;
    public Vector3D<float> Scale;

    public static Transform Identity => new()
    {
        Position = Vector3D<float>.Zero,
        Rotation = Quaternion<float>.Identity,
        Scale = Vector3D<float>.One,
    };

    /// <summary>Model matrix = Translation * Rotation * Scale.</summary>
    public readonly Mat4 ToMatrix()
    {
        var s = Mat4.Scale(Scale);
        var r = Mat4.FromQuaternion(Rotation);
        var t = Mat4.Translation(Position);
        return Mat4.Multiply(t, Mat4.Multiply(r, s));
    }
}
