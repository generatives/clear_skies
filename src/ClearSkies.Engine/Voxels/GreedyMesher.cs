using ClearSkies.Engine.Rendering;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Converts a ChunkData (plus its six optional loaded neighbours) into an efficient triangle mesh
/// using greedy quad merging. Outputs vertices in chunk-local space [0, ChunkData.Size]; the
/// chunk entity's Transform.Position places it in the world.
///
/// Meshing is light-independent: faces merge on <see cref="BlockId"/> plus, only for block types whose
/// appearance depends on orientation (<see cref="BlockDef.HasOrientedTexture"/>, e.g. Fan), which
/// <see cref="FaceRole"/> this face plays for the voxel's stored orientation — so two adjacent oriented
/// blocks facing different ways still mesh as separate quads where that matters, but every other block
/// type merges exactly as before. Lighting is applied
/// in the fragment shader, which samples the chunk light buffer at the air-side voxel using the
/// interpolated chunk-local position and the face normal — so a merged quad no longer needs per-cell
/// light in its merge key.
///
/// <see cref="BlockDef.Transparent"/> blocks are meshed alongside but into their own <see cref="RenderLayer"/>'s mesh:
/// cut out (glass: <see cref="ChunkMeshLayers.Cutout"/>) or translucent (water: <see cref="ChunkMeshLayers.Translucent"/>,
/// drawn alpha-blended after the opaque world). They don't hide their neighbours' faces, so an opaque block behind glass
/// still has its face; and a transparent face is hidden only by an opaque block (or <see cref="BlockDef.OpaqueModel"/>)
/// or another of its own type. An opaque model block hides faces against it like an opaque cube, since its model
/// covers them.
///
/// <see cref="BlockShape.Cross"/> blocks (plants) aren't greedy meshed: each gets two quads crossing diagonally through
/// its cell, both sides of each (see <see cref="EmitCross"/>), in the cut-out mesh. They hide nothing and nothing hides
/// them.
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

    // Reusable scratch buffers — mesher is single-threaded per chunk. verts/indices are cleared and
    // refilled each Mesh() call rather than reallocated, so their backing arrays stabilize at whatever
    // the largest chunk seen needs instead of re-growing (doubling + copying) from empty every call.
    // Safe to hand back directly: callers (ChunkMeshSystem) upload the span to the GPU synchronously
    // before this mesher is invoked again.
    private readonly MaskCell[] _mask     = new MaskCell[ChunkData.Size * ChunkData.Size];
    private readonly bool[]     _consumed = new bool    [ChunkData.Size * ChunkData.Size];
    private readonly List<Vertex> _verts   = new();
    private readonly List<uint>   _indices = new();
    private readonly List<Vertex> _tVerts   = new();
    private readonly List<uint>   _tIndices = new();
    private readonly List<BlockId> _blocks  = new();
    private readonly List<BlockId> _tBlocks = new();
    private readonly List<BlockId> _cBlocks = new();
    private readonly List<Vertex> _cVerts   = new();
    private readonly List<uint>   _cIndices = new();

    private readonly TextureAtlas? _atlas;

    public GreedyMesher(TextureAtlas? atlas = null)
    {
        _atlas = atlas;
    }

    /// <summary>
    /// Mesh <paramref name="chunk"/>, returning its faces by <see cref="RenderLayer"/>. Neighbour ChunkData parameters
    /// are for face-culling only; pass <c>null</c> for any unloaded neighbour (its side is treated as open air). With
    /// <paramref name="neighboursForTransparentOnly"/>, the neighbours only cull transparent faces, and every other
    /// border face is drawn as if the neighbour were air (see <see cref="ChunkVolume.MeshIgnoresNeighbours"/>). The
    /// returned lists are reused scratch buffers (see field docs) — consume them before calling Mesh() again.
    /// </summary>
    public ChunkMeshLayers Mesh(
        ChunkData  chunk,
        ChunkData? nX, ChunkData? pX,
        ChunkData? nY, ChunkData? pY,
        ChunkData? nZ, ChunkData? pZ,
        bool neighboursForTransparentOnly = false)
    {
        // Array order matches Faces[] (fi=0:+X, fi=1:-X, fi=2:+Y, fi=3:-Y, fi=4:+Z, fi=5:-Z).
        ChunkData?[] neighbors = { pX, nX, pY, nY, pZ, nZ };

        var verts   = _verts;
        var indices = _indices;
        verts.Clear();
        indices.Clear();
        _tVerts.Clear();
        _tIndices.Clear();
        _blocks.Clear();
        _tBlocks.Clear();
        _cBlocks.Clear();
        _cVerts.Clear();
        _cIndices.Clear();
        int sz      = ChunkData.Size;

        for (int fi = 0; fi < Faces.Length; fi++)
        {
            ref readonly var face = ref Faces[fi];
            var nb = neighbors[fi];

            for (int slice = 0; slice < sz; slice++)
            {
                // ── Build the 2-D face mask for this slice ──────────────────────────
                Array.Clear(_mask,     0, _mask.Length); // default(MaskCell) == MaskCell.Air (Id=0=Air, Role=Side)
                Array.Clear(_consumed, 0, _consumed.Length);

                // adjSlice is constant for all (u,v) at this face/slice.
                int adjSlice = slice + (face.FaceOffset == 1 ? 1 : -1);

                for (int u = 0; u < sz; u++)
                for (int v = 0; v < sz; v++)
                {
                    var blockId = GetBlock(chunk, face, slice, u, v);
                    ref readonly var blockDef = ref BlockRegistry.Get(blockId);
                    if (!blockDef.IsFullCube) continue; // air, or a model block (drawn separately)

                    BlockId adjId;
                    if (adjSlice < 0 || adjSlice >= sz)
                    {
                        if (nb == null) adjId = BlockId.Air;
                        else {
                            int nbSlice = face.FaceOffset == 1 ? 0 : sz - 1;
                            adjId = GetBlock(nb, face, nbSlice, u, v);
                            // Only transparent faces are culled across the border.
                            if (neighboursForTransparentOnly && !blockDef.Transparent) adjId = BlockId.Air;
                        }
                    }
                    else
                    {
                        adjId = GetBlock(chunk, face, adjSlice, u, v);
                    }

                    if (!BlockRegistry.Get(adjId).HidesFaceOf(blockId))
                    {
                        // Only look up this voxel's orientation (and classify this face's role) for block
                        // types whose Top/Bottom textures actually depend on it — every other block keeps
                        // merging purely by BlockId regardless of whatever orientation is stored.
                        var role = FaceRole.Side;
                        if (BlockRegistry.Get(blockId).HasOrientedTexture)
                        {
                            var voxelUp = GetOrientation(chunk, face, slice, u, v).Up;
                            role = BlockDef.GetFaceRole(face.Normal, voxelUp);
                        }
                        _mask[u + v * sz] = new MaskCell(blockId, role);
                    }
                }

                // ── Greedy merge (block id + face-role match) ───────────────────────────
                for (int v = 0; v < sz; v++)
                for (int u = 0; u < sz; u++)
                {
                    MaskCell start = _mask[u + v * sz];
                    if (start.Id == BlockId.Air || _consumed[u + v * sz]) continue;

                    // Expand width along U (same cell)
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

                    ref readonly var def = ref BlockRegistry.Get(start.Id);
                    string? texName = def.GetFaceTexture(start.Role);

                    float layer = -1f;
                    if (_atlas != null && _atlas.TryGetLayer(texName, out int l))
                        layer = l;

                    switch (def.Layer)
                    {
                        case RenderLayer.Translucent:
                            EmitQuad(_tVerts, _tIndices, face, slice + face.FaceOffset, u, v, du, dv, def.Color, layer);
                            _tBlocks.Add(start.Id);
                            break;
                        case RenderLayer.Cutout:
                            EmitQuad(_cVerts, _cIndices, face, slice + face.FaceOffset, u, v, du, dv, def.Color, layer);
                            _cBlocks.Add(start.Id);
                            break;
                        default:
                            EmitQuad(verts, indices, face, slice + face.FaceOffset, u, v, du, dv, def.Color, layer);
                            _blocks.Add(start.Id);
                            break;
                    }
                }
            }
        }

        if (chunk.HasAnyNonAir())
            for (int z = 0; z < sz; z++)
            for (int y = 0; y < sz; y++)
            for (int x = 0; x < sz; x++)
            {
                var id = chunk.Get(x, y, z);
                if (!IsCross[(byte)id]) continue;
                ref readonly var def = ref BlockRegistry.Get(id);
                float layer = _atlas != null && _atlas.TryGetLayer(def.Texture, out int l) ? l : -1f;
                EmitCross(_cVerts, _cIndices, _cBlocks, x, y, z, id, def.Color, layer);
            }

        return new ChunkMeshLayers(new LayerMesh(verts, indices, _blocks), new LayerMesh(_cVerts, _cIndices, _cBlocks),
                                   new LayerMesh(_tVerts, _tIndices, _tBlocks));
    }

    // Which block ids are BlockShape.Cross, so the per-voxel scan for them is a table lookup.
    private static readonly bool[] IsCross = BuildCrossTable();

    private static bool[] BuildCrossTable()
    {
        var t = new bool[256];
        for (int i = 0; i < t.Length; i++) t[i] = BlockRegistry.Get((BlockId)i).IsCross;
        return t;
    }

    /// <summary>1/√2: a diagonal quad's normal components.</summary>
    internal const float Diagonal = 0.70710677f;

    /// <summary>
    /// A cross block at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>): two unit quads standing
    /// on the cell's diagonals, A from its (0, 0) corner to (1, 1) in x, z and B from (1, 0) to (0, 1), each emitted
    /// twice, once facing each way, so they're seen from both sides with back faces culled. Along a quad, s runs 0-1
    /// along the diagonal and t up it; the front (which ChunkQuad packs with its side bit clear) has its corners in
    /// s-first order, (0,0) (1,0) (1,1) (0,1), the back t-first, which winds it the other way. U is s and V runs down
    /// world Y as on the cube's sides, so the sprite stands upright. vs_chunk rebuilds the same corners (faces 6 and 7).
    /// </summary>
    private static void EmitCross(List<Vertex> verts, List<uint> indices, List<BlockId> blocks,
                                  int x, int y, int z, BlockId id, Vector3D<float> color, float layer)
    {
        for (int diagonal = 0; diagonal < 2; diagonal++)
        for (int side = 0; side < 2; side++)
        {
            // Front normals: A (-1, 0, 1), B (-1, 0, -1); the back's are their opposites.
            float sign = side == 0 ? -Diagonal : Diagonal;
            var normal = new Vector3D<float>(sign, 0, diagonal == 0 ? -sign : sign);
            uint b = (uint)verts.Count;
            for (int corner = 0; corner < 4; corner++)
            {
                int s, t;
                if (side == 0) { s = corner == 1 || corner == 2 ? 1 : 0; t = corner >= 2 ? 1 : 0; }
                else           { s = corner >= 2 ? 1 : 0;                t = corner == 1 || corner == 2 ? 1 : 0; }
                var position = new Vector3D<float>(diagonal == 0 ? x + s : x + 1 - s, y + t, z + s);
                verts.Add(new Vertex { Position = position, Normal = normal, Color = color, Uv = new(s, -(y + t), layer) });
            }
            indices.Add(b);     indices.Add(b + 1); indices.Add(b + 2);
            indices.Add(b);     indices.Add(b + 2); indices.Add(b + 3);
            blocks.Add(id);
        }
    }

    // face.D/U/V are always a permutation of {0,1,2} (x,y,z); resolving the three coordinates with a
    // direct branch instead of a stackalloc'd Span avoids a per-voxel indirect-index round trip in what
    // is by far the hottest loop in meshing (called twice — self + adjacent — for every voxel of every
    // slice of every face: 6 * 32 * 32 * 32 = ~196k times per chunk).
    private static BlockId GetBlock(ChunkData chunk, in FaceDesc face, int slice, int u, int v)
    {
        int x, y, z;
        if (face.D == 0)      { x = slice; y = u; z = v; }
        else if (face.D == 1) { y = slice; x = u; z = v; }
        else                  { z = slice; x = u; y = v; }
        return chunk.Get(x, y, z);
    }

    private static BlockOrientation GetOrientation(ChunkData chunk, in FaceDesc face, int slice, int u, int v)
    {
        int x, y, z;
        if (face.D == 0)      { x = slice; y = u; z = v; }
        else if (face.D == 1) { y = slice; x = u; z = v; }
        else                  { z = slice; x = u; y = v; }
        return chunk.GetOrientation(x, y, z);
    }

    private static void EmitQuad(
        List<Vertex> verts, List<uint> indices,
        in FaceDesc face, int fp, int u0, int v0, int du, int dv,
        Vector3D<float> color, float textureLayer)
    {
        var normal = new Vector3D<float>(face.Normal.X, face.Normal.Y, face.Normal.Z);

        Vector3D<float> c0, c1, c2, c3;
        Vector3D<float> uv0, uv1, uv2, uv3;
        if (!face.Flip)
        {
            c0 = MakePos(face, fp, u0,      v0);
            c1 = MakePos(face, fp, u0,      v0 + dv);
            c2 = MakePos(face, fp, u0 + du, v0 + dv);
            c3 = MakePos(face, fp, u0 + du, v0);
            uv0 = MakeUv(face, u0,      v0,      textureLayer);
            uv1 = MakeUv(face, u0,      v0 + dv, textureLayer);
            uv2 = MakeUv(face, u0 + du, v0 + dv, textureLayer);
            uv3 = MakeUv(face, u0 + du, v0,      textureLayer);
        }
        else
        {
            c0 = MakePos(face, fp, u0,      v0);
            c1 = MakePos(face, fp, u0 + du, v0);
            c2 = MakePos(face, fp, u0 + du, v0 + dv);
            c3 = MakePos(face, fp, u0,      v0 + dv);
            uv0 = MakeUv(face, u0,      v0,      textureLayer);
            uv1 = MakeUv(face, u0 + du, v0,      textureLayer);
            uv2 = MakeUv(face, u0 + du, v0 + dv, textureLayer);
            uv3 = MakeUv(face, u0,      v0 + dv, textureLayer);
        }

        uint b = (uint)verts.Count;
        verts.Add(new Vertex { Position = c0, Normal = normal, Color = color, Uv = uv0 });
        verts.Add(new Vertex { Position = c1, Normal = normal, Color = color, Uv = uv1 });
        verts.Add(new Vertex { Position = c2, Normal = normal, Color = color, Uv = uv2 });
        verts.Add(new Vertex { Position = c3, Normal = normal, Color = color, Uv = uv3 });

        indices.Add(b);     indices.Add(b + 1); indices.Add(b + 2);
        indices.Add(b);     indices.Add(b + 2); indices.Add(b + 3);
    }

    // Tile-space UV. Texture V must track world-up (Y) on every SIDE face so "up" in the sprite (row 0,
    // v=0) lands at the top of the block, regardless of which position axis (face.U or face.V) happens to
    // carry Y for that face direction — for +X/-X, Y is face.U; for +Z/-Z, Y is face.V. It's also negated,
    // since image row 0 (v=0) is the sprite's TOP but increasing world Y is "up": without the negation a
    // block's bottom (low Y) would sample v≈0 (sprite top) and its top would sample v≈1 (sprite bottom) —
    // upside down. Top/bottom faces (+Y/-Y) never carry Y in either axis, so they pass through unchanged.
    private static Vector3D<float> MakeUv(in FaceDesc face, int u, int v, float layer)
    {
        if (face.U == 1) return new Vector3D<float>(v, -u, layer);
        if (face.V == 1) return new Vector3D<float>(u, -v, layer);
        return new Vector3D<float>(u, v, layer);
    }

    private static Vector3D<float> MakePos(in FaceDesc face, int fp, int u, int v)
    {
        float x, y, z;
        if (face.D == 0)      { x = fp; y = u; z = v; }
        else if (face.D == 1) { y = fp; x = u; z = v; }
        else                  { z = fp; x = u; y = v; }
        return new(x, y, z);
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

    /// <summary>Greedy-merge key for one visible face cell: which block, and (only meaningful for
    /// block types with HasOrientedTexture) which texture role this face plays for that voxel's
    /// orientation. default(MaskCell) == Air/Side, used as the mask's "empty" sentinel.</summary>
    private readonly struct MaskCell : IEquatable<MaskCell>
    {
        public readonly BlockId Id;
        public readonly FaceRole Role;

        public MaskCell(BlockId id, FaceRole role) { Id = id; Role = role; }

        public bool Equals(MaskCell other) => Id == other.Id && Role == other.Role;
        public override bool Equals(object? obj) => obj is MaskCell m && Equals(m);
        public override int GetHashCode() => HashCode.Combine(Id, Role);
        public static bool operator ==(MaskCell a, MaskCell b) => a.Equals(b);
        public static bool operator !=(MaskCell a, MaskCell b) => !a.Equals(b);
    }
}

/// <summary>One render layer's faces: its vertices, triangle indices into them, and each quad's block (four vertices
/// apiece, in order), whose colour and opacity the renderer looks up (see ChunkQuad).</summary>
public readonly record struct LayerMesh(List<Vertex> Vertices, List<uint> Indices, List<BlockId> Blocks);

/// <summary>A meshed chunk's faces, one <see cref="LayerMesh"/> per <see cref="RenderLayer"/>. The lists are
/// <see cref="GreedyMesher"/>'s reused scratch buffers: consume them before it meshes again.</summary>
public readonly record struct ChunkMeshLayers(LayerMesh Opaque, LayerMesh Cutout, LayerMesh Translucent);
