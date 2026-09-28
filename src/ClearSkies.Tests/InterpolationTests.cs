using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
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
}
