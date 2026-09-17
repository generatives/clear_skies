using System.Runtime.InteropServices;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// A single mesh vertex. Layout (48 bytes): position @0, normal @12, color @24, uv @36 (all Float32x3).
/// Light is no longer baked per-vertex (that breaks greedy meshing); the fragment shader samples
/// the chunk light buffer by voxel coordinate instead. <see cref="Position"/> is chunk-local
/// ([0, ChunkData.Size]); the fragment uses it plus the normal to find the air-side voxel.
///
/// <see cref="Uv"/> is (u, v, layer) into the block-texture array: u/v are tile-space (not
/// normalized — the fragment wraps with <c>fract</c> so a texture repeats per-block regardless
/// of how large a greedy-merged quad is), and layer is an index into the texture array, or -1
/// (the <see cref="Vertex(Vector3D{float}, Vector3D{float}, Vector3D{float})"/> constructor's
/// sentinel) meaning "untextured — use <see cref="Color"/> instead".
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    /// <summary>Byte size of the struct — every WebGPU <c>VertexBufferLayout.ArrayStride</c> that binds a
    /// buffer of these (the main pipeline, and the position-only depth passes in SunShadowPass/LightShadowPass
    /// that reuse the same vertex buffers) must use this, or a stale hardcoded stride silently misreads every
    /// vertex after the first whenever this struct's size changes.</summary>
    public const uint SizeBytes = 48;

    public Vector3D<float> Position;
    public Vector3D<float> Normal;
    public Vector3D<float> Color;
    public Vector3D<float> Uv;

    public Vertex(Vector3D<float> position, Vector3D<float> normal, Vector3D<float> color)
    {
        Position = position;
        Normal   = normal;
        Color    = color;
        Uv       = new Vector3D<float>(0, 0, -1);
    }
}
