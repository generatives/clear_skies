using System.Runtime.InteropServices;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// One greedy-meshed chunk quad packed into 8 bytes (four 48-byte <see cref="Vertex"/>es and six indices before),
/// read from a storage buffer by the shader's vs_chunk, six vertices per quad (vs_chunk_lines: 8 for the wireframe),
/// found by vertex index. A chunk's quads sit
/// on whole block corners, face one of six ways and take one block type each, so:
/// <list type="bullet">
/// <item><see cref="A"/>: the quad's first corner (chunk-local, 0-32) in bits 0-5, 6-11, 12-17; the face (0 +X, 1 -X,
/// 2 +Y, 3 -Y, 4 +Z, 5 -Z, the mesher's order; 6 and 7 a cross block's diagonals, see below) in bits 18-20; the texture
/// layer in bits 21-28, 255 for untextured; the low 3 bits of its width - 1 in bits 29-31.</item>
/// <item><see cref="B"/>: the block (<see cref="BlockId"/>) in bits 0-7; for a diagonal, which side it faces in bit 8;
/// the high 2 bits of width - 1 in bits 24-25; height - 1 in bits 26-30; where its light map starts in its mesh's (see
/// <see cref="LightOffset"/>) in bits 9-23 (low 15 bits) and 31 (top bit).</item>
/// </list>
/// A <see cref="BlockShape.Cross"/> block's quads (GreedyMesher.EmitCross) are faces 6 (diagonal A, from the cell's
/// low x, z corner to its high one) and 7 (diagonal B, from high x, low z to low x, high z), one cell in size, with the
/// cell's low corner as their first corner; B bit 8 is clear for the front side and set for the back.
/// What's the same for every quad of a block type (its colour, shown on untextured blocks, and its opacity) isn't stored
/// per quad: the shader looks it up by block in a table built from the <see cref="BlockRegistry"/>
/// (<see cref="BuildBlockTable"/>), so a new per-block property is a table column, not quad bits. The texture layer
/// stays in the quad because it depends on the face and the block's orientation too.
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

    /// <summary>Packs the quad the mesher emitted as <paramref name="v"/> (its four corners, in its order), of
    /// <paramref name="block"/>.</summary>
    public static ChunkQuad Pack(ReadOnlySpan<Vertex> v, BlockId block)
    {
        var n = v[0].Normal;
        if (MathF.Abs(n.X) > 0.1f && MathF.Abs(n.Z) > 0.1f) return PackDiagonal(v, block);
        uint face = n.X > 0.5f ? 0u : n.X < -0.5f ? 1u : n.Y > 0.5f ? 2u : n.Y < -0.5f ? 3u : n.Z > 0.5f ? 4u : 5u;
        var p = v[0].Position;
        var span = v[2].Position - p; // the opposite corner
        float su = face < 2 ? span.Y : span.X;
        float sv = face < 4 ? span.Z : span.Y;
        uint w = (uint)MathF.Round(su) - 1, h = (uint)MathF.Round(sv) - 1;
        uint layer = v[0].Uv.Z < 0f ? NoLayer : (uint)System.Math.Min((int)v[0].Uv.Z, NoLayer - 1);
        uint a = (uint)p.X | (uint)p.Y << 6 | (uint)p.Z << 12 | face << 18 | layer << 21 | (w & 7) << 29;
        uint b = (uint)block | (w >> 3) << 24 | h << 26;
        return new ChunkQuad(a, b);
    }

    /// <summary>A cross block's quad: diagonal A's normals have opposite x and z signs, B's the same; the back side's
    /// x is positive.</summary>
    private static ChunkQuad PackDiagonal(ReadOnlySpan<Vertex> v, BlockId block)
    {
        var n = v[0].Normal;
        uint face = n.X * n.Z < 0f ? 6u : 7u;
        uint back = n.X > 0f ? 1u : 0u;
        var o = Vector3D.Min(Vector3D.Min(v[0].Position, v[1].Position), Vector3D.Min(v[2].Position, v[3].Position));
        uint layer = v[0].Uv.Z < 0f ? NoLayer : (uint)System.Math.Min((int)v[0].Uv.Z, NoLayer - 1);
        uint a = (uint)o.X | (uint)o.Y << 6 | (uint)o.Z << 12 | face << 18 | layer << 21;
        return new ChunkQuad(a, (uint)block | back << 8);
    }

    /// <summary>The quad's block.</summary>
    public BlockId Block => (BlockId)(B & 255);

    /// <summary>A <see cref="LightOffset"/> for a quad without a light map: a cross block's (lit flat from its cell), or
    /// one past what 16 bits reach in a huge mesh (lit flat from the cell in front of it).</summary>
    public const uint NoLightmap = 0xFFFF;

    /// <summary>Where the quad's light map starts, in 32-bit words from its mesh's (see
    /// Rendering.WebGpu.QuadLightmap): its (width + 1) x (height + 1) face corners, 16 bits each, row by row along its
    /// width, then for a two-sided mesh (water) as many again for its back. <see cref="NoLightmap"/> for none.</summary>
    public uint LightOffset => ((B >> 9) & 0x7FFF) | (B >> 31) << 15;

    /// <summary>This quad with its light map at <paramref name="words"/> (see <see cref="LightOffset"/>).</summary>
    public ChunkQuad WithLightOffset(uint words)
        => new(A, (B & ~(0x7FFFu << 9 | 1u << 31)) | (words & 0x7FFF) << 9 | (words >> 15 & 1) << 31);

    /// <summary>How many face corners the quad's light map holds on one side: none for a cross block's quad.</summary>
    public int LightCorners
    {
        get
        {
            if (((A >> 18) & 7) >= 6) return 0;
            int w = (int)((A >> 29) | ((B >> 24) & 3) << 3) + 1, h = (int)((B >> 26) & 31) + 1;
            return (w + 1) * (h + 1);
        }
    }

    /// <summary>Lays out a mesh's light map: gives each quad its <see cref="LightOffset"/>, each starting on a whole
    /// word, both sides of every quad for a <paramref name="twoSided"/> mesh. Returns the words the light map takes.</summary>
    public static uint AssignLightOffsets(Span<ChunkQuad> quads, bool twoSided)
    {
        uint words = 0;
        for (int i = 0; i < quads.Length; i++)
        {
            uint corners = (uint)quads[i].LightCorners * (twoSided ? 2u : 1u);
            if (corners == 0 || words >= NoLightmap) { quads[i] = quads[i].WithLightOffset(NoLightmap); continue; }
            quads[i] = quads[i].WithLightOffset(words);
            words += (corners + 1) / 2;
        }
        return words;
    }

    /// <summary>Entries in <see cref="BuildBlockTable"/>: one per possible <see cref="BlockId"/>.</summary>
    public const int BlockTableSize = 256;

    /// <summary>What the shader looks up by a quad's block (blockTable): each block's colour (sRGB, as
    /// <see cref="BlockDef.Color"/>) and opacity (<see cref="BlockDef.EffectiveAlpha"/>; 1 for opaque blocks; for a
    /// cross block, 0 if it's <see cref="BlockDef.RigidCross"/>), plus 2 if its top takes the climate tint, 4 if all
    /// its faces do (<see cref="BlockDef.Tint"/>; opacities are 0-1, so the shader splits them apart).</summary>
    public static Vector4D<float>[] BuildBlockTable()
    {
        var table = new Vector4D<float>[BlockTableSize];
        for (int i = 0; i < table.Length; i++)
        {
            ref readonly var def = ref BlockRegistry.Get((BlockId)i);
            // A cross block is always drawn cut out, never blended, so its opacity is free to say whether its two
            // quads are twisted and shifted apart (1) or kept a clean X (0, BlockDef.RigidCross); see vs_chunk.
            float alpha = def.IsCross ? (def.RigidCross ? 0f : 1f) : def.Layer == RenderLayer.Translucent ? def.EffectiveAlpha : 1f;
            alpha += def.Tint == BlockTint.Top ? 2f : def.Tint == BlockTint.All ? 4f : 0f;
            table[i] = new Vector4D<float>(def.Color.X, def.Color.Y, def.Color.Z, alpha);
        }
        return table;
    }

    /// <summary>What vs_chunk decodes for corner <paramref name="corner"/> (0-3, the mesher's order), for tests:
    /// its position, normal and colour (its block's).</summary>
    public Vertex Corner(int corner)
    {
        var o = new Vector3D<float>(A & 63, (A >> 6) & 63, (A >> 12) & 63);
        uint face = (A >> 18) & 7;
        if (face >= 6)
        {
            // As vs_chunk: the front's corners s-first, the back's t-first (see GreedyMesher.EmitCross).
            bool back = (B & 256) != 0;
            float ds = back ? (corner >= 2 ? 1 : 0) : (corner == 1 || corner == 2 ? 1 : 0);
            float dt = back ? (corner == 1 || corner == 2 ? 1 : 0) : (corner >= 2 ? 1 : 0);
            var pos = o + new Vector3D<float>(face == 6 ? ds : 1 - ds, dt, ds);
            float k = back ? GreedyMesher.Diagonal : -GreedyMesher.Diagonal;
            var normal = new Vector3D<float>(k, 0, face == 6 ? -k : k);
            return new Vertex { Position = pos, Normal = normal, Color = BlockRegistry.Get(Block).Color };
        }
        float du = ((A >> 29) | ((B >> 24) & 3) << 3) + 1, dv = ((B >> 26) & 31) + 1;
        bool flip = face == 0 || face == 3 || face == 4;
        float cu = flip ? (corner == 1 || corner == 2 ? du : 0) : (corner >= 2 ? du : 0);
        float cv = flip ? (corner >= 2 ? dv : 0) : (corner == 1 || corner == 2 ? dv : 0);
        var p = face < 2 ? o + new Vector3D<float>(0, cu, cv)
              : face < 4 ? o + new Vector3D<float>(cu, 0, cv)
              : o + new Vector3D<float>(cu, cv, 0);
        float s = (face & 1) == 1 ? -1f : 1f;
        var n = face < 2 ? new Vector3D<float>(s, 0, 0) : face < 4 ? new Vector3D<float>(0, s, 0) : new Vector3D<float>(0, 0, s);
        return new Vertex { Position = p, Normal = n, Color = BlockRegistry.Get(Block).Color };
    }
}
