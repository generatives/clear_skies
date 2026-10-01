using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Net.Session;
using DefaultEcs;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>Players spawn where they left off, on their ship if they stood on one, once what they need has loaded.</summary>
public class SpawnTests
{
    private sealed class OnePlayer : IPlayerDirectory
    {
        public readonly Dictionary<PlayerId, PlayerDescription> SavedPlayers = new();
        public PlayerId PlayerFor(string name) => PlayerId.New();
        public PlayerDescription? Saved(PlayerId player) => SavedPlayers.GetValueOrDefault(player);
        public (Vector3 Position, float Yaw, float Pitch) NewPlayerSpawn => (new Vector3(0, 60, 0), 0, 0);
        public void Leaving(Entity player) { }
    }

    private static Entity? PlayerIn(HeadlessScene scene) =>
        scene.World.GetEntities().With<Player>().AsEnumerable().Cast<Entity?>().SingleOrDefault();

    [Fact]
    public void APlayerDescriptionKeepsWhatTheyStandOn()
    {
        var d = new PlayerDescription { Id = PlayerId.New(), Name = "a", FreeFly = false, Position = new Vector3(1, 2, 3),
                                        Support = new EntityId(77), LocalPosition = new Vector3(0.5f, 1.4f, -2) };
        var bytes = DescriptionBytes.Of(d);
        var back = DescriptionBytes.Read<PlayerDescription>(bytes);
        Assert.Equal(new EntityId(77), back.Support);
        Assert.Equal(d.LocalPosition, back.LocalPosition);

        // A row saved before support was: standing on nothing, where it was.
        var old = DescriptionBytes.Read<PlayerDescription>(bytes.AsSpan(0, bytes.Length - 16));
        Assert.True(old.Support.IsNone);
        Assert.Equal(d.Position, old.Position);
        Assert.Equal(d.Pitch, old.Pitch);
    }

    [Fact]
    public void APlayerSavedOnAFarShipSpawnsOnItsDeckAfterARestart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cs-test-{Guid.NewGuid():N}.db");
        try
        {
            PlayerId who;
            EntityId shipId;
            using (var db = SaveDatabase.Open(path))
            using (var scene = new HeadlessScene(firstFreeId: db.NextFreeId))
            {
                scene.EnablePersistence(db);
                var ship = scene.SpawnPlatform(new Vector3(400, 50, 0), size: 5);
                var player = scene.SpawnLocalPlayer(new Vector3(401, 51.4f, 1));
                Assert.True(scene.TickUntil(() => player.Get<Support>().Supporter == ship, 120));
                scene.Tick(30);
                (who, shipId) = (player.Get<Player>().Id, ship.Get<EntityId>());
                scene.Saver!.SaveAll();
            }
            var saved = DescriptionBytes.Read<PlayerDescription>(ReadPlayer(path, who));
            Assert.Equal(shipId, saved.Support);

            using (var db = SaveDatabase.Open(path))
            using (var scene = new HeadlessScene(firstFreeId: db.NextFreeId))
            {
                bool terrain = false;
                var net = new HostSession(null, scene.Session, scene.Commands, scene.Registry, scene.World, scene.Clock, scene.Ids,
                                          1337, 42, new OnePlayer(), _ => terrain);
                scene.AttachNet(net);
                scene.EnablePersistence(db);
                net.SpawnWhenReady(saved);

                // The ship loads around where they wait; they don't spawn until the terrain there has too.
                scene.Tick(30);
                Assert.True(scene.Registry.IsLive(shipId));
                Assert.Null(PlayerIn(scene));
                Assert.Equal(1, net.PendingSpawns);

                terrain = true;
                scene.Tick(2);
                var player = PlayerIn(scene) ?? throw new Xunit.Sdk.XunitException("Never spawned.");
                scene.Tick(120);
                var ship = scene.Registry.Find(shipId)!.Value;
                Assert.Equal(ship, player.Get<Support>().Supporter);
                Assert.InRange(player.Get<Transform>().Position.Y, 51f, 52f); // on the deck, not through it
            }
        }
        finally { File.Delete(path); }
    }

    private static byte[] ReadPlayer(string path, PlayerId who)
    {
        using var db = SaveDatabase.Open(path);
        return db.ReadPlayer(who) ?? throw new Xunit.Sdk.XunitException("Not saved.");
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(12f)]
    [InlineData(20f)]
    public void AClientRejoiningOnAMovingShipStaysWhereTheyStoodOnIt(float speed)
    {
        using var game = new LoopbackGame(latencyMs: 50);
        var ship = game.Host.SpawnPlatform(new Vector3(0, 50, 0), size: 6);
        game.Host.SpawnLocalPlayer(new Vector3(30, 80, 30), freeFly: true);
        game.Tick(2);
        var spot = new Vector3(1, 1.4f, 1); // a block and a half from the stern, in the deck's block space
        game.Directory.Save("crew", new PlayerDescription
        {
            Name = "crew", FreeFly = false, Position = new Vector3(1, 51.4f, 1), // where the ship was when they left
            Support = ship.Get<EntityId>(), LocalPosition = spot,
        });
        // The ship has flown on since, and still is. Its copy on the client used to sit still until the delay caught up
        // with its first snapshot, then set off at full speed from under them (they slid speed²/100 blocks aft).
        var body = ship.Get<PhysicsBodyComponent>().Body;
        game.Host.Physics.SetBodyLinearVelocity(body, new Vector3(speed, 0, 0));
        game.Tick(120);

        var (client, _) = game.Join("crew");
        var crew = client.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        var copy = client.Registry.Find(ship.Get<EntityId>())!.Value;
        float drift = 0f;
        for (int t = -1; t < 120; t++)
        {
            if (t >= 0) game.Tick();
            var at = crew.Get<Transform>().Position;
            var deck = copy.Get<Transform>();
            var onDeck = Vector3.Transform(new Vector3(at.X, at.Y, at.Z) - new Vector3(deck.Position.X, deck.Position.Y, deck.Position.Z),
                                           Quaternion.Inverse(new Quaternion(deck.Rotation.X, deck.Rotation.Y, deck.Rotation.Z, deck.Rotation.W)));
            drift = MathF.Max(drift, Vector2.Distance(new Vector2(onDeck.X, onDeck.Z), new Vector2(spot.X, spot.Z)));
        }
        Assert.Equal(copy, crew.Get<Support>().Supporter);
        Assert.InRange(crew.Get<Support>().LocalPosition.Y, 1f, 2f);
        // At most a tick's travel: the copy gets its body a tick after it's spawned, while they're already moving with it.
        Assert.True(drift < speed / 60f + 0.05f, $"slid {drift:F2} on the deck");
    }
}
