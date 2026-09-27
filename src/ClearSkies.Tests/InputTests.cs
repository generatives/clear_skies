using System.Numerics;
using BepuPhysics.Collidables;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Characters;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

public class InputLatchTests
{
    [Fact]
    public void PressInAFrameWithNoTickReachesTheNextTick()
    {
        var latch = new InputLatch();
        var clock = new TickClock();
        // 240 fps: the press lands in a frame that runs no tick.
        Assert.Equal(0, clock.Advance(1.0 / 240.0));
        latch.AddFrame(PlayerButtons.Up, Vector2.Zero);
        PlayerInput? got = null;
        for (int f = 0; f < 4 && got is null; f++)
        {
            latch.AddFrame(PlayerButtons.None, Vector2.Zero);
            if (clock.Advance(1.0 / 240.0) > 0) got = latch.Take(PlayerButtons.None, 0, 0);
        }
        Assert.NotNull(got);
        Assert.True(got!.Value.WasPressed(PlayerButtons.Up));
        Assert.False(got.Value.IsHeld(PlayerButtons.Up)); // already released, but the press still counts
    }

    [Fact]
    public void APressIsSeenByOnlyOneTick()
    {
        var latch = new InputLatch();
        latch.AddFrame(PlayerButtons.ToggleFly, Vector2.Zero);
        Assert.True(latch.Take(PlayerButtons.ToggleFly, 0, 0).WasPressed(PlayerButtons.ToggleFly));
        var second = latch.Take(PlayerButtons.ToggleFly, 0, 0);
        Assert.False(second.WasPressed(PlayerButtons.ToggleFly));
        Assert.True(second.IsHeld(PlayerButtons.ToggleFly));
    }

    [Fact]
    public void MouseMovementAddsUpAcrossFrames()
    {
        var latch = new InputLatch();
        latch.AddFrame(PlayerButtons.None, new Vector2(3, 1));
        latch.AddFrame(PlayerButtons.Left, new Vector2(2, -4));
        var input = latch.Take(PlayerButtons.None, 1f, 2f);
        Assert.Equal(new Vector2(5, -3), input.MouseDelta);
        Assert.True(input.WasPressed(PlayerButtons.Left));
        Assert.Equal((1f, 2f), (input.Yaw, input.Pitch));
        Assert.Equal(Vector2.Zero, latch.Take(PlayerButtons.None, 0, 0).MouseDelta);
    }

    [Fact]
    public void MoveAxesCancelOut()
    {
        var input = new PlayerInput { Held = PlayerButtons.Forward | PlayerButtons.Left | PlayerButtons.Right };
        Assert.Equal(new Vector2(0, 1), input.Move);
    }
}

public class PlayerMovementTests
{
    private static (World world, PhysicsWorld physics, Entity player) Scene(bool freeFly)
    {
        var world = new World();
        var physics = new PhysicsWorld(new Vector3(0, -6, 0), 1f / 60f);
        var player = world.CreateEntity();
        player.Set(new Transform { Position = new Vector3D<float>(0, 50, 0), Rotation = Quaternion<float>.Identity, Scale = Vector3D<float>.One });
        player.Set(new FreeFlyController { MoveSpeed = 10f });
        var character = new PlayerCharacter(physics.Characters, new Vector3(0, 49.2f, 0), new Capsule(0.3f, 1f),
            0.01f, 2f, 100f, 70f, 6f, 5f, entity: player);
        player.Set(new CharacterControllerComponent { Character = character, EyeHeight = 0.7f });
        player.Set(new CharacterModeComponent { FreeFly = freeFly });
        player.Set(new PlayerInput());
        return (world, physics, player);
    }

    [Fact]
    public void FreeFlyMovesAtItsSpeedPerTick()
    {
        var (world, physics, player) = Scene(freeFly: true);
        var movement = new PlayerMovementSystem(world);
        for (int i = 0; i < 60; i++)
        {
            player.Get<PlayerInput>() = new PlayerInput { Held = PlayerButtons.Forward };
            movement.Update(1f / 60f);
        }
        // Identity rotation faces -Z; 10 blocks/s for one second.
        Assert.Equal(-10f, player.Get<Transform>().Position.Z, 3);
        physics.Dispose();
        world.Dispose();
    }

    [Fact]
    public void TogglePressSwitchesToWalkingOnce()
    {
        var (world, physics, player) = Scene(freeFly: true);
        var movement = new PlayerMovementSystem(world);
        player.Get<PlayerInput>() = new PlayerInput { Pressed = PlayerButtons.ToggleFly, Held = PlayerButtons.ToggleFly };
        movement.Update(1f / 60f);
        Assert.False(player.Get<CharacterModeComponent>().FreeFly);
        player.Get<PlayerInput>() = new PlayerInput { Held = PlayerButtons.ToggleFly }; // still held, no new press
        movement.Update(1f / 60f);
        Assert.False(player.Get<CharacterModeComponent>().FreeFly);
        physics.Dispose();
        world.Dispose();
    }

    [Fact]
    public void FlySpeedStepsOncePerPress()
    {
        var (world, physics, player) = Scene(freeFly: true);
        var movement = new PlayerMovementSystem(world);
        player.Get<PlayerInput>() = new PlayerInput { Pressed = PlayerButtons.Next };
        movement.Update(1f / 60f);
        movement.Update(1f / 60f); // the same input again would be a second press; the sampler never does that
        player.Get<PlayerInput>() = new PlayerInput();
        movement.Update(1f / 60f);
        Assert.Equal(20f, player.Get<FreeFlyController>().MoveSpeed);
        physics.Dispose();
        world.Dispose();
    }

    [Fact]
    public void CharacterKeysComeFromTheTicksInput()
    {
        var keys = PlayerMovementSystem.CharacterKeys(new PlayerInput
        {
            Held = PlayerButtons.Forward | PlayerButtons.Down | PlayerButtons.Crouch,
            Pressed = PlayerButtons.Up,
        });
        Assert.Equal(new Vector2(0, 1), keys.Move);
        Assert.True(keys.Sprint && keys.Crouch && keys.JumpPressed);
    }
}

public class RenderInterpolationTests
{
    private static Transform At(float x) => new()
        { Position = new Vector3D<float>(x, 0, 0), Rotation = Quaternion<float>.Identity, Scale = Vector3D<float>.One };

    private sealed class Rig
    {
        public readonly World World = new();
        public readonly Time Time = new();
        public readonly RenderInterpolationSystem System;
        public readonly Entity Entity;
        public Rig()
        {
            System = new RenderInterpolationSystem(World, Time);
            Entity = World.CreateEntity();
            Entity.Set(At(0));
            Entity.Set(new SmoothedTransform());
        }

        /// <summary>One tick in which the simulation moves the entity to <paramref name="x"/>.</summary>
        public void Tick(float x, Action<float>? check = null)
        {
            System.BeginTick.Update(0);
            check?.Invoke(Entity.Get<Transform>().Position.X);
            Entity.Get<Transform>() = At(x);
            System.EndTick.Update(0);
        }

        public float Frame(float alpha)
        {
            Time.Alpha = alpha;
            System.Update(0);
            return Entity.Get<Transform>().Position.X;
        }
    }

    [Fact]
    public void DrawsBetweenTheLastTwoTicks()
    {
        var rig = new Rig();
        rig.Tick(0);
        rig.Tick(1);
        Assert.Equal(0.25f, rig.Frame(0.25f), 4);
        Assert.Equal(0.75f, rig.Frame(0.75f), 4);
    }

    [Fact]
    public void TicksSeeTheTruePoseNotTheDrawnOne()
    {
        var rig = new Rig();
        rig.Tick(0);
        rig.Tick(1);
        rig.Frame(0.5f);
        rig.Tick(2, check: x => Assert.Equal(1f, x));
        Assert.Equal(1.5f, rig.Frame(0.5f), 4);
    }

    [Fact]
    public void SeveralTicksInOneFrameKeepTheirHistory()
    {
        var rig = new Rig();
        rig.Tick(0);
        rig.Frame(0);
        rig.Tick(1);
        rig.Tick(2, check: x => Assert.Equal(1f, x));
        Assert.Equal(1.5f, rig.Frame(0.5f), 4);
    }

    [Fact]
    public void MovingOutsideTheTicksIsNotSmoothed()
    {
        var rig = new Rig();
        rig.Tick(0);
        rig.Tick(1);
        rig.Frame(0.5f);
        rig.Entity.Get<Transform>() = At(100); // a teleport between ticks
        Assert.Equal(100f, rig.Frame(0.5f));
        rig.Tick(101, check: x => Assert.Equal(100f, x));
        Assert.Equal(100.5f, rig.Frame(0.5f), 4);
    }

    [Fact]
    public void PositionOnlyLeavesRotationToOthers()
    {
        var rig = new Rig();
        rig.Entity.Get<SmoothedTransform>().PositionOnly = true;
        rig.Tick(0);
        rig.Tick(1);
        var turned = Quaternion<float>.CreateFromYawPitchRoll(1f, 0, 0);
        rig.Entity.Get<Transform>().Rotation = turned; // mouse-look, per frame
        rig.Frame(0.5f);
        Assert.Equal(turned, rig.Entity.Get<Transform>().Rotation);
        Assert.Equal(0.5f, rig.Entity.Get<Transform>().Position.X, 4);
    }
}
