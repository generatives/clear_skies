using ClearSkies.Engine.Core;
using System.Numerics;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Weather;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

public class WindTests
{
    /// <summary>The horizontal wind speed exceeded 10% of the time over many random points and times.</summary>
    private static float Speed90(WindField wind, ManualTickClock clock)
    {
        var rng = new Random(1);
        var speeds = new List<float>();
        for (int i = 0; i < 5000; i++)
        {
            clock.Tick = (uint)rng.Next(1, 10_000_000);
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
        var t = new ManualTickClock { Tick = 1 };
        var wind = new WindField(42, t, null);
        var s = wind.Settings;
        (s.GustUp, s.GustDown, s.CalmFull, s.CalmEdge) = (0, 0, -2, -1.9f); // no gusts or dead zones
        s.EddySpeed = 0;
        Assert.InRange(Speed90(wind, t), 3.6f, 4.4f);     // base currents' cores: 4 m/s
        (s.BaseSpeed, s.EddySpeed) = (0, 1);
        wind.Invalidate();
        Assert.InRange(Speed90(wind, t), 0.9f, 1.1f);     // eddies: 1 m/s
    }

    [Fact]
    public void WindIsDivergenceFree()
    {
        var t = new ManualTickClock { Tick = 12345 };
        var wind = new WindField(7, t, null);
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
        var t = new ManualTickClock { Tick = 999 };
        var a = new WindField(5, t, null);
        var b = new WindField(5, t, null);
        var other = new WindField(6, t, null);
        var p = new Vector3(1234.5f, 300f, -987.25f);
        Assert.Equal(a.Sample(p), b.Sample(p));
        Assert.NotEqual(a.Sample(p), other.Sample(p));
    }

    [Fact]
    public void WindIsContinuousAcrossChunkBorders()
    {
        var t = new ManualTickClock { Tick = 50 };
        var wind = new WindField(3, t, null);
        var below = wind.Sample(new Vector3(31.999f, 100f, 10f));
        var above = wind.Sample(new Vector3(32.001f, 100f, 10f));
        Assert.True(Vector3.Distance(below, above) < 0.01f, $"{below} then {above}");
    }

    [Fact]
    public void GustsAndEddiesChangeTheWindOverTime()
    {
        var t = new ManualTickClock { Tick = 600 };
        var wind = new WindField(11, t, null);
        var p = new Vector3(500f, 200f, 500f);
        var before = wind.Sample(p);
        t.Tick += 60 * 20; // 20 s on
        Assert.NotEqual(before, wind.Sample(p));
    }

    [Fact]
    public void WindDiesDownOverTerrainIncludingEdits()
    {
        using var scene = new HeadlessScene();
        var t = new ManualTickClock { Tick = 1 };
        var wind = new WindField(9, t, scene.WorldVolume);
        var open = new WindField(9, t, null);
        // Ground: stone under 20×20 chunks, its surface at y = 0. Just above it the air is still; well above, untouched.
        var min = new Vector3D<int>(-320, -64, -320);
        var max = new Vector3D<int>(319, -1, 319);
        scene.WorldVolume.FillBox(min, max, BlockId.Stone, BlockOrientation.Upright);
        var parked = new Vector3(16f, 4f, 16f);
        var high = new Vector3(16f, 16f + 32 * 5, 16f);
        Assert.True(wind.Sample(parked).Length() < 0.02f * MathF.Max(open.Sample(parked).Length(), 0.5f), $"{wind.Sample(parked)} at the ground");
        Assert.Equal(open.Sample(high), wind.Sample(high));

        // Dug away (a player's edit), the wind comes back once the terrain is looked at again.
        scene.WorldVolume.FillBox(min, max, BlockId.Air, BlockOrientation.Upright);
        t.Tick += WindField.TerrainRefreshTicks;
        Assert.Equal(open.Sample(parked), wind.Sample(parked));
    }

    [Fact]
    public void AnExposedPeakKeepsMostOfTheWind()
    {
        using var scene = new HeadlessScene();
        // A 45° peak of chunks, its top chunk at (0, -1, 0): six chunk layers, each a chunk wider each way.
        for (int y = -1; y >= -6; y--)
        {
            int r = -1 - y;
            for (int x = -r; x <= r; x++) for (int z = -r; z <= r; z++)
            {
                if (System.Math.Abs(x) + System.Math.Abs(z) > r) continue;
                scene.WorldVolume.FillBox(new Vector3D<int>(x * 32, y * 32, z * 32), new Vector3D<int>(x * 32 + 31, y * 32 + 31, z * 32 + 31),
                                          BlockId.Stone, BlockOrientation.Upright);
            }
        }
        var t = new ManualTickClock { Tick = 1 };
        var wind = new WindField(9, t, scene.WorldVolume);
        var open = new WindField(9, t, null);
        var top = new Vector3(16f, 16f, 16f); // the centre of the chunk on the peak
        float near = 0, sky = 0;
        var rng = new Random(3);
        for (int i = 0; i < 100; i++)
        {
            t.Tick = (uint)rng.Next(1, 1_000_000);
            near += wind.Sample(top).Length();
            sky += open.Sample(top).Length();
        }
        Assert.InRange(near / sky, 0.5f, 0.7f); // about 60% of the open wind
    }

    [Fact]
    public void WindNearTheGroundIsCalmEnoughToParkInAndNeverStrongerThanInOpenSky()
    {
        using var scene = new HeadlessScene();
        // Ground: two chunks of stone under 20×20 chunks, its surface at y = 0.
        scene.WorldVolume.FillBox(new Vector3D<int>(-320, -64, -320), new Vector3D<int>(319, -1, 319), BlockId.Stone, BlockOrientation.Upright);
        var t = new ManualTickClock { Tick = 1 };
        var wind = new WindField(9, t, scene.WorldVolume);
        var open = new WindField(9, t, null);
        (float Near, float Open) Mean(float height)
        {
            var rng = new Random(2);
            float near = 0, sky = 0;
            for (int i = 0; i < 200; i++)
            {
                t.Tick = (uint)rng.Next(1, 1_000_000);
                var p = new Vector3(rng.NextSingle() * 400 - 200, height, rng.NextSingle() * 400 - 200);
                near += wind.Sample(p).Length();
                sky += open.Sample(p).Length();
            }
            return (near / 200, sky / 200);
        }
        var parked = Mean(4f);
        Assert.True(parked.Near < 0.05f * parked.Open, $"{parked.Near} m/s just above the ground, {parked.Open} in open sky");
        foreach (float h in new[] { 16f, 32f, 48f, 64f, 96f })
        {
            var m = Mean(h);
            Assert.True(m.Near <= m.Open * 1.01f, $"{m.Near} m/s at {h} m above the ground, {m.Open} in open sky");
        }
    }

    [Fact]
    public void AnOverrideIsTheWindEverywhere()
    {
        var wind = new WindField(1, new ManualTickClock { Tick = 1 }, null) { Override = new Vector3(3, 0, -4) };
        Assert.Equal(new Vector3(3, 0, -4), wind.Sample(new Vector3(100, 50, 7)));
        wind.Override = null;
        Assert.NotEqual(new Vector3(3, 0, -4), wind.Sample(new Vector3(100, 50, 7)));
    }
}
