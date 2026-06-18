using System;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Math;

/// <summary>
/// A 4x4 matrix stored column-major in column-vector convention (result = M * v), matching
/// WGSL's <c>mat4x4&lt;f32&gt;</c> memory layout exactly. Built by hand to avoid any row/column
/// convention ambiguity when uploading to WebGPU. Flat index is <c>col * 4 + row</c>.
/// </summary>
public struct Mat4
{
    // 16 floats, column-major: M[col*4 + row]
    public float M0, M1, M2, M3, M4, M5, M6, M7, M8, M9, M10, M11, M12, M13, M14, M15;

    public static Mat4 Identity => new() { M0 = 1, M5 = 1, M10 = 1, M15 = 1 };

    /// <summary>Right-handed perspective with a [0,1] depth range (WebGPU/D3D convention).</summary>
    public static Mat4 PerspectiveRhZo(float fovYRadians, float aspect, float near, float far)
    {
        float tanHalf = MathF.Tan(fovYRadians * 0.5f);
        Mat4 m = default;
        m.M0 = 1f / (aspect * tanHalf);   // col0,row0
        m.M5 = 1f / tanHalf;              // col1,row1
        m.M10 = far / (near - far);       // col2,row2
        m.M11 = -1f;                      // col2,row3
        m.M14 = -(far * near) / (far - near); // col3,row2
        return m;
    }

    /// <summary>Right-handed look-at (camera looks down -Z).</summary>
    public static Mat4 LookAtRh(Vector3D<float> eye, Vector3D<float> target, Vector3D<float> up)
    {
        Vector3D<float> f = Vector3D.Normalize(target - eye);
        Vector3D<float> s = Vector3D.Normalize(Vector3D.Cross(f, up));
        Vector3D<float> u = Vector3D.Cross(s, f);
        Mat4 m = default;
        m.M0 = s.X; m.M4 = s.Y; m.M8 = s.Z;
        m.M1 = u.X; m.M5 = u.Y; m.M9 = u.Z;
        m.M2 = -f.X; m.M6 = -f.Y; m.M10 = -f.Z;
        m.M12 = -Vector3D.Dot(s, eye);
        m.M13 = -Vector3D.Dot(u, eye);
        m.M14 = Vector3D.Dot(f, eye);
        m.M15 = 1f;
        return m;
    }

    public static Mat4 Translation(Vector3D<float> t)
    {
        Mat4 m = Identity;
        m.M12 = t.X; m.M13 = t.Y; m.M14 = t.Z;
        return m;
    }

    public static Mat4 Scale(Vector3D<float> s)
    {
        Mat4 m = default;
        m.M0 = s.X; m.M5 = s.Y; m.M10 = s.Z; m.M15 = 1f;
        return m;
    }

    public static Mat4 FromQuaternion(Quaternion<float> q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float xx = x * x, yy = y * y, zz = z * z;
        float xy = x * y, xz = x * z, yz = y * z;
        float wx = w * x, wy = w * y, wz = w * z;
        Mat4 m = default;
        m.M0 = 1 - 2 * (yy + zz); m.M1 = 2 * (xy + wz);     m.M2 = 2 * (xz - wy);
        m.M4 = 2 * (xy - wz);     m.M5 = 1 - 2 * (xx + zz); m.M6 = 2 * (yz + wx);
        m.M8 = 2 * (xz + wy);     m.M9 = 2 * (yz - wx);     m.M10 = 1 - 2 * (xx + yy);
        m.M15 = 1f;
        return m;
    }

    /// <summary>Matrix product (a * b): applies b first, then a, in column-vector convention.</summary>
    public static Mat4 Multiply(in Mat4 a, in Mat4 b)
    {
        Span<float> af = stackalloc float[16] { a.M0, a.M1, a.M2, a.M3, a.M4, a.M5, a.M6, a.M7, a.M8, a.M9, a.M10, a.M11, a.M12, a.M13, a.M14, a.M15 };
        Span<float> bf = stackalloc float[16] { b.M0, b.M1, b.M2, b.M3, b.M4, b.M5, b.M6, b.M7, b.M8, b.M9, b.M10, b.M11, b.M12, b.M13, b.M14, b.M15 };
        Span<float> r = stackalloc float[16];
        for (int col = 0; col < 4; col++)
            for (int row = 0; row < 4; row++)
            {
                float sum = 0f;
                for (int k = 0; k < 4; k++)
                    sum += af[k * 4 + row] * bf[col * 4 + k];
                r[col * 4 + row] = sum;
            }
        return new Mat4
        {
            M0 = r[0], M1 = r[1], M2 = r[2], M3 = r[3],
            M4 = r[4], M5 = r[5], M6 = r[6], M7 = r[7],
            M8 = r[8], M9 = r[9], M10 = r[10], M11 = r[11],
            M12 = r[12], M13 = r[13], M14 = r[14], M15 = r[15],
        };
    }
}
