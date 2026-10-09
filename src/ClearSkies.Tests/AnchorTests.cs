using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Anchors hold ships to the terrain and to each other, toggles switch them, and a ship held to another is saved
/// and comes back aboard it.</summary>
public class AnchorTests
{
    private static GridVoxel V(int x, int y, int z, BlockId id) => new(x, y, z, id, BlockOrientation.Upright);

    /// <summary>A grid whose block space (voxel (0,0,0)'s corner) is at <paramref name="origin"/>.</summary>
    private static GridDescription Grid(Vector3 origin, bool locked, params GridVoxel[] voxels) =>
        new() { Body = BodyState.At(origin), Locked = locked, Voxels = voxels.ToList() };

    private static Entity Spawn(HeadlessScene scene, GridDescription d)
    {
        var grid = scene.SpawnGrid(d);
        Assert.True(scene.TickUntil(() => grid.Has<PhysicsBodyComponent>(), 10), "no body");
        return grid;
    }

    private static void SetAnchored(HeadlessScene scene, Entity ship, bool anchored) =>
        scene.Commands.Send(new SetShipAnchored { Ship = ship.Get<EntityId>(), Anchored = anchored });

    /// <summary>Edits a grid's blocks now, as the edit's event would (no editor to be in reach).</summary>
    private static void Edit(HeadlessScene scene, Entity grid, params VoxelOp[] ops) =>
        ((EditVoxelsHandler)scene.Commands.HandlerFor(CommandIds.EditVoxels)!).Apply(new EditVoxels { Volume = grid.Get<EntityId>(), Ops = ops }, default);

    private static bool Anchored(Entity ship) => ship.Get<ShipControls>().Anchored;

    private static Vector3 Position(Entity e)
    {
        var p = e.Get<Transform>().Position;
        return new Vector3(p.X, p.Y, p.Z);
    }

    /// <summary>Where <paramref name="rider"/>'s block space is in <paramref name="onto"/>'s.</summary>
    private static Vector3 On(Entity rider, Entity onto)
    {
        var t = onto.Get<Transform>();
        var q = new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W);
        return Vector3.Transform(Position(rider) - Position(onto), Quaternion.Conjugate(q));
    }

    private static List<AnchorLink> Links(Entity grid) => grid.Has<AnchorLinks>() ? grid.Get<AnchorLinks>().All : new();

    // ── descriptions ────────────────────────────────────────────────────────

    [Fact]
    public void AGridDescriptionKeepsItsHoldsAndOldOnesHaveNone()
    {
        var d = Grid(new Vector3(1, 2, 3), locked: false, V(0, 0, 0, BlockId.Anchor));
        d.Controls.Anchored = true;
        d.Anchors.Add(new AnchorLink { Target = new EntityId(2000), LocalPosition = new Vector3(3, 1, 3),
                                       LocalRotation = Quaternion.CreateFromYawPitchRoll(0.5f, 0, 0), HeldByTarget = true });
        d.Anchors.Add(new AnchorLink { Target = EntityRegistry.WorldVolume, LocalPosition = new Vector3(9, 9, 9),
                                       LocalRotation = Quaternion.Identity, HeldByRider = true });
        var bytes = DescriptionBytes.Of(d);
        var back = DescriptionBytes.Read<GridDescription>(bytes);
        Assert.True(back.Controls.Anchored);
        Assert.Equal(d.Anchors, back.Anchors);
        Assert.Equal(d.Anchors, GridDescription.ReadAnchors(bytes));
        Assert.Equal(new EntityId(2000), AnchorLinks.Support(back.Anchors)!.Value.Target);

        // Saved before anchors: not anchored, held to nothing.
        int anchorBytes = 1 + 1 + 2 * (4 + 12 + 16 + 1);
        var old = DescriptionBytes.Read<GridDescription>(bytes.AsSpan(0, bytes.Length - anchorBytes));
        Assert.False(old.Controls.Anchored);
        Assert.Empty(old.Anchors);
        Assert.Empty(GridDescription.ReadAnchors(bytes.AsSpan(0, bytes.Length - anchorBytes)));
    }

    // ── toggles ─────────────────────────────────────────────────────────────

    [Fact]
    public void ClickingAToggleFlipsItsShipsAnchors()
    {
        using var scene = new HeadlessScene();
        using var toggles = new ToggleControlSystem(scene.World, scene.Commands);
        var ship = Spawn(scene, Grid(new Vector3(0, 100, 0), locked: true, V(0, 0, 0, BlockId.Stone), V(0, 1, 0, BlockId.Toggle)));
        Assert.True(ship.Get<ChunkGrid>().Volume.TryGetBlockEntity(0, 1, 0, out var toggle));

        void Click()
        {
            scene.World.Publish(new BlockInteraction(toggle, InteractionPhase.Began, Vector3D<float>.Zero, -Vector3D<float>.UnitY, Vector2D<float>.Zero));
            scene.World.Publish(new BlockInteraction(toggle, InteractionPhase.Ended, Vector3D<float>.Zero, -Vector3D<float>.UnitY, Vector2D<float>.Zero));
        }

        Click();
        scene.Commands.Update(0);
        Assert.True(Anchored(ship));
        Assert.Equal(Engine.ECS.LeverControlSystem.MaxAngle, ToggleControlSystem.ArmAngle(toggle.Get<BlockRef>()));
        // No anchor on the ship: it can't hold, so its toggles go back off.
        scene.Tick(3);
        Assert.False(Anchored(ship));
        Assert.Equal(-Engine.ECS.LeverControlSystem.MaxAngle, ToggleControlSystem.ArmAngle(toggle.Get<BlockRef>()));
    }

    // ── holding ─────────────────────────────────────────────────────────────

    [Fact]
    public void AnAnchorHoldsAShipToTheTerrainBesideIt()
    {
        using var scene = new HeadlessScene();
        scene.WorldVolume.SetBlock(0, 40, 0, BlockId.Stone);
        // The stone overhangs clear of the terrain, so nothing but the anchor holds the ship up.
        var ship = Spawn(scene, Grid(new Vector3(0, 41, 0), locked: false, V(0, 0, 0, BlockId.Anchor), V(1, 0, 0, BlockId.Wood), V(2, 0, 0, BlockId.Stone)));
        SetAnchored(scene, ship, true);
        scene.Tick(120);

        Assert.True(Anchored(ship));
        Assert.Equal((true, true), scene.Anchors.HoldOn(ship, EntityRegistry.WorldVolume)); // welded, to a pin
        Assert.InRange(Position(ship).Y, 40.98f, 41.02f); // held up (2 s of falling is 12 blocks)
        Assert.Equal(1, scene.Anchors.Holding(ship));

        // Breaking its anchor lets it go, and turns its toggles off.
        Edit(scene, ship, VoxelOp.SetBlock(new(0, 0, 0), BlockId.Air, BlockOrientation.Upright));
        scene.Tick(60);
        Assert.False(Anchored(ship));
        Assert.Empty(Links(ship));
        Assert.True(Position(ship).Y < 39f, $"still held, at {Position(ship)}");
    }

    [Fact]
    public void WithNothingInReachTheTogglesGoBackOff()
    {
        using var scene = new HeadlessScene();
        scene.WorldVolume.SetBlock(0, 40, 0, BlockId.Stone);
        // Two blocks of air between the anchor and the terrain: out of reach.
        var ship = Spawn(scene, Grid(new Vector3(0, 43, 0), locked: true, V(0, 0, 0, BlockId.Anchor)));
        SetAnchored(scene, ship, true);
        scene.Tick(3);
        Assert.False(Anchored(ship));
        Assert.Empty(Links(ship));
    }

    [Fact]
    public void ALighterShipAnchoredOnAnotherRidesWithItThroughAnEdit()
    {
        using var scene = new HeadlessScene();
        var deck = Enumerable.Range(0, 64).Select(i => V(i % 8, 0, i / 8, BlockId.Stone)).ToArray();
        var carrier = Spawn(scene, Grid(new Vector3(0, 60, 0), locked: false, deck));
        var dinghy = Spawn(scene, Grid(new Vector3(3, 61, 3), locked: false, V(0, 0, 0, BlockId.Anchor), V(1, 0, 0, BlockId.Wood)));
        SetAnchored(scene, dinghy, true);
        scene.Tick(2);

        var link = Assert.Single(Links(dinghy));
        Assert.Equal(carrier.Get<EntityId>(), link.Target);
        Assert.True(link.HeldByRider);
        Assert.Equal((true, false), scene.Anchors.HoldOn(dinghy, carrier.Get<EntityId>())); // welded to its body

        // The carrier flies off sideways (and falls): the dinghy stays where it was on its deck.
        scene.Physics.SetBodyLinearVelocity(carrier.Get<PhysicsBodyComponent>().Body, new Vector3(6, 0, 0));
        scene.Tick(60);
        Assert.True(Position(carrier).X > 4f, "the carrier didn't move");
        Assert.True(Vector3.Distance(On(dinghy, carrier), new Vector3(3, 1, 3)) < 0.05f, $"slid to {On(dinghy, carrier)}");

        // Building on the dinghy moves its centre of mass, and so the weld's offset: it stays put.
        Edit(scene, dinghy, VoxelOp.SetBlock(new(1, 1, 0), BlockId.Stone, BlockOrientation.Upright),
                            VoxelOp.SetBlock(new(1, 2, 0), BlockId.Stone, BlockOrientation.Upright));
        scene.Tick(60);
        Assert.True(Vector3.Distance(On(dinghy, carrier), new Vector3(3, 1, 3)) < 0.05f, $"slid to {On(dinghy, carrier)}");
    }

    [Fact]
    public void ACarriersAnchorsEachHoldTheirOwnBombAndLetThemAllGo()
    {
        using var scene = new HeadlessScene();
        var hull = Enumerable.Range(0, 64).Select(i => V(i % 8, 0, i / 8, BlockId.Stone))
            .Append(V(1, -1, 1, BlockId.Anchor)).Append(V(6, -1, 6, BlockId.Anchor)).ToArray();
        var carrier = Spawn(scene, Grid(new Vector3(0, 60, 0), locked: true, hull));
        var bombs = new[] { new Vector3(1, 58, 1), new Vector3(6, 58, 6) }
            .Select(at => Spawn(scene, Grid(at, locked: false, V(0, 0, 0, BlockId.Stone)))).ToArray();
        SetAnchored(scene, carrier, true);
        scene.Tick(60);

        Assert.True(Anchored(carrier));
        Assert.Equal(2, scene.Anchors.Holding(carrier));
        foreach (var bomb in bombs)
        {
            // The bomb is lighter: it keeps the hold and rides on the carrier, though the carrier's anchors hold it.
            var link = Assert.Single(Links(bomb));
            Assert.Equal(carrier.Get<EntityId>(), link.Target);
            Assert.True(link.HeldByTarget && !link.HeldByRider);
            Assert.InRange(Position(bomb).Y, 57.98f, 58.02f);
        }
        Assert.Empty(Links(carrier));

        SetAnchored(scene, carrier, false);
        scene.Tick(60);
        foreach (var bomb in bombs)
        {
            Assert.Empty(Links(bomb));
            Assert.True(Position(bomb).Y < 56f, "bomb still held");
        }
    }

    [Fact]
    public void AGridHeldToAnotherSpawnsOnItAsItIsNow()
    {
        using var scene = new HeadlessScene();
        var carrier = Spawn(scene, Grid(new Vector3(100, 60, -20), locked: true, V(0, 0, 0, BlockId.Stone)));
        var d = Grid(Vector3.Zero, locked: false, V(0, 0, 0, BlockId.Anchor)); // described somewhere else entirely
        d.Anchors.Add(new AnchorLink { Target = carrier.Get<EntityId>(), LocalPosition = new Vector3(0, 1, 0),
                                       LocalRotation = Quaternion.Identity, HeldByRider = true });
        d.Controls.Anchored = true;
        var dinghy = Spawn(scene, d);
        Assert.True(Vector3.Distance(Position(dinghy), new Vector3(100, 61, -20)) < 1e-3f, $"spawned at {Position(dinghy)}");
        scene.Tick(60);
        Assert.Equal((true, false), scene.Anchors.HoldOn(dinghy, carrier.Get<EntityId>()));
        Assert.True(Vector3.Distance(Position(dinghy), new Vector3(100, 61, -20)) < 0.02f);
    }

    /// <summary>How well welds hold under a violent shove: an instant 10 m/s kick, then a spin. One weld holds within a few
    /// centimetres even 500 times lighter than its carrier. A stack whips (each weld only reaches the next, solved with 8
    /// iterations a tick) by up to a few tens of centimetres, and settles back in a second or so.</summary>
    [Theory]
    [InlineData(24, 1, 0.05f)]  // a dinghy over 500 times lighter than its carrier
    [InlineData(8, 3, 0.4f)]    // a carrier with a stack of three, each anchored on the one below
    public void HeldShipsStayPutWhileTheirCarrierIsShoved(int deckSize, int stack, float whip)
    {
        using var scene = new HeadlessScene();
        var deck = Enumerable.Range(0, deckSize * deckSize)
            .SelectMany(i => new[] { V(i % deckSize, 0, i / deckSize, BlockId.Stone), V(i % deckSize, -1, i / deckSize, BlockId.Stone) }).ToArray();
        var carrier = Spawn(scene, Grid(new Vector3(0, 60, 0), locked: false, deck));
        var riders = new List<(Entity Grid, Vector3 At)>();
        for (int i = 0; i < stack; i++)
        {
            var grid = Spawn(scene, Grid(new Vector3(3, 61 + i, 3), locked: false, V(0, 0, 0, BlockId.Anchor)));
            SetAnchored(scene, grid, true);
            scene.Tick(2);
            Assert.Single(Links(grid));
            riders.Add((grid, default));
        }
        // Where each took hold (each fell a little, apart from the carrier, before its anchor caught).
        scene.Tick(10);
        for (int i = 0; i < riders.Count; i++) riders[i] = (riders[i].Grid, On(riders[i].Grid, carrier));

        var body = carrier.Get<PhysicsBodyComponent>().Body;
        float worst = 0f, settled = 0f;
        for (int t = 0; t < 180; t++)
        {
            if (t == 0) scene.Physics.SetBodyLinearVelocity(body, new Vector3(10, 4, 0));
            if (t == 60) scene.Physics.SetBodyAngularVelocity(body, new Vector3(0, 2, 0.5f));
            scene.Tick();
            foreach (var (grid, at) in riders)
            {
                float slip = Vector3.Distance(On(grid, carrier), at);
                worst = MathF.Max(worst, slip);
                if (t >= 170) settled = MathF.Max(settled, slip);
            }
        }
        Assert.True(worst < whip, $"whipped {worst:F3}");
        Assert.True(settled < 0.02f, $"settled {settled:F3} off");
    }

    // ── saving ──────────────────────────────────────────────────────────────

    [Fact]
    public void AShipDockedOnAnotherComesBackAboardItAfterARestart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cs-test-{Guid.NewGuid():N}.db");
        try
        {
            EntityId carrierId, dinghyId;
            using (var game = new LoopbackGame(save: SaveDatabase.Open(path)))
            {
                var deck = Enumerable.Range(0, 64).Select(i => V(i % 8, 0, i / 8, BlockId.Stone)).ToArray();
                var carrier = Spawn(game.Host, Grid(new Vector3(400, 50, 0), locked: true, deck));
                var dinghy = Spawn(game.Host, Grid(new Vector3(403, 51, 3), locked: false, V(0, 0, 0, BlockId.Anchor), V(1, 0, 0, BlockId.Wood)));
                SetAnchored(game.Host, dinghy, true);
                game.Tick(30);
                Assert.Single(Links(dinghy));
                (carrierId, dinghyId) = (carrier.Get<EntityId>(), dinghy.Get<EntityId>());
                game.SaveAll();
            }

            using (var game = new LoopbackGame(save: SaveDatabase.Open(path)))
            {
                game.Join("crew", wait: false);
                float worst = 0f;
                bool both = false;
                for (int i = 0; i < 300; i++)
                {
                    game.Tick();
                    var carrier = game.Host.Registry.Find(carrierId);
                    var dinghy = game.Host.Registry.Find(dinghyId);
                    if (dinghy is null) continue;
                    Assert.NotNull(carrier); // never aboard nothing
                    both = true;
                    worst = MathF.Max(worst, Vector3.Distance(On(dinghy.Value, carrier!.Value), new Vector3(3, 1, 3)));
                }
                Assert.True(both, "never loaded");
                var back = game.Host.Registry.Find(dinghyId)!.Value;
                Assert.True(Anchored(back));
                Assert.Equal((true, false), game.Host.Anchors.HoldOn(back, carrierId));
                Assert.True(worst < 0.05f, $"moved {worst:F3} on the deck");
            }
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) File.Delete(f);
        }
    }
}
