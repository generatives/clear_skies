using System.Runtime.InteropServices;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// A single mesh vertex. Layout (36 bytes): position @0, normal @12, color @24 (all Float32x3).
/// Light is no longer baked per-vertex (that breaks greedy meshing); the fragment shader samples
/// the chunk light buffer by voxel coordinate instead. <see cref="Position"/> is chunk-local
/// ([0, ChunkData.Size]); the fragment uses it plus the normal to find the air-side voxel.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    public Vector3D<float> Position;
    public Vector3D<float> Normal;
    public Vector3D<float> Color;

    public Vertex(Vector3D<float> position, Vector3D<float> normal, Vector3D<float> color)
    {
        Position = position;
        Normal   = normal;
        Color    = color;
    }
}
