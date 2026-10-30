using ClearSkies.Engine.Voxels;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>An unloaded chunk's arrays go to the next chunk.</summary>
public class ChunkDataPoolTests
{
    private static BlockId Solid => (BlockId)1;

    [Fact]
    public void ReusedArraysStartFilled()
    {
        var old = new ChunkData();
        old.Set(0, 0, 0, Solid);
        old.Set(1, 0, 0, BlockId.Air);
        old.Release();

        var next = new ChunkData();
        next.Set(1, 1, 1, Solid);
        Assert.Equal(BlockId.Air, next.Get(0, 0, 0)); // a reused array is refilled, not left with the old blocks
        Assert.Equal(Solid, next.Get(1, 1, 1));
    }
}
