using ClearSkies.Game.Generation;
using Xunit;
using Xunit.Abstractions;

namespace ClearSkies.Tests;

public class ErosionTests
{
    private readonly ITestOutputHelper _out;

    public ErosionTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void The_spawn_landmass_wears_into_valleys_with_rivers()
    {
        var field = Erosion.For(1337).Of(11250f, -6250f);
        int running = field.Water.Flow.Count(f => f >= 40f);
        _out.WriteLine($"{field.Nx} x {field.Nz} samples in {field.Seconds:0.0} s: {running} with water running");
        Assert.True(running > 100); // (a dry landmass: few)

        // Mostly worn down, by up to a few hundred blocks, and raised by no more than the dells filled in.
        float least = 0f, most = 0f;
        for (int k = 0; k < field.Nz; k++)
        for (int i = 0; i < field.Nx; i++)
        {
            float s = field.Shift(field.X0 + (i + 0.5f) * Erosion.Cell, field.Z0 + (k + 0.5f) * Erosion.Cell, 0f);
            least = MathF.Min(least, s); most = MathF.Max(most, s);
        }
        _out.WriteLine($"shift {least:0} to {most:0}");
        Assert.InRange(least, -400f, -20f);
        Assert.InRange(most, 0f, 60f);
    }
}
