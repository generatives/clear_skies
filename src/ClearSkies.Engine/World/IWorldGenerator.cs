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

    /// <summary>Where the view is, as chunk columns, when it moves: what the generator keeps of the ground nearest them
    /// is kept longest.</summary>
    void FocusOn(IReadOnlyList<(int X, int Z)> columns) { }
}

/// <summary>A generator that can make a level of distant terrain's chunk itself, without the full chunks under it (see
/// <see cref="ECS.VoxelsStage"/>).</summary>
public interface ICoarseWorldGenerator
{
    /// <summary>Fills level <paramref name="level"/>'s chunk <paramref name="pos"/> (each voxel 2^level blocks on a
    /// side, in that level's chunks) with roughly what shrinking the full chunks under it would (see
    /// <see cref="Voxels.CoarseTerrain"/>): a voxel holds the ground that fills at least half of it, and plants and
    /// trees show as they would from afar.</summary>
    void GenerateCoarse(ChunkData data, ChunkPosition pos, int level);
}
