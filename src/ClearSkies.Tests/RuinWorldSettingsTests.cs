using ClearSkies.Game.Ruins;
using Xunit;

namespace ClearSkies.Tests;

public class RuinWorldSettingsTests
{
    [Fact]
    public void DefaultsToArterialRuinsOn()
    {
        var s = RuinWorldSettings.Parse(null);
        Assert.True(s.Enabled);
        Assert.Equal("arterial", s.Model);
    }

    [Fact]
    public void ParsesOffModelAndPopulation()
    {
        Assert.False(RuinWorldSettings.Parse("off").Enabled);
        var s = RuinWorldSettings.Parse("model=grid, population=60000");
        Assert.Equal("grid", s.Model);
        Assert.Equal(60000, s.Ruin.Population);
    }

    [Fact]
    public void RejectsUnknownSettings() => Assert.Throws<ArgumentException>(() => RuinWorldSettings.Parse("towers=9"));
}
