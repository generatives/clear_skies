using System.Runtime.InteropServices;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// A chunk mesh vertex packed into 8 bytes (a <see cref="Vertex"/> is 48), decoded by the shader's vs_chunk. A chunk's
/// vertices sit on whole block corners, face one of six ways and take one colour and texture per block type, so:
/// <list type="bullet">
/// <item><see cref="A"/>: x, y, z (chunk-local, 0-32) in bits 0-5, 6-11, 12-17; the face (0 +X, 1 -X, 2 +Y, 3 -Y,
/// 4 +Z, 5 -Z, the mesher's order) in bits 18-20; the texture layer in bits 21-28, 255 for untextured.</item>
/// <item><see cref="B"/>: the colour, 8 bits each of R, G, B.</item>
/// </list>
/// The texture coordinates aren't stored: they follow from the position and the face (see GreedyMesher.MakeUv).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ChunkVertex
{
    public const uint SizeBytes = 8;
    public const int NoLayer = 255;

    public readonly uint A, B;

    private ChunkVertex(uint a, uint b) { A = a; B = b; }

    public static ChunkVertex Pack(in Vertex v)
    {
        var n = v.Normal;
        uint face = n.X > 0.5f ? 0u : n.X < -0.5f ? 1u : n.Y > 0.5f ? 2u : n.Y < -0.5f ? 3u : n.Z > 0.5f ? 4u : 5u;
        uint layer = v.Uv.Z < 0f ? NoLayer : (uint)System.Math.Min((int)v.Uv.Z, NoLayer - 1);
        uint a = (uint)v.Position.X | (uint)v.Position.Y << 6 | (uint)v.Position.Z << 12 | face << 18 | layer << 21;
        uint b = Channel(v.Color.X) | Channel(v.Color.Y) << 8 | Channel(v.Color.Z) << 16;
        return new ChunkVertex(a, b);
    }

    private static uint Channel(float c) => (uint)System.Math.Clamp((int)MathF.Round(c * 255f), 0, 255);

    /// <summary>What vs_chunk decodes (for tests): position, normal, colour and (u, v, layer).</summary>
    public Vertex Unpack()
    {
        var p = new Vector3D<float>(A & 63, (A >> 6) & 63, (A >> 12) & 63);
        uint face = (A >> 18) & 7;
        float s = (face & 1) == 1 ? -1f : 1f;
        var n = face < 2 ? new Vector3D<float>(s, 0, 0) : face < 4 ? new Vector3D<float>(0, s, 0) : new Vector3D<float>(0, 0, s);
        uint layerBits = (A >> 21) & 255;
        float layer = layerBits == NoLayer ? -1f : layerBits;
        var uv = face < 2 ? new Vector3D<float>(p.Z, -p.Y, layer)
               : face < 4 ? new Vector3D<float>(p.X, p.Z, layer)
               : new Vector3D<float>(p.X, -p.Y, layer);
        var c = new Vector3D<float>(B & 255, (B >> 8) & 255, (B >> 16) & 255) / 255f;
        return new Vertex { Position = p, Normal = n, Color = c, Uv = uv };
    }
}
