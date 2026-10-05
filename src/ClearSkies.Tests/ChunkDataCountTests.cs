using ClearSkies.Engine.Voxels;
using Xunit;

namespace ClearSkies.Tests;

/// <summary><see cref="ChunkData.HasAnyNonAir"/> and <see cref="ChunkData.HasAnyColliding"/> come from counts kept up
/// to date by every write; these check them against a full scan of the blocks.</summary>
public class ChunkDataCountTests
{
    private static void AssertMatchesScan(ChunkData data)
    {
        bool nonAir = false, colliding = false;
        int s = ChunkData.Size;
        for (int z = 0; z < s; z++) for (int y = 0; y < s; y++) for (int x = 0; x < s; x++)
        {
            var id = data.Get(x, y, z);
            nonAir |= id != BlockId.Air;
            colliding |= BlockRegistry.Get(id).Collides;
        }
        Assert.Equal(nonAir, data.HasAnyNonAir());
        Assert.Equal(colliding, data.HasAnyColliding());
    }

    [Fact]
    public void Uniform_chunks()
    {
        var air = new ChunkData();
        Assert.False(air.HasAnyNonAir());
        Assert.False(air.HasAnyColliding());

        var stone = new ChunkData();
        int s = ChunkData.Size;
        for (int z = 0; z < s; z++) for (int y = 0; y < s; y++) for (int x = 0; x < s; x++) stone.Set(x, y, z, BlockId.Stone);
        stone.Compact();
        Assert.True(stone.IsUniform(out _));
        Assert.True(stone.HasAnyNonAir());
        Assert.True(stone.HasAnyColliding());
    }

    [Fact]
    public void A_chunk_of_passable_blocks_has_blocks_but_nothing_colliding()
    {
        var data = new ChunkData();
        data.Set(3, 4, 5, BlockId.Lever);
        Assert.True(data.HasAnyNonAir());
        Assert.False(data.HasAnyColliding());
    }

    [Fact]
    public void Counts_follow_random_edits_digging_out_and_loading()
    {
        var rng = new Random(1234);
        BlockId[] ids = { BlockId.Air, BlockId.Stone, BlockId.Lever, BlockId.Dirt, BlockId.Air };
        var data = new ChunkData();
        // Edits confined to a small box, so blocks get overwritten and removed as well as added.
        for (int i = 0; i < 5000; i++)
        {
            data.Set(rng.Next(4), rng.Next(4), rng.Next(4), ids[rng.Next(ids.Length)]);
            if (i % 50 == 0) AssertMatchesScan(data);
        }
        AssertMatchesScan(data);

        // Dug out to nothing: the array stays, but there are no blocks.
        for (int z = 0; z < 4; z++) for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) data.Set(x, y, z, BlockId.Air);
        Assert.False(data.IsUniform(out _));
        AssertMatchesScan(data);

        // A uniform stone chunk broken into: counts start from its uniform block.
        var stone = new ChunkData();
        stone.LoadBlockBytes(Enumerable.Repeat((byte)BlockId.Stone, ChunkData.Volume).ToArray());
        stone.Compact();
        stone.Set(0, 0, 0, BlockId.Air);
        AssertMatchesScan(stone);
        for (int z = 0; z < ChunkData.Size; z++) for (int y = 0; y < ChunkData.Size; y++) for (int x = 0; x < ChunkData.Size; x++)
            stone.Set(x, y, z, BlockId.Lever);
        AssertMatchesScan(stone);

        // Loaded from bytes: recounted.
        var mixed = new ChunkData();
        mixed.Set(1, 2, 3, BlockId.Stone);
        mixed.Set(4, 5, 6, BlockId.Lever);
        var loaded = new ChunkData();
        loaded.LoadBlockBytes(mixed.BlocksAsBytes());
        AssertMatchesScan(loaded);
        var levers = new ChunkData();
        levers.LoadBlockBytes(stone.BlocksAsBytes());
        AssertMatchesScan(levers);
        Assert.False(levers.HasAnyColliding());
    }

    [Fact]
    public void A_released_chunk_reads_as_air()
    {
        var data = new ChunkData();
        data.Set(1, 1, 1, BlockId.Stone);
        data.Release();
        Assert.False(data.HasAnyNonAir());
        Assert.False(data.HasAnyColliding());
    }
}
