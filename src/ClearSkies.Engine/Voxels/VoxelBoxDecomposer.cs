using System.Numerics;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Greedy 3-D box decomposition of a chunk's solid blocks — the volumetric analogue of the 2-D
/// <see cref="GreedyMesher"/>. Produces a small set of axis-aligned boxes (local centre + size, in
/// block units, each homogeneous in <see cref="BlockId"/> so a caller can derive per-box mass from a
/// single block's <see cref="BlockDef.Weight"/>) that exactly cover the solid voxels, for use as
/// physics collision shapes. Not a minimal cover, but a large reduction from one box per block.
/// </summary>
public sealed class VoxelBoxDecomposer
{
    private readonly bool[] _consumed = new bool[ChunkData.Size * ChunkData.Size * ChunkData.Size];
    private bool _mergeBlockTypes;

    /// <summary>Returns boxes as (centre, size, block id) in chunk-local block units (centre relative
    /// to the chunk origin). Each box contains only one <see cref="BlockId"/> — unless
    /// <paramref name="mergeBlockTypes"/> is set, in which case boxes span any solid blocks and the id is just the
    /// first voxel's. Static terrain colliders use that: they don't need per-box mass, and merging across
    /// grass/dirt/stone layers cuts the box count (and so the BigCompound tree build) substantially.</summary>
    public List<(Vector3 center, Vector3 size, BlockId Id)> Decompose(ChunkData data, bool mergeBlockTypes = false)
    {
        int sz = ChunkData.Size;
        Array.Clear(_consumed, 0, _consumed.Length);
        var boxes = new List<(Vector3, Vector3, BlockId)>();
        _mergeBlockTypes = mergeBlockTypes;

        for (int z = 0; z < sz; z++)
        for (int y = 0; y < sz; y++)
        for (int x = 0; x < sz; x++)
        {
            if (_consumed[Idx(x, y, z)] || !IsSolid(data, x, y, z)) continue;
            BlockId id = data.Get(x, y, z);

            // Grow along X.
            int w = 1;
            while (x + w < sz && !_consumed[Idx(x + w, y, z)] && Matches(data, x + w, y, z, id)) w++;

            // Grow along Y while the whole [x,x+w) row stays this id and unconsumed.
            int h = 1;
            while (y + h < sz && RowFree(data, x, w, y + h, z, id)) h++;

            // Grow along Z while the whole [x,x+w)×[y,y+h) slab stays this id and unconsumed.
            int d = 1;
            while (z + d < sz && SlabFree(data, x, w, y, h, z + d, id)) d++;

            for (int dz = 0; dz < d; dz++)
            for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++)
                _consumed[Idx(x + dx, y + dy, z + dz)] = true;

            boxes.Add((new Vector3(x + w * 0.5f, y + h * 0.5f, z + d * 0.5f), new Vector3(w, h, d), id));
        }

        return boxes;
    }

    private bool RowFree(ChunkData data, int x, int w, int y, int z, BlockId id)
    {
        for (int dx = 0; dx < w; dx++)
            if (_consumed[Idx(x + dx, y, z)] || !Matches(data, x + dx, y, z, id)) return false;
        return true;
    }

    private bool SlabFree(ChunkData data, int x, int w, int y, int h, int z, BlockId id)
    {
        for (int dy = 0; dy < h; dy++)
            if (!RowFree(data, x, w, y + dy, z, id)) return false;
        return true;
    }

    private bool Matches(ChunkData data, int x, int y, int z, BlockId id) =>
        _mergeBlockTypes ? IsSolid(data, x, y, z) : data.Get(x, y, z) == id;
    private static bool IsSolid(ChunkData data, int x, int y, int z) => BlockRegistry.Get(data.Get(x, y, z)).IsSolid;
    private static int  Idx(int x, int y, int z) => ChunkData.Index(x, y, z);
}
