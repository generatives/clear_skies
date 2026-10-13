using ClearSkies.Engine.Rendering;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

public class ParticleTests
{
    private static ParticleGroup Group(int capacity) => new(new ParticleSettings
    {
        Rate = 100f, MinLife = 10f, MaxLife = 10f, MinSpeed = 1f, MaxSpeed = 1f, Size = 1f, Capacity = capacity,
    })
    {
        Direction = new Vector3D<float>(0, 1, 0),
    };

    [Fact]
    public void SpawnsAtItsRateWhileOn()
    {
        var group = Group(64);
        group.Update(0.1f); // off
        Assert.Equal(0, group.Count);
        group.Intensity = 1f;
        group.Update(0.1f);
        Assert.Equal(10, group.Count);
        group.Intensity = 0.5f;
        group.Update(0.1f);
        Assert.Equal(15, group.Count);
    }

    [Fact]
    public void AFullGroupReplacesItsOldest()
    {
        var group = Group(4);
        group.Intensity = 1f;
        group.Update(0.1f); // 10 spawned into 4 slots
        Assert.Equal(4, group.Count);
        var drawn = new ParticleRenderer.Instance[8];
        Assert.Equal(4, group.Write(drawn));
    }

    [Fact]
    public void ParticlesMoveShrinkAndDie()
    {
        var group = Group(64);
        group.Intensity = 1f;
        group.Update(0.015f); // one (1.5 due), at the origin
        group.Intensity = 0f;
        group.Update(5f);
        var drawn = new ParticleRenderer.Instance[1];
        Assert.Equal(1, group.Write(drawn));
        Assert.Equal(5f, drawn[0].PositionSize.Y, 3); // 1 block/s up for 5 s
        Assert.Equal(0.5f, drawn[0].PositionSize.W, 3); // half its life gone
        group.Update(5f);
        Assert.Equal(0, group.Count);
    }
}
