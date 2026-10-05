using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Input;
using ClearSkies.Net.Protocol;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Net.Sync;

/// <summary>A player played on another machine, as the host simulates them: their inputs waiting to be applied, oldest
/// first, and the last one applied (its number goes back in their snapshots, see <see cref="SnapshotFlags.HasInput"/>).</summary>
public sealed class InputQueue
{
    internal readonly List<InputSample> Waiting = new();
    /// <summary>The newest input received (older or repeated ones are skipped).</summary>
    public uint Newest { get; internal set; }
    /// <summary>The last input applied: their snapshots say they're where that left them.</summary>
    public uint Applied { get; internal set; }
    internal PlayerInput Last;
    internal int LeastWaiting = int.MaxValue;
    internal int WindowTicks;

    public int Count => Waiting.Count;
}

/// <summary>ECS component on a player the host simulates for another machine.</summary>
public struct RemoteInput
{
    public InputQueue Queue;
}

/// <summary>
/// The host's side of other machines' input. Each player played elsewhere sends every tick's input, numbered
/// (<see cref="PlayerInputMessage"/>); here they wait in order and one is applied each tick, as that player's
/// <see cref="PlayerInput"/> and look, before anything reads them. So the host moves them through exactly the inputs
/// their machine predicted them with, in the same order, even across a stall: what built up meanwhile is worked through
/// one a tick as the host's clock catches up.
/// <para>No input in hand (late, or lost in every packet that carried it): the last one is held again, without its
/// presses. A queue that stays longer than it needs to (more than <see cref="Target"/> waiting all through
/// <see cref="WindowTicks"/>, as when this machine dropped ticks it couldn't catch up) is shortened by skipping inputs,
/// their presses kept, so the delay doesn't stay.</para>
/// </summary>
public sealed class RemoteInputs : IDebugUiSystem
{
    /// <summary>How many inputs a queue keeps in hand against jitter before it's shortened.</summary>
    public const int Target = 2;
    /// <summary>Ticks a queue is watched over before it's shortened (a second).</summary>
    public const int WindowTicks = 60;

    private readonly EntitySet _players;
    private long _received, _repeated, _skipped;

    public RemoteInputs(World world)
    {
        _players = world.GetEntities().With<RemoteInput>().With<PlayerInput>().AsSet();
    }

    /// <summary>Inputs from the machine that plays <paramref name="player"/>.</summary>
    public void Receive(Entity player, in PlayerInputMessage message)
    {
        if (!player.Has<PlayerInput>()) return; // not simulated here
        if (!player.Has<RemoteInput>()) player.Set(new RemoteInput { Queue = new InputQueue() });
        var queue = player.Get<RemoteInput>().Queue;
        foreach (var sample in message.Samples)
        {
            if (sample.Sequence <= queue.Newest) continue; // already have it
            queue.Waiting.Add(sample);
            queue.Newest = sample.Sequence;
            _received++;
        }
    }

    /// <summary>Once a tick, first: each player's next input, as their <see cref="PlayerInput"/> and look.</summary>
    public void Update()
    {
        foreach (ref readonly var e in _players.GetEntities())
        {
            var queue = e.Get<RemoteInput>().Queue;
            ref var input = ref e.Get<PlayerInput>();
            if (queue.Waiting.Count > 0)
            {
                var next = queue.Waiting[0];
                queue.Waiting.RemoveAt(0);
                input = next.ToInput();
                queue.Applied = next.Sequence;
            }
            else
            {
                input = queue.Last with { Pressed = PlayerButtons.None }; // still holding what they held
                _repeated++;
            }
            queue.Last = input;
            Shorten(queue);

            if (e.Has<MouseLookComponent>())
            {
                ref var look = ref e.Get<MouseLookComponent>();
                (look.Yaw, look.Pitch) = (input.Yaw, input.Pitch);
                if (e.Has<Transform>()) e.Get<Transform>().Rotation = look.BodyRotation;
            }
        }
    }

    /// <summary>Skips inputs a queue has had more of than it needs all through the last <see cref="WindowTicks"/>,
    /// keeping their presses for the next one applied.</summary>
    private void Shorten(InputQueue queue)
    {
        queue.LeastWaiting = System.Math.Min(queue.LeastWaiting, queue.Waiting.Count);
        if (++queue.WindowTicks < WindowTicks) return;
        int excess = queue.LeastWaiting - Target;
        queue.LeastWaiting = int.MaxValue;
        queue.WindowTicks = 0;
        if (excess <= 0) return;
        var pressed = PlayerButtons.None;
        for (int i = 0; i < excess; i++) pressed |= queue.Waiting[i].Pressed;
        queue.Waiting.RemoveRange(0, excess);
        queue.Waiting[0] = queue.Waiting[0] with { Pressed = queue.Waiting[0].Pressed | pressed };
        _skipped += excess;
    }

    public string DebugName => "Remote input";

    public void DrawDebugUi()
    {
        ImGui.Text($"Inputs received {_received:N0}, held over {_repeated:N0}, skipped {_skipped:N0}");
        foreach (ref readonly var e in _players.GetEntities())
            ImGui.Text($"  {e.Get<Player>().Name}: {e.Get<RemoteInput>().Queue.Count} waiting, applied #{e.Get<RemoteInput>().Queue.Applied}");
    }
}
