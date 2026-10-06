using System.Numerics;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Weather;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

public class WindTests
{
    private const double TickSeconds = 1 / 60.0;

    /// <summary>The horizontal wind speed exceeded 10% of the time over many random points and times.</summary>
    private static float Speed90(WindField wind, Func<uint> setTick, Action<uint> tick)
    {
        var rng = new Random(1);
        var speeds = new List<float>();
        for (int i = 0; i < 5000; i++)
        {
            tick((uint)rng.Next(1, 10_000_000));
            var p = new Vector3(rng.NextSingle() * 50000, rng.NextSingle() * 1000, rng.NextSingle() * 50000);
            var w = wind.Sample(p);
            speeds.Add(new Vector2(w.X, w.Z).Length());
        }
        speeds.Sort();
        return speeds[speeds.Count * 9 / 10];
    }

    [Fact]
    public void BaseAndEddySpeedsAreTheSpeedsExceededTenPercentOfTheTime()
    {
        uint t = 1;
        var wind = new WindField(42, () => t, TickSeconds, null);
        var s = wind.Settings;
        (s.GustUp, s.GustDown, s.CalmFull, s.CalmEdge) = (0, 0, -2, -1.9f); // no gusts or dead zones
        s.EddySpeed = 0;
        Assert.InRange(Speed90(wind, () => t, v => t = v), 3.6f, 4.4f);     // base currents' cores: 4 m/s
        (s.BaseSpeed, s.EddySpeed) = (0, 1);
        wind.Invalidate();
        Assert.InRange(Speed90(wind, () => t, v => t = v), 0.9f, 1.1f);     // eddies: 1 m/s
    }

    [Fact]
    public void WindIsDivergenceFree()
    {
        uint t = 12345;
        var wind = new WindField(7, () => t, TickSeconds, null);
        wind.Settings.VerticalScale = 1f; // the only thing that bends it
        float largestDivergence = 0f, largestGradient = 0f;
        for (int i = 0; i < 50; i++)
        {
            var c = new ChunkPosition(i * 13, i % 7, -i * 5);
            var dx = wind.ChunkWind(c.Offset(1, 0, 0)) - wind.ChunkWind(c.Offset(-1, 0, 0));
            var dy = wind.ChunkWind(c.Offset(0, 1, 0)) - wind.ChunkWind(c.Offset(0, -1, 0));
            var dz = wind.ChunkWind(c.Offset(0, 0, 1)) - wind.ChunkWind(c.Offset(0, 0, -1));
            largestDivergence = MathF.Max(largestDivergence, MathF.Abs(dx.X + dy.Y + dz.Z));
            largestGradient = MathF.Max(largestGradient, MathF.Max(MathF.Abs(dx.X), MathF.Max(MathF.Abs(dy.Y), MathF.Abs(dz.Z))));
        }
        Assert.True(largestGradient > 0.01f, "the wind varies");
        Assert.True(largestDivergence < 1e-3f * largestGradient, $"divergence {largestDivergence} against gradients of {largestGradient}");
    }

    [Fact]
    public void EveryMachineComputesTheSameWindFromTheSeedAndTick()
    {
        uint t = 999;
        var a = new WindField(5, () => t, TickSeconds, null);
        var b = new WindField(5, () => t, TickSeconds, null);
        var other = new WindField(6, () => t, TickSeconds, null);
        var p = new Vector3(1234.5f, 300f, -987.25f);
        Assert.Equal(a.Sample(p), b.Sample(p));
        Assert.NotEqual(a.Sample(p), other.Sample(p));
    }

    [Fact]
    public void WindIsContinuousAcrossChunkBorders()
    {
        uint t = 50;
        var wind = new WindField(3, () => t, TickSeconds, null);
        var below = wind.Sample(new Vector3(31.999f, 100f, 10f));
        var above = wind.Sample(new Vector3(32.001f, 100f, 10f));
        Assert.True(Vector3.Distance(below, above) < 0.01f, $"{below} then {above}");
    }

    [Fact]
    public void GustsAndEddiesChangeTheWindOverTime()
    {
        uint t = 600;
        var wind = new WindField(11, () => t, TickSeconds, null);
        var p = new Vector3(500f, 200f, 500f);
        var before = wind.Sample(p);
        t += 60 * 20; // 20 s on
        Assert.NotEqual(before, wind.Sample(p));
    }

    [Fact]
    public void WindDiesDownAtTerrainIncludingEdits()
    {
        using var scene = new HeadlessScene();
        uint t = 1;
        var wind = new WindField(9, () => t, TickSeconds, scene.WorldVolume);
        var open = new WindField(9, () => t, TickSeconds, null);
        // A chunk of solid stone at (0, 0, 0): wind there is (nearly) still, and four chunks away untouched.
        scene.WorldVolume.FillBox(new Vector3D<int>(0, 0, 0), new Vector3D<int>(31, 31, 31), BlockId.Stone, BlockOrientation.Upright);
        var inside = new Vector3(16f, 16f, 16f);
        var far = new Vector3(16f + 32 * 5, 16f, 16f);
        Assert.True(wind.Sample(inside).Length() < 0.15f * MathF.Max(open.Sample(inside).Length(), 0.5f), $"{wind.Sample(inside)} at the terrain");
        Assert.Equal(open.Sample(far), wind.Sample(far));

        // Dug out (a player's edit), the wind comes back once the terrain is looked at again.
        scene.WorldVolume.FillBox(new Vector3D<int>(0, 0, 0), new Vector3D<int>(31, 31, 31), BlockId.Air, BlockOrientation.Upright);
        t += WindField.TerrainRefreshTicks;
        Assert.Equal(open.Sample(inside), wind.Sample(inside));
    }

    [Fact]
    public void AnOverrideIsTheWindEverywhere()
    {
        var wind = new WindField(1, () => 1, TickSeconds, null) { Override = new Vector3(3, 0, -4) };
        Assert.Equal(new Vector3(3, 0, -4), wind.Sample(new Vector3(100, 50, 7)));
        wind.Override = null;
        Assert.NotEqual(new Vector3(3, 0, -4), wind.Sample(new Vector3(100, 50, 7)));
    }
}
