using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Voxels;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>How <see cref="LodTerrainSystem.Shrink"/> turns 2x2x2 blocks into one coarse cell.</summary>
public class LodShrinkTests
{
    private static BlockId ShrinkCell(params (int k, BlockId id)[] blocks)
    {
        var full = new ChunkData();
        foreach (var (k, id) in blocks) full.Set(k & 1, (k >> 1) & 1, k >> 2, id);
        var coarse = new ChunkData();
        LodTerrainSystem.Shrink(full, coarse, 0, 0, 0);
        return coarse.Get(0, 0, 0);
    }

    [Fact]
    public void AnyCubeMakesTheCellSolid() => Assert.Equal(BlockId.Stone, ShrinkCell((5, BlockId.Stone)));

    [Fact]
    public void MostCommonOpaqueCubeWins()
        => Assert.Equal(BlockId.Dirt, ShrinkCell((0, BlockId.Stone), (1, BlockId.Dirt), (2, BlockId.Dirt)));

    [Fact]
    public void OpaqueBeatsSeeThrough()
        => Assert.Equal(BlockId.Stone, ShrinkCell((0, BlockId.Water), (1, BlockId.Water), (2, BlockId.Stone)));

    [Fact]
    public void SeeThroughCubeKeptWithoutOpaque() => Assert.Equal(BlockId.Glass, ShrinkCell((3, BlockId.Glass)));

    [Fact]
    public void PlantsAndModelsAreLeftOut()
        => Assert.Equal(BlockId.Air, ShrinkCell((0, BlockId.TallGrass), (1, BlockId.Lever)));

    [Fact]
    public void UniformChunkFillsItsOctant()
    {
        var full = new ChunkData();
        for (int z = 0; z < ChunkData.Size; z++) for (int y = 0; y < ChunkData.Size; y++) for (int x = 0; x < ChunkData.Size; x++)
            full.Set(x, y, z, BlockId.Stone);
        full.Compact();
        var coarse = new ChunkData();
        LodTerrainSystem.Shrink(full, coarse, 16, 0, 16);
        Assert.Equal(BlockId.Stone, coarse.Get(16, 0, 16));
        Assert.Equal(BlockId.Stone, coarse.Get(31, 15, 31));
        Assert.Equal(BlockId.Air, coarse.Get(15, 0, 16));
        Assert.Equal(BlockId.Air, coarse.Get(16, 16, 16));
    }
}
