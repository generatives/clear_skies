using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering;
using Xunit;

namespace ClearSkies.Tests;

public class ClimateTintTests
{
    /// <summary>Full strength east of x = 3000, none west of it.</summary>
    private sealed class EastOf3000 : IClimateTintMap
    {
        public void Column(float x, float z, ReadOnlySpan<float> heights, Span<Vector4> tints)
        {
            for (int j = 0; j < heights.Length; j++) tints[j] = new Vector4(1f, 0.5f, 0f, x > 3000f ? 1f : 0f);
        }
    }

    [Fact]
    public void TheFarVolumeReachesTerrainPastTheNearOne()
    {
        // Near volume from (-1024, -1024): 2048 blocks across, so x = 3000 is past it, in the far volume only.
        var texels = ClimateTintSystem.Build(new EastOf3000(), -1024f, -1024f);
        int Strength(int i, int k, int layer) => texels[4 * (i + ClimateTintSystem.SizeXZ * (k + ClimateTintSystem.SizeXZ * layer)) + 3];

        Assert.Equal(0, Strength(ClimateTintSystem.SizeXZ - 1, 128, 0)); // the near volume's east edge: x ~ 1020
        // The far volume's texel over x ~ 3040: its low corner is FarOffset from the near one's.
        int i = (int)((3040f - (-1024f + ClimateTintSystem.FarOffset)) / ClimateTintSystem.FarCellXZ);
        Assert.Equal(255, Strength(i, 128, ClimateTintSystem.SizeY));
        Assert.Equal(0, Strength(i - 2, 128, ClimateTintSystem.SizeY)); // x ~ 2912
    }
}
