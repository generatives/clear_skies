using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Generation;

public interface IWorldGenerator
{
    void Generate(ChunkData data, ChunkPosition pos);
}
