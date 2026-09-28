using Xunit;
using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Rendering;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Tests;

/// <summary>The camera as a child of the player (<see cref="EyeSystem"/>), free-flying without a capsule, and
/// piloting leaving the player where they are.</summary>
public class ViewTests
{
    private static Entity Camera(World world)
    {
        var cam = world.CreateEntity();
        cam.Set(new CameraComponent { Camera = new Camera(), Active = true });
        return cam;
    }

    private static void Look(Entity player, float yaw, float pitch)
    {
        ref var look = ref player.Get<MouseLookComponent>();
        look.Yaw = yaw;
        look.Pitch = pitch;
        player.Get<Transform>().Rotation = look.BodyRotation;
    }

    [Fact]
    public void TheCameraIsAtTheEyeAndLooksTheWayThePlayerLooks()
    {
        using var scene = new HeadlessScene();
        var player = scene.SpawnLocalPlayer(new Vector3(3, 60, -2), freeFly: true);
        var cam = Camera(scene.World);
        EyeSystem.Attach(cam, player);
        var eyes = new EyeSystem(scene.World);
        var hierarchy = new HierarchyTransformSystem(scene.World);

        foreach (var pitch in new[] { 0f, 0.8f, -1.2f })
        {
            Look(player, 0.3f, pitch);
            eyes.Update(0);
            hierarchy.Update(0);
            var t = cam.Get<Transform>();
            // Looking up or down turns the eye in place: it doesn't swing around the body.
            Assert.Equal(3f, t.Position.X, 4);
            Assert.Equal(60.7f, t.Position.Y, 4);
            Assert.Equal(-2f, t.Position.Z, 4);
            var forward = Vec.Rotate(t.Rotation, new Vector3D<float>(0, 0, -1));
            var expected = player.Get<MouseLookComponent>().Forward;
            Assert.True(Vector3D.Distance(forward, expected) < 1e-4f, $"camera looks {forward}, player {expected}");
        }
        // The player itself stays upright.
        var up = Vec.Rotate(player.Get<Transform>().Rotation, new Vector3D<float>(0, 1, 0));
        Assert.Equal(1f, up.Y, 4);
    }

    [Fact]
    public void TheCameraIsDrawnWhereThePlayerIsDrawn()
    {
        using var scene = new HeadlessScene();
        var player = scene.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        player.Set(new InterpolatedTransform { PositionOnly = true });
        var cam = Camera(scene.World);
        EyeSystem.Attach(cam, player);
        var time = new Time();
        var interpolation = new TickInterpolationSystem(scene.World, time);
        var eyes = new EyeSystem(scene.World);

        interpolation.Update(SystemStage.Simulation, 0);
        player.Get<Transform>().Position = new Vector3D<float>(1, 60, 0);
        interpolation.Update(SystemStage.Simulation, 0);

        time.Alpha = 0.25f;
        eyes.Update(0);
        interpolation.Update(SystemStage.Frame, 0);
        var drawn = cam.DrawnPose().Position;
        Assert.Equal(0.25f, drawn.X, 4);
        Assert.Equal(60.7f, drawn.Y, 4);
    }

    [Fact]
    public void FreeFlyingTakesTheCapsuleOutOfTheSimulationUntilLanding()
    {
        using var scene = new HeadlessScene();
        var player = scene.SpawnLocalPlayer(new Vector3(0, 70, 0));
        Assert.Equal(1, scene.Physics.Characters.CharacterCount);

        Players.SetFreeFlying(player, true);
        Assert.Equal(0, scene.Physics.Characters.CharacterCount);
        Assert.True(player.Get<CharacterControllerComponent>().Character.Suspended);
        scene.Tick(30);
        Assert.Equal(70f, player.Get<Transform>().Position.Y, 4); // no gravity, nothing to read back

        player.Get<Transform>().Position = new Vector3D<float>(5, 80, 0); // flew somewhere
        Players.SetFreeFlying(player, false);
        Assert.Equal(1, scene.Physics.Characters.CharacterCount);
        var capsule = player.Get<CharacterControllerComponent>().Character.Position;
        Assert.True(Vector3.Distance(capsule, new Vector3(5, 80, 0)) < 1e-4f, $"landed at {capsule}");
        scene.Tick(30);
        Assert.True(player.Get<Transform>().Position.Y < 79.5f, "walking again: it falls");
    }

    [Fact]
    public void TogglingFlightAgainLandsWhereThePlayerFlewTo()
    {
        using var scene = new HeadlessScene();
        var player = scene.SpawnLocalPlayer(new Vector3(0, 70, 0), freeFly: true);
        for (int i = 0; i < 60; i++)
        {
            player.Get<PlayerInput>() = new PlayerInput { Held = PlayerButtons.Forward }; // yaw 0: towards -Z
            scene.Tick();
        }
        player.Get<PlayerInput>() = new PlayerInput { Pressed = PlayerButtons.ToggleFly };
        scene.Tick();
        Assert.False(player.Has<FreeFlying>());
        var capsule = player.Get<CharacterControllerComponent>().Character.Position;
        Assert.InRange(capsule.Z, -10.5f, -9.5f);
    }

    [Fact]
    public void PilotingHoldsThePlayerStillWhereTheyStand()
    {
        using var scene = new HeadlessScene();
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0));
        var player = scene.SpawnLocalPlayer(new Vector3(0, 52, 0));
        Assert.True(scene.TickUntil(() => player.Get<Support>().HasSupporter, 300), "the player never landed");
        var before = player.Get<Transform>().Position;

        player.Set<Piloting>();
        for (int i = 0; i < 60; i++)
        {
            player.Get<PlayerInput>() = new PlayerInput { Held = PlayerButtons.Forward | PlayerButtons.Right };
            scene.Tick();
        }
        var after = player.Get<Transform>().Position;
        Assert.True(Vector3D.Distance(before, after) < 0.05f, $"moved from {before} to {after}");
        Assert.Equal(grid, player.Get<Support>().Supporter);
    }

    [Fact]
    public void ACameraUnderAGridOutlivesIt()
    {
        using var scene = new HeadlessScene();
        var hierarchy = new HierarchyTransformSystem(scene.World);
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0));
        var cam = Camera(scene.World);
        Hierarchy.SetParent(cam, grid, LocalTransform.Identity);
        Hierarchy.DestroyRecursive(grid);
        Assert.True(cam.IsAlive);
        Assert.False(cam.Has<Parent>());

        // Disposed directly: its children go when the hierarchy next runs, but not the camera.
        var other = scene.SpawnPlatform(new Vector3(40, 50, 0));
        Hierarchy.SetParent(cam, other, LocalTransform.Identity);
        other.Dispose();
        hierarchy.Update(0);
        Assert.True(cam.IsAlive);
        Assert.False(cam.Has<Parent>());
    }
}
