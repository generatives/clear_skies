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
