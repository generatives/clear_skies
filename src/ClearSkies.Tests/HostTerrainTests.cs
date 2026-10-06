using System.Numerics;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Session;
using DefaultEcs;
using Xunit;
using static ClearSkies.Tests.HeadlessStreamingTests;

namespace ClearSkies.Tests;

/// <summary>The Host keeps every edited terrain chunk, as the authority describes it, and saves it; terrain streaming
/// loads edited chunks from the Host.</summary>
public class HostTerrainTests
{
    private const int S = ChunkData.Size;

    private static ChunkLoadSystem Streaming(HeadlessScene scene, IChunkStore store) =>
        new(scene.World, scene.WorldVolume, new ChunkCountBudget(100_000), () => new Flat(), viewDistance: 64, minChunkY: 0, store);

    private static EditVoxels Edit(Entity editor, params VoxelOp[] ops) =>
        new() { Volume = EntityRegistry.WorldVolume, Editor = editor.Get<EntityId>(), Ops = ops };

    private static BlockId Block(byte[] chunk, int x, int y, int z)
    {
        var data = new ChunkData();
        StaticWorldSerializer.Read(chunk, data);
        return data.Get(x, y, z);
    }

    [Fact]
    public void TheHostKeepsEachChunkTheAuthorityEditsAndSavesIt()
    {
        using var game = new LoopbackGame(hostPlayer: new PlayerDescription { FreeFly = true, Position = new Vector3(16, 60, 5) });
        var player = game.Host.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        // A block in the sky, and a box across two chunks.
        game.Host.Commands.Send(Edit(player, VoxelOp.SetBlock(new(5, 58, 5), BlockId.Wood, BlockOrientation.Upright),
                                     new VoxelOp(new(30, 58, 5), new(33, 58, 5), BlockId.Stone, BlockOrientation.Upright)));
        game.Tick();

        var sky = new ChunkPosition(0, 1, 0);
        Assert.Equal(new[] { sky, new ChunkPosition(1, 1, 0) }.OrderBy(p => p.X), game.Hub.Chunks.Keys.OrderBy(p => p.X));
        Assert.True(game.Hub.Chunks[sky].Unsaved);
        Assert.Equal(BlockId.Wood, Block(game.Hub.Chunks[sky].Data!, 5, 58 - S, 5));
        Assert.Null(game.Save.ReadChunk(sky)); // not saved yet

        game.SaveAll();
        Assert.False(game.Hub.Chunks[sky].Unsaved);
        Assert.Equal(BlockId.Wood, Block(game.Save.ReadChunk(sky)!, 5, 58 - S, 5));
        Assert.Equal(BlockId.Stone, Block(game.Save.ReadChunk(new ChunkPosition(1, 1, 0))!, 1, 58 - S, 5));
    }

    [Fact]
    public void TheHostingMachineLoadsEditedChunksFromTheHost()
    {
        // A save with the ground under the spawn dug out, and a pillar in the sky.
        using var db = SaveDatabase.InMemory();
        var ground = new ChunkPosition(0, -1, 0);
        var dug = new ChunkData();
        for (int z = 0; z < S; z++) for (int y = 0; y < S - 1; y++) for (int x = 0; x < S; x++) dug.Set(x, y, z, BlockId.Stone);
        db.WriteChunk(ground, StaticWorldSerializer.ToBytes(dug));
        var sky = new ChunkPosition(0, 1, 0);
        var pillar = new ChunkData();
        for (int y = 0; y < S; y++) pillar.Set(3, y, 3, BlockId.Wood);
        db.WriteChunk(sky, StaticWorldSerializer.ToBytes(pillar));

        var chunks = new HostChunkStore();
        using var game = new LoopbackGame(save: db, hostChunks: chunks);
        var load = Streaming(game.Host, chunks);
        Interest(game.Host, 16, 16, View(64));
        Settle(load);

        Assert.Equal(BlockId.Air, game.Host.WorldVolume.GetBlock(5, -1, 5)); // dug
        Assert.Equal(BlockId.Stone, game.Host.WorldVolume.GetBlock(5, -2, 5));
        Assert.Equal(BlockId.Wood, game.Host.WorldVolume.GetBlock(3, S + 10, 3));
        Assert.Equal(BlockId.Stone, game.Host.WorldVolume.GetBlock(S + 5, -1, 5)); // the next column's, as generated
        Assert.Equal(0, chunks.Asked);
    }

    [Fact]
    public void AnEditedChunkUnloadedAndLoadedAgainComesBackFromTheHost()
    {
        var chunks = new HostChunkStore();
        using var game = new LoopbackGame(hostChunks: chunks);
        var load = Streaming(game.Host, chunks);
        var view = Interest(game.Host, 16, 16, View(64));
        var editor = game.Host.SpawnLocalPlayer(new Vector3(16, 5, 16), freeFly: true);
        game.Tick();
        Settle(load);

        game.Host.Commands.Send(Edit(editor, VoxelOp.SetBlock(new(16, -1, 16), BlockId.Air, BlockOrientation.Upright),
                                     VoxelOp.SetBlock(new(16, 3, 16), BlockId.Wood, BlockOrientation.Upright)));
        game.Tick();
        Assert.Equal(BlockId.Air, game.Host.WorldVolume.GetBlock(16, -1, 16));

        // Away (the player too, so nothing wants the terrain there), and back.
        foreach (var e in new[] { view, editor })
            e.Get<Transform>().Position = new Silk.NET.Maths.Vector3D<float>(100 * S, 10, 100 * S);
        game.Tick();
        Settle(load);
        Assert.False(game.Host.WorldVolume.IsLoaded(new ChunkPosition(0, -1, 0)));
        view.Get<Transform>().Position = new Silk.NET.Maths.Vector3D<float>(16, 10, 16);
        Settle(load);
        Assert.Equal(BlockId.Air, game.Host.WorldVolume.GetBlock(16, -1, 16));
        Assert.Equal(BlockId.Stone, game.Host.WorldVolume.GetBlock(16, -2, 16));
        Assert.Equal(BlockId.Wood, game.Host.WorldVolume.GetBlock(16, 3, 16));
    }
}
