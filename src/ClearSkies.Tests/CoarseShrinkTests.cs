using ClearSkies.Engine.Voxels;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>How <see cref="CoarseTerrain.Shrink"/> turns 2x2x2 blocks into one coarse cell.</summary>
public class CoarseShrinkTests
{
    private static BlockId ShrinkCell(bool majority, params (int k, BlockId id)[] blocks)
    {
        var full = new ChunkData();
        // A cell in the middle of the chunk, so its blocks' neighbours are in it (past the chunk counts as showing).
        foreach (var (k, id) in blocks) full.Set(16 + (k & 1), 16 + ((k >> 1) & 1), 16 + (k >> 2), id);
        var coarse = new ChunkData();
        CoarseTerrain.Shrink(full, coarse, 0, 0, 0, majority);
        return coarse.Get(8, 8, 8);
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
        CoarseTerrain.Shrink(full, coarse, 0, 0, 0, majority: true, plants: true);
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
        CoarseTerrain.Shrink(full, coarse, 0, 0, 0, majority: true, plants: true);
        Assert.Equal(BlockId.Dirt, coarse.Get(0, 1, 0));
        Assert.Equal(BlockId.ShortGrass, coarse.Get(0, 2, 0));
    }

    /// <summary>The cell at the middle of a chunk of flat layers (block by height, air where null), shrunk.</summary>
    private static BlockId Layers(Func<int, BlockId> at)
    {
        var full = new ChunkData();
        for (int y = 0; y < ChunkData.Size; y++)
        {
            var id = at(y);
            if (id == BlockId.Air) continue;
            for (int z = 0; z < ChunkData.Size; z++) for (int x = 0; x < ChunkData.Size; x++) full.Set(x, y, z, id);
        }
        var coarse = new ChunkData();
        CoarseTerrain.Shrink(full, coarse, 0, 0, 0);
        return coarse.Get(8, 8, 8);
    }

    [Fact]
    public void GroundShowsItsTopBlock()
        // Grass on dirt, the cell spanning y 16-17: 4 dirt to 4 grass, but only the grass shows.
        => Assert.Equal(BlockId.Grass, Layers(y => y < 17 ? BlockId.Dirt : y == 17 ? BlockId.Grass : BlockId.Air));

    [Fact]
    public void AnUndersideShowsWhatsUnderneath()
        // An overhang: stone under dirt, open below; seen from below it's stone.
        => Assert.Equal(BlockId.Stone, Layers(y => y < 16 ? BlockId.Air : y == 16 ? BlockId.Stone : BlockId.Dirt));

    [Fact]
    public void BuriedBlocksDontCount()
        // Sand under stone at the surface: the stone shows.
        => Assert.Equal(BlockId.Stone, Layers(y => y == 16 ? BlockId.Sand : y <= 17 ? BlockId.Stone : BlockId.Air));

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
        CoarseTerrain.Shrink(full, coarse, 16, 0, 16);
        Assert.Equal(BlockId.Stone, coarse.Get(16, 0, 16));
        Assert.Equal(BlockId.Stone, coarse.Get(31, 15, 31));
        Assert.Equal(BlockId.Air, coarse.Get(15, 0, 16));
        Assert.Equal(BlockId.Air, coarse.Get(16, 16, 16));
    }
}
