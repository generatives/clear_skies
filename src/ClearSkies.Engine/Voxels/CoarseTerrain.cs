namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Makes coarse terrain for a level of distant terrain (see <see cref="ECS.ChunkLoadSystem.SetLevels"/>): each of its
/// voxels is a 2x2x2 cell of the level below it, so a coarse chunk covers eight of that level's chunks. A level two up
/// is shrunk from level one, and so on.
/// </summary>
public static class CoarseTerrain
{
    private const int S = ChunkData.Size;

    /// <summary>Shrinks <paramref name="chunk"/> (at <paramref name="pos"/>, in its level's chunks) into the chunk of
    /// the next level over it, in <paramref name="into"/> (made there if it isn't yet).</summary>
    public static void ShrinkInto(Dictionary<ChunkPosition, ChunkData> into, ChunkPosition pos, ChunkData chunk,
                                  bool majority = true, bool plants = false)
    {
        var at = new ChunkPosition(pos.X >> 1, pos.Y >> 1, pos.Z >> 1);
        if (!into.TryGetValue(at, out var coarse)) into[at] = coarse = new ChunkData();
        Shrink(chunk, coarse, (pos.X & 1) * 16, (pos.Y & 1) * 16, (pos.Z & 1) * 16, majority, plants);
    }

    /// <summary>Shrinks full chunk <paramref name="full"/> into the 16³ octant of <paramref name="coarse"/> at
    /// (ox, oy, oz). Model blocks are left out, and plants unless <paramref name="plants"/> (then an open cell keeps one, see below). By <paramref name="majority"/>: a cell is filled when at least
    /// 4 of its 8 blocks are cubes, with the more common kind (opaque or see-through) and its most common block, so coarse
    /// surfaces round to the nearest cell and meet full detail flush on average; thin things (a fence of cubes, a
    /// one-block ledge) may go, except leaves (<see cref="BlockDef.ShowsInnerFaces"/>), which fill a cell with
    /// <see cref="LeavesToFill"/> of its blocks, so trees stay about the size they are. Otherwise any cube fills the cell: the most common opaque one, else the most common
    /// see-through one. Either way blocks count by how many of their faces show (aren't against an opaque cube; past
    /// the full chunk counts as showing), and only buried blocks count when none of that kind shows, so the cell takes
    /// what's seen: grass on dirt stays grass, a cliff face or an overhang's underside the stone it shows.</summary>
    public static void Shrink(ChunkData full, ChunkData coarse, int ox, int oy, int oz, bool majority = true,
                              bool plants = false)
    {
        if (full.IsUniform(out var only))
        {
            if (!IsCube(only)) return;
            for (int z = 0; z < 16; z++) for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
                coarse.Set(ox + x, oy + y, oz + z, only);
            return;
        }
        var cube = CubeTable;
        Span<BlockId> cell = stackalloc BlockId[8];
        Span<int> faces = stackalloc int[8]; // how many faces each of the cell's blocks shows
        Span<BlockId> plantOn = stackalloc BlockId[16]; // per x: a plant on top of the cell below (for the cell above)
        for (int z = 0; z < 16; z++)
        {
            plantOn.Clear();
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                bool anyCube = false, same = true;
                for (int k = 0; k < 8; k++)
                {
                    var id = cell[k] = full.Get(2 * x + (k & 1), 2 * y + ((k >> 1) & 1), 2 * z + (k >> 2));
                    anyCube |= cube[(byte)id];
                    same &= id == cell[0];
                }
                // Most cells are all one block (air, or rock under the ground): without a cube that's air, and all one
                // cube that cube, whichever rule; neither needs the faces counted.
                BlockId pick;
                if (!anyCube) pick = BlockId.Air;
                else if (same) pick = cell[0];
                else
                {
                    for (int k = 0; k < 8; k++)
                        faces[k] = cube[(byte)cell[k]] ? ShownFaces(full, 2 * x + (k & 1), 2 * y + ((k >> 1) & 1), 2 * z + (k >> 2)) : 0;
                    pick = majority ? ByMajority(cell, faces) : ByAny(cell, faces);
                }
                if (pick != BlockId.Air) coarse.Set(ox + x, oy + y, oz + z, pick);
                if (!plants) continue;
                // Plants (grass, mushrooms): a cell left open keeps the commonest one in it, or failing that one that
                // grew on top of the cell below, whose ground filled it; so coarse ground stays as grassy as full detail.
                if (pick == BlockId.Air)
                {
                    var plant = MostCommonPlant(cell, upperOnly: false);
                    if (plant == BlockId.Air) plant = plantOn[x];
                    if (plant != BlockId.Air) coarse.Set(ox + x, oy + y, oz + z, plant);
                    plantOn[x] = BlockId.Air;
                }
                else plantOn[x] = same ? BlockId.Air : MostCommonPlant(cell, upperOnly: true); // one cube: no plant in it
            }
        }
    }

    // Per block id: a full cube; an opaque one (hides what's against it).
    private static readonly bool[] CubeTable = BuildTable(id => IsCube(id));
    private static readonly bool[] OpaqueCubeTable = BuildTable(id => IsCube(id) && !BlockRegistry.Get(id).Transparent);
    private static bool[] BuildTable(Func<BlockId, bool> of)
    {
        var t = new bool[256];
        for (int i = 0; i < t.Length; i++) t[i] = of((BlockId)i);
        return t;
    }

    private static BlockId MostCommonPlant(ReadOnlySpan<BlockId> cell, bool upperOnly)
    {
        BlockId best = BlockId.Air;
        int bestCount = 0;
        for (int i = 0; i < cell.Length; i++)
        {
            if (upperOnly && ((i >> 1) & 1) == 0) continue;
            var id = cell[i];
            if (id == BlockId.Air || !BlockRegistry.Get(id).IsCross) continue;
            int n = 0;
            for (int j = 0; j < cell.Length; j++) if (cell[j] == id) n++;
            if (n > bestCount) { best = id; bestCount = n; }
        }
        return best;
    }

    private static BlockId ByAny(ReadOnlySpan<BlockId> cell, ReadOnlySpan<int> faces)
    {
        var pick = Seen(cell, faces, opaque: true);
        return pick != BlockId.Air ? pick : Seen(cell, faces, opaque: false);
    }

    /// <summary>The cube of a kind with the most faces showing in a cell (a tie going to the commoner there), or failing
    /// any showing, the most common of all its blocks.</summary>
    private static BlockId Seen(ReadOnlySpan<BlockId> cell, ReadOnlySpan<int> faces, bool opaque)
    {
        BlockId best = BlockId.Air;
        int bestScore = 0;
        for (int i = 0; i < cell.Length; i++)
        {
            var id = cell[i];
            if (faces[i] == 0 || BlockRegistry.Get(id).Transparent == opaque) continue;
            int score = 0;
            for (int j = 0; j < cell.Length; j++) if (cell[j] == id) score += 8 * faces[j] + 1;
            if (score > bestScore) { best = id; bestScore = score; }
        }
        return best != BlockId.Air ? best : MostCommon(cell, opaque);
    }

    /// <summary>How many faces of full chunk block (x, y, z) aren't against an opaque cube (past the chunk: showing).</summary>
    private static int ShownFaces(ChunkData full, int x, int y, int z)
    {
        var opaque = OpaqueCubeTable;
        return Shows(x - 1, y, z) + Shows(x + 1, y, z) + Shows(x, y - 1, z) + Shows(x, y + 1, z) + Shows(x, y, z - 1)
             + Shows(x, y, z + 1);

        int Shows(int x, int y, int z)
            => (uint)x >= S || (uint)y >= S || (uint)z >= S || !opaque[(byte)full.Get(x, y, z)] ? 1 : 0;
    }

    /// <summary>Leaf blocks that fill a cell short of a majority of cubes. With any one, trees grew: a canopy seen
    /// from above covered 1.2 to 1.9 times what it does in full detail, and an autumn birch's orange crown shrank to
    /// show the grass under it as full detail swapped in. With 3, coarse canopies cover what full detail's do (0.9 to
    /// 1.0 times, measured over generated forests).</summary>
    private const int LeavesToFill = 3;

    private static BlockId ByMajority(ReadOnlySpan<BlockId> cell, ReadOnlySpan<int> faces)
    {
        int opaque = 0, see = 0;
        int leaves = 0;
        foreach (var id in cell)
        {
            if (!IsCube(id)) continue;
            var def = BlockRegistry.Get(id);
            if (def.Transparent) { see++; if (def.ShowsInnerFaces) leaves++; }
            else opaque++;
        }
        if (opaque + see >= 4) return Seen(cell, faces, opaque: opaque >= see);
        return leaves >= LeavesToFill ? MostCommon(cell, opaque: false, leavesOnly: true) : BlockId.Air;
    }

    private static bool IsCube(BlockId id) => id != BlockId.Air && BlockRegistry.Get(id).IsFullCube;

    private static BlockId MostCommon(ReadOnlySpan<BlockId> cell, bool opaque, bool leavesOnly = false)
    {
        BlockId best = BlockId.Air;
        int bestCount = 0;
        for (int i = 0; i < cell.Length; i++)
        {
            var id = cell[i];
            if (!IsCube(id) || BlockRegistry.Get(id).Transparent == opaque) continue;
            if (leavesOnly && !BlockRegistry.Get(id).ShowsInnerFaces) continue;
            int n = 0;
            for (int j = 0; j < cell.Length; j++) if (cell[j] == id) n++;
            if (n > bestCount) { best = id; bestCount = n; }
        }
        return best;
    }
}
