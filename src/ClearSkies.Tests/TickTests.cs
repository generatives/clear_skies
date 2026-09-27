using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Characters;
using Xunit;

namespace ClearSkies.Tests;

public class TickClockTests
{
    [Theory]
    [InlineData(1.0 / 60.0)]
    [InlineData(1.0 / 144.0)]
    [InlineData(1.0 / 30.0)]
    [InlineData(1.0 / 45.0)]
    [InlineData(1.0 / 240.0)]
    public void OneSecondOfFramesRunsSixtyTicks(double frameSeconds)
    {
        var clock = new TickClock();
        int ticks = 0;
        int frames = (int)System.Math.Round(1.0 / frameSeconds);
        for (int i = 0; i < frames; i++) ticks += clock.Advance(frameSeconds);
        Assert.InRange(ticks, 59, 60);
        Assert.Equal(0, clock.DroppedTicks);
    }

    [Fact]
    public void SixtyFpsRunsExactlyOneTickEveryFrame()
    {
        var clock = new TickClock();
        for (int i = 0; i < 600; i++) Assert.Equal(1, clock.Advance(1.0 / 60.0));
    }

    [Fact]
    public void ThirtyFpsRunsTwoTicksAFrame()
    {
        var clock = new TickClock();
        for (int i = 0; i < 100; i++) Assert.Equal(2, clock.Advance(1.0 / 30.0));
    }

    [Fact]
    public void LongFrameIsCappedAndTheBacklogDropped()
    {
        var clock = new TickClock();
        Assert.Equal(TickClock.MaxTicksPerFrame, clock.Advance(1.0));
        Assert.True(clock.DroppedTicks >= 50);
        // Nothing left over to chase next frame.
        Assert.Equal(1, clock.Advance(1.0 / 60.0));
    }

    [Fact]
    public void AlphaIsTheFractionOfATickLeftOver()
    {
        var clock = new TickClock();
        Assert.Equal(0, clock.Advance(0.25 / 60.0));
        Assert.Equal(0.25f, clock.Alpha, 3);
        Assert.Equal(1, clock.Advance(1.0 / 60.0));
        Assert.Equal(0.25f, clock.Alpha, 3);
        Assert.Equal(0, clock.Advance(0.5 / 60.0));
        Assert.Equal(0.75f, clock.Alpha, 3);
    }

    [Fact]
    public void RateSpeedsTicksUp()
    {
        var clock = new TickClock { Rate = 1.02 };
        int ticks = 0;
        for (int i = 0; i < 6000; i++) ticks += clock.Advance(1.0 / 60.0);
        Assert.InRange(ticks, 6119, 6121);
    }

    [Fact]
    public void NegativeFrameTimeRunsNothing()
    {
        var clock = new TickClock();
        Assert.Equal(0, clock.Advance(-1));
        Assert.Equal(0f, clock.Alpha);
    }
}

public class FixedStepPhysicsTests
{
    /// <summary>The same second of play at different frame rates ends with the same body state: physics steps once per
    /// tick, and each tick's impulse is for one fixed step.</summary>
    [Theory]
    [InlineData(1.0 / 30.0)]
    [InlineData(1.0 / 144.0)]
    [InlineData(1.0 / 60.0)]
    public void ForceAppliedPerTickDoesNotDependOnFrameRate(double frameSeconds)
    {
        var (velocity, ticks) = Simulate(frameSeconds);
        var (reference, referenceTicks) = Simulate(1.0 / 60.0);
        Assert.Equal(referenceTicks, ticks);
        Assert.Equal(reference.Y, velocity.Y, 3);
    }

    private static (Vector3 velocity, int ticks) Simulate(double frameSeconds)
    {
        const float step = 1f / 60f;
        using var physics = new PhysicsWorld(new Vector3(0, -6, 0), step);
        var body = physics.AddDynamicBox(new Vector3(0, 100, 0), Vector3.One, 1f, new ColliderInfo(ColliderKind.Other));
        var clock = new TickClock();
        int ticks = 0;
        double elapsed = 0;
        while (ticks < 60)
        {
            int n = clock.Advance(frameSeconds);
            elapsed += frameSeconds;
            for (int i = 0; i < n && ticks < 60; i++, ticks++)
            {
                physics.ApplyLinearImpulse(body, new Vector3(0, 10f, 0) * step); // a 10 N lift for one tick
                physics.Step();
            }
        }
        return (physics.GetBodyLinearVelocity(body), ticks);
    }
}

public class FrameHistoryTests
{
    private static readonly double[] NoSystems = Array.Empty<double>();

    [Fact]
    public void FindsTheSpikeAndWhereItsTimeWent()
    {
        var history = new FrameHistory();
        for (int i = 0; i < 100; i++) history.Record(16.7, 1, -1, new[] { 2.0, 1.0, 0.5 }, 0.2, 0.3);
        history.Record(48, 3, 2, new[] { 1.0, 30.0, 0.5 }, 0.2, 9.0);
        for (int i = 0; i < 10; i++) history.Record(16.7, 1, -1, new[] { 2.0, 1.0, 0.5 }, 0.2, 0.3);

        var (average, worst, spikes, ago) = history.Summarize();
        Assert.Equal(48f, worst);
        Assert.Equal(10, ago);
        Assert.Equal(1, spikes);
        Assert.InRange(average, 16.9f, 17.1f);

        ref readonly var slowest = ref history.Get(ago);
        Assert.Equal(3, slowest.Ticks);
        Assert.Equal(2, slowest.GcGeneration);
        Assert.Equal(40.7f, slowest.CpuMs, 3);
        Assert.Equal((1, 30f), slowest.Slowest[0]);
        Assert.Equal((FrameHistory.FrameEnd, 9f), slowest.Slowest[1]);
        Assert.Equal((0, 1f), slowest.Slowest[2]);
    }

    [Fact]
    public void KeepsOnlyTheLatestFramesAsARing()
    {
        var history = new FrameHistory();
        for (int i = 0; i < FrameHistory.Capacity + 7; i++) history.Record(i, 1, -1, NoSystems, 0, 0);
        Assert.Equal(FrameHistory.Capacity, history.Count);
        Assert.Equal(FrameHistory.Capacity + 6, history.Get(0).Ms);
        Assert.Equal(7, history.Get(FrameHistory.Capacity - 1).Ms);
        // Plotting from Offset goes oldest to newest.
        Assert.Equal(7f, history.Milliseconds[history.Offset]);
    }

    [Fact]
    public void HeldHistoryRecordsNothing()
    {
        var history = new FrameHistory();
        history.Record(16, 1, -1, NoSystems, 0, 0);
        history.Paused = true;
        history.Record(99, 1, -1, NoSystems, 0, 0);
        Assert.Equal(1, history.Count);
        Assert.Equal(16f, history.Get(0).Ms);
    }
}
