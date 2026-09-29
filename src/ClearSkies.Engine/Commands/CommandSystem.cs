using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Serialization;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.Commands;

/// <summary>
/// The one place every discrete change is applied, at a fixed point in each tick. Gameplay code calls
/// <see cref="Send{T}"/> (from any stage); each tick this works through, in order: the network inbox (commands to
/// decide here, events to apply and rejections of our commands, received from other machines), then the outgoing
/// commands sent here since the last tick. A command whose authority is this machine is validated, applied and
/// broadcast as an event; one whose authority is elsewhere is applied now if its handler predicts, and sent. With
/// nobody connected every authority is local, so a command sent in one tick is applied in that same tick, with no
/// network.
///
/// Three lists, each in the order things happened:
/// <list type="bullet">
/// <item><b>Network inbox</b>: what other machines sent since the last tick, handled first.</item>
/// <item><b>Outgoing commands</b>: commands sent from this machine since the last tick, each carrying its typed
/// command; a coalescing handler's later command for the same target replaces its earlier one before it's sent.</item>
/// <item><b>Unconfirmed predictions</b>: commands this machine applied early and sent to another authority, until that
/// authority answers. Each carries its command and what applying it changed, so it can be undone and redone.</item>
/// </list>
///
/// An answer settles a prediction: its own event unchanged confirms it; an adjusted event or a rejection undoes it and
/// every later prediction to the same authority (newest first), applies the event in its place (or nothing), and redoes
/// the later ones. Any other event from that authority was decided before all of its predictions still waiting (their
/// answers would have come first otherwise), so it goes beneath them the same way: undo them, apply it, redo them.
/// </summary>
public sealed class CommandSystem : ISystem, IDebugUiSystem
{
    private readonly Dictionary<Type, CommandHandlerBase> _byType = new();
    private readonly Dictionary<ushort, CommandHandlerBase> _byId = new();
    private readonly List<InboxItem> _networkInbox = new();
    private readonly List<OutgoingCommand> _outgoingCommands = new();
    private int _outgoingSent; // how many of the outgoing commands have been sent this tick (only coalesce into later ones)
    private readonly List<Prediction> _unconfirmedPredictions = new();
    private readonly Dictionary<EntityId, uint> _lastEventNumberSent = new();                           // as the authority, per target
    private readonly Dictionary<(PeerId Authority, EntityId Target), uint> _lastEventNumberApplied = new(); // as a receiver
    private readonly EntityRegistry _registry;
    private readonly Func<uint> _tick;
    private uint _nextSeq = 1;

    private readonly List<IDescriber> _describers = new();
    private readonly EntitySet _describeRequests;
    private readonly List<Entity> _unclaimed = new();

    public CommandSystem(Session session, EntityRegistry registry, Func<uint> tick)
    {
        Session = session;
        Descriptions = new DescriptionSink(this);
        _describeRequests = registry.World.GetEntities().With<DescribeRequest>().AsSet();
        _registry = registry;
        _tick = tick;
    }

    public Session Session { get; }

    /// <summary>Where commands, events and rejections go: this machine alone until a network session takes over.</summary>
    public ICommandRouter Router { get; set; } = new LocalCommandRouter();
    public uint Tick => _tick();
    internal NetWriter Scratch { get; } = new();
    public CommandStats Stats { get; } = new();
    public string? LastRejection { get; internal set; }

    /// <summary>Raised after every event is applied (by its authority or on receipt, not predictions), with the
    /// handler, the event's metadata and the event itself (boxed).</summary>
    public event Action<CommandHandlerBase, EventMeta, object>? Applied;

    /// <summary>Registers a handler; its <see cref="CommandHandlerBase.Id"/> and command type must be unique.</summary>
    public T Register<T>(T handler) where T : CommandHandlerBase
    {
        if (_byId.ContainsKey(handler.Id)) throw new InvalidOperationException($"Command ID {handler.Id} is registered twice.");
        _byId[handler.Id] = handler;
        _byType[handler.CommandType] = handler;
        handler.Owner = this;
        if (handler is IDescriber describer)
        {
            _describers.Add(describer);
            _describers.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        return handler;
    }

    /// <summary>Descriptions of entities with a <see cref="DescribeRequest"/>, made at the end of each tick.</summary>
    public DescriptionSink Descriptions { get; }

    /// <summary>Raised after each round of describing, once every description is out.</summary>
    public event Action? DescribedAll;

    /// <summary>Sends a command given in its wire form (a stored spawn command, for example), as if sent here.</summary>
    public void SendSerialized(ushort handlerId, ReadOnlySpan<byte> payload)
    {
        Require(handlerId).SendSerialized(payload);
    }

    internal CommandHandler<T> HandlerOf<T>() where T : struct, ICommand =>
        _byType.TryGetValue(typeof(T), out var h) ? (CommandHandler<T>)h
            : throw new InvalidOperationException($"No handler is registered for {typeof(T).Name}.");

    public CommandHandlerBase? HandlerFor(ushort id) => _byId.GetValueOrDefault(id);

    public IEnumerable<CommandHandlerBase> Handlers => _byId.Values;

    /// <summary>Sends a command. It's applied (or sent to its authority) when the command system next runs.</summary>
    public void Send<T>(in T command) where T : struct, ICommand
    {
        if (!_byType.TryGetValue(typeof(T), out var handler))
            throw new InvalidOperationException($"No handler is registered for {typeof(T).Name}.");
        var typed = (CommandHandler<T>)handler;
        for (int i = _outgoingSent; i < _outgoingCommands.Count; i++)
            if (_outgoingCommands[i] is OutgoingCommand<T> earlier && typed.Coalesces(earlier.Command, command))
            {
                earlier.Command = command;
                return;
            }
        _outgoingCommands.Add(new OutgoingCommand<T>(typed, command));
    }

    // ── from the network ────────────────────────────────────────────────────

    private enum InboxKind : byte { Command, Event, Rejection }

    private readonly record struct InboxItem(InboxKind Kind, CommandHandlerBase? Handler, PeerId From, uint Seq, EventMeta Meta, byte[]? Payload);

    /// <summary>A command from another machine, for this one to decide.</summary>
    public void ReceiveCommand(PeerId from, ushort handlerId, uint seq, byte[] payload)
        => _networkInbox.Add(new InboxItem(InboxKind.Command, Require(handlerId), from, seq, default, payload));

    /// <summary>An event decided by another machine, to apply here.</summary>
    public void ReceiveEvent(in EventMeta meta, ushort handlerId, byte[] payload)
        => _networkInbox.Add(new InboxItem(InboxKind.Event, Require(handlerId), meta.Authority, 0, meta, payload));

    /// <summary>A command of ours that its authority rejected.</summary>
    public void ReceiveRejection(PeerId authority, uint seq)
        => _networkInbox.Add(new InboxItem(InboxKind.Rejection, null, authority, seq, default, null));

    private CommandHandlerBase Require(ushort id) =>
        _byId.TryGetValue(id, out var h) ? h : throw new InvalidDataException($"Unknown command ID {id}.");

    // ── each tick ───────────────────────────────────────────────────────────

    public void Update(float dt)
    {
        for (int i = 0; i < _networkInbox.Count; i++)
        {
            var item = _networkInbox[i];
            switch (item.Kind)
            {
                case InboxKind.Command: item.Handler!.RunRemoteCommand(item.From, item.Seq, item.Payload); Stats.Received++; break;
                case InboxKind.Event: item.Handler!.RunEvent(item.Meta, item.Payload); break;
                case InboxKind.Rejection: OnRejected(item.Seq); break;
            }
        }
        _networkInbox.Clear();

        // Commands sent from here; AfterApply may send more, which run this same tick (up to a limit, so a handler that
        // keeps sending can't hang the game).
        for (int i = 0; i < _outgoingCommands.Count; i++)
        {
            if (i >= 10_000) throw new InvalidOperationException("Commands keep sending commands.");
            _outgoingSent = i + 1;
            _outgoingCommands[i].Run();
        }
        _outgoingCommands.Clear();
        _outgoingSent = 0;

        DescribeRequested();
    }

    /// <summary>Calls every describer in order, then removes every <see cref="DescribeRequest"/>.</summary>
    public void DescribeRequested()
    {
        if (_describeRequests.Count == 0) { DescribedAll?.Invoke(); return; }
        Descriptions.Reset();
        foreach (var d in _describers) d.Describe(Descriptions);
        _unclaimed.Clear();
        foreach (ref readonly var e in _describeRequests.GetEntities()) _unclaimed.Add(e);
        foreach (var e in _unclaimed)
        {
            if (!Descriptions.Claimed(e)) Console.WriteLine($"[describe] nothing describes entity {e} ({(e.Has<EntityId>() ? e.Get<EntityId>() : EntityId.None)})");
            e.Remove<DescribeRequest>();
        }
        DescribedAll?.Invoke();
    }

    // ── bookkeeping used by the handlers ────────────────────────────────────

    internal uint NextSeq() => _nextSeq++;

    /// <summary>Metadata for an event this machine sends as its own authority without a command (a description sent
    /// to a joining player), numbered after every event already sent for <paramref name="target"/>.</summary>
    public EventMeta StampEvent(EntityId target) =>
        new(Session.LocalPeer, 0, Session.LocalPeer, target, NextEventNumber(target), Tick);

    internal uint NextEventNumber(EntityId target)
    {
        uint n = _lastEventNumberSent.GetValueOrDefault(target) + 1;
        _lastEventNumberSent[target] = n;
        return n;
    }

    /// <summary>The number of the last event applied here from <paramref name="authority"/> for
    /// <paramref name="target"/> (or, on that authority itself, the last it sent).</summary>
    public uint LastEventNumber(PeerId authority, EntityId target) =>
        authority == Session.LocalPeer ? _lastEventNumberSent.GetValueOrDefault(target) : _lastEventNumberApplied.GetValueOrDefault((authority, target));

    /// <summary>False for an event already applied (numbers only go up per authority and target).</summary>
    internal bool AcceptEventNumber(in EventMeta meta)
    {
        var key = (meta.Authority, meta.Target);
        if (_lastEventNumberApplied.TryGetValue(key, out var last) && meta.EventNumber <= last) return false;
        _lastEventNumberApplied[key] = meta.EventNumber;
        return true;
    }

    internal PeerId OwnerOf(EntityId entity)
    {
        if (_registry.TryGet(entity, out var e) && e.Has<NetOwner>()) return e.Get<NetOwner>().Owner;
        return PeerId.Host;
    }

    internal bool HasAppliedListeners => Applied != null;

    internal void RaiseApplied(CommandHandlerBase handler, in EventMeta meta, object evt) => Applied?.Invoke(handler, meta, evt);

    // ── unconfirmed predictions ─────────────────────────────────────────────

    /// <summary>Commands this machine applied early and is waiting on its authority to answer.</summary>
    public int UnconfirmedPredictionCount => _unconfirmedPredictions.Count;

    internal void AddPrediction(Prediction p) => _unconfirmedPredictions.Add(p);

    internal Prediction? FindPrediction(uint seq)
    {
        foreach (var p in _unconfirmedPredictions) if (p.Seq == seq) return p;
        return null;
    }

    /// <summary>Its event came back as predicted: nothing to change.</summary>
    internal void ConfirmPrediction(Prediction p) => _unconfirmedPredictions.Remove(p);

    /// <summary>The authority decided otherwise: <paramref name="apply"/> (its event) takes the prediction's place.</summary>
    internal void ReplacePrediction(Prediction p, Action apply)
    {
        int index = _unconfirmedPredictions.IndexOf(p);
        if (index >= 0) Rewind(p.Authority, index, p, apply);
        else apply();
    }

    /// <summary>A command of ours was rejected: its prediction (if it had one) is undone.</summary>
    private void OnRejected(uint seq)
    {
        Stats.RejectedHere++;
        if (FindPrediction(seq) is { } p) Rewind(p.Authority, _unconfirmedPredictions.IndexOf(p), p, apply: null);
    }

    /// <summary>An event from <paramref name="authority"/> that isn't one of our predictions coming back: applied beneath
    /// our predictions still waiting on that authority, which decided it before them.</summary>
    internal void ApplyBeneathPredictions(PeerId authority, Action apply)
    {
        foreach (var p in _unconfirmedPredictions)
            if (p.Authority == authority) { Rewind(authority, 0, drop: null, apply); return; }
        apply();
    }

    /// <summary>Undoes the predictions to <paramref name="authority"/> from <paramref name="from"/> on (newest first),
    /// forgets <paramref name="drop"/>, runs <paramref name="apply"/>, and redoes the rest (oldest first).</summary>
    private void Rewind(PeerId authority, int from, Prediction? drop, Action? apply)
    {
        var affected = new List<Prediction>();
        for (int i = from; i < _unconfirmedPredictions.Count; i++)
            if (_unconfirmedPredictions[i].Authority == authority) affected.Add(_unconfirmedPredictions[i]);
        for (int i = affected.Count - 1; i >= 0; i--) affected[i].Undo();
        if (drop != null) _unconfirmedPredictions.Remove(drop);
        apply?.Invoke();
        foreach (var p in affected) if (p != drop) p.Redo();
    }

    // ── debug UI ────────────────────────────────────────────────────────────
    public string DebugName => "Commands";

    public void DrawDebugUi()
    {
        ImGui.Text($"Handlers: {_byId.Count}   Unconfirmed predictions: {_unconfirmedPredictions.Count}");
        ImGui.Text($"Applied: {Stats.Applied}   Sent: {Stats.Sent}   Received: {Stats.Received}");
        ImGui.Text($"Rejected here: {Stats.Rejected}   Ours rejected: {Stats.RejectedHere}");
        if (LastRejection is { } r) ImGui.TextDisabled($"Last rejected: {r}");
    }
}

/// <summary>A command sent from this machine this tick, waiting for the command system to run it.</summary>
internal abstract class OutgoingCommand
{
    public abstract void Run();
}

internal sealed class OutgoingCommand<T> : OutgoingCommand where T : struct, ICommand
{
    private readonly CommandHandler<T> _handler;
    public T Command;

    public OutgoingCommand(CommandHandler<T> handler, in T command)
    {
        _handler = handler;
        Command = command;
    }

    public override void Run() => _handler.RunLocal(Command);
}

/// <summary>A command this machine applied before its authority answered (see
/// <see cref="PredictedCommandHandler{T, TUndo}"/>): undone if the authority rejects or adjusts it, and undone and redone
/// around anything that has to go beneath it.</summary>
internal abstract class Prediction
{
    protected Prediction(uint seq, PeerId authority)
    {
        Seq = seq;
        Authority = authority;
    }

    /// <summary>This machine's number for the command, which its answer carries back.</summary>
    public uint Seq { get; }

    /// <summary>Who decides it.</summary>
    public PeerId Authority { get; }

    /// <summary>Puts back what applying it changed.</summary>
    public abstract void Undo();

    /// <summary>Applies it (again), first noting what that changes.</summary>
    public abstract void Redo();
}

public sealed class CommandStats
{
    public long Applied, Sent, Received, Rejected, RejectedHere;
}
