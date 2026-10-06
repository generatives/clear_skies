using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;
using Xunit;

namespace ClearSkies.Tests;

public class TreeTests
{
    [Fact]
    public void Trees_and_cacti_suit_their_ground_and_height()
    {
        static IEnumerable<Trees.Kind> On(BlockId ground, int top) =>
            Enumerable.Range(0, 20000).Select(i => Trees.At(ground, i, top, -i * 7, 0.8f, 99))
                      .Where(k => k != Trees.Kind.None).Distinct();

        Assert.Equal(new[] { Trees.Kind.Oak }, On(BlockId.Grass, 200));
        Assert.Equal(new[] { Trees.Kind.Pine }, On(BlockId.Grass, 600));
        Assert.Equal(new[] { Trees.Kind.Pine }, On(BlockId.Snow, 800));
        Assert.Equal(new[] { Trees.Kind.Cactus }, On(BlockId.Sand, 50));
        Assert.Empty(On(BlockId.Rock, 300));
        Assert.Empty(On(BlockId.Dirt, 300));
    }

    [Fact]
    public void Leaves_keep_their_faces_against_each_other()
    {
        Assert.False(BlockRegistry.Get(BlockId.Leaves).HidesFaceOf(BlockId.Leaves));
        Assert.True(BlockRegistry.Get(BlockId.Glass).HidesFaceOf(BlockId.Glass));
    }

    [Theory]
    [InlineData(Trees.Kind.Oak)]
    [InlineData(Trees.Kind.Pine)]
    [InlineData(Trees.Kind.Cactus)]
    public void A_grown_tree_stands_on_its_top_within_its_reach(Trees.Kind kind)
    {
        for (ulong h = 0; h < 50; h++)
        {
            var data = new ChunkData();
            const int x = 16, z = 16, top = 0;
            Trees.Grow(data, originY: 0, kind, x, top, z, h * 0x9E3779B97F4A7C15UL);
            var stem = kind == Trees.Kind.Cactus ? BlockId.Cactus : BlockId.Log;
            Assert.Equal(stem, data.Get(x, top + 1, z));
            for (int ly = 0; ly < ChunkData.Size; ly++)
            for (int lz = 0; lz < ChunkData.Size; lz++)
            for (int lx = 0; lx < ChunkData.Size; lx++)
            {
                if (data.Get(lx, ly, lz) == BlockId.Air) continue;
                Assert.InRange(lx, x - Trees.Reach, x + Trees.Reach);
                Assert.InRange(lz, z - Trees.Reach, z + Trees.Reach);
                Assert.InRange(ly, top + 1, top + Trees.MaxHeight);
            }
        }
    }
}
