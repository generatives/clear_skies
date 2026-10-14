using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

public class ParticleTests
{
    private static ParticleEmitter Emitter(int capacity, Vector3D<float> origin = default) => new(new ParticleSettings
    {
        Origin = origin, Direction = new Vector3D<float>(0, 1, 0),
        Rate = 100f, MinLife = 10f, MaxLife = 10f, MinSpeed = 1f, MaxSpeed = 1f, Size = 1f, Capacity = capacity,
    });

    [Fact]
    public void SpawnsAtItsRateWhileOn()
    {
        var group = Emitter(64);
        group.Update(0.1f, Transform.Identity); // off
        Assert.Equal(0, group.Count);
        group.Intensity = 1f;
        group.Update(0.1f, Transform.Identity);
        Assert.Equal(10, group.Count);
        group.Intensity = 0.5f;
        group.Update(0.1f, Transform.Identity);
        Assert.Equal(15, group.Count);
    }

    [Fact]
    public void AFullGroupReplacesItsOldest()
    {
        var group = Emitter(4);
        group.Intensity = 1f;
        group.Update(0.1f, Transform.Identity); // 10 spawned into 4 slots
        Assert.Equal(4, group.Count);
        var drawn = new ParticleRenderer.Instance[8];
        Assert.Equal(4, group.Write(drawn));
    }

    [Fact]
    public void ParticlesMoveShrinkAndDie()
    {
        var group = Emitter(64);
        group.Intensity = 1f;
        group.Update(0.015f, Transform.Identity); // one (1.5 due), at the origin
        group.Intensity = 0f;
        group.Update(5f, Transform.Identity);
        var drawn = new ParticleRenderer.Instance[1];
        Assert.Equal(1, group.Write(drawn));
        Assert.Equal(5f, drawn[0].PositionSize.Y, 3); // 1 block/s up for 5 s
        Assert.Equal(0.5f, drawn[0].PositionSize.W, 3); // half its life gone
        group.Update(5f, Transform.Identity);
        Assert.Equal(0, group.Count);
    }

    [Fact]
    public void ParticlesSpawnFromTheEntitysPose()
    {
        var group = Emitter(64, origin: new Vector3D<float>(0, 1, 0));
        group.Intensity = 1f;
        var pose = Transform.Identity;
        pose.Position = new Vector3D<float>(10, 0, 0);
        pose.Rotation = Quaternion<float>.CreateFromAxisAngle(new Vector3D<float>(0, 0, 1), -MathF.PI / 2); // up turned to +x
        group.Update(0.015f, pose);
        group.Intensity = 0f;
        group.Update(1f, pose);
        var drawn = new ParticleRenderer.Instance[1];
        Assert.Equal(1, group.Write(drawn));
        Assert.Equal(12f, drawn[0].PositionSize.X, 3); // 1 block out along +x, then 1 block/s for 1 s
        Assert.Equal(0f, drawn[0].PositionSize.Y, 3);
    }
}
