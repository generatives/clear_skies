using System.Numerics;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Support;
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
/// <para>An input can say the one before it landed the player on a ship, and where (<see cref="InputSample.Landed"/>).
/// Their machine sees the ship where it was a moment ago, so here they'd come down further along its deck, or miss it:
/// they're put where they landed there, if that's within reach of where they are here (<see cref="ClaimSlack"/>, and as
/// far as the ship moves in <see cref="ClaimSeconds"/>). It's the same deck in its own space on both machines.</para>
/// </summary>
public sealed class RemoteInputs : IDebugUiSystem
{
    /// <summary>How many inputs a queue keeps in hand against jitter before it's shortened.</summary>
    public const int Target = 2;
    /// <summary>Ticks a queue is watched over before it's shortened (a second).</summary>
    public const int WindowTicks = 60;

    /// <summary>How far, in metres, from where a player is here a landing they claim may be, on a ship standing still.</summary>
    public const float ClaimSlack = 1f;
    /// <summary>...and further, as far as the ship's deck there moves in this many seconds: longer than a round trip and
    /// the delay a ship is drawn with.</summary>
    public const float ClaimSeconds = 0.5f;

    private readonly EntitySet _players;
    private readonly EntityRegistry _registry;
    private long _received, _repeated, _skipped, _landed, _landingsRefused;

    public RemoteInputs(World world, EntityRegistry registry)
    {
        _players = world.GetEntities().With<RemoteInput>().With<PlayerInput>().AsSet();
        _registry = registry;
    }

    /// <summary>For how fast a ship moves (set by the body sync); without it, ships count as standing still.</summary>
    public PhysicsWorld? Physics { get; set; }

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
                if (!next.Landed.IsNone) Land(e, next);
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

    /// <summary>Puts the player where their machine had the last input land them, if it's within reach.</summary>
    private void Land(Entity player, in InputSample claim)
    {
        if (player.Has<FreeFlying>() || !player.Has<Support>() || !player.Has<Transform>()) return;
        if (!_registry.TryGet(claim.Landed, out var ship) || !ship.Has<Supportable>() || !ship.Has<Transform>()) return;
        ref readonly var support = ref player.Get<Support>();
        if (support.Supporter == ship && Vector3.Distance(support.LocalPosition, claim.LandedAt) < OwnPlayerPrediction.Tolerance) return;

        ref readonly var st = ref ship.Get<Transform>();
        var rotation = new Quaternion(st.Rotation.X, st.Rotation.Y, st.Rotation.Z, st.Rotation.W);
        var spot = new Vector3(st.Position.X, st.Position.Y, st.Position.Z) + Vector3.Transform(claim.LandedAt, rotation);
        float speed = 0f;
        if (Physics != null && ship.Has<PhysicsBodyComponent>())
        {
            ref readonly var body = ref ship.Get<PhysicsBodyComponent>();
            var centre = body.BodyPosition(st);
            speed = (Physics.GetBodyLinearVelocity(body.Body) + Vector3.Cross(Physics.GetBodyAngularVelocity(body.Body), spot - centre)).Length();
        }
        ref readonly var t = ref player.Get<Transform>();
        if (Vector3.Distance(new Vector3(t.Position.X, t.Position.Y, t.Position.Z), spot) > ClaimSlack + speed * ClaimSeconds)
        {
            _landingsRefused++;
            return;
        }
        Players.PlaceOn(player, ship, claim.LandedAt, claim.LandedVelocity);
        _landed++;
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
        ImGui.Text($"Landings put where their machine had them {_landed:N0}, out of reach {_landingsRefused:N0}");
        foreach (ref readonly var e in _players.GetEntities())
            ImGui.Text($"  {e.Get<Player>().Name}: {e.Get<RemoteInput>().Queue.Count} waiting, applied #{e.Get<RemoteInput>().Queue.Applied}");
    }
}
