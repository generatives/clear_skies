using ClearSkies.Game.Generation;
using Xunit;
using Xunit.Abstractions;

namespace ClearSkies.Tests;

public class HydrologyTests
{
    private readonly ITestOutputHelper _out;

    public HydrologyTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void The_spawn_landmass_settles_into_lakes_and_running_water()
    {
        var worn = Erosion.For(1337).Of(11250f, -6250f);
        var field = worn.Water;
        int land = 0, creeks = 0, rivers = 0;
        for (int c = 0; c < field.Nx * field.Nz; c++)
        {
            if (field.Flow[c] > 0f) land++;
            if (field.Flow[c] >= 40f && float.IsNaN(field.Lake[c])) creeks++;
            if (field.Flow[c] >= 300f && float.IsNaN(field.Lake[c])) rivers++;
        }
        _out.WriteLine($"{field.Nx} x {field.Nz} columns in {worn.Seconds:0.0} s (with erosion): {land} wet, {field.LakeColumns} lake, {creeks} creek, {rivers} river");
        Assert.True(field.LakeColumns > 0);
        Assert.True(creeks > 100 && rivers > 0);
    }
}
