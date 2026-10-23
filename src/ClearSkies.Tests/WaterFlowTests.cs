using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Flowing water (<see cref="WaterFlow"/>, <see cref="WaterLevel"/>): how it falls, spreads, runs downhill,
/// drains and makes sources.</summary>
public class WaterFlowTests
{
    /// <summary>A volume with a stone floor at y = 0 from -20 to 20, and its water.</summary>
    private sealed class Pool : IDisposable
    {
        private readonly World _world = new();
        public readonly ChunkVolume Volume;
        public readonly WaterFlow Flow;

        public Pool()
        {
            Volume = new ChunkVolume(_world.CreateEntity(), _world);
            Volume.FillBox(new(-20, 0, -20), new(20, 0, 20), BlockId.Stone, BlockOrientation.Upright);
            Flow = new WaterFlow(Volume);
        }

        public void Set(int x, int y, int z, BlockId block, BlockOrientation? state = null)
        {
            Volume.SetBlock(x, y, z, block, state ?? BlockOrientation.Upright);
            Flow.Wake(new Vector3D<int>(x, y, z));
        }

        /// <summary>Steps until nothing changes (or a limit), applying each step's changes.</summary>
        public int Settle(int limit = 200)
        {
            for (int i = 0; i < limit; i++)
            {
                var changes = Flow.Step();
                if (changes.Count == 0) return i;
                foreach (var c in changes) Volume.SetBlock(c.Cell.X, c.Cell.Y, c.Cell.Z, c.Block, c.State);
            }
            throw new Exception("water never settled");
        }

        public BlockId Block(int x, int y, int z) => Volume.GetBlock(x, y, z);
        public BlockOrientation State(int x, int y, int z) => Volume.GetOrientation(x, y, z);

        public void Dispose() => _world.Dispose();
    }

    [Fact]
    public void Levels_round_trip_and_upright_water_is_a_source()
    {
        Assert.True(WaterLevel.IsSource(BlockOrientation.Upright));
        Assert.Equal(8, WaterLevel.Height(WaterLevel.Source));
        for (int level = 1; level <= WaterLevel.Weakest; level++)
        {
            var s = WaterLevel.Flowing(level);
            Assert.Equal(level, WaterLevel.Level(s));
            Assert.False(WaterLevel.IsSource(s));
            Assert.False(WaterLevel.IsFalling(s));
            Assert.Equal(8 - level, WaterLevel.Height(s));
        }
        var falling = WaterLevel.Flowing(0, falling: true);
        Assert.True(WaterLevel.IsFalling(falling));
        Assert.False(WaterLevel.IsSource(falling));
        Assert.Equal(8, WaterLevel.Height(falling));
    }

    [Fact]
    public void A_source_on_flat_ground_spreads_seven_blocks_each_a_level_weaker()
    {
        using var pool = new Pool();
        pool.Set(0, 1, 0, BlockId.Water);
        pool.Settle();
        Assert.True(WaterLevel.IsSource(pool.State(0, 1, 0)));
        for (int x = 1; x <= 7; x++)
        {
            Assert.Equal(BlockId.Water, pool.Block(x, 1, 0));
            Assert.Equal(x, WaterLevel.Level(pool.State(x, 1, 0)));
        }
        Assert.Equal(BlockId.Air, pool.Block(8, 1, 0));
        Assert.Equal(WaterLevel.Weakest, WaterLevel.Level(pool.State(3, 1, 4))); // seven steps away round the corner
        Assert.Equal(BlockId.Air, pool.Block(4, 1, 4));
    }

    [Fact]
    public void Water_falls_then_spreads_where_it_lands()
    {
        using var pool = new Pool();
        pool.Set(0, 6, 0, BlockId.Stone);
        pool.Set(0, 7, 0, BlockId.Water);
        pool.Settle();
        // The source spreads one each way on its pillar, and from there the water falls past its sides.
        Assert.Equal(BlockId.Water, pool.Block(1, 7, 0));
        for (int y = 1; y <= 6; y++) Assert.True(WaterLevel.IsFalling(pool.State(1, y, 0)));
        Assert.Equal(1, WaterLevel.Level(pool.State(2, 1, 0)));
        Assert.Equal(BlockId.Air, pool.Block(2, 7, 0)); // over the drop it falls, not spreads
    }

    [Fact]
    public void Water_runs_towards_a_nearby_drop_rather_than_every_way()
    {
        using var pool = new Pool();
        // A ledge five blocks high whose edge is three blocks to +X of the source.
        pool.Volume.FillBox(new(-15, 1, -15), new(2, 5, 15), BlockId.Stone, BlockOrientation.Upright);
        pool.Set(0, 6, 0, BlockId.Water);
        pool.Settle();
        Assert.Equal(BlockId.Water, pool.Block(2, 6, 0));
        Assert.True(WaterLevel.IsFalling(pool.State(3, 3, 0))); // over the edge
        Assert.Equal(BlockId.Air, pool.Block(-1, 6, 0));
        Assert.Equal(BlockId.Air, pool.Block(0, 6, 1));
        Assert.Equal(BlockId.Air, pool.Block(1, 6, 1));
    }

    [Fact]
    public void Flowing_water_drains_away_once_its_source_is_gone()
    {
        using var pool = new Pool();
        pool.Set(0, 1, 0, BlockId.Water);
        pool.Settle();
        pool.Set(0, 1, 0, BlockId.Air);
        pool.Settle();
        for (int x = -8; x <= 8; x++)
        for (int z = -8; z <= 8; z++)
            Assert.Equal(BlockId.Air, pool.Block(x, 1, z));
    }

    [Fact]
    public void Flowing_water_between_two_sources_becomes_a_source()
    {
        using var pool = new Pool();
        pool.Set(0, 1, 0, BlockId.Water);
        pool.Set(2, 1, 0, BlockId.Water);
        pool.Settle();
        Assert.True(WaterLevel.IsSource(pool.State(1, 1, 0)));
        // So taking it away, it fills in again as a source.
        pool.Set(1, 1, 0, BlockId.Air);
        pool.Settle();
        Assert.True(WaterLevel.IsSource(pool.State(1, 1, 0)));
        Assert.Equal(1, WaterLevel.Level(pool.State(1, 1, 1))); // but not beside them, with only one source next to it
    }

    [Fact]
    public void Water_at_rest_changes_nothing()
    {
        using var pool = new Pool();
        for (int x = -2; x <= 2; x++) pool.Set(x, 0, 0, BlockId.Water); // a trench of sources in the floor
        Assert.Equal(0, pool.Settle());
    }
}
