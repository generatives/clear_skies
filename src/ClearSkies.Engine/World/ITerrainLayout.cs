namespace ClearSkies.Engine.Generation;

/// <summary>A chunk column (x, z) whose chunks <see cref="MinY"/> to <see cref="MaxY"/> (inclusive) may hold
/// generated terrain.</summary>
public readonly record struct TerrainColumn(int X, int Z, int MinY, int MaxY);

/// <summary>
/// Where a world's generated terrain can be, known without generating it, so <c>ChunkLoadSystem</c> only loads
/// chunks that may hold something instead of every chunk in a box of sky. The world is divided into regions of
/// 2^<see cref="RegionChunkShift"/> x 2^<see cref="RegionChunkShift"/> chunk columns (unbounded in y).
/// </summary>
public interface ITerrainLayout
{
    /// <summary>log2 of a region's width in chunks.</summary>
    int RegionChunkShift { get; }

    /// <summary>Every chunk column of region (<paramref name="regionX"/>, <paramref name="regionZ"/>) that may hold
    /// terrain. Conservative: it may list chunks that generate empty, but never leaves out one that doesn't.</summary>
    IReadOnlyList<TerrainColumn> TerrainColumns(int regionX, int regionZ);
}
