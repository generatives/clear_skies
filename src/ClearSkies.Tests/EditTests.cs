using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Session;
using ClearSkies.Net.Sync;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>A flat world: stone below y = 0, air above.</summary>
public sealed class FlatGenerator : IWorldGenerator
{
    public void Generate(ChunkData data, ChunkPosition pos)
    {
        if (pos.Y >= 0) return;
        for (int z = 0; z < ChunkData.Size; z++) for (int y = 0; y < ChunkData.Size; y++) for (int x = 0; x < ChunkData.Size; x++)
            data.Set(x, y, z, BlockId.Stone);
    }

    public ulong ColumnLayers(int chunkX, int chunkZ, int minChunkY) => ulong.MaxValue;
}

/// <summary>A <see cref="LoopbackGame"/> whose host keeps built-on terrain in a save and serves it, and whose clients
/// fetch it, as the game does.</summary>
public sealed class TerrainGame : IDisposable
{
    public readonly LoopbackGame Game;
    public readonly SaveDatabase Db = SaveDatabase.InMemory();
    public readonly DatabaseChunkStore Store;
    public readonly List<RemoteChunkStore> ClientStores = new();

    public TerrainGame(double latencyMs = 0)
    {
        Game = new LoopbackGame(latencyMs);
        Store = new DatabaseChunkStore(Db);
        var editor = new HostChunkEditor(Store, () => new FlatGenerator());
        Handler(Game.Host).WorldEditor = editor;
        var world = Game.Host.WorldVolume;
        Game.HostNet.EditedChunks = () => Store.SavedChunks().Concat(world.All.Where(c => c.Value.Data.IsDirty).Select(c => c.Key)).Distinct();
        Game.HostNet.ChunkSource = pos =>
        {
            if (world.GetData(pos) is { } loaded) return StaticWorldSerializer.ToBytes(loaded);
            var data = new ChunkData();
            return Store.TryLoad(pos, data) ? StaticWorldSerializer.ToBytes(data) : null;
        };
    }

    public static EditVoxelsHandler Handler(HeadlessScene scene) => (EditVoxelsHandler)scene.Commands.HandlerFor(CommandIds.EditVoxels)!;

    public (HeadlessScene Scene, ClientSession Net, RemoteChunkStore Chunks) Join(string name)
    {
        var (scene, net) = Game.Join(name);
        var store = new RemoteChunkStore(net.Welcome.EditedChunks);
        Handler(scene).WorldEditor = store;
        net.Chunks = store;
        ClientStores.Add(store);
        return (scene, net, store);
    }

    /// <summary>Loads a chunk into a scene's world volume the way streaming would: generated, or the saved/fetched data.</summary>
    public static void Load(HeadlessScene scene, IChunkStore store, ChunkPosition pos)
    {
        var data = new ChunkData();
        if (!store.TryLoad(pos, data)) new FlatGenerator().Generate(data, pos);
        scene.WorldVolume.AddChunk(pos, data);
    }

    public void Dispose()
    {
        Game.Dispose();
        Db.Dispose();
    }
}

public class TerrainEditTests
{
    private static Entity PlayerOf(HeadlessScene s) => s.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();

    [Fact]
    public void TheHostRecordsEditsToChunksItHasntLoaded()
    {
        using var t = new TerrainGame();
        var host = t.Game.Host;
        var player = host.SpawnLocalPlayer(new Vector3(0, 5, 0), freeFly: true);
        host.Tick();
        // Dig into the ground below the player; that chunk isn't loaded on the host.
        host.Commands.Send(new EditVoxels { Volume = NetRegistry.WorldVolume, Editor = player.Get<NetId>().Value,
            Ops = new[] { VoxelOp.SetBlock(new(0, -1, 0), BlockId.Air, BlockOrientation.Upright), VoxelOp.SetBlock(new(1, 2, 0), BlockId.Wood, BlockOrientation.Upright) } });
        host.Tick();
        Assert.False(host.WorldVolume.IsLoaded(new ChunkPosition(0, -1, 0)));
        var below = new ChunkData();
        Assert.True(t.Store.TryLoad(new ChunkPosition(0, -1, 0), below));
        Assert.Equal(BlockId.Air, below.Get(0, 31, 0));   // the dug block...
        Assert.Equal(BlockId.Stone, below.Get(1, 31, 0)); // ...in generated ground
        var above = new ChunkData();
        Assert.True(t.Store.TryLoad(new ChunkPosition(0, 0, 0), above));
        Assert.Equal(BlockId.Wood, above.Get(1, 2, 0));
    }

    [Fact]
    public void AJoiningClientFetchesBuiltOnChunks()
    {
        using var t = new TerrainGame(latencyMs: 30);
        var host = t.Game.Host;
        var hostPlayer = host.SpawnLocalPlayer(new Vector3(0, 5, 0), freeFly: true);
        host.Tick();
        host.Commands.Send(new EditVoxels { Volume = NetRegistry.WorldVolume, Editor = hostPlayer.Get<NetId>().Value,
            Ops = new[] { VoxelOp.SetBlock(new(2, 3, 2), BlockId.Lamp, BlockOrientation.Upright) } });
        host.Tick();
        var (client, _, store) = t.Join("builder");
        var pos = new ChunkPosition(0, 0, 0);
        Assert.Contains(pos, store.SavedChunks());
        Assert.False(store.IsReady(pos));
        store.Request(pos);
        t.Game.Tick(10);
        Assert.True(store.IsReady(pos));
        TerrainGame.Load(client, store, pos);
        Assert.Equal(BlockId.Lamp, client.WorldVolume.GetBlock(2, 3, 2));
    }

    [Fact]
    public void EditsDuringAFetchAreNeitherLostNorDoubled()
    {
        using var t = new TerrainGame(latencyMs: 75);
        var host = t.Game.Host;
        var hostPlayer = host.SpawnLocalPlayer(new Vector3(0, 5, 0), freeFly: true);
        host.Tick();
        void Place(int x, BlockId b) => host.Commands.Send(new EditVoxels { Volume = NetRegistry.WorldVolume,
            Editor = hostPlayer.Get<NetId>().Value, Ops = new[] { VoxelOp.SetBlock(new(x, 3, 2), b, BlockOrientation.Upright) } });
        Place(1, BlockId.Wood);
        host.Tick();
        var (client, _, store) = t.Join("builder");
        var pos = new ChunkPosition(0, 0, 0);
        store.Request(pos);
        client.Tick(); // the request goes out...
        Place(2, BlockId.Stone); // ...and the host edits again while it's on its way
        host.Tick();
        t.Game.Tick(5);
        Place(3, BlockId.Dirt); // and again after it has answered
        t.Game.Tick(20);
        Assert.True(store.IsReady(pos));
        TerrainGame.Load(client, store, pos);
        Assert.Equal(BlockId.Wood, client.WorldVolume.GetBlock(1, 3, 2));
        Assert.Equal(BlockId.Stone, client.WorldVolume.GetBlock(2, 3, 2));
        Assert.Equal(BlockId.Dirt, client.WorldVolume.GetBlock(3, 3, 2));
    }

    [Fact]
    public void AnEditToAChunkAClientHasntLoadedMarksItForFetching()
    {
        using var t = new TerrainGame();
        var host = t.Game.Host;
        var hostPlayer = host.SpawnLocalPlayer(new Vector3(500, 5, 0), freeFly: true);
        host.Tick();
        var (client, _, store) = t.Join("far");
        var pos = new ChunkPosition(15, 0, 0);
        Assert.DoesNotContain(pos, store.SavedChunks());
        host.Commands.Send(new EditVoxels { Volume = NetRegistry.WorldVolume, Editor = hostPlayer.Get<NetId>().Value,
            Ops = new[] { VoxelOp.SetBlock(new(500, 3, 5), BlockId.Wood, BlockOrientation.Upright) } });
        t.Game.Tick(5);
        Assert.Contains(pos, store.SavedChunks()); // the client will fetch it when it travels there
        store.Request(pos);
        t.Game.Tick(5);
        TerrainGame.Load(client, store, pos);
        Assert.Equal(BlockId.Wood, client.WorldVolume.GetBlock(500, 3, 5));
    }

    [Fact]
    public void BothPlayersBuildOnTerrainAndShipsAt150Ms()
    {
        using var t = new TerrainGame(latencyMs: 75);
        var host = t.Game.Host;
        var hostPlayer = host.SpawnLocalPlayer(new Vector3(0, 5, 0), freeFly: true);
        var ship = host.SpawnPlatform(new Vector3(0, 10, 0), size: 6);
        host.Tick(2);
        var (client, _, store) = t.Join("builder");
        var pos = new ChunkPosition(0, 0, 0);
        TerrainGame.Load(host, t.Store, pos);   // the terrain here is loaded on both machines
        TerrainGame.Load(client, store, pos);
        var clientPlayer = PlayerOf(client);
        clientPlayer.Get<Transform>().Position = new Vector3D<float>(1, 6, 1);
        t.Game.Tick(10);
        uint shipId = ship.Get<NetId>().Value;

        // Each builds on the terrain and on the ship at the same time.
        client.Commands.Send(new EditVoxels { Volume = NetRegistry.WorldVolume, Editor = clientPlayer.Get<NetId>().Value,
            Ops = new[] { VoxelOp.SetBlock(new(1, 1, 1), BlockId.Wood, BlockOrientation.Upright) } });
        client.Commands.Send(new EditVoxels { Volume = shipId, Editor = clientPlayer.Get<NetId>().Value,
            Ops = new[] { VoxelOp.SetBlock(new(1, 1, 1), BlockId.Lamp, BlockOrientation.Upright) } });
        host.Commands.Send(new EditVoxels { Volume = NetRegistry.WorldVolume, Editor = hostPlayer.Get<NetId>().Value,
            Ops = new[] { VoxelOp.SetBlock(new(2, 1, 1), BlockId.Stone, BlockOrientation.Upright) } });
        host.Commands.Send(new EditVoxels { Volume = shipId, Editor = hostPlayer.Get<NetId>().Value,
            Ops = new[] { VoxelOp.SetBlock(new(2, 1, 1), BlockId.Dirt, BlockOrientation.Upright) } });
        t.Game.Tick(40);

        foreach (var scene in new[] { host, client })
        {
            Assert.Equal(BlockId.Wood, scene.WorldVolume.GetBlock(1, 1, 1));
            Assert.Equal(BlockId.Stone, scene.WorldVolume.GetBlock(2, 1, 1));
            var v = scene.Registry.Find(shipId)!.Value.Get<ChunkGrid>().Volume;
            Assert.Equal(BlockId.Lamp, v.GetBlock(1, 1, 1));
            Assert.Equal(BlockId.Dirt, v.GetBlock(2, 1, 1));
        }
        Assert.Equal(0, client.Commands.PendingCount);
    }
}
