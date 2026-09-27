using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;

namespace ClearSkies.Engine.Commands;

/// <summary>The part of every handler the <see cref="CommandSystem"/> drives without knowing its command type.</summary>
public abstract class CommandHandlerBase
{
    /// <summary>Unique across handlers; identifies the command on the wire (see <see cref="CommandIds"/>).</summary>
    public abstract ushort Id { get; }

    public virtual string Name => GetType().Name.Replace("Handler", "");

    internal CommandSystem Owner { get; set; } = null!;

    internal abstract Type CommandType { get; }
    internal abstract void RunQueued(int slot);
    internal abstract void ClearQueue();
    internal abstract bool EnqueueSerialized(ReadOnlySpan<byte> payload, out int slot);
    internal abstract void RunRemoteCommand(PeerId from, uint seq, ReadOnlySpan<byte> payload);
    internal abstract void RunEvent(in EventMeta meta, ReadOnlySpan<byte> payload);

    // Prediction (only PredictedCommandHandler does anything).
    internal virtual void RestorePrediction(uint seq) { }
    internal virtual void ReapplyPrediction(uint seq) { }
    internal virtual void ForgetPrediction(uint seq) { }
}

/// <summary>
/// Handles one kind of command: where the gameplay rules for it live. The authority (by default the owner of the
/// command's target) runs <see cref="Validate"/> to accept, adjust or reject it, then <see cref="Apply"/> and
/// <see cref="AfterApply"/>, and the accepted command goes to every other machine as an event, which they apply with
/// the same <see cref="Apply"/>. Handlers are registered once at startup, get their dependencies through their
/// constructors, and work the same with nobody connected.
///
/// Two rules keep every machine in step: set values, don't add to them (a duplicated or reordered event still gives
/// the right result), and <see cref="Apply"/> reads only the event and replicated gameplay state, never physics poses
/// or anything local. Physics effects that only matter on the owner go in <see cref="AfterApply"/>.
/// </summary>
public abstract class CommandHandler<T> : CommandHandlerBase where T : struct, ICommand
{
    private readonly List<T> _queued = new();

    /// <summary>Several per tick for one target: only the latest is sent (lever and wheel drags).</summary>
    public virtual bool Coalesce => false;

    public abstract void Write(NetWriter writer, in T command);
    public abstract T Read(ref NetReader reader);

    /// <summary>Which peer decides this command. Default: the owner of its target.</summary>
    public virtual PeerId Authority(in T command, in AuthorityContext ctx) => ctx.OwnerOf(command.Target);

    /// <summary>Authority only. Accept, adjust the command in place, or reject it.</summary>
    public virtual Verdict Validate(ref T command, in CommandContext ctx) => Verdict.Accept;

    /// <summary>Every machine, identically: the event's effect on gameplay state.</summary>
    public abstract void Apply(in T evt, in ApplyContext ctx);

    /// <summary>Authority only, after <see cref="Apply"/>: owner-side effects and follow-on commands.</summary>
    public virtual void AfterApply(in T evt, in CommandContext ctx) { }

    internal sealed override Type CommandType => typeof(T);

    /// <summary>Queues a command sent this tick; coalescing replaces an earlier one for the same target.</summary>
    internal bool Enqueue(in T command, out int slot)
    {
        if (Coalesce)
            for (int i = 0; i < _queued.Count; i++)
                if (_queued[i].Target == command.Target)
                {
                    _queued[i] = command;
                    slot = i;
                    return false;
                }
        _queued.Add(command);
        slot = _queued.Count - 1;
        return true;
    }

    internal sealed override void ClearQueue() => _queued.Clear();

    internal sealed override bool EnqueueSerialized(ReadOnlySpan<byte> payload, out int slot)
    {
        var reader = new NetReader(payload);
        return Enqueue(Read(ref reader), out slot);
    }

    internal sealed override void RunQueued(int slot) => RunLocal(_queued[slot]);

    /// <summary>A command from this machine: decided here, or predicted (if it can be) and sent to its authority.</summary>
    private void RunLocal(T command)
    {
        var sys = Owner;
        var local = sys.Session.LocalPeer;
        uint seq = sys.NextSeq();
        var authority = Authority(command, new AuthorityContext(sys, local));
        if (authority == local)
        {
            RunAsAuthority(command, local, seq);
            return;
        }
        var payload = Serialize(command);
        TryPredict(command, seq, authority, payload);
        sys.Router.SendCommand(authority, Id, seq, payload);
        sys.Stats.Sent++;
    }

    internal sealed override void RunRemoteCommand(PeerId from, uint seq, ReadOnlySpan<byte> payload)
    {
        var reader = new NetReader(payload);
        var command = Read(ref reader);
        var sys = Owner;
        var authority = Authority(command, new AuthorityContext(sys, from));
        if (authority != sys.Session.LocalPeer)
        {
            // Sent here before its target changed hands: the new authority decides it.
            sys.Router.ForwardCommand(authority, from, Id, seq, payload);
            sys.Stats.Forwarded++;
            return;
        }
        // One of ours, forwarded back here because its target became ours meanwhile: undo the prediction and decide it.
        if (from == sys.Session.LocalPeer) sys.Reconcile(seq, apply: null);
        RunAsAuthority(command, from, seq);
    }

    private void RunAsAuthority(T command, PeerId sender, uint seq)
    {
        var sys = Owner;
        var ctx = new CommandContext(sender, sys.Tick);
        if (Validate(ref command, ctx) == Verdict.Reject)
        {
            sys.Stats.Rejected++;
            sys.LastRejection = $"{Name} on {command.Target} from {sender}";
            if (sender != sys.Session.LocalPeer) sys.Router.SendRejection(sender, seq);
            return;
        }
        var meta = new EventMeta(sender, seq, sys.Session.LocalPeer, command.Target.Entity, sys.NextEventNumber(command.Target.Entity), sys.Tick);
        ApplyEvent(command, new ApplyContext(sender, IsAuthority: true, IsPrediction: false, sys.Tick), meta);
        AfterApply(command, ctx);
        sys.Router.BroadcastEvent(Id, meta, Serialize(command));
    }

    internal sealed override void RunEvent(in EventMeta meta, ReadOnlySpan<byte> payload)
    {
        var sys = Owner;
        if (!sys.AcceptEventNumber(meta)) return; // a duplicate
        var reader = new NetReader(payload);
        var evt = Read(ref reader);
        if (meta.Origin == sys.Session.LocalPeer && TryConfirm(meta, payload, evt))
            return; // our own prediction coming back
        ApplyEvent(evt, new ApplyContext(meta.Origin, IsAuthority: false, IsPrediction: false, meta.Tick), meta);
    }

    /// <summary>Applies an event (not a prediction) and tells anyone listening.</summary>
    internal void ApplyEvent(in T evt, in ApplyContext ctx, in EventMeta meta)
    {
        Apply(evt, ctx);
        Owner.Stats.Applied++;
        if (Owner.HasAppliedListeners) Owner.RaiseApplied(this, meta, evt);
    }

    /// <summary>Predicted handlers apply a command sent to a remote authority straight away.</summary>
    internal virtual void TryPredict(in T command, uint seq, PeerId authority, byte[] payload) { }

    /// <summary>Predicted handlers settle a prediction when its event comes back; true if it was one.</summary>
    internal virtual bool TryConfirm(in EventMeta meta, ReadOnlySpan<byte> payload, in T evt) => false;

    /// <summary>The command written out, as it goes on the wire.</summary>
    public byte[] Serialize(in T command)
    {
        var w = Owner.Scratch;
        w.Clear();
        Write(w, command);
        return w.ToArray();
    }
}

/// <summary>
/// A command the sender applies straight away, before its authority confirms it. <see cref="Capture"/> records what
/// <see cref="CommandHandler{T}.Apply"/> is about to change so a rejection can put it back with <see cref="Restore"/>.
/// When the command's own event comes back as predicted nothing changes; if the authority adjusted it, the prediction
/// is undone and the event applied instead.
/// </summary>
public abstract class PredictedCommandHandler<T, TUndo> : CommandHandler<T> where T : struct, ICommand
{
    private readonly Dictionary<uint, (T Command, TUndo Undo, byte[] Payload)> _predictions = new();

    /// <summary>Captures what Apply is about to change, so a rejection can put it back.</summary>
    public abstract TUndo Capture(in T command);

    public abstract void Restore(in TUndo undo);

    internal sealed override void TryPredict(in T command, uint seq, PeerId authority, byte[] payload)
    {
        _predictions[seq] = (command, Capture(command), payload);
        Apply(command, new ApplyContext(Owner.Session.LocalPeer, IsAuthority: false, IsPrediction: true, Owner.Tick));
        Owner.AddPending(this, seq, authority);
    }

    internal sealed override bool TryConfirm(in EventMeta meta, ReadOnlySpan<byte> payload, in T evt)
    {
        if (!_predictions.TryGetValue(meta.OriginSeq, out var p)) return false; // not ours any more: apply it
        if (payload.SequenceEqual(p.Payload))
        {
            Owner.RemovePending(meta.OriginSeq);
            _predictions.Remove(meta.OriginSeq);
            return true;
        }
        // Adjusted by the authority: undo the prediction (and any later ones to the same authority), apply the event
        // as decided, then redo the later ones on top.
        var e = evt;
        var m = meta;
        Owner.Reconcile(meta.OriginSeq, () => ApplyEvent(e, new ApplyContext(m.Origin, false, false, m.Tick), m));
        return true;
    }

    internal sealed override void RestorePrediction(uint seq)
    {
        if (_predictions.TryGetValue(seq, out var p)) Restore(p.Undo);
    }

    internal sealed override void ReapplyPrediction(uint seq)
    {
        if (!_predictions.TryGetValue(seq, out var p)) return;
        _predictions[seq] = (p.Command, Capture(p.Command), p.Payload);
        Apply(p.Command, new ApplyContext(Owner.Session.LocalPeer, IsAuthority: false, IsPrediction: true, Owner.Tick));
    }

    internal sealed override void ForgetPrediction(uint seq) => _predictions.Remove(seq);

    /// <summary>Predictions waiting for their event (for tests and the debug panel).</summary>
    public int PendingCount => _predictions.Count;
}
