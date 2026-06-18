using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>CPU-side mesh helpers for test geometry.</summary>
public static class PrimitiveFactory
{
    /// <summary>A unit cube centred on the origin (extent ±0.5) with per-face normals and a flat colour.</summary>
    public static (Vertex[] vertices, uint[] indices) Cube(Vector3D<float> color)
    {
        // 6 faces, each: normal + 4 corners (CCW when viewed from outside).
        (Vector3D<float> n, Vector3D<float>[] corners)[] faces =
        {
            (new(0, 0, 1),  new Vector3D<float>[] { new(-.5f,-.5f,.5f), new(.5f,-.5f,.5f), new(.5f,.5f,.5f), new(-.5f,.5f,.5f) }),   // +Z
            (new(0, 0, -1), new Vector3D<float>[] { new(.5f,-.5f,-.5f), new(-.5f,-.5f,-.5f), new(-.5f,.5f,-.5f), new(.5f,.5f,-.5f) }), // -Z
            (new(1, 0, 0),  new Vector3D<float>[] { new(.5f,-.5f,.5f), new(.5f,-.5f,-.5f), new(.5f,.5f,-.5f), new(.5f,.5f,.5f) }),   // +X
            (new(-1, 0, 0), new Vector3D<float>[] { new(-.5f,-.5f,-.5f), new(-.5f,-.5f,.5f), new(-.5f,.5f,.5f), new(-.5f,.5f,-.5f) }), // -X
            (new(0, 1, 0),  new Vector3D<float>[] { new(-.5f,.5f,.5f), new(.5f,.5f,.5f), new(.5f,.5f,-.5f), new(-.5f,.5f,-.5f) }),   // +Y
            (new(0, -1, 0), new Vector3D<float>[] { new(-.5f,-.5f,-.5f), new(.5f,-.5f,-.5f), new(.5f,-.5f,.5f), new(-.5f,-.5f,.5f) }), // -Y
        };

        var vertices = new List<Vertex>(24);
        var indices = new List<uint>(36);
        foreach (var (n, corners) in faces)
        {
            uint baseIndex = (uint)vertices.Count;
            foreach (var c in corners)
                vertices.Add(new Vertex(c, n, color));
            indices.AddRange(new[] { baseIndex, baseIndex + 1, baseIndex + 2, baseIndex, baseIndex + 2, baseIndex + 3 });
        }
        return (vertices.ToArray(), indices.ToArray());
    }
}
