using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;
using Xunit;
using Xunit.Abstractions;

namespace ClearSkies.Tests;

public class RiverSpillTests
{
    private readonly ITestOutputHelper _out;
    public RiverSpillTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Running_water_lies_where_it_runs_and_runs_on_over_drops()
    {
        // A river on this seed's spawn landmass coming down steep ground (found by looking).
        var worn = Erosion.For(1337).Of(12585f, -8075f);
        var generator = new HeartWorldGenerator(1337);
        var chunks = new Dictionary<(int, int, int), ChunkData>();
        ChunkData Chunk(int x, int y, int z)
        {
            var key = (x >> 5, y >> 5, z >> 5);
            if (!chunks.TryGetValue(key, out var data))
                generator.Generate(chunks[key] = data = new ChunkData(), new ChunkPosition(key.Item1, key.Item2, key.Item3));
            return data;
        }
        int channel = 0, wet = 0, flowing = 0, noPlan = 0; var under = new int[10];
        for (int z = -8100; z < -8050; z++)
        for (int x = 12560; x < 12610; x++)
        {
            int y = 480;
            while (y > 340 && Chunk(x, y, z).Get(x & 31, y & 31, z & 31) == BlockId.Air) y--;
            var top = Chunk(x, y, z);
            bool water = top.Get(x & 31, y & 31, z & 31) == BlockId.Water;
            if (water && !WaterLevel.IsSource(top.GetOrientation(x & 31, y & 31, z & 31))) flowing++;
            int cell = worn.Water.ColumnAt(x + 0.5f, z + 0.5f);
            if (cell < 0 || worn.Water.Flow[cell] < 40f) continue;
            channel++;
            if (water) wet++;
            var plan = Water.For(1337).At(worn.Water, x, z);
            if (plan.Level == int.MinValue) noPlan++; else if (!water) under[Math.Clamp(y - plan.Level, 0, 9)]++;
        }
        _out.WriteLine($"{channel} channel columns, {wet} wet; {flowing} flowing; dry: no plan {noPlan}, ground over plan by " + string.Join(" ", under));
        Assert.True(channel > 0 && wet > channel / 4); // (about a third, so far: the rest the flood runs too little through)
        Assert.True(flowing > 0);
    }
}
