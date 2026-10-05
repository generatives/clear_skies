using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Session;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

public class SaveDatabaseTests
{
    [Fact]
    public void WorldSettingsRoundTrip()
    {
        using var db = SaveDatabase.InMemory();
        Assert.Null(db.Seed);
        db.Seed = 1337;
        db.NextFreeId = 5000;
        Assert.Equal(1337UL, db.Seed);
        Assert.Equal(5000u, db.NextFreeId);
    }

    [Fact]
    public void EntitiesRoundTripWithNullablePositions()
    {
        using var db = SaveDatabase.InMemory();
        db.WriteEntity(new EntityId(2000), CommandIds.SpawnGrid, new Vector3(1, 2, 3), new byte[] { 9, 8, 7 });
        db.WriteEntity(new EntityId(2001), CommandIds.SpawnGrid, null, new byte[] { 1 }); // a global entity
        db.WriteEntity(new EntityId(2000), CommandIds.SpawnGrid, new Vector3(4, 5, 6), new byte[] { 6 }); // overwritten
        var index = db.ReadEntityIndex().OrderBy(e => e.Id.Value).ToList();
        Assert.Equal(2, index.Count);
        Assert.Equal(new Vector3(4, 5, 6), index[0].Position);
        Assert.Null(index[1].Position);
        Assert.Equal(new byte[] { 6 }, db.ReadEntity(new EntityId(2000))!.Value.Data);
        db.DeleteEntity(new EntityId(2000));
        Assert.Null(db.ReadEntity(new EntityId(2000)));
    }

    [Fact]
    public void APlayerIsKnownByNameAcrossSessions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cs-players-{Guid.NewGuid():N}.db");
        try
        {
            PlayerId declan;
            using (var db = SaveDatabase.Open(path))
            {
                declan = db.PlayerFor("Declan");
                Assert.Equal(declan, db.PlayerFor("Declan"));
                Assert.NotEqual(declan, db.PlayerFor("Guest"));
            }
            using (var db = SaveDatabase.Open(path)) Assert.Equal(declan, db.PlayerFor("Declan"));
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) File.Delete(f);
        }
    }

    [Fact]
    public void PlayersAndChunksRoundTrip()
    {
        using var db = SaveDatabase.InMemory();
        var id = db.PlayerFor("Declan");
        Assert.Null(db.ReadPlayer(id)); // known, but never saved
        db.WritePlayer(id, "Declan", new byte[] { 1, 2 });
        Assert.Equal(new byte[] { 1, 2 }, db.ReadPlayer(id));
        Assert.Null(db.ReadPlayer(PlayerId.New()));

        var store = new DatabaseChunkStore(db);
        var data = new ChunkData();
        data.Set(3, 4, 5, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.East));
        store.Save(new ChunkPosition(-1, 2, 7), data);
        Assert.Equal(new[] { new ChunkPosition(-1, 2, 7) }, store.SavedChunks());
        var back = new ChunkData();
        Assert.True(store.TryLoad(new ChunkPosition(-1, 2, 7), back));
        Assert.Equal(BlockId.Lever, back.Get(3, 4, 5));
        Assert.Equal(data.GetOrientation(3, 4, 5), back.GetOrientation(3, 4, 5));
        Assert.False(store.TryLoad(new ChunkPosition(0, 0, 0), back));
    }

    [Fact]
    public void AFailedTransactionLeavesThePreviousSaveIntact()
    {
        using var db = SaveDatabase.InMemory();
        db.WriteEntity(new EntityId(2000), 7, Vector3.Zero, new byte[] { 1 });
        Assert.Throws<InvalidOperationException>(() => db.InTransaction(() =>
        {
            db.WriteEntity(new EntityId(2000), 7, Vector3.Zero, new byte[] { 2 });
            db.WriteEntity(new EntityId(2001), 7, Vector3.Zero, new byte[] { 3 });
            throw new InvalidOperationException("crash mid-save");
        }));
        Assert.Equal(new byte[] { 1 }, db.ReadEntity(new EntityId(2000))!.Value.Data);
        Assert.Null(db.ReadEntity(new EntityId(2001)));
    }

    [Fact]
    public void IdBlocksNeverRepeatAcrossSessions()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cs-test-{Guid.NewGuid():N}.db");
        try
        {
            var seen = new HashSet<EntityId>();
            for (int session = 0; session < 3; session++)
            {
                using var game = new LoopbackGame(save: SaveDatabase.Open(path));
                for (int i = 0; i < 1500; i++)
                {
                    Assert.True(seen.Add(game.Host.Registry.Allocate()));
                    if (i % 100 == 0) game.Tick(); // more IDs from the Host as they run low
                }
                game.SaveAll();
            }
        }
        finally { File.Delete(path); }
    }
}

/// <summary>The Host streams entities by View Volume: loaded from the save as one comes into a view, released (saved,
/// then despawned) once out of every view.</summary>
public class StreamingTests
{
    /// <summary>A ship with a lamp on it at (20, 50, 0), and a client's player beside it, free-flying.</summary>
    private static (LoopbackGame Game, Entity Player, Entity Grid) Scene(SaveDatabase? save = null)
    {
        var game = new LoopbackGame(save: save);
        var grid = game.Host.SpawnPlatform(new Vector3(20, 50, 0), size: 3);
        grid.Get<ChunkGrid>().Volume.SetBlock(1, 1, 1, BlockId.Lamp);
        game.Tick(2);
        var (client, _) = game.Join();
        var player = client.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        return (game, player, grid);
    }

    /// <summary>Moves the client's player, and ticks until the Host has its new View Volume.</summary>
    private static void MovePlayer(LoopbackGame game, Entity player, float x, int ticks = SimulationParticipant.ViewTicks + 5)
    {
        game.Teleport(player, new Vector3(x, 60, 0));
        game.Tick(ticks);
    }

    private static bool OnClient(LoopbackGame game, EntityId id) => game.Clients[0].Scene.Registry.IsLive(id);

    [Fact]
    public void AGridOutOfEveryViewIsSavedAndReleased()
    {
        var (game, player, grid) = Scene();
        using var _ = game;
        var id = grid.Get<EntityId>();
        Assert.True(OnClient(game, id));
        MovePlayer(game, player, 1200); // 1,180 from the grid: out of the view, even with hysteresis
        Assert.False(grid.IsAlive);
        Assert.False(OnClient(game, id));
        Assert.NotNull(game.Save.ReadEntity(id));
        var entry = game.Save.ReadEntityIndex().Single(e => e.Id == id);
        Assert.True(Vector3.Distance(entry.Position!.Value, new Vector3(20, 50, 0)) < 3f);
    }

    [Fact]
    public void ComingBackLoadsItAgainAsItWas()
    {
        var (game, player, grid) = Scene();
        using var _ = game;
        var id = grid.Get<EntityId>();
        var before = DescriptionTests.DescribeNow(game.Host, grid).Hash;
        MovePlayer(game, player, 1200);
        MovePlayer(game, player, 0);
        var back = game.Host.Registry.Find(id);
        Assert.NotNull(back);
        Assert.Equal(before, DescriptionTests.DescribeNow(game.Host, back!.Value).Hash);
        Assert.Equal(BlockId.Lamp, back.Value.Get<ChunkGrid>().Volume.GetBlock(1, 1, 1));
        var copy = game.Clients[0].Scene.Registry.Find(id);
        Assert.NotNull(copy);
        Assert.Equal(BlockId.Lamp, copy!.Value.Get<ChunkGrid>().Volume.GetBlock(1, 1, 1));
    }

    [Fact]
    public void ViewsHaveHysteresis()
    {
        var (game, player, grid) = Scene();
        using var _ = game;
        var id = grid.Get<EntityId>();
        MovePlayer(game, player, 1080); // 1,060 away: past the view, but within its hysteresis
        Assert.True(grid.IsAlive);
        Assert.True(OnClient(game, id));
        MovePlayer(game, player, 1200);
        Assert.False(game.Host.Registry.IsLive(id));
        MovePlayer(game, player, 1080); // back to 1,060: not yet in view
        Assert.False(game.Host.Registry.IsLive(id));
        MovePlayer(game, player, 1000);
        Assert.True(game.Host.Registry.IsLive(id));
        Assert.True(OnClient(game, id));
    }

    [Fact]
    public void ALiveEntityIsNeverLoadedTwice()
    {
        var (game, _, _) = Scene();
        using var __ = game;
        game.SaveAll(); // now in the save, and live
        game.Tick(5);
        Assert.Single(game.Host.World.GetEntities().With<DynamicGrid>().AsEnumerable());
        Assert.Single(game.Clients[0].Scene.World.GetEntities().With<DynamicGrid>().AsEnumerable());
    }

    [Fact]
    public void AGlobalEntityIsAlwaysLoaded()
    {
        // A stored row with no position: in every view, however far anyone is.
        var db = SaveDatabase.InMemory();
        EntityDescription d;
        using (var scene = new HeadlessScene())
            d = DescriptionTests.DescribeNow(scene, scene.SpawnPlatform(new Vector3(20, 50, 0), size: 3));
        db.WriteEntity(d.Id, d.Kind, null, d.Data);

        using var game = new LoopbackGame(save: db);
        var (client, _) = game.Join();
        var player = client.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        MovePlayer(game, player, 50_000);
        Assert.True(game.Host.Registry.IsLive(d.Id));
        Assert.True(client.Registry.IsLive(d.Id));
        game.SaveAll();
        Assert.Null(db.ReadEntityIndex().Single(e => e.Id == d.Id).Position); // still global
    }

    [Fact]
    public void SavingAndRestartingRestoreShipsAndThePlayer()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cs-test-{Guid.NewGuid():N}.db");
        try
        {
            EntityId gridId;
            PlayerId who;
            var (game, player, grid) = Scene(SaveDatabase.Open(path));
            using (game)
            {
                (gridId, who) = (grid.Get<EntityId>(), player.Get<Player>().Id);
                MovePlayer(game, player, 7);
                game.SaveAll();
            }
            using (game = new LoopbackGame(save: SaveDatabase.Open(path)))
            {
                var (client, _) = game.Join();
                player = client.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
                Assert.Equal(who, player.Get<Player>().Id);
                Assert.Equal(7f, player.Get<Transform>().Position.X, 2);
                Assert.True(game.Host.Registry.IsLive(gridId)); // loaded into the restored player's view
                Assert.True(client.Registry.IsLive(gridId));
                Assert.True(game.Host.Registry.Allocate().Value > gridId.Value);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ADeletedGridLeavesTheSave()
    {
        var (game, _, grid) = Scene();
        using var __ = game;
        var id = grid.Get<EntityId>();
        game.SaveAll();
        Assert.NotNull(game.Save.ReadEntity(id));
        game.Host.Commands.Send(new DespawnEntity { Entity = id });
        game.Tick(3);
        Assert.Null(game.Save.ReadEntity(id));
        Assert.False(game.Host.Registry.IsLive(id));
        Assert.False(OnClient(game, id));
    }
}

public class SpawnQueueTests
{
    [Fact]
    public void AStoredGridSpawnsOnlyOnceTheTerrainAroundItIsReady()
    {
        var db = SaveDatabase.InMemory();
        EntityDescription d;
        using (var scene = new HeadlessScene())
            d = DescriptionTests.DescribeNow(scene, scene.SpawnPlatform(new Vector3(20, 50, 0), size: 3));
        db.WriteEntity(d.Id, d.Kind, new Vector3(20, 50, 0), d.Data);

        bool ready = false;
        using var game = new LoopbackGame(save: db, hostTerrainReady: _ => ready);
        var (client, _) = game.Join(wait: false);
        game.Tick(10);
        Assert.False(game.Host.Registry.IsLive(d.Id));
        Assert.True(game.Host.World.GetEntities().With<TerrainInterest>().AsEnumerable().Any()); // its terrain is loading meanwhile
        Assert.True(client.Registry.IsLive(d.Id)); // only drawn there: it needs nothing
        ready = true;
        game.Tick(5);
        Assert.True(game.Host.Registry.Find(d.Id)!.Value.Has<PhysicsBodyComponent>());
        Assert.True(client.Registry.IsLive(d.Id));
    }
}
