using ClearSkies.Engine.Rendering;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Converts a ChunkData (plus its six optional loaded neighbours) into an efficient triangle mesh
/// using greedy quad merging. Outputs vertices in chunk-local space [0, ChunkData.Size]; the
/// chunk entity's Transform.Position places it in the world.
///
/// Meshing is light-independent: faces merge purely on <see cref="BlockId"/>. Lighting is applied in
/// the fragment shader, which samples the chunk light buffer at the air-side voxel using the
/// interpolated chunk-local position and the face normal — so a merged quad no longer needs per-cell
/// light in its merge key.
/// </summary>
public sealed class GreedyMesher
{
    // Six face directions. For each:
    //   D          = axis being swept (0=X, 1=Y, 2=Z)
    //   U, V       = the two free axes (width, height in the 2-D mask)
    //   FaceOffset = 0 → face sits at the low end of the block (slice);
    //                1 → face sits at the high end (slice+1)
    //   Flip       = which of the two CCW quad-corner orderings to use
    //                (derived from cross-product analysis to match the face normal)
    private static readonly FaceDesc[] Faces =
    {
        new(new( 1,0,0), d:0, u:1, v:2, faceOffset:1, flip:true),   // +X
        new(new(-1,0,0), d:0, u:1, v:2, faceOffset:0, flip:false),  // -X
        new(new( 0,1,0), d:1, u:0, v:2, faceOffset:1, flip:false),  // +Y
        new(new( 0,-1,0),d:1, u:0, v:2, faceOffset:0, flip:true),   // -Y
        new(new( 0,0,1), d:2, u:0, v:1, faceOffset:1, flip:true),   // +Z
        new(new( 0,0,-1),d:2, u:0, v:1, faceOffset:0, flip:false),  // -Z
    };

    // Reusable scratch buffers — mesher is single-threaded per chunk.
    private readonly BlockId[] _mask     = new BlockId[ChunkData.Size * ChunkData.Size];
    private readonly bool[]    _consumed = new bool   [ChunkData.Size * ChunkData.Size];

    /// <summary>
    /// Mesh <paramref name="chunk"/>. Neighbour ChunkData parameters are for face-culling only;
    /// pass <c>null</c> for any unloaded neighbour (its side is treated as open air).
    /// </summary>
    public (Vertex[] vertices, uint[] indices) Mesh(
        ChunkData  chunk,
        ChunkData? nX, ChunkData? pX,
        ChunkData? nY, ChunkData? pY,
        ChunkData? nZ, ChunkData? pZ)
    {
        // Array order matches Faces[] (fi=0:+X, fi=1:-X, fi=2:+Y, fi=3:-Y, fi=4:+Z, fi=5:-Z).
        ChunkData?[] neighbors = { pX, nX, pY, nY, pZ, nZ };

        var verts   = new List<Vertex>();
        var indices = new List<uint>();
        int sz      = ChunkData.Size;

        for (int fi = 0; fi < Faces.Length; fi++)
        {
            ref readonly var face = ref Faces[fi];
            var nb = neighbors[fi];

            for (int slice = 0; slice < sz; slice++)
            {
                // ── Build the 2-D face mask for this slice ──────────────────────────
                Array.Clear(_mask,     0, _mask.Length);
                Array.Clear(_consumed, 0, _consumed.Length);

                // adjSlice is constant for all (u,v) at this face/slice.
                int adjSlice = slice + (face.FaceOffset == 1 ? 1 : -1);

                for (int u = 0; u < sz; u++)
                for (int v = 0; v < sz; v++)
                {
                    var blockId = GetBlock(chunk, face, slice, u, v);
                    if (!BlockRegistry.Get(blockId).IsSolid) continue;

                    BlockId adjId;
                    if (adjSlice < 0 || adjSlice >= sz)
                    {
                        if (nb == null) adjId = BlockId.Air;
                        else {
                            int nbSlice = face.FaceOffset == 1 ? 0 : sz - 1;
                            adjId = GetBlock(nb, face, nbSlice, u, v);
                        }
                    }
                    else
                    {
                        adjId = GetBlock(chunk, face, adjSlice, u, v);
                    }

                    if (!BlockRegistry.Get(adjId).IsSolid)
                        _mask[u + v * sz] = blockId;
                }

                // ── Greedy merge (block id only) ─────────────────────────────────────
                for (int v = 0; v < sz; v++)
                for (int u = 0; u < sz; u++)
                {
                    BlockId start = _mask[u + v * sz];
                    if (start == BlockId.Air || _consumed[u + v * sz]) continue;

                    // Expand width along U (same block)
                    int du = 1;
                    while (u + du < sz
                        && _mask[(u + du) + v * sz] == start
                        && !_consumed[(u + du) + v * sz])
                        du++;

                    // Expand height along V (all cells in the row must match)
                    int dv = 1;
                    bool canExpand = true;
                    while (canExpand && v + dv < sz)
                    {
                        for (int k = u; k < u + du; k++)
                        {
                            if (_mask[k + (v + dv) * sz] != start || _consumed[k + (v + dv) * sz])
                            { canExpand = false; break; }
                        }
                        if (canExpand) dv++;
                    }

                    // Mark rectangle as consumed
                    for (int dv2 = 0; dv2 < dv; dv2++)
                    for (int du2 = 0; du2 < du; du2++)
                        _consumed[(u + du2) + (v + dv2) * sz] = true;

                    EmitQuad(verts, indices, face, slice + face.FaceOffset, u, v, du, dv,
                             BlockRegistry.Get(start).Color);
                }
            }
        }

        return (verts.ToArray(), indices.ToArray());
    }

    private static BlockId GetBlock(ChunkData chunk, in FaceDesc face, int slice, int u, int v)
    {
        Span<int> p = stackalloc int[3];
        p[face.D] = slice;
        p[face.U] = u;
        p[face.V] = v;
        return chunk.Get(p[0], p[1], p[2]);
    }

    private static void EmitQuad(
        List<Vertex> verts, List<uint> indices,
        in FaceDesc face, int fp, int u0, int v0, int du, int dv,
        Vector3D<float> color)
    {
        var normal = new Vector3D<float>(face.Normal.X, face.Normal.Y, face.Normal.Z);

        Vector3D<float> c0, c1, c2, c3;
        if (!face.Flip)
        {
            c0 = MakePos(face, fp, u0,      v0);
            c1 = MakePos(face, fp, u0,      v0 + dv);
            c2 = MakePos(face, fp, u0 + du, v0 + dv);
            c3 = MakePos(face, fp, u0 + du, v0);
        }
        else
        {
            c0 = MakePos(face, fp, u0,      v0);
            c1 = MakePos(face, fp, u0 + du, v0);
            c2 = MakePos(face, fp, u0 + du, v0 + dv);
            c3 = MakePos(face, fp, u0,      v0 + dv);
        }

        uint b = (uint)verts.Count;
        verts.Add(new Vertex { Position = c0, Normal = normal, Color = color });
        verts.Add(new Vertex { Position = c1, Normal = normal, Color = color });
        verts.Add(new Vertex { Position = c2, Normal = normal, Color = color });
        verts.Add(new Vertex { Position = c3, Normal = normal, Color = color });

        indices.Add(b);     indices.Add(b + 1); indices.Add(b + 2);
        indices.Add(b);     indices.Add(b + 2); indices.Add(b + 3);
    }

    private static Vector3D<float> MakePos(in FaceDesc face, int fp, int u, int v)
    {
        Span<float> p = stackalloc float[3];
        p[face.D] = fp;
        p[face.U] = u;
        p[face.V] = v;
        return new(p[0], p[1], p[2]);
    }

    private readonly struct FaceDesc
    {
        public Vector3D<int> Normal     { get; }
        public int           D          { get; }
        public int           U          { get; }
        public int           V          { get; }
        public int           FaceOffset { get; }
        public bool          Flip       { get; }

        public FaceDesc(Vector3D<int> normal, int d, int u, int v, int faceOffset, bool flip)
        {
            Normal = normal; D = d; U = u; V = v; FaceOffset = faceOffset; Flip = flip;
        }
    }
}
