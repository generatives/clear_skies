using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Session;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Transport;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>A host and clients in one process over a <see cref="LoopbackNetwork"/>, stepped tick by tick in lockstep,
/// with network time advancing a tick per tick.</summary>
public sealed class LoopbackGame : IDisposable
{
    public const ulong Checksum = 42;
    public readonly LoopbackNetwork Network = new() { ManualTime = 0 };
    public readonly HeadlessScene Host = new();
    public readonly HostSession HostNet;
    public readonly List<(HeadlessScene Scene, ClientSession Net)> Clients = new();
    public readonly Players Directory = new();

    /// <summary>Like a save: each name gets a player ID the first time it's seen; nothing is kept but what a test
    /// <see cref="Save"/>s.</summary>
    public sealed class Players : IPlayerDirectory
    {
        private readonly Dictionary<string, PlayerId> _ids = new();
        private readonly Dictionary<PlayerId, PlayerDescription> _saved = new();
        public PlayerId PlayerFor(string name) => _ids.TryGetValue(name, out var id) ? id : _ids[name] = PlayerId.New();
        public PlayerDescription? Saved(PlayerId player) => _saved.TryGetValue(player, out var d) ? d : null;

        /// <summary>As if <paramref name="name"/> had played before and left as <paramref name="d"/>.</summary>
        public void Save(string name, PlayerDescription d)
        {
            d.Id = PlayerFor(name);
            _saved[d.Id] = d;
        }

        public (Vector3 Position, float Yaw, float Pitch) NewPlayerSpawn => (new Vector3(0, 60, 0), 0, 0);
        public void Leaving(DefaultEcs.Entity player) { }
    }

    public LoopbackGame(double latencyMs = 0, double lossChance = 0)
    {
        Network.LatencyMs = latencyMs;
        Network.LossChance = lossChance;
        HostNet = new HostSession(Network.Listen(), Host.Session, Host.Commands, Host.Registry, Host.World, Host.Clock, Host.Ids,
            seed: 1337, generationChecksum: Checksum, directory: Directory);
        HostNet.TimeSource = () => Network.Now;
        Host.AttachNet(HostNet);
    }

    /// <summary>Ticks every machine once, advancing network time by a tick.</summary>
    public void Tick(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            Network.ManualTime += 1000.0 / 60.0;
            Host.Tick();
            foreach (var (scene, _) in Clients) scene.Tick();
        }
    }

    /// <summary>A client joining: hello, welcome, then ticking until its player has arrived.</summary>
    public (HeadlessScene Scene, ClientSession Net) Join(string name = "client", ulong checksum = Checksum, int maxTicks = 600)
    {
        var transport = Network.Connect();
        var scene = new HeadlessScene(new Engine.Entities.Session(SessionRole.Host, PeerId.Host));
        using (var join = new JoinRequest(transport, new Hello(ProtocolVersion.Current, name, checksum)))
        {
            Welcome welcome;
            int guard = 0;
            while (!join.TryComplete(out welcome))
            {
                if (++guard > maxTicks) throw new TimeoutException("No welcome.");
                Tick();
            }
            var net = new ClientSession(transport, welcome, scene.Session, scene.Commands, scene.Registry, scene.World, scene.Clock, _ => true)
                { TimeSource = () => Network.Now };
            scene.AttachNet(net);
            Clients.Add((scene, net));
            for (int i = 0; i < maxTicks && !net.Joined; i++) Tick();
            if (!net.Joined) throw new TimeoutException("Never joined.");
            return (scene, net);
        }
    }

    public void Dispose()
    {
        foreach (var (scene, _) in Clients) scene.Dispose();
        Host.Dispose();
    }
}

public class TransportTests
{
    [Fact]
    public void LoopbackDeliversReliablePacketsInOrderAfterTheLatency()
    {
        var net = new LoopbackNetwork { ManualTime = 0, LatencyMs = 50, JitterMs = 30 };
        var host = net.Listen();
        var client = net.Connect();
        var got = new List<byte>();
        host.Received += (c, d, ch) => got.Add(d[0]);
        for (byte i = 0; i < 20; i++) client.Send(new ConnectionId(0), new[] { i }, Channel.Reliable);
        host.Poll();
        Assert.Empty(got);
        net.ManualTime = 100;
        host.Poll();
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (byte)i), got);
    }

    [Fact]
    public void LoopbackLosesOnlyUnreliablePackets()
    {
        var net = new LoopbackNetwork { ManualTime = 0, LossChance = 0.5 };
        var host = net.Listen();
        var client = net.Connect();
        int reliable = 0, unreliable = 0;
        host.Received += (c, d, ch) => { if (ch == Channel.Reliable) reliable++; else unreliable++; };
        for (int i = 0; i < 200; i++)
        {
            client.Send(new ConnectionId(0), new byte[] { 1 }, Channel.Reliable);
            client.Send(new ConnectionId(0), new byte[] { 2 }, Channel.Unreliable);
        }
        host.Poll();
        Assert.Equal(200, reliable);
        Assert.InRange(unreliable, 60, 140);
    }

    [Fact]
    public void QuaternionsSurviveSmallestThreeEncoding()
    {
        var rng = new Random(3);
        for (int i = 0; i < 500; i++)
        {
            var q = Quaternion.Normalize(new Quaternion((float)rng.NextDouble() - .5f, (float)rng.NextDouble() - .5f, (float)rng.NextDouble() - .5f, (float)rng.NextDouble() - .5f));
            var back = QuaternionCodec.Unpack(QuaternionCodec.Pack(q));
            if (Quaternion.Dot(q, back) < 0) back = -back; // the same rotation
            float error = MathF.Max(MathF.Max(MathF.Abs(q.X - back.X), MathF.Abs(q.Y - back.Y)), MathF.Max(MathF.Abs(q.Z - back.Z), MathF.Abs(q.W - back.W)));
            Assert.True(error < 1e-5f, $"off by {error}");
        }
    }

    [Fact]
    public void BodySnapshotsRoundTrip()
    {
        var s = new BodySnapshot
        {
            Entity = new EntityId(1234), Epoch = 3, Support = new EntityId(1100), Position = new Vector3(1.5f, -2, 300),
            Rotation = Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f), LinearVelocity = new Vector3(1, 2, 3),
            AngularVelocity = new Vector3(0.5f, 0, -0.25f), Flags = SnapshotFlags.HasLook, Look = new LookAngles(-2.5f, 0.4f),
        };
        var w = new NetWriter();
        s.Write(w);
        var r = new NetReader(w.Written);
        var b = BodySnapshot.Read(ref r);
        Assert.Equal(s.Entity, b.Entity);
        Assert.Equal(s.Support, b.Support);
        Assert.Equal(s.Position, b.Position);
        Assert.True(Vector3.Distance(s.LinearVelocity, b.LinearVelocity) < 0.01f);
        Assert.Equal(-2.5f, b.Look.Yaw, 2);
        Assert.Equal(0.4f, b.Look.Pitch, 2);
        Assert.True(r.AtEnd);
    }
}

public class ClockSyncTests
{
    [Fact]
    public void AFarOffClockSnapsAndACloseOneSlews()
    {
        var clock = new Engine.Core.ManualTickClock { Tick = 0 };
        var sync = new ClockSync(clock);
        // The host is at tick 1000; 100 ms round trip.
        sync.OnPong(new TimePong(0, 1000, 0), nowMs: 100);
        Assert.InRange(clock.Tick, 1002u, 1004u); // snapped: 1000 + half the round trip (3 ticks)
        Assert.Equal(1, sync.Snaps);

        // A little behind (the host 3 ticks further on than we'd think): run faster, no jump.
        uint before = clock.Tick;
        for (int i = 0; i < 4; i++) sync.OnPong(new TimePong(200 + i, clock.Tick + 3 + 3, 0), nowMs: 300 + i);
        Assert.Equal(before, clock.Tick);
        Assert.True(clock.Rate > 1);
        Assert.True(clock.Rate <= 1 + ClockSync.MaxSlew);
    }

    [Fact]
    public void JitteryAnswersAreDiscounted()
    {
        var clock = new Engine.Core.ManualTickClock { Tick = 5000 };
        var sync = new ClockSync(clock);
        // Most answers: 60 ms round trip, host 3 ticks ahead of us... one very late one says otherwise.
        for (int i = 0; i < 12; i++) sync.OnPong(new TimePong(i * 250, 5000 + 1, 0), nowMs: i * 250 + 60);
        sync.OnPong(new TimePong(4000, 5001, 0), nowMs: 4000 + 600);
        Assert.InRange(sync.RoundTripMs, 55, 65);
    }
}

public class JoinTests
{
    [Fact]
    public void AClientJoinsAndReceivesTheWorld()
    {
        using var game = new LoopbackGame();
        var hostPlayer = game.Host.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        var grid = game.Host.SpawnPlatform(new Vector3(20, 50, 0), size: 4);
        game.Tick(2);
        var (client, net) = game.Join();

        // The grid, exactly as the host has it.
        var gridId = grid.Get<EntityId>();
        var copy = client.Registry.Find(gridId);
        Assert.NotNull(copy);
        Assert.Equal(GridSerializer.Voxels(grid.Get<ChunkGrid>().Volume), GridSerializer.Voxels(copy!.Value.Get<ChunkGrid>().Volume));
        Assert.False(copy.Value.Get<NetOwner>().IsLocal); // the host's

        // Its own player, owned by it; the host's player, owned by the host.
        var mine = client.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        Assert.Equal(net.Session.LocalPeer, mine.Get<NetOwner>().Owner);
        Assert.True(client.Registry.IsLive(hostPlayer.Get<EntityId>()));
        // The host has the client's player too, owned by the client.
        var remote = game.Host.Registry.Find(mine.Get<EntityId>())!.Value;
        Assert.False(remote.Get<NetOwner>().IsLocal);
        Assert.False(remote.Has<CharacterControllerComponent>()); // simulated by its owner only
    }

    [Fact]
    public void AWrongChecksumIsRefused()
    {
        using var game = new LoopbackGame();
        var e = Assert.Throws<InvalidOperationException>(() => game.Join(checksum: 7));
        Assert.Contains("generation", e.Message);
    }

    [Fact]
    public void ANameAlreadyInTheGameIsRefused()
    {
        using var game = new LoopbackGame();
        game.Join("a");
        var e = Assert.Throws<InvalidOperationException>(() => game.Join("a"));
        Assert.Contains("already in the game", e.Message);
        game.Join("b");
    }

    [Fact]
    public void TheClientsClockLinesUpWithTheHosts()
    {
        using var game = new LoopbackGame(latencyMs: 40);
        game.Host.Clock.Tick = 5000;
        var (client, net) = game.Join();
        game.Tick(120);
        Assert.InRange((int)client.Clock.Tick - (int)game.Host.Clock.Tick, -2, 2);
        Assert.InRange(net.ClockSync.RoundTripMs, 70, 100);
    }

    [Fact]
    public void TwoPlayersSeeEachOtherMove()
    {
        using var game = new LoopbackGame(latencyMs: 30);
        var hostPlayer = game.Host.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        game.Tick();
        var (client, _) = game.Join();
        var clientPlayer = client.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();

        // Both fly off in different directions.
        for (int i = 0; i < 120; i++)
        {
            hostPlayer.Get<Engine.Input.PlayerInput>() = new Engine.Input.PlayerInput { Held = Engine.Input.PlayerButtons.Forward };
            clientPlayer.Get<Engine.Input.PlayerInput>() = new Engine.Input.PlayerInput { Held = Engine.Input.PlayerButtons.Back };
            game.Tick();
        }
        hostPlayer.Get<Engine.Input.PlayerInput>() = default;
        clientPlayer.Get<Engine.Input.PlayerInput>() = default;
        game.Tick(30);
        var hostSeenByClient = client.Registry.Find(hostPlayer.Get<EntityId>())!.Value;
        var clientSeenByHost = game.Host.Registry.Find(clientPlayer.Get<EntityId>())!.Value;
        Assert.True(Vector3D.Distance(hostSeenByClient.Get<Transform>().Position, hostPlayer.Get<Transform>().Position) < 0.5f);
        Assert.True(Vector3D.Distance(clientSeenByHost.Get<Transform>().Position, clientPlayer.Get<Transform>().Position) < 0.5f);
        Assert.True(hostPlayer.Get<Transform>().Position.Z < -15);   // flew forward (-Z)
        Assert.True(clientPlayer.Get<Transform>().Position.Z > 15);  // and back
    }

    [Fact]
    public void AThirdPlayerSeesTheOtherClientThroughTheHost()
    {
        using var game = new LoopbackGame();
        game.Host.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        game.Tick();
        var (a, _) = game.Join("a");
        var (b, _) = game.Join("b");
        var aPlayer = a.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        aPlayer.Get<Transform>().Position = new Vector3D<float>(12, 70, 3);
        game.Tick(30);
        var aSeenByB = b.Registry.Find(aPlayer.Get<EntityId>());
        Assert.NotNull(aSeenByB);
        Assert.True(Vector3D.Distance(aSeenByB!.Value.Get<Transform>().Position, new Vector3D<float>(12, 70, 3)) < 0.1f);
    }

    [Fact]
    public void ClientEditsGoThroughTheHostToEveryone()
    {
        using var game = new LoopbackGame(latencyMs: 75);
        game.Host.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        var grid = game.Host.SpawnPlatform(new Vector3(0, 55, 0), size: 4);
        game.Tick(2);
        var (a, _) = game.Join("a");
        var (b, _) = game.Join("b");
        var aPlayer = a.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        aPlayer.Get<Transform>().Position = new Vector3D<float>(0, 57, 0);
        game.Tick(10);
        var gridId = grid.Get<EntityId>();
        a.Commands.Send(new EditVoxels { Volume = gridId, Editor = aPlayer.Get<EntityId>(),
            Ops = new[] { VoxelOp.SetBlock(new(1, 1, 1), BlockId.Wood, BlockOrientation.Upright) } });
        a.Tick(); // predicted at once on a...
        Assert.Equal(BlockId.Wood, a.Registry.Find(gridId)!.Value.Get<ChunkGrid>().Volume.GetBlock(1, 1, 1));
        Assert.Equal(1, a.Commands.UnconfirmedPredictionCount);
        game.Tick(30); // ...then decided by the host and sent to everyone
        Assert.Equal(BlockId.Wood, grid.Get<ChunkGrid>().Volume.GetBlock(1, 1, 1));
        Assert.Equal(BlockId.Wood, b.Registry.Find(gridId)!.Value.Get<ChunkGrid>().Volume.GetBlock(1, 1, 1));
        Assert.Equal(0, a.Commands.UnconfirmedPredictionCount);
    }

    [Fact]
    public void ARejectedEditIsUndoneOnTheClient()
    {
        using var game = new LoopbackGame(latencyMs: 50);
        game.Host.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        var grid = game.Host.SpawnPlatform(new Vector3(0, 55, 0), size: 4);
        game.Tick(2);
        var (a, _) = game.Join("a");
        var aPlayer = a.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        aPlayer.Get<Transform>().Position = new Vector3D<float>(0, 300, 0); // far out of reach, as the host will see
        game.Tick(10);
        var gridId = grid.Get<EntityId>();
        a.Commands.Send(new EditVoxels { Volume = gridId, Editor = aPlayer.Get<EntityId>(),
            Ops = new[] { VoxelOp.SetBlock(new(1, 1, 1), BlockId.Wood, BlockOrientation.Upright) } });
        a.Tick();
        Assert.Equal(BlockId.Wood, a.Registry.Find(gridId)!.Value.Get<ChunkGrid>().Volume.GetBlock(1, 1, 1));
        game.Tick(30);
        Assert.Equal(BlockId.Air, a.Registry.Find(gridId)!.Value.Get<ChunkGrid>().Volume.GetBlock(1, 1, 1));
        Assert.Equal(BlockId.Air, grid.Get<ChunkGrid>().Volume.GetBlock(1, 1, 1));
    }

    [Fact]
    public void AClientsOwnModeToggleIsDecidedByItAndSeenByTheHost()
    {
        using var game = new LoopbackGame(latencyMs: 20);
        game.Host.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        game.Tick();
        var (a, _) = game.Join("a");
        var aPlayer = a.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        aPlayer.Get<Engine.Input.PlayerInput>() = new Engine.Input.PlayerInput { Pressed = Engine.Input.PlayerButtons.ToggleFly };
        a.Tick();
        aPlayer.Get<Engine.Input.PlayerInput>() = default;
        Assert.False(aPlayer.Has<FreeFlying>()); // its own authority: at once
        game.Tick(10);
        Assert.False(game.Host.Registry.Find(aPlayer.Get<EntityId>())!.Value.Has<FreeFlying>());
    }

    [Fact]
    public void JoiningWorksWithLatencyAndLoss()
    {
        using var game = new LoopbackGame(latencyMs: 75, lossChance: 0.02);
        var hostPlayer = game.Host.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        game.Host.SpawnPlatform(new Vector3(20, 50, 0), size: 4);
        game.Tick();
        var (a, _) = game.Join("a");
        Assert.Equal(2, a.World.GetEntities().With<Player>().AsEnumerable().Count());
        Assert.Single(a.World.GetEntities().With<DynamicGrid>().AsEnumerable());
    }

    [Fact]
    public void ALeavingPlayerIsDespawnedEverywhere()
    {
        using var game = new LoopbackGame();
        game.Host.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        game.Tick();
        var (a, aNet) = game.Join("a");
        var (b, _) = game.Join("b");
        var aId = a.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single().Get<EntityId>();
        Assert.True(b.Registry.IsLive(aId));
        aNet.Dispose();
        game.Clients.RemoveAll(c => c.Net == aNet);
        game.Tick(10);
        Assert.False(game.Host.Registry.IsLive(aId));
        Assert.False(b.Registry.IsLive(aId));
    }
}

public class GenerationChecksumTests
{
    [Fact]
    public void TheChecksumIsStableAndCoversRealTerrain()
    {
        ulong a = Game.GenerationChecksum.Compute();
        ulong b = Game.GenerationChecksum.Compute();
        Assert.Equal(a, b);
        Assert.NotEqual(0UL, a);
    }
}
