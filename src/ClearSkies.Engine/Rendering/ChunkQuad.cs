using System.Runtime.InteropServices;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// One greedy-meshed chunk quad packed into 8 bytes (four 48-byte <see cref="Vertex"/>es and six indices before),
/// drawn by the shader's vs_chunk as one 6-vertex instance (vs_chunk_lines: 8 for the wireframe). A chunk's quads sit
/// on whole block corners, face one of six ways and take one colour and texture per block type, so:
/// <list type="bullet">
/// <item><see cref="A"/>: the quad's first corner (chunk-local, 0-32) in bits 0-5, 6-11, 12-17; the face (0 +X, 1 -X,
/// 2 +Y, 3 -Y, 4 +Z, 5 -Z, the mesher's order) in bits 18-20; the texture layer in bits 21-28, 255 for untextured;
/// the low 3 bits of its width - 1 in bits 29-31.</item>
/// <item><see cref="B"/>: the colour, 8 bits each of R, G, B; the high 2 bits of width - 1 in bits 24-25; height - 1
/// in bits 26-30.</item>
/// </list>
/// Width and height run along the face's two free axes as the mesher sets them (X faces: y, z; Y faces: x, z; Z faces:
/// x, y); the corners' order and the texture coordinates follow from the face (see GreedyMesher.EmitQuad, MakeUv).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ChunkQuad
{
    public const uint SizeBytes = 8;
    public const int NoLayer = 255;

    public readonly uint A, B;

    private ChunkQuad(uint a, uint b) { A = a; B = b; }

    /// <summary>Packs the quad the mesher emitted as <paramref name="v"/> (its four corners, in its order).</summary>
    public static ChunkQuad Pack(ReadOnlySpan<Vertex> v)
    {
        var n = v[0].Normal;
        uint face = n.X > 0.5f ? 0u : n.X < -0.5f ? 1u : n.Y > 0.5f ? 2u : n.Y < -0.5f ? 3u : n.Z > 0.5f ? 4u : 5u;
        var p = v[0].Position;
        var span = v[2].Position - p; // the opposite corner
        float su = face < 2 ? span.Y : span.X;
        float sv = face < 4 ? span.Z : span.Y;
        uint w = (uint)MathF.Round(su) - 1, h = (uint)MathF.Round(sv) - 1;
        uint layer = v[0].Uv.Z < 0f ? NoLayer : (uint)System.Math.Min((int)v[0].Uv.Z, NoLayer - 1);
        var c = v[0].Color;
        uint a = (uint)p.X | (uint)p.Y << 6 | (uint)p.Z << 12 | face << 18 | layer << 21 | (w & 7) << 29;
        uint b = Channel(c.X) | Channel(c.Y) << 8 | Channel(c.Z) << 16 | (w >> 3) << 24 | h << 26;
        return new ChunkQuad(a, b);
    }

    private static uint Channel(float c) => (uint)System.Math.Clamp((int)MathF.Round(c * 255f), 0, 255);

    /// <summary>What vs_chunk decodes for corner <paramref name="corner"/> (0-3, the mesher's order), for tests:
    /// its position, normal and colour.</summary>
    public Vertex Corner(int corner)
    {
        var o = new Vector3D<float>(A & 63, (A >> 6) & 63, (A >> 12) & 63);
        uint face = (A >> 18) & 7;
        float du = ((A >> 29) | ((B >> 24) & 3) << 3) + 1, dv = ((B >> 26) & 31) + 1;
        bool flip = face == 0 || face == 3 || face == 4;
        float cu = flip ? (corner == 1 || corner == 2 ? du : 0) : (corner >= 2 ? du : 0);
        float cv = flip ? (corner >= 2 ? dv : 0) : (corner == 1 || corner == 2 ? dv : 0);
        var p = face < 2 ? o + new Vector3D<float>(0, cu, cv)
              : face < 4 ? o + new Vector3D<float>(cu, 0, cv)
              : o + new Vector3D<float>(cu, cv, 0);
        float s = (face & 1) == 1 ? -1f : 1f;
        var n = face < 2 ? new Vector3D<float>(s, 0, 0) : face < 4 ? new Vector3D<float>(0, s, 0) : new Vector3D<float>(0, 0, s);
        var c = new Vector3D<float>(B & 255, (B >> 8) & 255, (B >> 16) & 255) / 255f;
        return new Vertex { Position = p, Normal = n, Color = c };
    }
}
