using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Input;
using ClearSkies.Net.Sync;
using DefaultEcs;
using Xunit;
using Xunit.Abstractions;

namespace ClearSkies.Tests;

/// <summary>A client's own player: simulated by the host from the client's input, and predicted on the client.</summary>
public class OwnPlayerPredictionTests
{
    private readonly ITestOutputHelper _out;
    public OwnPlayerPredictionTests(ITestOutputHelper output) => _out = output;

    /// <summary>A host with a wide platform and a client walking on it.</summary>
    private static (LoopbackGame game, HeadlessScene client, Entity player, OwnPlayerPrediction prediction) Walker(double latencyMs, double loss = 0)
    {
        var game = new LoopbackGame(latencyMs, loss);
        game.Host.SpawnPlatform(new Vector3(0, 50, 0), 64);
        game.Host.SpawnLocalPlayer(new Vector3(-20, 52, 0));
        game.Tick(2);
        var (client, _) = game.Join("walker");
        var player = client.World.GetEntities().With<LocalPlayer>().AsEnumerable().Single();
        CrewTests.PlaceCrew(game, player, new Vector3(0, 51.4f, 0));
        game.Tick(60);
        return (game, client, player, (OwnPlayerPrediction)client.Net!.Prediction!);
    }

    private static Vector3 At(Entity player) => player.Get<CharacterControllerComponent>().Character.Position;

    /// <summary>Walking, jumping and turning with a round trip of 200 ms: the client moves at the first tick, the host
    /// follows the same path, and they end where each other has them. With loss, an input sometimes isn't at the host
    /// in time; it holds the last one a tick longer and later skips one to catch up, so the client is put right by
    /// about a tick's walk (0.08).</summary>
    [Theory]
    [InlineData(100, 0, 0.1f)]
    [InlineData(100, 0.1, 0.15f)]
    public void AClientsPlayerMovesAtOnceAndTheHostAgrees(double latencyMs, double loss, float mostCorrected)
    {
        var (game, client, player, prediction) = Walker(latencyMs, loss);
        using var _ = game;
        var simulated = game.Simulated(player);
        long before = prediction.Corrections;
        float largest = 0f;
        var start = At(player);
        for (int t = 0; t < 120; t++)
        {
            // Walk a second, jumping halfway, then turn and walk back.
            player.Get<PlayerInput>() = new PlayerInput
            {
                Held = t < 60 ? PlayerButtons.Forward : PlayerButtons.Back,
                Pressed = t == 30 ? PlayerButtons.Up : PlayerButtons.None,
                Yaw = t * 0.01f,
            };
            long c0 = prediction.Corrections;
            client.Tick();
            if (prediction.Corrections != c0) largest = MathF.Max(largest, prediction.LastCorrection);
            if (t == 0) Assert.True(Vector3.Distance(At(player), start) > 0f, "didn't move at once");
            game.Network.ManualTime += 1000.0 / 60.0;
            game.Host.Tick();
        }
        player.Get<PlayerInput>() = default;
        game.Tick(60);
        float apart = Vector3.Distance(At(player), At(simulated));
        _out.WriteLine($"{prediction.Corrections - before} corrections, largest {largest:F3}; {apart:F4} apart at rest");
        Assert.True(Vector3.Distance(At(player), start) > 1f, "they walked");
        Assert.True(apart < 0.05f, $"host and client {apart:F3} apart");
        Assert.True(largest < mostCorrected, $"corrected by up to {largest:F3}");
    }

    [Fact]
    public void AClientWalksOnThroughAHostStall()
    {
        // The host at 1 fps for three seconds, its clock dropping the ticks it can't catch up, while the client walks.
        var (game, client, player, prediction) = Walker(20);
        using var _ = game;
        var simulated = game.Simulated(player);
        var queue = simulated.Get<RemoteInput>().Queue;
        var hostClock = new ClearSkies.Engine.Core.TickClock();
        var start = At(player);
        int most = 0;
        for (int frame = 1; frame <= 600; frame++)
        {
            bool stalled = frame <= 180;
            player.Get<PlayerInput>() = new PlayerInput { Held = frame <= 240 ? PlayerButtons.Forward : PlayerButtons.None };
            game.Network.ManualTime += 1000.0 / 60.0;
            if (!stalled || frame % 60 == 0) game.Host.Tick(hostClock.Advance(stalled ? 1.0 : 1 / 60.0));
            client.Tick();
            most = Math.Max(most, queue.Count);
        }
        float apart = Vector3.Distance(At(player), At(simulated));
        _out.WriteLine($"queue up to {most}, now {queue.Count}; corrections {prediction.Corrections}, largest {prediction.LargestCorrection:F3}; {apart:F4} apart");
        Assert.True(Vector3.Distance(At(player), start) > 5f, "they walked");
        Assert.True(apart < 0.05f, $"host and client {apart:F3} apart");
        Assert.InRange(queue.Count, 0, RemoteInputs.Target + 1); // the stall's backlog is gone
    }
}
