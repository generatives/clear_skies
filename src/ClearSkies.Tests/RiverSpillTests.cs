using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;
using Xunit;

namespace ClearSkies.Tests;

public class RiverSpillTests
{
    [Fact]
    public void Rivers_run_on_over_drops_instead_of_stopping_short()
    {
        // A river on this seed's spawn landmass coming down steep ground in falls (found by looking).
        var water = Water.For(1337);
        int rivers = 0, spills = 0;
        for (int z = -8100; z < -8050; z++)
        for (int x = 12560; x < 12610; x++)
        {
            var (level, spill) = water.Settled(x, z);
            if (level == int.MinValue) continue;
            rivers++;
            if (spill) spills++;
        }
        Assert.True(spills > 0 && spills < rivers / 2);
    }

    [Fact]
    public void Spilling_water_is_generated_flowing_so_it_runs_over_the_lip()
    {
        var water = Water.For(1337);
        var generator = new HeartWorldGenerator(1337);
        for (int z = -8100; z < -8050; z++)
        for (int x = 12560; x < 12610; x++)
        {
            var (level, spill) = water.Settled(x, z);
            if (!spill) continue;
            var data = new ChunkData();
            var pos = new ChunkPosition(x >> 5, level >> 5, z >> 5);
            generator.Generate(data, pos);
            int lx = x & 31, ly = level & 31, lz = z & 31;
            if (data.Get(lx, ly, lz) != BlockId.Water) continue; // (ice or lava)
            Assert.False(WaterLevel.IsSource(data.GetOrientation(lx, ly, lz)));
            return;
        }
        Assert.Fail("no spilling water found");
    }
}
