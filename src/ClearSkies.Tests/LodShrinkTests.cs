using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Voxels;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>How <see cref="LodTerrainSystem.Shrink"/> turns 2x2x2 blocks into one coarse cell.</summary>
public class LodShrinkTests
{
    private static BlockId ShrinkCell(bool majority, params (int k, BlockId id)[] blocks)
    {
        var full = new ChunkData();
        foreach (var (k, id) in blocks) full.Set(k & 1, (k >> 1) & 1, k >> 2, id);
        var coarse = new ChunkData();
        LodTerrainSystem.Shrink(full, coarse, 0, 0, 0, majority);
        return coarse.Get(0, 0, 0);
    }

    private static BlockId Any(params (int k, BlockId id)[] blocks) => ShrinkCell(false, blocks);
    private static BlockId Majority(params (int k, BlockId id)[] blocks) => ShrinkCell(true, blocks);

    [Fact]
    public void AnyCubeMakesTheCellSolid() => Assert.Equal(BlockId.Stone, Any((5, BlockId.Stone)));

    [Fact]
    public void MostCommonOpaqueCubeWins()
        => Assert.Equal(BlockId.Dirt, Any((0, BlockId.Stone), (1, BlockId.Dirt), (2, BlockId.Dirt)));

    [Fact]
    public void OpaqueBeatsSeeThrough()
        => Assert.Equal(BlockId.Stone, Any((0, BlockId.Water), (1, BlockId.Water), (2, BlockId.Stone)));

    [Fact]
    public void SeeThroughCubeKeptWithoutOpaque() => Assert.Equal(BlockId.Glass, Any((3, BlockId.Glass)));

    [Fact]
    public void AnOpenCellKeepsItsPlant()
    {
        var full = new ChunkData();
        full.Set(1, 0, 0, BlockId.TallGrass);
        var coarse = new ChunkData();
        LodTerrainSystem.Shrink(full, coarse, 0, 0, 0, majority: true, plants: true);
        Assert.Equal(BlockId.TallGrass, coarse.Get(0, 0, 0));
    }

    [Fact]
    public void APlantOnFilledGroundMovesUpACell()
    {
        // Ground fills the lower 3 blocks of a 2-block column pair, grass on top at y 3: the cell it's in (y 2-3) fills
        // by majority, so the grass goes in the open cell above.
        var full = new ChunkData();
        for (int x = 0; x < 2; x++) for (int z = 0; z < 2; z++) for (int y = 0; y < 3; y++) full.Set(x, y, z, BlockId.Dirt);
        full.Set(0, 3, 0, BlockId.ShortGrass);
        var coarse = new ChunkData();
        LodTerrainSystem.Shrink(full, coarse, 0, 0, 0, majority: true, plants: true);
        Assert.Equal(BlockId.Dirt, coarse.Get(0, 1, 0));
        Assert.Equal(BlockId.ShortGrass, coarse.Get(0, 2, 0));
    }

    [Fact]
    public void TheTopBlockWinsOverMoreBlocksUnderIt()
    {
        // Grass on dirt, the surface in the cell's upper layer: 4 dirt to 4 grass, but grass is what's seen.
        Assert.Equal(BlockId.Grass, Majority((0, BlockId.Dirt), (1, BlockId.Dirt), (4, BlockId.Dirt), (5, BlockId.Dirt),
                                            (2, BlockId.Grass), (3, BlockId.Grass), (6, BlockId.Dirt), (7, BlockId.Grass)));
        // The surface lower down: grass on the bottom layer, nothing over it.
        Assert.Equal(BlockId.Grass, Majority((0, BlockId.Grass), (1, BlockId.Grass), (4, BlockId.Grass), (2, BlockId.Dirt)));
    }

    [Fact]
    public void BlocksUnderOpaqueCubesAreNotTops()
    {
        // Stone over sand: the sand is buried, the stone is the top.
        Assert.Equal(BlockId.Stone, Majority((0, BlockId.Sand), (1, BlockId.Sand), (4, BlockId.Sand), (5, BlockId.Sand),
                                            (2, BlockId.Stone), (3, BlockId.Stone), (6, BlockId.Stone), (7, BlockId.Stone)));
    }

    [Fact]
    public void PlantsAndModelsAreLeftOut()
        => Assert.Equal(BlockId.Air, Majority((0, BlockId.TallGrass), (1, BlockId.Lever), (2, BlockId.Stone), (3, BlockId.Stone)));

    [Fact]
    public void MajorityNeedsHalfTheBlocks()
    {
        Assert.Equal(BlockId.Air, Majority((0, BlockId.Stone), (1, BlockId.Stone), (2, BlockId.Dirt)));
        Assert.Equal(BlockId.Stone, Majority((0, BlockId.Stone), (1, BlockId.Stone), (2, BlockId.Dirt), (3, BlockId.Stone)));
    }

    [Fact]
    public void MajorityTakesTheCommonerKind()
    {
        Assert.Equal(BlockId.Water, Majority((0, BlockId.Water), (1, BlockId.Water), (2, BlockId.Water), (4, BlockId.Stone)));
        Assert.Equal(BlockId.Stone, Majority((0, BlockId.Water), (1, BlockId.Water), (2, BlockId.Stone), (4, BlockId.Stone)));
    }

    [Fact]
    public void LeavesFillACellAlone() => Assert.Equal(BlockId.Leaves, Majority((6, BlockId.Leaves)));

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
