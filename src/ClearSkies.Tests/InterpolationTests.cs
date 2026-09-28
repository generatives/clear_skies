using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Input;
using DefaultEcs;
using Silk.NET.Maths;
using Xunit;
using Xunit.Abstractions;

namespace ClearSkies.Tests;

/// <summary>How far behind, and how smoothly, one machine draws a body another moves.</summary>
public class InterpolationTests
{
    private readonly ITestOutputHelper _out;
    public InterpolationTests(ITestOutputHelper output) => _out = output;

    private const float Speed = 0.1f; // blocks a tick

    /// <summary>A host whose player moves steadily along X, a tick at a time, and a client watching it.</summary>
    private static (LoopbackGame Game, Entity Mover, Entity Seen) Watch(double latencyMs)
    {
        var game = new LoopbackGame(latencyMs);
        var mover = game.Host.SpawnLocalPlayer(new Vector3(0, 60, 0), freeFly: true);
        game.Tick();
        var (client, _) = game.Join();
        var seen = client.Registry.Find(mover.Get<EntityId>())!.Value;
        return (game, mover, seen);
    }

    private static void Step(LoopbackGame game, Entity mover, bool hostRuns = true)
    {
        game.Network.ManualTime += 1000.0 / 60.0;
        if (hostRuns)
        {
            mover.Get<Transform>().Position = new Vector3D<float>(game.Host.Clock.Tick * Speed, 60, 0);
            game.Host.Tick();
        }
        foreach (var (scene, _) in game.Clients) scene.Tick();
    }

    /// <summary>How many of the host's ticks behind the client draws the mover.</summary>
    private static double Behind(LoopbackGame game, Entity seen) => game.Host.Clock.Tick - 1 - seen.Get<Transform>().Position.X / Speed;

    [Theory]
    [InlineData(0, 4.5)]
    [InlineData(30, 4.5 + 2)]
    public void OnAQuietNetworkRemoteBodiesAreDrawnJustBehind(double latencyMs, double most)
    {
        var (game, mover, seen) = Watch(latencyMs);
        using var _ = game;
        for (int i = 0; i < 300; i++) Step(game, mover);
        double behind = Behind(game, seen);
        _out.WriteLine($"{behind:0.00} ticks behind at {latencyMs} ms");
        Assert.InRange(behind - latencyMs / (1000.0 / 60.0), 0, most);
    }

    [Fact]
    public void AfterTheHostHitchesTheMoverIsSmoothAgainQuickly()
    {
        var (game, mover, seen) = Watch(0);
        using var _ = game;
        for (int i = 0; i < 300; i++) Step(game, mover);
        for (int i = 0; i < 20; i++) Step(game, mover, hostRuns: false); // a third of a second: its ticks are dropped
        // After half a second, the client draws it moving steadily, and not far behind.
        for (int i = 0; i < 30; i++) Step(game, mover);
        int rough = 0;
        float last = seen.Get<Transform>().Position.X;
        for (int i = 0; i < 180; i++)
        {
            Step(game, mover);
            float x = seen.Get<Transform>().Position.X;
            if (MathF.Abs(x - last - Speed) > Speed * 0.1f) rough++;
            last = x;
        }
        double behind = Behind(game, seen);
        _out.WriteLine($"{rough} rough frames of 180; {behind:0.00} ticks behind");
        Assert.True(rough <= 10, $"{rough} rough frames");
        Assert.InRange(behind, 0, 6);
    }

    /// <summary>Both players walk steadily across a platform, host and client each at 60 fps with a couple of
    /// milliseconds of noise in their frame times: each draws the other moving as steadily as they draw themselves.
    /// Arrivals wobble by a tick with frame timing; if the delay followed them it would keep speeding the body up and
    /// slowing it down.</summary>
    [Fact]
    public void WalkingPlayersAreDrawnMovingSteadily()
    {
        var game = new LoopbackGame(30);
        using var _ = game;
        game.Host.SpawnPlatform(new Vector3(0, 50, 0), 64);
        var hostPlayer = game.Host.SpawnLocalPlayer(new Vector3(-3, 52, 25));
        game.Tick(2);
        var (client, _) = game.Join("walker");
        var clientPlayer = client.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        Players.SetFreeFlying(clientPlayer, false);
        clientPlayer.Get<CharacterControllerComponent>().Character.TeleportTo(new Vector3(3, 51.4f, 25));
        game.Tick(60);
        var hostSeen = client.Registry.Find(hostPlayer.Get<EntityId>())!.Value;
        var clientSeen = game.Host.Registry.Find(clientPlayer.Get<EntityId>())!.Value;

        var rng = new Random(2);
        double now = 0, hostNoise = 0, clientNoise = 0;
        var onClient = new List<(double T, float Z)>();
        var onHost = new List<(double T, float Z)>();
        for (int frame = 0; frame < 240; frame++)
        {
            hostPlayer.Get<PlayerInput>() = new PlayerInput { Held = PlayerButtons.Forward };
            clientPlayer.Get<PlayerInput>() = new PlayerInput { Held = PlayerButtons.Forward };
            double h = (rng.NextDouble() * 2 - 1) * 0.002, c = (rng.NextDouble() * 2 - 1) * 0.002;
            game.Network.ManualTime += 1000.0 / 60.0;
            now += 1 / 60.0;
            game.Host.Frame(1 / 60.0 + h - hostNoise);
            client.Frame(1 / 60.0 + c - clientNoise);
            (hostNoise, clientNoise) = (h, c);
            if (frame < 90) continue; // up to speed
            onClient.Add((now + c, hostSeen.Get<Transform>().Position.Z));
            onHost.Add((now + h, clientSeen.Get<Transform>().Position.Z));
        }
        float seenByClient = Jitter(onClient), seenByHost = Jitter(onHost);
        _out.WriteLine($"off steady motion, frame to frame: host as the client sees them {seenByClient:0.0000}, client as the host sees them {seenByHost:0.0000} (a frame's step is about 0.08)");
        Assert.True(seenByClient < 0.002f, $"the host's player jitters by up to {seenByClient} blocks a frame");
        Assert.True(seenByHost < 0.002f, $"the client's player jitters by up to {seenByHost} blocks a frame");
    }

    /// <summary>The largest frame-to-frame change in how far a path is off a straight line in real time.</summary>
    private static float Jitter(List<(double T, float Z)> path)
    {
        double mt = path.Average(p => p.T), mz = path.Average(p => p.Z);
        double slope = path.Sum(p => (p.T - mt) * (p.Z - mz)) / path.Sum(p => (p.T - mt) * (p.T - mt));
        var off = path.Select(p => p.Z - mz - slope * (p.T - mt)).ToList();
        return (float)off.Zip(off.Skip(1), (a, b) => System.Math.Abs(b - a)).Max();
    }
}
