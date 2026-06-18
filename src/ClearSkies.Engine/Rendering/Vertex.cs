using System.Runtime.InteropServices;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// A single mesh vertex. Layout (36 bytes): position @0, normal @12, color @24, all Float32x3.
/// Normal is unused for shading in Milestone 1 but kept in the layout for later lighting work.
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
        Normal = normal;
        Color = color;
    }
}
