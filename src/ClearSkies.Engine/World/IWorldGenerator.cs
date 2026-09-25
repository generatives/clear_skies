using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Generation;

public interface IWorldGenerator
{
    void Generate(ChunkData data, ChunkPosition pos);

    /// <summary>The layers of chunk column (<paramref name="chunkX"/>, <paramref name="chunkZ"/>) that
    /// <see cref="Generate"/> may put blocks in, as bits: bit i is layer <paramref name="minChunkY"/> + i. It may
    /// include layers that turn out to be air, but must not leave out any that don't. Called for every column in view,
    /// so it should be cheap: bounds, not generation.</summary>
    ulong ColumnLayers(int chunkX, int chunkZ, int minChunkY);
}
