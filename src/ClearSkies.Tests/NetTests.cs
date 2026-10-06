using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Persistence;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Session;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Transport;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;

namespace ClearSkies.Tests;

/// <summary>A Host and Participants in one process, stepped tick by tick in lockstep, with network time advancing a tick
/// per tick. The hosting machine's Participant (<see cref="Host"/>'s scene, the authority, with nobody playing on it)
/// joins the Host directly, as in the game; clients over <see cref="Network"/>.</summary>
public sealed class LoopbackGame : IDisposable
{
    public const ulong Checksum = 42;
    public readonly LoopbackNetwork Network = new() { ManualTime = 0 };
    public readonly SaveDatabase Save;
    public readonly Host Hub;
    public readonly HeadlessScene Host = new();
    public readonly HostNetwork Remote;
    public readonly SimulationParticipant HostNet;
    public readonly List<(HeadlessScene Scene, SimulationParticipant Net)> Clients = new();

    /// <param name="save">The world's save, which the hosting machine streams entities from and saves to (none: an empty
    /// one, and no streaming).</param>
    /// <param name="hostTerrainReady">Whether the terrain around a point is ready on the hosting machine (always, if
    /// not given: test scenes stream none).</param>
    /// <param name="hostPlayer">Someone playing on the hosting machine, as the game has them: they join with its
    /// Participant, where this says (none: nobody, as on a dedicated host).</param>
    public LoopbackGame(double latencyMs = 0, double lossChance = 0, SaveDatabase? save = null, Func<Vector3, bool>? hostTerrainReady = null,
                        PlayerDescription? hostPlayer = null)
    {
        Network.LatencyMs = latencyMs;
        Network.LossChance = lossChance;
        Save = save ?? SaveDatabase.InMemory();
        if (hostPlayer is not null) SavePlayer("host", hostPlayer);
        Hub = new Host(Save, Host.Clock, seed: 1337, generationChecksum: Checksum, newPlayerSpawn: (new Vector3(0, 60, 0), 0, 0));
        Remote = new HostNetwork(Hub, Network.Listen());
        HostNet = SimulationParticipant.Join(Hub, new Hello(ProtocolVersion.Current, hostPlayer is null ? "" : "host", Checksum),
                                             Host.Session, Host.Commands, Host.Registry, Host.World, Host.Clock, hostTerrainReady ?? (_ => true));
        HostNet.TimeSource = () => Network.Now;
        HostNet.Viewing = hostPlayer is not null;
        Host.AttachNet(HostNet, Remote, Hub);
        if (save is not null) Host.EnablePersistence(Save, Hub.Ids);
        if (hostPlayer is not null && !Host.TickUntil(() => HostNet.Joined, 60)) throw new TimeoutException("The host's player never spawned.");
    }

    /// <summary>As if <paramref name="name"/> had played before and left as <paramref name="d"/>.</summary>
    public void SavePlayer(string name, PlayerDescription d)
    {
        d.Id = Save.PlayerFor(name);
        d.Name = name;
        Save.WritePlayer(d.Id, name, DescriptionBytes.Of(d));
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

    /// <summary>Saves, as on exit (the hosting machine's world writes everything).</summary>
    public void SaveAll() => (Host.Saver ?? throw new InvalidOperationException("No save to write to.")).SaveAll();

    /// <summary>A client joining: hello, welcome, then (unless not to <paramref name="wait"/>) ticking until its player
    /// has arrived.</summary>
    public (HeadlessScene Scene, SimulationParticipant Net) Join(string name = "client", ulong checksum = Checksum, int maxTicks = 600, bool wait = true)
    {
        var transport = Network.Connect();
        var scene = new HeadlessScene();
        using (var join = new JoinRequest(transport, new Hello(ProtocolVersion.Current, name, checksum)))
        {
            Welcome welcome;
            int guard = 0;
            while (!join.TryComplete(out welcome))
            {
                if (++guard > maxTicks) throw new TimeoutException("No welcome.");
                Tick();
            }
            var link = new RemoteHost(transport, welcome);
            var net = SimulationParticipant.Join(link, scene.Session, scene.Commands, scene.Registry, scene.World, scene.Clock, _ => true);
            net.TimeSource = () => Network.Now;
            scene.AttachNet(net, link);
            Clients.Add((scene, net));
            if (!wait) return (scene, net);
            for (int i = 0; i < maxTicks && !net.Joined; i++) Tick();
            if (!net.Joined) throw new TimeoutException("Never joined.");
            return (scene, net);
        }
    }

    /// <summary>The hosting machine's player for a client's <paramref name="player"/>: the one it simulates.</summary>
    public Entity Simulated(Entity player) => Host.Registry.Find(player.Get<EntityId>())!.Value;

    /// <summary>Moves a client's player, at rest: on the hosting machine, which simulates them, and on the client, which
    /// predicts them.</summary>
    public void Teleport(Entity player, Vector3 at)
    {
        foreach (var e in new[] { Simulated(player), player }) ClearSkies.Engine.ECS.Players.Teleport(e, new Vector3D<float>(at.X, at.Y, at.Z));
    }

    public void Dispose()
    {
        foreach (var (scene, _) in Clients) scene.Dispose();
        Host.Dispose();
        Remote.Dispose();
        Save.Dispose();
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
    /// <summary>A host and a client clock, both stepped in real time; the client's ticks run at its Rate.</summary>
    private sealed class Pair
    {
        public readonly Engine.Core.ManualTickClock Client = new();
        public readonly ClockSync Sync;
        public double NowMs, HostStart;

        public Pair(uint clientTick, double hostTick, bool settling = false)
        {
            Client.Tick = clientTick;
            HostStart = hostTick;
            Sync = new ClockSync(Client) { Settling = settling };
        }

        public double HostTick(double atMs) => HostStart + atMs / ClockSync.TickMs;

        public void Advance(double ms)
        {
            double ticks = Client.Alpha + ms / ClockSync.TickMs * Client.Rate;
            Client.Tick += (uint)ticks;
            Client.Alpha = (float)(ticks - System.Math.Floor(ticks));
            NowMs += ms;
        }

        /// <summary>A ping now, answered halfway through <paramref name="rttMs"/>; the answer is handled on arrival.</summary>
        public void Ping(double rttMs)
        {
            double sent = NowMs, host = HostTick(sent + rttMs / 2);
            Advance(rttMs);
            Sync.OnPong(new TimePong(sent, (uint)host, (float)(host - System.Math.Floor(host))), NowMs);
        }

        /// <summary>The host's tick now less the client's.</summary>
        public double Off => HostTick(NowMs) - Client.Now;
    }

    [Fact]
    public void AFarOffClockSnapsAndACloseOneSlews()
    {
        var p = new Pair(clientTick: 0, hostTick: 1000);
        p.Ping(100);
        Assert.Equal(1, p.Sync.Snaps);
        Assert.InRange(p.Off, -0.5, 0.5);

        // The host 3 ticks further on than we'd think: run faster, no jump.
        p.HostStart += 3;
        for (int i = 0; i < 4; i++) { p.Advance(150); p.Ping(100); }
        Assert.Equal(1, p.Sync.Snaps);
        Assert.True(p.Client.Rate > 1);
        Assert.True(p.Client.Rate <= 1 + ClockSync.MaxSlew);
    }

    [Fact]
    public void SlewingDoesNotOvershoot()
    {
        // 4 ticks behind: slews (no snap), and the answers from before it sped up don't keep it speeding past.
        var p = new Pair(clientTick: 5000, hostTick: 5004);
        double most = 0;
        for (int i = 0; i < 80; i++)
        {
            p.Advance(150);
            p.Ping(100);
            most = System.Math.Min(most, p.Off);
        }
        Assert.Equal(0, p.Sync.Snaps);
        Assert.InRange(p.Off, -0.5, 0.5);
        Assert.True(most > -0.6, $"overshot to {most:0.00} ticks ahead");
    }

    [Fact]
    public void JitteryAnswersAreDiscounted()
    {
        var clock = new Engine.Core.ManualTickClock { Tick = 5000 };
        var sync = new ClockSync(clock);
        // Most answers: 60 ms round trip... one very late one says otherwise.
        for (int i = 0; i < 12; i++) sync.OnPong(new TimePong(i * 250, 5000 + 1, 0), nowMs: i * 250 + 60);
        sync.OnPong(new TimePong(4000, 5001, 0), nowMs: 4000 + 600);
        Assert.InRange(sync.RoundTripMs, 55, 65);
    }

    [Fact]
    public void TicksASlowFrameDroppedArePutBackWithoutASnap()
    {
        var p = new Pair(clientTick: 5000, hostTick: 5000, settling: true);
        for (int i = 0; i < 8; i++) { p.Advance(190); p.Ping(60); }
        long snaps = p.Sync.Snaps;

        // A 400 ms frame (loading): 15 ticks run, 9 dropped, so the clock is 9 behind the host's.
        p.Advance(400);
        p.Client.Tick -= 9;
        p.Client.DroppedTicks += 9;
        Assert.InRange(p.Off, 8.5, 9.5);
        p.Sync.Update(p.NowMs);
        Assert.InRange(p.Off, -0.5, 0.5);
        Assert.Equal(9, p.Sync.SkippedTicks);

        // The host's answers find it on time: no snap, and settled.
        for (int i = 0; i < ClockSync.SettleAnswers; i++) { p.Advance(190); p.Ping(60); }
        Assert.Equal(snaps, p.Sync.Snaps);
        Assert.True(p.Sync.Settled);
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

        // Its own player, played here but owned by the host; and the host's player.
        var mine = client.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        Assert.Equal(PeerId.Host, mine.Get<NetOwner>().Owner);
        Assert.Equal(net.Session.LocalPeer, mine.Get<Player>().ControllingPeer);
        Assert.True(client.Registry.IsLive(hostPlayer.Get<EntityId>()));
        // The host simulates the client's player, from its input.
        var remote = game.Host.Registry.Find(mine.Get<EntityId>())!.Value;
        Assert.True(remote.Get<NetOwner>().IsLocal);
        Assert.False(remote.Get<Player>().IsLocal);
        Assert.True(remote.Has<CharacterControllerComponent>());
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
    public void TheClientsClockHasSettledByTheTimeItJoins()
    {
        // The welcome's tick is a one-way trip old by the time it arrives: joining waits for pings to put that right, so
        // the game doesn't start with the clock running fast to catch up.
        using var game = new LoopbackGame(latencyMs: 50);
        game.Host.Clock.Tick = 5000;
        var (client, net) = game.Join();
        Assert.True(net.ClockSync.Settled);
        Assert.False(net.ClockSync.Settling);
        Assert.Equal(1.0, client.Clock.Rate);
        Assert.InRange((int)client.Clock.Tick - (int)game.Host.Clock.Tick, -1, 1);
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
        game.Teleport(aPlayer, new Vector3(12, 70, 3));
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
        game.Teleport(aPlayer, new Vector3(0, 57, 0));
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
        game.Teleport(aPlayer, new Vector3(0, 300, 0)); // far out of reach
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
    public void TheHostingMachinesPlayerIsSimulatedThereAndSeenByClients()
    {
        using var game = new LoopbackGame(hostPlayer: new PlayerDescription { FreeFly = true, Position = new Vector3(5, 60, 5) });
        var mine = game.Host.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        Assert.True(mine.Get<NetOwner>().IsLocal);
        Assert.True(mine.Get<Player>().IsLocal);
        Assert.Equal(new Vector3D<float>(5, 60, 5), mine.Get<Transform>().Position);
        Assert.True(game.Hub.Entities[mine.Get<EntityId>()].IsPlayer);

        var (client, _) = game.Join();
        var copy = client.Registry.Find(mine.Get<EntityId>());
        Assert.NotNull(copy);
        Assert.False(copy!.Value.Has<LocalPlayer>());
        Assert.Equal(PeerId.Host, copy.Value.Get<Player>().ControllingPeer);

        // It moves there, and the client sees it move.
        Players.Teleport(mine, new Vector3D<float>(25, 60, 5));
        game.Tick(30);
        Assert.InRange(copy.Value.Get<Transform>().Position.X, 24.9f, 25.1f);
    }

    [Fact]
    public void ALeavingPlayerIsSavedAndRejoinsWhereTheyLeft()
    {
        using var game = new LoopbackGame();
        var (a, aNet) = game.Join("a");
        var player = a.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        game.Teleport(player, new Vector3(40, 70, -10));
        game.Tick(10);
        aNet.Dispose();
        game.Clients.RemoveAll(c => c.Net == aNet);
        game.Tick(10);
        Assert.False(game.Host.Registry.IsLive(player.Get<EntityId>()));
        Assert.Empty(game.Hub.Entities.Values.Where(e => e.IsPlayer)); // gone from the Host's record too

        var (again, _) = game.Join("a");
        var back = again.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        Assert.Equal(player.Get<Player>().Id, back.Get<Player>().Id);
        Assert.InRange(Vector3.Distance(new Vector3(40, 70, -10), new Vector3(back.Get<Transform>().Position.X, back.Get<Transform>().Position.Y, back.Get<Transform>().Position.Z)), 0, 0.01f);
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
