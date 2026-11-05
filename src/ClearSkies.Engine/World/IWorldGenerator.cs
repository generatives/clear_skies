using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Generation;

public interface IWorldGenerator
{
    /// <summary>Fills chunk <paramref name="pos"/> of level <paramref name="level"/>: each voxel 2^level blocks on a
    /// side, in that level's chunks. Level 0 is full detail; a higher level is distant terrain, roughly what shrinking
    /// the full chunks under it would make (see <see cref="Voxels.CoarseTerrain"/>): a voxel holds the ground that fills
    /// at least half of it, and plants and trees show as they would from afar.</summary>
    void Generate(ChunkData data, ChunkPosition pos, int level = 0);

    /// <summary>The layers of chunk column (<paramref name="chunkX"/>, <paramref name="chunkZ"/>) that
    /// <see cref="Generate"/> may put blocks in, as bits: bit i is layer <paramref name="minChunkY"/> + i. It may
    /// include layers that turn out to be air, but must not leave out any that don't. Called for every column in view,
    /// so it should be cheap: bounds, not generation.</summary>
    ulong ColumnLayers(int chunkX, int chunkZ, int minChunkY);
}
