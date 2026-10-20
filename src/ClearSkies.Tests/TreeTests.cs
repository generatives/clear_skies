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

        Assert.Equal(new[] { Trees.Kind.Oak }, On(BlockId.Grass, 500));
        Assert.Equal(new[] { Trees.Kind.Pine }, On(BlockId.Grass, 900));
        Assert.Equal(new[] { Trees.Kind.Pine }, On(BlockId.Snow, 1100));
        Assert.Equal(new[] { Trees.Kind.Cactus }, On(BlockId.Sand, 350));
        Assert.Empty(On(BlockId.Rock, 600));
        Assert.Empty(On(BlockId.Dirt, 600));
    }

    [Fact]
    public void Biomes_go_by_heat_and_wetness_and_set_what_grows()
    {
        Assert.Equal(Biome.Badlands, Biomes.Pick(1.2f, 0f));
        Assert.Equal(Biome.Desert, Biomes.Pick(0.8f, 0.3f));
        Assert.Equal(Biome.Autumn, Biomes.Pick(0.2f, 0.8f));
        Assert.Equal(Biome.Temperate, Biomes.Pick(0.55f, 0.5f));
        Assert.True(Biomes.Heat(200f, 0f) > Biomes.Heat(1200f, 0f)); // hotter deep down

        Assert.Equal(new[] { Trees.Kind.Birch }, Enumerable.Range(0, 20000)
            .Select(i => Trees.At(BlockId.Grass, i, 500, -i * 7, 0.8f, 99, Biome.Autumn))
            .Where(k => k != Trees.Kind.None).Distinct());
        Assert.Equal(BlockId.RedSand, ContinentTerrain.Block(500, 500, 0f, 0.5f, 0f, Biome.Badlands));
        Assert.True(Biomes.Tint(0.55f, 0.55f).A < 0.05f);                   // temperate keeps its green
        Assert.True(Biomes.Tint(0.3f, 0.75f).A > 0.8f);                     // autumn tints strongly
        float between = Biomes.Tint(0.43f, 0.65f).A;                        // and halfway it blends
        Assert.InRange(between, 0.2f, 0.6f);
        Assert.Equal(BlockId.Sand, ContinentTerrain.Block(500, 500, 0f, 0.5f, 0f, Biome.Desert));
    }

    [Fact]
    public void Cave_crystals_glow_and_come_in_sparse_clusters()
    {
        Assert.True(BlockRegistry.Get(BlockId.AmberCrystal).LightEmission > 0);
        Assert.True(BlockRegistry.Get(BlockId.GoldCrystal).LightEmission > 0);
        Assert.Equal(BlockId.AmberCrystal, Crystals.For(Biome.Badlands));
        Assert.Equal(BlockId.GoldCrystal, Crystals.For(Biome.Autumn));

        var lit = new List<(int X, int Z)>();
        for (int z = 0; z < 480; z++)
        for (int x = 0; x < 480; x++)
            if (Crystals.At(x, 300, z, 7)) lit.Add((x, z));
        Assert.InRange(lit.Count, 20, 480 * 480 / 500);                     // a few, not a sprinkle everywhere
        Assert.All(lit, a => Assert.Contains(lit, b => b != a                // and each beside others in its cluster
            && System.Math.Abs(a.X - b.X) <= 2 * Crystals.Radius && System.Math.Abs(a.Z - b.Z) <= 2 * Crystals.Radius));
    }

    [Fact]
    public void Leaves_keep_their_faces_against_each_other()
    {
        Assert.False(BlockRegistry.Get(BlockId.Leaves).HidesFaceOf(BlockId.Leaves));
        Assert.True(BlockRegistry.Get(BlockId.Glass).HidesFaceOf(BlockId.Glass));
    }

    [Fact]
    public void Leaves_let_light_through_but_are_packed_for_lighting_as_see_through()
    {
        var leaves = BlockRegistry.Get(BlockId.Leaves);
        Assert.False(leaves.BlocksLight);
        Assert.True(leaves.CatchesLight);
        Assert.True(BlockRegistry.Get(BlockId.Glass).CatchesLight);
        Assert.True(BlockRegistry.Get(BlockId.Lever).CatchesLight);
        Assert.False(BlockRegistry.Get(BlockId.TallGrass).CatchesLight);
        Assert.False(BlockRegistry.Get(BlockId.Stone).CatchesLight);

        var data = new ChunkData();
        data.Set(9, 20, 3, BlockId.Leaves);
        data.Set(1, 1, 1, BlockId.Stone);
        var packed = GridStore.Pack(data);
        Assert.Equal(0u, packed.Words[20 + 32 * 3]);                 // the leaf stops no rays
        Assert.NotNull(packed.SeeThrough);
        Assert.Equal(1u << 9, packed.SeeThrough![20 + 32 * 3]);         // but is marked as see-through
        Assert.Equal(1UL << (1 + 4 * (2 + 4 * 0)), packed.SeeThroughBricks);
        Assert.Null(GridStore.Pack(new ChunkData()).SeeThrough);
    }

    [Fact]
    public void Only_the_skin_of_a_pool_of_water_is_packed_for_lighting()
    {
        var data = new ChunkData();
        for (int z = 4; z <= 12; z++)
        for (int y = 4; y <= 12; y++)
        for (int x = 4; x <= 12; x++)
            data.Set(x, y, z, BlockId.Water);
        var bits = GridStore.Pack(data).SeeThrough!;
        static bool Has(uint[] b, int x, int y, int z) => ((b[y + 32 * z] >> x) & 1) == 1;
        Assert.True(Has(bits, 4, 8, 8));    // its side
        Assert.True(Has(bits, 8, 12, 8));   // its top
        Assert.False(Has(bits, 8, 8, 8));   // its inside
    }

    [Theory]
    [InlineData(Trees.Kind.Oak)]
    [InlineData(Trees.Kind.Pine)]
    [InlineData(Trees.Kind.Cactus)]
    [InlineData(Trees.Kind.Birch)]
    public void A_grown_tree_stands_on_its_top_within_its_reach(Trees.Kind kind)
    {
        for (ulong h = 0; h < 50; h++)
        {
            var data = new ChunkData();
            const int x = 16, z = 16, top = 0;
            Trees.Grow(data, originY: 0, kind, x, top, z, h * 0x9E3779B97F4A7C15UL);
            var stem = kind switch { Trees.Kind.Cactus => BlockId.Cactus, Trees.Kind.Birch => BlockId.BirchLog, _ => BlockId.Log };
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
