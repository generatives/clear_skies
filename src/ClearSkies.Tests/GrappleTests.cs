using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Xunit;

namespace ClearSkies.Tests;

public class GrappleTests
{
    private const float Up = MathF.PI / 2 - 0.01f; // as high as the view goes (LookInputSystem)

    /// <summary>One tick with E pressed (or still held) and <paramref name="also"/> held, looking at
    /// <paramref name="pitch"/>.</summary>
    private static void Grapple(HeadlessScene scene, Entity player, bool press, float pitch = Up,
                                PlayerButtons also = PlayerButtons.None)
    {
        player.Get<PlayerInput>() = new PlayerInput
        {
            Held = PlayerButtons.Grapple | also, Pressed = press ? PlayerButtons.Grapple : PlayerButtons.None,
            Pitch = pitch, Aiming = true,
        };
        scene.Tick();
    }

    private static void Hold(HeadlessScene scene, Entity player, int ticks, PlayerButtons also = PlayerButtons.None)
    {
        for (int i = 0; i < ticks; i++) Grapple(scene, player, press: false, also: also);
    }

    private static Vector3 Position(Entity player) => player.Get<CharacterControllerComponent>().Character.Position;

    private static float DistanceToHook(Entity player)
    {
        ref readonly var g = ref player.Get<Grapple>();
        var hook = g.WorldPoint(g.Anchor.Get<Transform>());
        return Vector3.Distance(Position(player), new Vector3(hook.X, hook.Y, hook.Z));
    }

    /// <summary>A block of terrain overhead at y 520, and a player hanging under it.</summary>
    private static (HeadlessScene Scene, Entity Player) UnderAnOverhang()
    {
        var scene = new HeadlessScene();
        scene.WorldVolume.SetBlock(0, 520, 0, BlockId.Stone);
        var player = scene.SpawnLocalPlayer(new Vector3(0.5f, 510, 0.5f));
        Grapple(scene, player, press: true);
        return (scene, player);
    }

    [Fact]
    public void ARopeHooksOntoTerrainAndHoldsThePlayerUpStretchingALittle()
    {
        var (scene, player) = UnderAnOverhang();
        using var _ = scene;
        Assert.True(player.Has<Grapple>());
        float length = player.Get<Grapple>().Length;
        Assert.InRange(length, 9.5f, 10.5f);

        Hold(scene, player, 60 * 4);
        float stretch = DistanceToHook(player) - length;
        Assert.InRange(stretch, 0.05f, 0.5f); // springy, not rigid, but not much
        Assert.True(player.Get<CharacterControllerComponent>().Character.LinearVelocity.Length() < 0.5f, "settled");
    }

    [Fact]
    public void LettingGoOfRReleasesTheRope()
    {
        var (scene, player) = UnderAnOverhang();
        using var _ = scene;
        Hold(scene, player, 60);
        player.Get<PlayerInput>() = new PlayerInput { Pitch = Up, Aiming = true };
        scene.Tick();
        Assert.False(player.Has<Grapple>());
        Assert.False(player.Get<CharacterControllerComponent>().Character.Grappling);
        float y = Position(player).Y;
        scene.Tick(30);
        Assert.True(Position(player).Y < y - 1f, "falls once let go");
    }

    [Fact]
    public void NothingInReachHooksNothing()
    {
        using var scene = new HeadlessScene();
        var player = scene.SpawnLocalPlayer(new Vector3(0.5f, 510, 0.5f));
        Grapple(scene, player, press: true);
        Assert.False(player.Has<Grapple>());
    }

    [Fact]
    public void ASwingKeepsItsMomentumAndStaysOnTheRope()
    {
        var (scene, player) = UnderAnOverhang();
        using var _ = scene;
        Hold(scene, player, 60 * 3);
        float length = player.Get<Grapple>().Length;
        float bottom = Position(player).Y;
        player.Get<CharacterControllerComponent>().Character.SetVelocity(new Vector3(8, 0, 0));

        float highest = bottom, longest = 0f;
        for (int i = 0; i < 60 * 3; i++)
        {
            Hold(scene, player, 1);
            highest = MathF.Max(highest, Position(player).Y);
            longest = MathF.Max(longest, DistanceToHook(player));
        }
        // 8 m/s swings up about v²/2g = 64/36 ≈ 1.8 blocks with the air brake off; it would barely rise with it on.
        Assert.True(highest - bottom > 1.2f, $"rose {highest - bottom} blocks");
        Assert.InRange(longest - length, 0f, 1f);
    }

    [Fact]
    public void ARopeHooksOntoAShip()
    {
        using var scene = new HeadlessScene();
        var voxels = new List<GridVoxel>();
        for (int x = 0; x < 4; x++) for (int z = 0; z < 4; z++) voxels.Add(new(x, 0, z, BlockId.Wood, BlockOrientation.Upright));
        var ship = scene.SpawnGrid(GridDescription.FromVoxels(new Vector3(0, 500, 0), voxels));
        var shipAt = ship.Get<Transform>().Position;
        var player = scene.SpawnLocalPlayer(new Vector3(shipAt.X + 2f, 492, shipAt.Z + 2f));
        Grapple(scene, player, press: true);
        Assert.True(player.Has<Grapple>());
        Assert.Equal(ship, player.Get<Grapple>().Anchor);

        Hold(scene, player, 60 * 4);
        float stretch = DistanceToHook(player) - player.Get<Grapple>().Length;
        Assert.InRange(stretch, 0.05f, 0.5f);
    }

    [Fact]
    public void SpaceClimbsTheRopeAndCtrlLetsItOut()
    {
        var (scene, player) = UnderAnOverhang();
        using var _ = scene;
        Hold(scene, player, 60 * 2);
        float hanging = Position(player).Y;

        Hold(scene, player, 60, PlayerButtons.Up); // a second at 4 blocks/s
        Assert.InRange(player.Get<Grapple>().Length, 5.5f, 6.5f);
        Hold(scene, player, 60 * 2);
        Assert.InRange(Position(player).Y - hanging, 3.5f, 4.5f);

        Hold(scene, player, 30, PlayerButtons.Crouch);
        Assert.InRange(player.Get<Grapple>().Length, 7.5f, 8.5f);
        Hold(scene, player, 60 * 2);
        Assert.InRange(Position(player).Y - hanging, 1.5f, 2.5f);

        Hold(scene, player, 60 * 5, PlayerButtons.Up); // no shorter than a block
        Assert.Equal(GrappleSystem.MinimumLength, player.Get<Grapple>().Length);
    }

    [Fact]
    public void WasdStillSteersWhileHanging()
    {
        var (scene, player) = UnderAnOverhang();
        using var _ = scene;
        Hold(scene, player, 60 * 2);
        float x = Position(player).X;
        Hold(scene, player, 30, PlayerButtons.Right); // facing -Z, so right is +X
        Assert.True(Position(player).X - x > 0.3f, $"moved {Position(player).X - x} blocks");
    }
}
