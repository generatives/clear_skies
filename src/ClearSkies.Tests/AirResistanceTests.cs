using System.Numerics;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Xunit;

namespace ClearSkies.Tests;

public class AirResistanceTests
{
    /// <summary>An unlocked grid of Wood, <paramref name="sx"/>×<paramref name="sy"/>×<paramref name="sz"/> blocks, high
    /// in the sky with air resistance in <paramref name="wind"/>, and its body.</summary>
    private static (HeadlessScene Scene, Entity Grid) Ship(int sx, int sy, int sz, Vector3 wind)
    {
        var scene = new HeadlessScene();
        scene.AddAirResistance(wind);
        var voxels = new List<GridVoxel>();
        for (int x = 0; x < sx; x++) for (int y = 0; y < sy; y++) for (int z = 0; z < sz; z++)
            voxels.Add(new(x, y, z, BlockId.Wood, BlockOrientation.Upright));
        var grid = scene.SpawnGrid(GridDescription.FromVoxels(new Vector3(0, 500, 0), voxels));
        scene.SpawnLocalPlayer(new Vector3(0, 520, 0), freeFly: true); // someone nearby, so the grid is simulated
        scene.Tick();
        scene.Commands.Send(new SetGridLocked { Grid = grid.Get<EntityId>(), Locked = false });
        scene.Tick();
        return (scene, grid);
    }

    [Fact]
    public void AShipsDragEntriesComeFromItsBlocks()
    {
        var (scene, grid) = Ship(5, 1, 3, Vector3.Zero);
        using var _ = scene;
        var faces = grid.Get<ResistsAir>().Faces;
        Assert.Equal(3f, faces[ResistsAir.Index(0, true)].Area);  // ±x: 1 high × 3 deep
        Assert.Equal(15f, faces[ResistsAir.Index(1, true)].Area); // ±y: 5 × 3
        Assert.Equal(5f, faces[ResistsAir.Index(2, false)].Area); // ±z: 5 × 1
        // Centres relative to the centre of mass (2.5, 0.5, 1.5): the +x face at x = 5, the −y face at y = 0.
        Assert.True(Vector3.Distance(new Vector3(2.5f, 0, 0), faces[ResistsAir.Index(0, true)].Centroid) < 1e-4f);
        Assert.True(Vector3.Distance(new Vector3(0, -0.5f, 0), faces[ResistsAir.Index(1, false)].Centroid) < 1e-4f);
    }

    [Fact]
    public void EditingAShipWorksOutItsEntriesAgain()
    {
        var (scene, grid) = Ship(2, 1, 1, Vector3.Zero);
        using var _ = scene;
        Assert.Equal(1f, grid.Get<ResistsAir>().Faces[ResistsAir.Index(0, true)].Area);
        grid.Get<ChunkGrid>().Volume.SetBlock(0, 1, 0, BlockId.Wood);
        scene.Tick();
        Assert.Equal(2f, grid.Get<ResistsAir>().Faces[ResistsAir.Index(0, true)].Area);
    }

    [Fact]
    public void AShipIsCarriedUpToTheWindsSpeedButNeverPastIt()
    {
        var wind = new Vector3(6, 0, 0);
        var (scene, grid) = Ship(3, 3, 3, wind);
        using var _ = scene;
        var body = grid.Get<PhysicsBodyComponent>().Body;
        float fastest = 0f;
        for (int t = 0; t < 60 * 60; t++)
        {
            scene.Tick();
            fastest = MathF.Max(fastest, scene.Physics.GetBodyLinearVelocity(body).X);
        }
        Assert.InRange(scene.Physics.GetBodyLinearVelocity(body).X, 5f, 6f);
        Assert.True(fastest <= 6f + 1e-3f, $"reached {fastest} m/s in a 6 m/s wind");
    }

    [Fact]
    public void ACubeMeetsTheSameDragFlyingDiagonallyAsStraight()
    {
        float Slowed(Vector3 velocity)
        {
            var (scene, grid) = Ship(3, 3, 3, Vector3.Zero);
            using var _ = scene;
            var body = grid.Get<PhysicsBodyComponent>().Body;
            scene.Physics.SetBodyLinearVelocity(body, velocity);
            scene.Physics.SetBodyAngularVelocity(body, Vector3.Zero);
            scene.Tick();
            var v = scene.Physics.GetBodyLinearVelocity(body);
            return new Vector2(velocity.X, velocity.Z).Length() - new Vector2(v.X, v.Z).Length();
        }
        float straight = Slowed(new Vector3(10, 0, 0));
        float diagonal = Slowed(new Vector3(10, 0, 10) / MathF.Sqrt(2));
        Assert.True(straight > 0f);
        Assert.True(MathF.Abs(diagonal - straight) < 0.02f * straight, $"straight {straight}, diagonal {diagonal}");
    }

    [Fact]
    public void AFallingPlayerReachesATerminalSpeed()
    {
        using var scene = new HeadlessScene();
        scene.AddAirResistance();
        var player = scene.SpawnLocalPlayer(new Vector3(0, 2000, 0));
        scene.Tick(60 * 10);
        var v = player.Get<CharacterControllerComponent>().Character.LinearVelocity;
        Assert.InRange(-v.Y, 15f, 30f);
    }

    [Fact]
    public void AnAirbornePlayerIsBlownAlongByAStrongWind()
    {
        // (The character's own air brake, with no keys held, holds them against a light wind: 50 N against 12 N at 8 m/s.)
        using var scene = new HeadlessScene();
        scene.AddAirResistance(new Vector3(20, 0, 0));
        var player = scene.SpawnLocalPlayer(new Vector3(0, 2000, 0));
        scene.Tick(60 * 3);
        var v = player.Get<CharacterControllerComponent>().Character.LinearVelocity;
        Assert.True(v.X > 1f, $"drifting at {v.X} m/s");
    }

    /// <summary>A player dropped high in still air, holding <paramref name="held"/> and looking ahead (−z) at
    /// <paramref name="pitch"/>, for <paramref name="seconds"/>; their velocity then, and whether they're gliding.</summary>
    private static (Vector3 Velocity, bool Gliding) Drop(PlayerButtons held, float pitch, float seconds)
    {
        using var scene = new HeadlessScene();
        scene.AddAirResistance();
        var player = scene.SpawnLocalPlayer(new Vector3(0, 2000, 0));
        for (int t = 0; t < seconds * 60; t++)
        {
            player.Get<PlayerInput>() = new PlayerInput { Held = held, Pitch = pitch };
            scene.Tick();
        }
        var character = player.Get<CharacterControllerComponent>().Character;
        return (character.LinearVelocity, character.Gliding);
    }

    [Fact]
    public void HoldingSpaceWhileFallingGlidesAheadSlowly()
    {
        var (falling, _) = Drop(PlayerButtons.None, 0f, 10f);
        var (gliding, isGliding) = Drop(PlayerButtons.Up, 0f, 10f);
        Assert.True(isGliding);
        Assert.InRange(-gliding.Y, 0.3f, 3f); // sinking gently, against 15–30 m/s falling
        Assert.True(-gliding.Y < -falling.Y / 5, $"gliding sinks at {-gliding.Y} m/s, falling at {-falling.Y}");
        Assert.True(-gliding.Z > 2f * -gliding.Y, $"gliding {-gliding.Z} m/s ahead while sinking {-gliding.Y} m/s");
    }

    [Fact]
    public void DivingPicksUpSpeed()
    {
        var (gliding, _) = Drop(PlayerButtons.Up, 0f, 10f);
        var (diving, isGliding) = Drop(PlayerButtons.Up, -1.2f, 10f);
        Assert.True(isGliding);
        Assert.True(diving.Length() > 2f * gliding.Length(), $"diving at {diving.Length()} m/s, gliding at {gliding.Length()}");
    }

    /// <summary>A gliding player high in still air, run for <paramref name="seconds"/> at each look in turn.</summary>
    private static Entity Glide(HeadlessScene scene, Entity player, float yaw, float pitch, float seconds)
    {
        for (int t = 0; t < seconds * 60; t++)
        {
            player.Get<PlayerInput>() = new PlayerInput { Held = PlayerButtons.Up, Yaw = yaw, Pitch = pitch };
            scene.Tick();
        }
        return player;
    }

    [Fact]
    public void AGliderBanksRoundToFollowTheLook()
    {
        using var scene = new HeadlessScene();
        scene.AddAirResistance();
        var player = scene.SpawnLocalPlayer(new Vector3(0, 2000, 0));
        Glide(scene, player, 0f, 0f, 10f); // gliding ahead, along −z
        Glide(scene, player, MathF.PI / 2, 0f, 1f); // looking left, along −x
        var v = player.Get<CharacterControllerComponent>().Character.LinearVelocity;
        Assert.True(-v.X > 4f * MathF.Abs(v.Z), $"a second after looking left, moving ({v.X}, {v.Z})");
    }

    [Fact]
    public void PullingUpOutOfADiveClimbsUntilTheSpeedRunsOut()
    {
        using var scene = new HeadlessScene();
        scene.AddAirResistance();
        var player = scene.SpawnLocalPlayer(new Vector3(0, 2000, 0));
        var character = () => player.Get<CharacterControllerComponent>().Character;
        Glide(scene, player, 0f, -1f, 6f);
        float bottom = character().Position.Y, fast = character().LinearVelocity.Length();
        float top = bottom, slowest = fast;
        for (int i = 0; i < 4 * 60; i++)
        {
            Glide(scene, player, 0f, 0.8f, 1f / 60);
            top = MathF.Max(top, character().Position.Y);
            slowest = MathF.Min(slowest, character().LinearVelocity.Length());
        }
        Assert.True(top > bottom + 5f, $"climbed {top - bottom} m");
        Assert.True(slowest < fast / 3, $"slowed from {fast} to {slowest} m/s");
        Assert.True(character().Position.Y < top - 1f, "kept climbing with no speed left");
        Assert.True(-character().LinearVelocity.Z > 0f, "slid backwards after the climb");
    }

    [Fact]
    public void TheGliderOpensOnlyOnTheWayDownAndClosesOnLanding()
    {
        using var scene = new HeadlessScene();
        scene.AddAirResistance();
        scene.SpawnPlatform(new Vector3(-4, 0, -4), 16);
        var player = scene.SpawnLocalPlayer(new Vector3(0, 1.5f, 0));
        var character = () => player.Get<CharacterControllerComponent>().Character;
        Assert.True(scene.TickUntil(() => character().Supported, 120));

        // Space held from the jump: rising, no glider; on the way down, it opens.
        player.Get<PlayerInput>() = new PlayerInput { Held = PlayerButtons.Up, Pressed = PlayerButtons.Up };
        scene.Tick();
        player.Get<PlayerInput>() = new PlayerInput { Held = PlayerButtons.Up };
        scene.Tick(3);
        Assert.False(character().Gliding);
        Assert.True(scene.TickUntil(() => character().Gliding, 120));
        Assert.True(scene.TickUntil(() => character().Supported, 600));
        scene.Tick();
        Assert.False(character().Gliding);
    }
}
