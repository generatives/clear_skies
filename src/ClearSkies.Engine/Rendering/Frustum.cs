using ClearSkies.Engine.Math;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Six-plane view frustum (left, right, bottom, top, near, far), extracted from a combined
/// projection*view matrix (Gribb-Hartmann method, adapted for this engine's [0,1]/"ZO" depth
/// convention — see <see cref="Math.Mat4.PerspectiveRhZo"/>/<see cref="Math.Mat4.OrthoRhZo"/>).
/// Each plane is (A,B,C,D) with "inside" meaning A*x+B*y+C*z+D &gt;= 0.
///
/// Used to skip drawing (and shadow-casting) meshes that can't possibly be visible: with no culling,
/// every loaded chunk mesh — including everything behind and to the sides of the camera, and every
/// loaded chunk outside the sun shadow's much smaller covered volume — was drawn unconditionally
/// every frame, which is the dominant cost at a large view distance.
/// </summary>
public readonly struct Frustum
{
    private readonly Vector4D<float> _left, _right, _bottom, _top, _near, _far;

    private Frustum(Vector4D<float> left, Vector4D<float> right, Vector4D<float> bottom,
                    Vector4D<float> top, Vector4D<float> near, Vector4D<float> far)
    {
        _left = left; _right = right; _bottom = bottom; _top = top; _near = near; _far = far;
    }

    public static Frustum FromViewProjection(in Mat4 vp)
    {
        // Matrix is column-major (M[col*4+row] — see Mat4's own doc comment); row i of the mathematical
        // matrix is (M[i], M[4+i], M[8+i], M[12+i]).
        var r0 = new Vector4D<float>(vp.M0, vp.M4, vp.M8,  vp.M12);
        var r1 = new Vector4D<float>(vp.M1, vp.M5, vp.M9,  vp.M13);
        var r2 = new Vector4D<float>(vp.M2, vp.M6, vp.M10, vp.M14);
        var r3 = new Vector4D<float>(vp.M3, vp.M7, vp.M11, vp.M15);

        return new Frustum(
            Normalize(r3 + r0), // left
            Normalize(r3 - r0), // right
            Normalize(r3 + r1), // bottom
            Normalize(r3 - r1), // top
            Normalize(r2),      // near (ZO depth: near plane is where clip-space z == 0)
            Normalize(r3 - r2)  // far  (ZO depth: far plane is where clip-space z == w)
        );
    }

    private static Vector4D<float> Normalize(Vector4D<float> p)
    {
        float len = MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
        return len > 1e-8f ? p / len : p;
    }

    /// <summary>False only when [min,max] is provably entirely outside at least one plane — a
    /// conservative (may return true for some actually-invisible boxes, never false for a visible one)
    /// AABB/frustum test using the standard "positive vertex" trick.</summary>
    public bool Intersects(Vector3D<float> min, Vector3D<float> max)
    {
        return Inside(_left, min, max) && Inside(_right, min, max) && Inside(_bottom, min, max)
            && Inside(_top, min, max) && Inside(_near, min, max) && Inside(_far, min, max);
    }

    private static bool Inside(Vector4D<float> p, Vector3D<float> min, Vector3D<float> max)
    {
        float px = p.X >= 0f ? max.X : min.X;
        float py = p.Y >= 0f ? max.Y : min.Y;
        float pz = p.Z >= 0f ? max.Z : min.Z;
        return p.X * px + p.Y * py + p.Z * pz + p.W >= 0f;
    }
}
