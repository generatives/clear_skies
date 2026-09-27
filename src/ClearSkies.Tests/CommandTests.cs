using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

public class EditVoxelsTests
{
    private static (HeadlessScene scene, Entity grid, Entity player) Scene()
    {
        var scene = new HeadlessScene();
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0), size: 4);
        var player = scene.SpawnLocalPlayer(new Vector3(0, 52, 0), freeFly: true);
        scene.Tick();
        return (scene, grid, player);
    }

    private static EditVoxels Edit(Entity grid, Entity player, params VoxelOp[] ops) =>
        new() { Volume = grid.Get<NetId>().Value, Editor = player.Get<NetId>().Value, Ops = ops };

    private static ChunkVolume Vol(Entity grid) => grid.Get<ChunkGrid>().Volume;

    [Fact]
    public void PlacesAndBreaksBlocksInVoxelSpace()
    {
        var (scene, grid, player) = Scene();
        using var _ = scene;
        scene.Commands.Send(Edit(grid, player, VoxelOp.SetBlock(new(1, 1, 1), BlockId.Wood, BlockOrientation.Upright)));
        scene.Tick();
        Assert.Equal(BlockId.Wood, Vol(grid).GetBlock(1, 1, 1));
        scene.Commands.Send(Edit(grid, player, VoxelOp.SetBlock(new(1, 1, 1), BlockId.Air, BlockOrientation.Upright)));
        scene.Tick();
        Assert.Equal(BlockId.Air, Vol(grid).GetBlock(1, 1, 1));
    }

    [Fact]
    public void FillBoxSetsEveryCellInTheBox()
    {
        var (scene, grid, player) = Scene();
        using var _ = scene;
        scene.Commands.Send(Edit(grid, player, VoxelOp.FillBox(new(2, 3, 2), 1, BlockId.Stone, BlockOrientation.Upright)));
        scene.Tick();
        for (int x = 1; x <= 3; x++) for (int y = 2; y <= 4; y++) for (int z = 1; z <= 3; z++)
            Assert.Equal(BlockId.Stone, Vol(grid).GetBlock(x, y, z));
        Assert.Equal(BlockId.Air, Vol(grid).GetBlock(2, 5, 2));
    }

    [Fact]
    public void FillBoxAcrossChunksMatchesSettingBlockByBlock()
    {
        using var world = new World();
        var a = new ChunkVolume(world.CreateEntity(), world);
        var b = new ChunkVolume(world.CreateEntity(), world);
        var min = new Vector3D<int>(-3, 28, 30);
        var max = new Vector3D<int>(4, 35, 33);
        a.FillBox(min, max, BlockId.Dirt, BlockOrientation.Upright);
        for (int x = min.X; x <= max.X; x++) for (int y = min.Y; y <= max.Y; y++) for (int z = min.Z; z <= max.Z; z++)
            b.SetBlock(x, y, z, BlockId.Dirt);
        for (int x = min.X - 1; x <= max.X + 1; x++) for (int y = min.Y - 1; y <= max.Y + 1; y++) for (int z = min.Z - 1; z <= max.Z + 1; z++)
            Assert.Equal(b.GetBlock(x, y, z), a.GetBlock(x, y, z));
    }

    [Fact]
    public void EditsOutOfReachAreRejected()
    {
        var (scene, grid, player) = Scene();
        using var _ = scene;
        // 16 reach + 2 margin in creative, from the eye (2.7 above the deck): 22 blocks up is too far, 17 is fine.
        scene.Commands.Send(Edit(grid, player, VoxelOp.SetBlock(new(1, 24, 1), BlockId.Wood, BlockOrientation.Upright)));
        scene.Commands.Send(Edit(grid, player, VoxelOp.SetBlock(new(1, 17, 1), BlockId.Wood, BlockOrientation.Upright)));
        scene.Tick();
        Assert.Equal(BlockId.Air, Vol(grid).GetBlock(1, 24, 1));
        Assert.Equal(BlockId.Wood, Vol(grid).GetBlock(1, 17, 1));
        Assert.Equal(1, scene.Commands.Stats.Rejected);
    }

    [Fact]
    public void SurvivalAllowsSingleBlocksOnlyAndShorterReach()
    {
        var (scene, grid, player) = Scene();
        using var _ = scene;
        scene.Limits.Mode = GameMode.Survival;
        scene.Commands.Send(Edit(grid, player, VoxelOp.FillBox(new(2, 3, 2), 1, BlockId.Stone, BlockOrientation.Upright)));
        scene.Commands.Send(Edit(grid, player, VoxelOp.SetBlock(new(1, 14, 1), BlockId.Wood, BlockOrientation.Upright)));
        scene.Commands.Send(Edit(grid, player, VoxelOp.SetBlock(new(1, 5, 1), BlockId.Wood, BlockOrientation.Upright)));
        scene.Tick();
        Assert.Equal(BlockId.Air, Vol(grid).GetBlock(2, 3, 2));
        Assert.Equal(BlockId.Air, Vol(grid).GetBlock(1, 14, 1));
        Assert.Equal(BlockId.Wood, Vol(grid).GetBlock(1, 5, 1));
    }

    [Fact]
    public void CreativeBrushIsLimitedToRadiusEight()
    {
        var (scene, grid, player) = Scene();
        using var _ = scene;
        scene.Commands.Send(Edit(grid, player, VoxelOp.FillBox(new(2, 9, 2), 9, BlockId.Stone, BlockOrientation.Upright)));
        scene.Tick();
        Assert.Equal(1, scene.Commands.Stats.Rejected);
        scene.Commands.Send(Edit(grid, player, VoxelOp.FillBox(new(2, 9, 2), 8, BlockId.Stone, BlockOrientation.Upright)));
        scene.Tick();
        Assert.Equal(BlockId.Stone, Vol(grid).GetBlock(2, 9, 2));
    }

    [Fact]
    public void BreakingTheLastBlockDespawnsTheGrid()
    {
        var (scene, grid, player) = Scene();
        using var _ = scene;
        scene.Commands.Send(Edit(grid, player, VoxelOp.FillBox(new(1, 0, 1), 2, BlockId.Air, BlockOrientation.Upright)));
        scene.Tick();
        Assert.False(grid.IsAlive);
    }

    [Fact]
    public void UndoRestoresBlocksAndBlockEntityState()
    {
        var (scene, grid, player) = Scene();
        using var _ = scene;
        var volume = Vol(grid);
        volume.SetBlock(1, 1, 1, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.North));
        Assert.True(volume.TryGetBlockEntity(1, 1, 1, out var lever));
        lever.Get<Lever>().Value = 0.7f;
        var handler = (EditVoxelsHandler)scene.Commands.HandlerFor(CommandIds.EditVoxels)!;
        var edit = Edit(grid, player, VoxelOp.FillBox(new(1, 1, 1), 1, BlockId.Wood, BlockOrientation.Upright));
        var undo = handler.Capture(edit);
        handler.Apply(edit, default);
        Assert.Equal(BlockId.Wood, volume.GetBlock(1, 1, 1));
        handler.Restore(undo);
        Assert.Equal(BlockId.Lever, volume.GetBlock(1, 1, 1));
        Assert.Equal(BlockId.Stone, volume.GetBlock(1, 0, 1));
        Assert.Equal(BlockId.Air, volume.GetBlock(2, 2, 2));
        Assert.True(volume.TryGetBlockEntity(1, 1, 1, out var restored));
        Assert.Equal(0.7f, restored.Get<Lever>().Value);
    }

    [Fact]
    public void ANewLeverTakesTheSettingOfItsAxis()
    {
        var (scene, grid, player) = Scene();
        using var _ = scene;
        var volume = Vol(grid);
        volume.SetBlock(0, 1, 0, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.North));
        volume.TryGetBlockEntity(0, 1, 0, out var first);
        first.Get<Lever>().Value = 0.5f;
        // Facing the other way on the same axis: the negated setting.
        scene.Commands.Send(Edit(grid, player, VoxelOp.SetBlock(new(3, 1, 3), BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.South))));
        scene.Tick();
        Assert.True(volume.TryGetBlockEntity(3, 1, 3, out var second));
        Assert.Equal(-0.5f, second.Get<Lever>().Value);
    }

    [Fact]
    public void RoundTripsThroughTheWireFormat()
    {
        var handler = new EditVoxelsHandler(null!, new EditLimits());
        var edit = new EditVoxels
        {
            Volume = 1, Editor = 2000,
            Ops = new[] { VoxelOp.SetBlock(new(-5, 7, 9), BlockId.Fan, BlockOrientation.From(Direction.East, Direction.Up)),
                          VoxelOp.FillBox(new(1, 2, 3), 2, BlockId.Air, BlockOrientation.Upright) },
        };
        var w = new NetWriter();
        handler.Write(w, edit);
        var r = new NetReader(w.Written);
        var back = handler.Read(ref r);
        Assert.Equal(edit.Volume, back.Volume);
        Assert.Equal(edit.Editor, back.Editor);
        Assert.Equal(edit.Ops, back.Ops);
        Assert.True(r.AtEnd);
    }
}

public class ControlCommandTests
{
    private static (HeadlessScene scene, Entity grid, Entity player) Scene()
    {
        var scene = new HeadlessScene();
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0), size: 6);
        var player = scene.SpawnLocalPlayer(new Vector3(0, 52, 0), freeFly: true);
        scene.Tick(2); // presence, then the body
        return (scene, grid, player);
    }

    private static EntityAddress At(Entity grid, int x, int y, int z) => EntityAddress.OfBlock(grid.Get<NetId>().Value, new(x, y, z));

    [Fact]
    public void LeversOnOneAxisMoveTogetherAndOthersDont()
    {
        var (scene, grid, _) = Scene();
        using var __ = scene;
        var v = grid.Get<ChunkGrid>().Volume;
        v.SetBlock(0, 1, 0, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.North)); // N/S axis
        v.SetBlock(1, 1, 0, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.South)); // same axis, reversed
        v.SetBlock(2, 1, 0, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.East));  // E/W axis
        scene.Commands.Send(new SetLever { Lever = At(grid, 0, 1, 0), Value = 0.4f });
        scene.Tick();
        float Value(int x) { v.TryGetBlockEntity(x, 1, 0, out var e); return e.Get<Lever>().Value; }
        Assert.Equal(0.4f, Value(0));
        Assert.Equal(-0.4f, Value(1));
        Assert.Equal(0f, Value(2));
    }

    [Fact]
    public void LeverValuesAreClamped()
    {
        var (scene, grid, _) = Scene();
        using var __ = scene;
        var v = grid.Get<ChunkGrid>().Volume;
        v.SetBlock(0, 1, 0, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.North));
        scene.Commands.Send(new SetLever { Lever = At(grid, 0, 1, 0), Value = 3f });
        scene.Tick();
        v.TryGetBlockEntity(0, 1, 0, out var e);
        Assert.Equal(1f, e.Get<Lever>().Value);
    }

    [Fact]
    public void AShipsWheelsTurnTogether()
    {
        var (scene, grid, _) = Scene();
        using var __ = scene;
        var v = grid.Get<ChunkGrid>().Volume;
        v.SetBlock(0, 1, 0, BlockId.SteeringWheel, BlockOrientation.Upright);
        v.SetBlock(3, 1, 3, BlockId.SteeringWheel, BlockOrientation.Upright);
        scene.Commands.Send(new SetWheel { Wheel = At(grid, 0, 1, 0), Angle = 1.2f });
        scene.Tick();
        v.TryGetBlockEntity(3, 1, 3, out var other);
        Assert.Equal(1.2f, other.Get<SteeringWheel>().Angle);
    }

    [Fact]
    public void LockingMakesTheBodyKinematicAndUnlockingDynamic()
    {
        var (scene, grid, _) = Scene();
        using var __ = scene;
        uint id = grid.Get<NetId>().Value;
        var body = grid.Get<PhysicsBodyComponent>().Body;
        Assert.True(grid.Get<DynamicGrid>().Locked); // grids start locked
        scene.Commands.Send(new SetGridLocked { Grid = id, Locked = false });
        scene.Tick();
        Assert.False(grid.Get<DynamicGrid>().Locked);
        Assert.True(scene.Physics.GetBodyMass(body) > 0);
        scene.Commands.Send(new SetGridLocked { Grid = id, Locked = true });
        scene.Tick();
        Assert.Equal(0f, scene.Physics.GetBodyMass(body));
    }

    [Fact]
    public void RightingResetsRotation()
    {
        var (scene, grid, _) = Scene();
        using var __ = scene;
        var body = grid.Get<PhysicsBodyComponent>().Body;
        var (p, _) = scene.Physics.GetBodyPose(body);
        scene.Physics.SetBodyPose(body, p, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f));
        scene.Commands.Send(new RightGrid { Grid = grid.Get<NetId>().Value });
        scene.Tick();
        Assert.Equal(Quaternion.Identity, scene.Physics.GetBodyPose(body).orientation);
    }

    [Fact]
    public void TheWalkFlyToggleIsACommand()
    {
        var (scene, _, player) = Scene();
        using var __ = scene;
        player.Get<Engine.Input.PlayerInput>() = new Engine.Input.PlayerInput { Pressed = Engine.Input.PlayerButtons.ToggleFly };
        scene.Tick();
        Assert.False(player.Get<CharacterModeComponent>().FreeFly);
        Assert.True(scene.Commands.Stats.Applied > 0);
    }
}

/// <summary>A router standing in for the network, recording what's sent.</summary>
public sealed class RecordingRouter : ICommandRouter
{
    public readonly List<(PeerId To, ushort Id, uint Seq, byte[] Payload)> Commands = new();
    public readonly List<(ushort Id, EventMeta Meta, byte[] Payload)> Events = new();
    public readonly List<(PeerId To, uint Seq)> Rejections = new();
    public void SendCommand(PeerId authority, ushort handlerId, uint seq, ReadOnlySpan<byte> payload) => Commands.Add((authority, handlerId, seq, payload.ToArray()));
    public void BroadcastEvent(ushort handlerId, in EventMeta meta, ReadOnlySpan<byte> payload) => Events.Add((handlerId, meta, payload.ToArray()));
    public void SendRejection(PeerId to, uint seq) => Rejections.Add((to, seq));
}

public class PredictionTests
{
    private static readonly PeerId Client = new(2);

    /// <summary>A client whose grid (with two levers on one axis) is owned by the host: every command goes away to be
    /// decided.</summary>
    private static (HeadlessScene scene, RecordingRouter router, Entity grid, Func<float> lever) ClientScene()
    {
        var session = new Session(SessionRole.Client, Client);
        var scene = new HeadlessScene(session);
        var router = new RecordingRouter();
        scene.Commands.Router = router;
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0), size: 4);
        grid.Set(session.OwnerFor(PeerId.Host));
        var v = grid.Get<ChunkGrid>().Volume;
        v.SetBlock(0, 1, 0, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.North));
        return (scene, router, grid, () => { v.TryGetBlockEntity(0, 1, 0, out var e); return e.Get<Lever>().Value; });
    }

    private static SetLever Set(Entity grid, float value) =>
        new() { Lever = EntityAddress.OfBlock(grid.Get<NetId>().Value, new(0, 1, 0)), Value = value };

    [Fact]
    public void APredictedCommandAppliesAtOnceAndGoesToItsAuthority()
    {
        var (scene, router, grid, lever) = ClientScene();
        using var _ = scene;
        scene.Commands.Send(Set(grid, 0.3f));
        scene.Commands.Update(0);
        Assert.Equal(0.3f, lever());
        Assert.Single(router.Commands);
        Assert.Equal(PeerId.Host, router.Commands[0].To);
        Assert.Equal(1, scene.Commands.PendingCount);
    }

    [Fact]
    public void AnUnpredictedCommandWaitsForItsEvent()
    {
        var (scene, router, grid, _) = ClientScene();
        using var _ = scene;
        scene.Commands.Send(new SetGridLocked { Grid = grid.Get<NetId>().Value, Locked = false });
        scene.Commands.Update(0);
        Assert.True(grid.Get<DynamicGrid>().Locked);
        Assert.Equal(0, scene.Commands.PendingCount);
        var (to, id, seq, payload) = router.Commands[0];
        scene.Commands.ReceiveEvent(new EventMeta(Client, seq, PeerId.Host, grid.Get<NetId>().Value, 1, 0), id, payload);
        scene.Commands.Update(0);
        Assert.False(grid.Get<DynamicGrid>().Locked);
    }

    [Fact]
    public void ItsOwnEventComingBackChangesNothing()
    {
        var (scene, router, grid, lever) = ClientScene();
        using var _ = scene;
        scene.Commands.Send(Set(grid, 0.3f));
        scene.Commands.Update(0);
        var (_, id, seq, payload) = router.Commands[0];
        scene.Commands.ReceiveEvent(new EventMeta(Client, seq, PeerId.Host, grid.Get<NetId>().Value, 1, 0), id, payload);
        scene.Commands.Update(0);
        Assert.Equal(0.3f, lever());
        Assert.Equal(0, scene.Commands.PendingCount);
    }

    [Fact]
    public void ARejectionRestoresTheUndoAndRedoesLaterPredictions()
    {
        var (scene, router, grid, lever) = ClientScene();
        using var _ = scene;
        scene.Commands.Send(Set(grid, 0.3f));
        scene.Commands.Update(0);
        var v = grid.Get<ChunkGrid>().Volume;
        uint gridId = grid.Get<NetId>().Value;
        // A second, unrelated prediction to the same authority: a block edit.
        var player = scene.SpawnLocalPlayer(new Vector3(0, 52, 0), freeFly: true);
        scene.Commands.Send(new EditVoxels { Volume = gridId, Editor = player.Get<NetId>().Value,
            Ops = new[] { VoxelOp.SetBlock(new(2, 1, 2), BlockId.Wood, BlockOrientation.Upright) } });
        scene.Commands.Update(0);
        Assert.Equal(BlockId.Wood, v.GetBlock(2, 1, 2));
        Assert.Equal(2, scene.Commands.PendingCount);

        scene.Commands.ReceiveRejection(PeerId.Host, router.Commands[0].Seq);
        scene.Commands.Update(0);
        Assert.Equal(0f, lever());                         // the rejected lever is back
        Assert.Equal(BlockId.Wood, v.GetBlock(2, 1, 2));    // the later edit is still predicted
        Assert.Equal(1, scene.Commands.PendingCount);
    }

    [Fact]
    public void RejectingTheLaterOfTwoKeepsTheEarlier()
    {
        var (scene, router, grid, lever) = ClientScene();
        using var _ = scene;
        scene.Commands.Send(Set(grid, 0.3f));
        scene.Commands.Update(0);
        scene.Commands.Send(Set(grid, 0.6f));
        scene.Commands.Update(0);
        Assert.Equal(0.6f, lever());
        scene.Commands.ReceiveRejection(PeerId.Host, router.Commands[1].Seq);
        scene.Commands.Update(0);
        Assert.Equal(0.3f, lever());
    }

    [Fact]
    public void AnAdjustedEventReplacesThePrediction()
    {
        var (scene, router, grid, lever) = ClientScene();
        using var _ = scene;
        scene.Commands.Send(Set(grid, 0.3f));
        scene.Commands.Update(0);
        var (_, id, seq, _) = router.Commands[0];
        var handler = (SetLeverHandler)scene.Commands.HandlerFor(id)!;
        var w = new NetWriter();
        handler.Write(w, Set(grid, 0.25f)); // the authority decided otherwise
        scene.Commands.ReceiveEvent(new EventMeta(Client, seq, PeerId.Host, grid.Get<NetId>().Value, 1, 0), id, w.ToArray());
        scene.Commands.Update(0);
        Assert.Equal(0.25f, lever());
        Assert.Equal(0, scene.Commands.PendingCount);
    }

    [Fact]
    public void CoalescedCommandsSendOnlyTheLatestPerTargetPerTick()
    {
        var (scene, router, grid, lever) = ClientScene();
        using var _ = scene;
        scene.Commands.Send(Set(grid, 0.1f));
        scene.Commands.Send(Set(grid, 0.2f));
        scene.Commands.Send(Set(grid, 0.3f));
        scene.Commands.Update(0);
        Assert.Single(router.Commands);
        Assert.Equal(0.3f, lever());
    }

    [Fact]
    public void DuplicateEventsAreDropped()
    {
        var (scene, router, grid, lever) = ClientScene();
        using var _ = scene;
        var handler = (SetLeverHandler)scene.Commands.HandlerFor(CommandIds.SetLever)!;
        var w = new NetWriter();
        handler.Write(w, Set(grid, 0.5f));
        var meta = new EventMeta(PeerId.Host, 1, PeerId.Host, grid.Get<NetId>().Value, 7, 0);
        scene.Commands.ReceiveEvent(meta, CommandIds.SetLever, w.ToArray());
        scene.Commands.Update(0);
        Assert.Equal(0.5f, lever());
        w.Clear();
        handler.Write(w, Set(grid, -0.5f));
        scene.Commands.ReceiveEvent(meta, CommandIds.SetLever, w.ToArray()); // same number again
        scene.Commands.Update(0);
        Assert.Equal(0.5f, lever());
    }

    [Fact]
    public void TheAuthorityValidatesARemoteCommandAndBroadcastsTheEvent()
    {
        using var scene = new HeadlessScene(); // the host
        var router = new RecordingRouter();
        scene.Commands.Router = router;
        var grid = scene.SpawnPlatform(new Vector3(0, 50, 0), size: 4);
        var v = grid.Get<ChunkGrid>().Volume;
        v.SetBlock(0, 1, 0, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.North));
        var handler = (SetLeverHandler)scene.Commands.HandlerFor(CommandIds.SetLever)!;
        var w = new NetWriter();
        handler.Write(w, Set(grid, 5f));
        scene.Commands.ReceiveCommand(Client, CommandIds.SetLever, 42, w.ToArray());
        scene.Commands.Update(0);
        v.TryGetBlockEntity(0, 1, 0, out var e);
        Assert.Equal(1f, e.Get<Lever>().Value); // clamped
        var ev = Assert.Single(router.Events);
        Assert.Equal(Client, ev.Meta.Origin);
        Assert.Equal(42u, ev.Meta.OriginSeq);

        // One the authority rejects goes back to its sender.
        w.Clear();
        handler.Write(w, new SetLever { Lever = EntityAddress.OfBlock(grid.Get<NetId>().Value, new(3, 3, 3)), Value = 0f });
        scene.Commands.ReceiveCommand(Client, CommandIds.SetLever, 43, w.ToArray());
        scene.Commands.Update(0);
        Assert.Equal((Client, 43u), Assert.Single(router.Rejections));
    }
}
