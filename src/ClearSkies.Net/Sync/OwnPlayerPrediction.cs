using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Session;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Net.Sync;

/// <summary>
/// A client's own player, predicted. The host simulates every player; this machine also simulates its own, straight away,
/// from its own input, so it moves with no delay. Each tick, after the input is sampled and before movement:
/// <list type="number">
/// <item>Where the last tick left the player is recorded under that tick's input number.</item>
/// <item>If the host has said where its simulation left them (a snapshot with the number of the last of their inputs it
/// applied, <see cref="SnapshotFlags.HasInput"/>), that's compared with what was recorded for the same input. Any
/// difference, beyond <see cref="Tolerance"/>, is how far the prediction went wrong: the player is moved by it now (and
/// drawn easing over, <see cref="InterpolatedTransform.Smooth"/>), and
/// so is every later record, since they all carry the same mistake. Positions are compared in the space of what the
/// player stands on (a ship's, or the world's), where a ship's own motion, which the two machines see at different
/// times, doesn't count.</item>
/// <item>This tick's input is numbered and sent to the host, with the few before it (<see cref="PlayerInputMessage"/>).
/// If the last one landed the player on a ship, it says where on it (<see cref="InputSample.Landed"/>): the ship here is
/// where the host had it a moment ago, so on its own the host would have them come down somewhere else on its deck.</item>
/// </list>
/// The host runs the same inputs in the same order (<see cref="RemoteInputs"/>), so the two agree unless the world
/// differed: a ship seen at another moment, another player in the way, a stall.
/// </summary>
public sealed class OwnPlayerPrediction : ISystem
{
    /// <summary>Records kept: two seconds of inputs, longer than any round trip worth predicting through.</summary>
    public const int HistoryLength = 128;
    /// <summary>Prediction errors smaller than this (metres) are left alone.</summary>
    public const float Tolerance = 0.02f;
    /// <summary>Velocity errors in the air smaller than this (m/s) are left alone. On the ground none are corrected: the
    /// character controller closes them itself within a tick or two.</summary>
    public const float VelocityTolerance = 0.5f;
    /// <summary>Answers in a row that may say the player stands on something else than recorded before that's
    /// corrected: a landing reaches each machine a tick or two apart, and settles itself.</summary>
    public const int SupportMismatchAnswers = 30;

    /// <summary>Where a tick's input left the player: on <see cref="Support"/> (its space) or in the world.</summary>
    private struct Record
    {
        public uint Input;
        public EntityId Support;
        public Vector3 Position;
        public Vector3 Velocity;
        public bool FreeFlying;
        /// <summary>Standing on something (a ship, terrain), as the character controller has it.</summary>
        public bool Grounded;
    }

    private readonly ClientSession _net;
    private readonly EntityRegistry _registry;
    private readonly EntitySet _local;
    private readonly Record[] _history = new Record[HistoryLength];
    private readonly InputSample[] _sent = new InputSample[PlayerInputMessage.MaxSamples];
    private int _sentCount;
    private uint _input;
    private BodySnapshot? _answer;
    private uint _lastAnswer;
    private int _supportMismatches;
    private int _modeMismatches;
    private uint _landedOn; // the last input that landed the player on a ship, which the next one tells the host

    public OwnPlayerPrediction(ClientSession net, World world, EntityRegistry registry)
    {
        _net = net;
        _registry = registry;
        _local = world.GetEntities().With<LocalPlayer>().With<Player>().With<Transform>().With<PlayerInput>().AsSet();
        net.Prediction = this;
    }

    /// <summary>Corrections made, and the largest and latest (metres).</summary>
    public long Corrections { get; private set; }
    public float LargestCorrection { get; private set; }
    public float LastCorrection { get; private set; }
    /// <summary>Inputs sent but not yet answered by the host.</summary>
    public uint Unanswered => _input - _lastAnswer;

    /// <summary>The host's word on where the player is, from a snapshot (newest wins).</summary>
    public void Answer(in BodySnapshot snapshot)
    {
        if ((snapshot.Flags & SnapshotFlags.HasInput) == 0 || snapshot.Input <= _lastAnswer) return;
        _lastAnswer = snapshot.Input;
        _answer = snapshot;
    }

    public void Update(float dt)
    {
        foreach (ref readonly var e in _local.GetEntities())
        {
            var landing = _input > 0 ? Remember(e) : null;
            if (_answer is { } answer)
            {
                _answer = null;
                Correct(e, answer);
            }
            Send(e.Get<PlayerInput>(), landing);
            return; // one local player
        }
    }

    /// <summary>Records where the last input left the player. If it landed them on something new, returns that record,
    /// which the next input tells the host.</summary>
    private Record? Remember(Entity e)
    {
        var before = _history[(_input - 1) % HistoryLength];
        var now = _history[_input % HistoryLength] = Capture(e, _input);
        if (!Landed(before, now)) return null;
        _landedOn = _input;
        return now;
    }

    /// <summary>Whether the player came to stand on something they weren't on the input before.</summary>
    private bool Landed(in Record before, in Record now)
    {
        bool hadBefore = _input > 1 && before.Input == _input - 1; // (the first input has none: the host placed them)
        return hadBefore && now.Grounded && !now.Support.IsNone && now.Support != before.Support;
    }

    private void Send(in PlayerInput input, Record? landing)
    {
        var sample = new InputSample(++_input, input.Held, input.Pressed, input.Yaw, input.Pitch,
                                     landing?.Support ?? default, landing?.Position ?? default, landing?.Velocity ?? default);
        if (_sentCount == _sent.Length) Array.Copy(_sent, 1, _sent, 0, --_sentCount);
        _sent[_sentCount++] = sample;
        _net.SendInput(new PlayerInputMessage(_sent[.._sentCount]));
    }

    private static Record Capture(Entity e, uint input)
    {
        var r = new Record { Input = input, FreeFlying = e.Has<FreeFlying>() };
        ref readonly var t = ref e.Get<Transform>();
        r.Position = new Vector3(t.Position.X, t.Position.Y, t.Position.Z);
        if (!r.FreeFlying && e.Has<Support>() && e.Get<Support>() is { HasSupporter: true } support && support.Supporter.Has<EntityId>())
        {
            r.Support = support.Supporter.Get<EntityId>();
            r.Position = support.LocalPosition;
        }
        if (!r.FreeFlying && e.Has<CharacterControllerComponent>())
        {
            ref readonly var character = ref e.Get<CharacterControllerComponent>().Character;
            r.Velocity = character.LinearVelocity;
            r.Grounded = character.Supported;
        }
        return r;
    }

    /// <summary>Moves the player by however far the record for the host's answer was from it, and every later record
    /// with them.</summary>
    private void Correct(Entity e, in BodySnapshot answer)
    {
        // Up to the input that landed on a ship, the host had them where it did itself: the next input puts them where
        // they landed here.
        if (answer.Input <= _landedOn) return;
        ref var predicted = ref _history[answer.Input % HistoryLength];
        if (predicted.Input != answer.Input) return; // too old, or never recorded
        bool flying = (answer.Flags & SnapshotFlags.FreeFlying) != 0;
        // Walking or flying: each machine switches on the same input (V), so they differ only if that input was lost on
        // the way; then the host's word goes.
        if (predicted.FreeFlying != flying)
        {
            if (++_modeMismatches >= SupportMismatchAnswers)
            {
                _modeMismatches = 0;
                Players.SetFreeFlying(e, flying);
            }
            return;
        }
        _modeMismatches = 0;

        // How far off, in the space of what they stood on (or the world's), and that in world space now.
        Vector3 error;
        Vector3 worldError;
        if (predicted.Support == answer.Support)
        {
            _supportMismatches = 0;
            error = answer.Position - predicted.Position;
            // Standing on something, their height on it is the character controller's, here as there: moved up or down
            // they'd be pushed back onto it, while the later records kept the move, so the next answer would say they
            // were off the other way, and so on, a bounce that feeds itself.
            if (predicted.Grounded && !flying) error.Y = 0;
            worldError = Vector3.Transform(error, RotationOf(answer.Support));
        }
        else
        {
            // They stood on different things: one landed a tick before the other, which settles itself. Compared where
            // both are now, a moving ship would count its motion since the record as error, so only if it lasts.
            if (++_supportMismatches < SupportMismatchAnswers) return;
            error = default;
            worldError = ToWorld(answer.Support, answer.Position) - ToWorld(predicted.Support, predicted.Position);
        }
        // Velocity only in the air, where it carries on (a jump, a fall): on the ground the controller sets it each tick.
        var velocityError = flying || predicted.Grounded ? Vector3.Zero : answer.LinearVelocity - predicted.Velocity;
        bool move = worldError.Length() > Tolerance;
        bool push = velocityError.Length() > VelocityTolerance;
        if (!move && !push) return;

        if (move)
        {
            ref var t = ref e.Get<Transform>();
            var by = new Vector3D<float>(worldError.X, worldError.Y, worldError.Z);
            t.Position += by;
            if (e.Has<InterpolatedTransform>()) e.Get<InterpolatedTransform>().Smooth(by); // eased out, not a pop
            if (!flying && e.Has<CharacterControllerComponent>()) e.Get<CharacterControllerComponent>().Character.MoveBy(worldError);
            LastCorrection = worldError.Length();
            LargestCorrection = MathF.Max(LargestCorrection, LastCorrection);
            Corrections++;
        }
        if (push && e.Has<CharacterControllerComponent>())
        {
            ref var character = ref e.Get<CharacterControllerComponent>().Character;
            character.SetVelocity(character.LinearVelocity + velocityError);
        }

        // Every later record carries the same mistake.
        for (uint i = answer.Input + 1; i <= _input; i++) // (this tick's was recorded before the correction too)
        {
            ref var later = ref _history[i % HistoryLength];
            if (later.Input != i) continue;
            if (move && later.Support == predicted.Support && predicted.Support == answer.Support) later.Position += error;
            if (push && !later.Grounded) later.Velocity += velocityError;
        }
    }

    private Quaternion RotationOf(EntityId support)
    {
        if (support.IsNone || !_registry.TryGet(support, out var s) || !s.Has<Transform>()) return Quaternion.Identity;
        var r = s.Get<Transform>().Rotation;
        return new Quaternion(r.X, r.Y, r.Z, r.W);
    }

    private Vector3 ToWorld(EntityId support, Vector3 position)
    {
        if (support.IsNone || !_registry.TryGet(support, out var s) || !s.Has<Transform>()) return position;
        ref readonly var st = ref s.Get<Transform>();
        return new Vector3(st.Position.X, st.Position.Y, st.Position.Z) + Vector3.Transform(position, RotationOf(support));
    }
}
