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

/// <summary>Clients load edited terrain from the Host: chunks edited before they joined, while they were loading them,
/// and while they were far away.</summary>
public class ClientTerrainTests
{
    private const int S = ChunkData.Size;

    private static EditVoxels Edit(Entity editor, params VoxelOp[] ops) =>
        new() { Volume = EntityRegistry.WorldVolume, Editor = editor.Get<EntityId>(), Ops = ops };

    /// <summary>Ticks the game and streams on every machine until nothing's loading or asked for, for a second.</summary>
    private static void Settle(LoopbackGame game, params (ChunkLoadSystem Load, HostChunkStore Chunks)[] streams)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        int quiet = 0;
        while (quiet < 60 && DateTime.UtcNow < deadline)
        {
            game.Tick();
            bool idle = true;
            foreach (var (load, chunks) in streams)
            {
                int before = load.LoadedChunks;
                load.Update(1 / 60f);
                idle &= load.ColumnsInFlight == 0 && chunks.Asked == 0 && load.LoadedChunks == before;
            }
            quiet = idle ? quiet + 1 : 0;
            Thread.Sleep(0);
        }
    }

    /// <summary>The Host with terrain streamed on the hosting machine, someone playing there at
    /// <paramref name="hostAt"/>, and <paramref name="latencyMs"/> each way.</summary>
    private static (LoopbackGame Game, ChunkLoadSystem Load, HostChunkStore Chunks, Entity Player) Hosted(Vector3 hostAt, double latencyMs = 0)
    {
        var chunks = new HostChunkStore();
        var game = new LoopbackGame(latencyMs, hostPlayer: new PlayerDescription { FreeFly = true, Position = hostAt }, hostChunks: chunks);
        var load = Streaming(game.Host, chunks);
        var player = game.Host.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        return (game, load, chunks, player);
    }

    private static (HeadlessScene Scene, ChunkLoadSystem Load, HostChunkStore Chunks) Join(LoopbackGame game, string name = "client")
    {
        var chunks = new HostChunkStore();
        var (scene, _) = game.Join(name, chunks: chunks);
        return (scene, Streaming(scene, chunks), chunks);
    }

    private static void AssertSameChunk(ChunkVolume a, ChunkVolume b, ChunkPosition pos)
    {
        Assert.True(a.IsLoaded(pos) && b.IsLoaded(pos), $"{pos} not loaded on both");
        Assert.True(a.GetData(pos)!.BlocksAsBytes().SequenceEqual(b.GetData(pos)!.BlocksAsBytes()), $"{pos} differs");
    }

    [Fact]
    public void AJoiningClientLoadsChunksEditedBeforeItJoined()
    {
        var (game, hostLoad, hostChunks, player) = Hosted(new Vector3(5, 3, 5));
        using var _ = game;
        Settle(game, (hostLoad, hostChunks));
        game.Host.Commands.Send(Edit(player, VoxelOp.FillBox(new(5, -2, 5), 1, BlockId.Air, BlockOrientation.Upright),
                                     VoxelOp.SetBlock(new(8, 6, 5), BlockId.Wood, BlockOrientation.Upright)));
        game.Tick();
        Assert.Equal(BlockId.Air, game.Host.WorldVolume.GetBlock(5, -2, 5));

        var (client, load, chunks) = Join(game);
        Assert.Contains(new ChunkPosition(0, -1, 0), chunks.EditedChunks());
        Settle(game, (hostLoad, hostChunks), (load, chunks));
        Assert.Equal(BlockId.Air, client.WorldVolume.GetBlock(5, -2, 5));
        Assert.Equal(BlockId.Stone, client.WorldVolume.GetBlock(5, -4, 5));
        Assert.Equal(BlockId.Wood, client.WorldVolume.GetBlock(8, 6, 5));
        AssertSameChunk(game.Host.WorldVolume, client.WorldVolume, new ChunkPosition(0, -1, 0));
    }

    [Fact]
    public void EditsBeforeDuringAndAfterAClientLoadsAChunkAllReachIt()
    {
        var (game, hostLoad, hostChunks, player) = Hosted(new Vector3(16, 3, 16), latencyMs: 75);
        using var _ = game;
        Settle(game, (hostLoad, hostChunks));
        var ground = new ChunkPosition(0, -1, 0);

        // A hole dug a cell a tick from before the client joins until well after it has the chunk.
        int cell = 0;
        void Dig()
        {
            int x = 4 + cell % 24, z = 4 + cell / 24 % 24;
            game.Host.Commands.Send(Edit(player, VoxelOp.SetBlock(new(x, -1 - cell / 576, z), BlockId.Air, BlockOrientation.Upright)));
            cell++;
        }
        for (int i = 0; i < 20; i++) { Dig(); game.Tick(); }
        var chunks = new HostChunkStore();
        var transport = game.Join("client", wait: false, chunks: chunks);
        var load = Streaming(transport.Scene, chunks);
        for (int i = 0; i < 200; i++)
        {
            Dig();
            game.Tick();
            hostLoad.Update(1 / 60f);
            load.Update(1 / 60f);
            Thread.Sleep(0);
        }
        Settle(game, (hostLoad, hostChunks), (load, chunks));
        Assert.Equal(220, cell);
        Assert.Equal(0, game.Host.Commands.Stats.Rejected);
        AssertSameChunk(game.Host.WorldVolume, transport.Scene.WorldVolume, ground);
        Assert.Equal(BlockId.Air, transport.Scene.WorldVolume.GetBlock(4 + 219 % 24, -1, 4 + 219 / 24 % 24));
    }

    [Fact]
    public void AnEditFarFromAClientReachesItWhenItGoesThere()
    {
        var far = new Vector3(100 * S + 16, 3, 16);
        var (game, hostLoad, hostChunks, player) = Hosted(far);
        using var _ = game;
        var (client, load, chunks) = Join(game);
        Settle(game, (hostLoad, hostChunks), (load, chunks));
        var there = new ChunkPosition(100, -1, 0);
        Assert.False(client.WorldVolume.IsLoaded(there));

        game.Host.Commands.Send(Edit(player, VoxelOp.SetBlock(new(100 * S + 16, -1, 16), BlockId.Air, BlockOrientation.Upright)));
        Settle(game, (hostLoad, hostChunks), (load, chunks));
        Assert.False(client.WorldVolume.IsLoaded(there)); // the edit didn't make it there

        Interest(client, far.X, far.Z, View(64));
        Settle(game, (hostLoad, hostChunks), (load, chunks));
        Assert.Equal(BlockId.Air, client.WorldVolume.GetBlock(100 * S + 16, -1, 16));
        Assert.Equal(BlockId.Stone, client.WorldVolume.GetBlock(100 * S + 17, -1, 16));
        AssertSameChunk(game.Host.WorldVolume, client.WorldVolume, there);
    }
}
