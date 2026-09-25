using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// The sky world's <see cref="ITerrainLayout"/>: a region is one <see cref="RegionGrid"/> cell, and the chunks that
/// may hold terrain are the ones <see cref="SkyWorldGenerator.Generate"/>'s bounding-volume check doesn't reject,
/// worked out from the cell's island definitions alone (no noise).
/// </summary>
public sealed class SkyTerrainLayout : ITerrainLayout
{
    private const int S = ChunkData.Size;

    private readonly ulong _seed;

    public SkyTerrainLayout(ulong seed) => _seed = seed;

    public int RegionChunkShift => RegionGrid.CellShift - ChunkData.Shift;

    public IReadOnlyList<TerrainColumn> TerrainColumns(int regionX, int regionZ)
    {
        Span<IslandDef> islands = stackalloc IslandDef[4];
        int count = RegionGrid.ResolveIslandsForCell(_seed, regionX, regionZ, islands);
        if (count == 0) return Array.Empty<TerrainColumn>();

        int regionChunks = 1 << RegionChunkShift;
        int cx0 = regionX * regionChunks, cz0 = regionZ * regionChunks;
        var columns = new Dictionary<(int x, int z), (int minY, int maxY)>();

        for (int i = 0; i < count; i++)
        {
            var (reach, yMin, yMax) = SkyWorldGenerator.IslandBounds(in islands[i]);
            float centerX = islands[i].CenterX, centerZ = islands[i].CenterZ;

            // Same tests as Generate: the chunk's y span overlaps [yMin, yMax], and its horizontal box comes within
            // reach of the island's centre.
            int minY = (int)MathF.Ceiling((yMin - S) / S), maxY = (int)MathF.Floor(yMax / S);
            int xLo = System.Math.Max(cx0, (int)MathF.Floor((centerX - reach) / S) - 1);
            int xHi = System.Math.Min(cx0 + regionChunks - 1, (int)MathF.Floor((centerX + reach) / S) + 1);
            int zLo = System.Math.Max(cz0, (int)MathF.Floor((centerZ - reach) / S) - 1);
            int zHi = System.Math.Min(cz0 + regionChunks - 1, (int)MathF.Floor((centerZ + reach) / S) + 1);

            for (int cz = zLo; cz <= zHi; cz++)
            for (int cx = xLo; cx <= xHi; cx++)
            {
                float dx = MathF.Max(0f, MathF.Abs(centerX - (cx * S + S * 0.5f)) - S * 0.5f);
                float dz = MathF.Max(0f, MathF.Abs(centerZ - (cz * S + S * 0.5f)) - S * 0.5f);
                if (dx * dx + dz * dz > reach * reach) continue;

                columns[(cx, cz)] = columns.TryGetValue((cx, cz), out var c)
                    ? (System.Math.Min(c.minY, minY), System.Math.Max(c.maxY, maxY))
                    : (minY, maxY);
            }
        }

        var result = new TerrainColumn[columns.Count];
        int n = 0;
        foreach (var ((x, z), (minY, maxY)) in columns) result[n++] = new TerrainColumn(x, z, minY, maxY);
        return result;
    }
}
