using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Math;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>Projection parameters plus view/projection matrix builders (WebGPU clip space: Y-up, depth [0,1],
/// reversed: near is 1, far is 0 — see <see cref="Mat4.PerspectiveRhZoReversed"/>).</summary>
public sealed class Camera
{
    public float FovRadians { get; set; } = MathF.PI / 3f; // 60°
    public float NearPlane { get; set; } = 0.1f;
    /// <summary>Past the furthest the clouds reach (<see cref="CloudLayer.Distance"/>, well past the streamed world's
    /// fog), so the fog, not the far plane, is what ends the view. Depth is reversed floats, so a far plane this far
    /// costs no depth precision up close.</summary>
    public float FarPlane { get; set; } = CloudLayer.Distance + 2000f;

    public Mat4 GetView(in Transform t)
    {
        var forward = Vec.Rotate(t.Rotation, new Vector3D<float>(0, 0, -1));
        var up = Vec.Rotate(t.Rotation, new Vector3D<float>(0, 1, 0));
        return Mat4.LookAtRh(t.Position, t.Position + forward, up);
    }

    public Mat4 GetProjection(float aspect) => Mat4.PerspectiveRhZoReversed(FovRadians, aspect, NearPlane, FarPlane);
}
