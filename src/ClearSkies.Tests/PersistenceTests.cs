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
                using var db = SaveDatabase.Open(path);
                using var scene = new HeadlessScene(firstFreeId: db.NextFreeId);
                scene.EnablePersistence(db);
                for (int i = 0; i < 1500; i++) Assert.True(seen.Add(scene.Registry.Allocate()));
                scene.Saver!.SaveAll();
            }
        }
        finally { File.Delete(path); }
    }
}

public class StreamingTests
{
    private static (HeadlessScene scene, SaveDatabase db, Entity player, Entity grid) Scene()
    {
        var db = SaveDatabase.InMemory();
        var scene = new HeadlessScene();
        scene.EnablePersistence(db);
        var player = scene.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        var grid = scene.SpawnPlatform(new Vector3(20, 50, 0), size: 3);
        grid.Get<ChunkGrid>().Volume.SetBlock(1, 1, 1, BlockId.Lamp);
        scene.Tick(2);
        return (scene, db, player, grid);
    }

    private static void MovePlayer(Entity player, float x) =>
        player.Get<Transform>().Position = new Vector3D<float>(x, 60, 0);

    [Fact]
    public void AGridOutsideEveryWindowIsStoredAndDespawned()
    {
        var (scene, db, player, grid) = Scene();
        using var _ = scene; using var __ = db;
        var id = grid.Get<EntityId>();
        MovePlayer(player, 1200); // 1,180 from the grid: past the unload window
        scene.Tick(3);
        Assert.False(grid.IsAlive);
        Assert.NotNull(db.ReadEntity(id));
        Assert.True(scene.Index!.TryGet(id, out var entry));
        Assert.True(Vector3.Distance(entry.Position!.Value, new Vector3(20, 50, 0)) < 1f);
    }

    [Fact]
    public void ComingBackLoadsItAgainAsItWas()
    {
        var (scene, db, player, grid) = Scene();
        using var _ = scene; using var __ = db;
        var id = grid.Get<EntityId>();
        var before = DescriptionTests.DescribeNow(scene, grid).Hash;
        MovePlayer(player, 1200);
        scene.Tick(3);
        MovePlayer(player, 0);
        scene.Tick(2);
        var back = scene.Registry.Find(id);
        Assert.NotNull(back);
        Assert.Equal(before, DescriptionTests.DescribeNow(scene, back!.Value).Hash);
        Assert.Equal(BlockId.Lamp, back.Value.Get<ChunkGrid>().Volume.GetBlock(1, 1, 1));
    }

    [Fact]
    public void TheWindowsHaveHysteresis()
    {
        var (scene, db, player, grid) = Scene();
        using var _ = scene; using var __ = db;
        var id = grid.Get<EntityId>();
        MovePlayer(player, 1100); // 1,080 away: inside the unload window, outside the load window
        scene.Tick(3);
        Assert.True(grid.IsAlive);
        MovePlayer(player, 1200);
        scene.Tick(3);
        Assert.False(scene.Registry.IsLive(id));
        MovePlayer(player, 1100); // back to 1,080: not yet within the load window
        scene.Tick(3);
        Assert.False(scene.Registry.IsLive(id));
        MovePlayer(player, 1000);
        scene.Tick(2);
        Assert.True(scene.Registry.IsLive(id));
    }

    [Fact]
    public void ALiveEntityIsNeverLoadedTwice()
    {
        var (scene, db, player, grid) = Scene();
        using var _ = scene; using var __ = db;
        scene.Saver!.SaveAll(); // now in the index and live
        scene.Tick(5);
        Assert.Single(scene.World.GetEntities().With<DynamicGrid>().AsEnumerable());
    }

    [Fact]
    public void AGlobalEntityIsAlwaysLoaded()
    {
        var (scene, db, player, grid) = Scene();
        using var _ = scene; using var __ = db;
        // A stored row with no position, far from everyone as far as position goes.
        var d = DescriptionTests.DescribeNow(scene, grid);
        Hierarchy.DestroyRecursive(grid);
        db.WriteEntity(d.Id, d.Kind, null, d.Data);
        scene.Index!.Set(new StoredEntity(d.Id, d.Kind, null));
        MovePlayer(player, 50_000);
        scene.Tick(5); // loaded, and not unloaded again for being far away
        Assert.True(scene.Registry.IsLive(d.Id));
        scene.Saver!.SaveAll();
        Assert.True(scene.Index.TryGet(d.Id, out var entry) && entry.Position is null); // still global
    }

    [Fact]
    public void AutosaveAndRestartRestoreShipsAndThePlayer()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cs-test-{Guid.NewGuid():N}.db");
        try
        {
            EntityId gridId;
            PlayerId who;
            using (var db = SaveDatabase.Open(path))
            using (var scene = new HeadlessScene(firstFreeId: db.NextFreeId))
            {
                scene.EnablePersistence(db);
                var player = scene.SpawnLocalPlayer(new Vector3(5, 60, 5), freeFly: true);
                var grid = scene.SpawnPlatform(new Vector3(20, 50, 0), size: 3);
                scene.Tick(2);
                (gridId, who) = (grid.Get<EntityId>(), player.Get<Player>().Id);
                MovePlayer(player, 7);
                scene.Saver!.SaveAll();
            }
            using (var db = SaveDatabase.Open(path))
            using (var scene = new HeadlessScene(firstFreeId: db.NextFreeId))
            {
                scene.EnablePersistence(db);
                var saved = db.ReadPlayer(who);
                Assert.NotNull(saved);
                scene.Commands.Send(new Spawn<PlayerDescription> { Description = DescriptionBytes.Read<PlayerDescription>(saved) });
                scene.Tick(3);
                var player = scene.World.GetEntities().With<Player>().AsEnumerable().Single();
                Assert.Equal(who, player.Get<Player>().Id);
                Assert.Equal(7f, player.Get<Transform>().Position.X);
                Assert.True(scene.Registry.IsLive(gridId)); // loaded around the restored player
                Assert.True(scene.Registry.Allocate().Value > gridId.Value);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ADeletedGridLeavesTheSave()
    {
        var (scene, db, player, grid) = Scene();
        using var _ = scene; using var __ = db;
        var id = grid.Get<EntityId>();
        scene.Saver!.SaveAll();
        Assert.NotNull(db.ReadEntity(id));
        scene.Commands.Send(new DespawnEntity { Entity = id });
        scene.Tick(3);
        Assert.Null(db.ReadEntity(id));
        Assert.False(scene.Registry.IsLive(id));
    }
}

public class TerrainGatingTests
{
    [Fact]
    public void AGridGetsABodyOnlyOnceTheTerrainAroundItIsReady()
    {
        using var scene = new HeadlessScene();
        bool ready = false;
        scene.Presence.TerrainReady = _ => ready;
        scene.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        var grid = scene.SpawnPlatform(new Vector3(20, 50, 0), size: 3);
        scene.Tick(3);
        Assert.False(grid.Has<PhysicsPresence>());
        Assert.False(grid.Has<PhysicsBodyComponent>());
        Assert.True(grid.Has<TerrainInterest>()); // its terrain is being loaded meanwhile
        ready = true;
        scene.Tick(2);
        Assert.True(grid.Has<PhysicsBodyComponent>());
        ready = false; // once it has a body it keeps it
        scene.Tick(2);
        Assert.True(grid.Has<PhysicsBodyComponent>());
    }

    [Fact]
    public void TerrainWhoseBlocksDontCollideCountsAsReady()
    {
        // A chunk of nothing but water has blocks but no collider (water is passable): it mustn't hold back the grids
        // near it forever.
        using var scene = new HeadlessScene();
        scene.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        scene.WorldVolume.SetBlock(4, 40, 4, BlockId.Water);
        scene.WorldVolume.SetBlock(4, 10, 4, BlockId.Stone);
        Assert.True(scene.TickUntil(() => scene.PhysicsBodies.CollidersReady(scene.WorldVolume, new Vector3(4, 40, 4), 64f), 120));
    }
}
