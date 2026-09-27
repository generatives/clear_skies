using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Serialization;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.Commands;

/// <summary>
/// The one place every discrete change is applied, at a fixed point in each tick. Gameplay code calls
/// <see cref="Send{T}"/> (from any stage); each tick this drains, in order: events, commands and rejections received
/// from other machines, then the commands sent here since the last tick. A command whose authority is this machine is
/// validated, applied and broadcast as an event; one whose authority is elsewhere is applied now if its handler
/// predicts, and sent. With nobody connected every authority is local, so a command sent in one tick is applied in
/// that same tick, with no network.
/// </summary>
public sealed class CommandSystem : ISystem, IDebugUiSystem
{
    private readonly Dictionary<Type, CommandHandlerBase> _byType = new();
    private readonly Dictionary<ushort, CommandHandlerBase> _byId = new();
    private readonly List<(CommandHandlerBase Handler, int Slot)> _queue = new();
    private readonly List<Incoming> _inbox = new();
    private readonly List<Pending> _pending = new();
    private readonly Dictionary<uint, uint> _eventNumbers = new();      // as the authority: last number given per target
    private readonly Dictionary<(uint Authority, uint Target), uint> _lastApplied = new(); // as a receiver
    private readonly NetRegistry _registry;
    private readonly Func<uint> _tick;
    private uint _nextSeq = 1;

    private readonly List<IDescriber> _describers = new();
    private readonly EntitySet _describeRequests;
    private readonly List<Entity> _unclaimed = new();

    public CommandSystem(Session session, NetRegistry registry, Func<uint> tick, ICommandRouter? router = null, World? world = null)
    {
        Session = session;
        Descriptions = new DescriptionSink(this);
        _describeRequests = (world ?? registry.World).GetEntities().With<DescribeRequest>().AsSet();
        _registry = registry;
        _tick = tick;
        Router = router ?? new LocalCommandRouter();
    }

    public Session Session { get; }
    public ICommandRouter Router { get; set; }
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
        var handler = Require(handlerId);
        if (handler.EnqueueSerialized(payload, out int slot)) _queue.Add((handler, slot));
    }

    internal CommandHandler<T> HandlerOf<T>() where T : struct, ICommand =>
        _byType.TryGetValue(typeof(T), out var h) ? (CommandHandler<T>)h
            : throw new InvalidOperationException($"No handler is registered for {typeof(T).Name}.");

    /// <summary>Whether a handler for <typeparamref name="T"/> is registered.</summary>
    public bool Handles<T>() where T : struct, ICommand => _byType.ContainsKey(typeof(T));

    public CommandHandlerBase? HandlerFor(ushort id) => _byId.GetValueOrDefault(id);

    public IEnumerable<CommandHandlerBase> Handlers => _byId.Values;

    /// <summary>Sends a command. It's applied (or sent to its authority) when the command system next runs.</summary>
    public void Send<T>(in T command) where T : struct, ICommand
    {
        if (!_byType.TryGetValue(typeof(T), out var handler))
            throw new InvalidOperationException($"No handler is registered for {typeof(T).Name}.");
        var typed = (CommandHandler<T>)handler;
        if (typed.Enqueue(command, out int slot)) _queue.Add((handler, slot));
    }

    // ── from the network ────────────────────────────────────────────────────

    private readonly record struct Incoming(byte Kind, CommandHandlerBase? Handler, PeerId From, uint Seq, EventMeta Meta, byte[]? Payload);

    public void ReceiveCommand(PeerId from, ushort handlerId, uint seq, byte[] payload)
        => _inbox.Add(new Incoming(0, Require(handlerId), from, seq, default, payload));

    public void ReceiveEvent(in EventMeta meta, ushort handlerId, byte[] payload)
        => _inbox.Add(new Incoming(1, Require(handlerId), meta.Authority, 0, meta, payload));

    public void ReceiveRejection(PeerId authority, uint seq)
        => _inbox.Add(new Incoming(2, null, authority, seq, default, null));

    private CommandHandlerBase Require(ushort id) =>
        _byId.TryGetValue(id, out var h) ? h : throw new InvalidDataException($"Unknown command ID {id}.");

    // ── each tick ───────────────────────────────────────────────────────────

    public void Update(float dt)
    {
        for (int i = 0; i < _inbox.Count; i++)
        {
            var item = _inbox[i];
            switch (item.Kind)
            {
                case 0: item.Handler!.RunRemoteCommand(item.From, item.Seq, item.Payload); Stats.Received++; break;
                case 1: item.Handler!.RunEvent(item.Meta, item.Payload); break;
                case 2: Reject(item.From, item.Seq); break;
            }
        }
        _inbox.Clear();

        // Commands sent from here; AfterApply may send more, which run this same tick (up to a limit, so a handler that
        // keeps sending can't hang the game).
        for (int i = 0; i < _queue.Count; i++)
        {
            if (i >= 10_000) throw new InvalidOperationException("Commands keep sending commands.");
            _queue[i].Handler.RunQueued(_queue[i].Slot);
        }
        _queue.Clear();
        foreach (var h in _byId.Values) h.ClearQueue();

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
            if (!Descriptions.Claimed(e)) Console.WriteLine($"[describe] nothing describes entity {e} (net ID {(e.Has<NetId>() ? e.Get<NetId>().Value : 0)})");
            e.Remove<DescribeRequest>();
        }
        DescribedAll?.Invoke();
    }

    // ── bookkeeping used by the handlers ────────────────────────────────────

    internal uint NextSeq() => _nextSeq++;

    internal uint NextEventNumber(uint target)
    {
        uint n = _eventNumbers.GetValueOrDefault(target) + 1;
        _eventNumbers[target] = n;
        return n;
    }

    /// <summary>False for an event already applied (numbers only go up per authority and target).</summary>
    internal bool AcceptEventNumber(in EventMeta meta)
    {
        var key = (meta.Authority.Value, meta.Target);
        if (_lastApplied.TryGetValue(key, out var last) && meta.EventNumber <= last) return false;
        _lastApplied[key] = meta.EventNumber;
        return true;
    }

    internal PeerId OwnerOf(uint entity)
    {
        if (_registry.TryGet(entity, out var e) && e.Has<NetOwner>()) return e.Get<NetOwner>().Owner;
        return PeerId.Host;
    }

    internal bool HasAppliedListeners => Applied != null;

    internal void RaiseApplied(CommandHandlerBase handler, in EventMeta meta, object evt) => Applied?.Invoke(handler, meta, evt);

    private readonly record struct Pending(CommandHandlerBase Handler, uint Seq, PeerId Authority);

    internal void AddPending(CommandHandlerBase handler, uint seq, PeerId authority) => _pending.Add(new Pending(handler, seq, authority));

    internal void RemovePending(uint seq) => _pending.RemoveAll(p => p.Seq == seq);

    /// <summary>Predictions not yet confirmed or rejected.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>A command of ours was rejected: undo it and every later prediction to the same authority (newest
    /// first), then redo the later ones.</summary>
    private void Reject(PeerId authority, uint seq)
    {
        Stats.RejectedHere++;
        Reconcile(seq, apply: null);
    }

    /// <summary>Unwinds predictions back to <paramref name="seq"/>, runs <paramref name="apply"/> in its place (nothing
    /// for a rejection, the authority's version for an adjusted event), and redoes the later ones.</summary>
    internal void Reconcile(uint seq, Action? apply)
    {
        int index = _pending.FindIndex(p => p.Seq == seq);
        if (index < 0) { apply?.Invoke(); return; }
        var authority = _pending[index].Authority;
        var later = new List<Pending>();
        for (int i = index + 1; i < _pending.Count; i++)
            if (_pending[i].Authority == authority) later.Add(_pending[i]);

        for (int i = later.Count - 1; i >= 0; i--) later[i].Handler.RestorePrediction(later[i].Seq);
        var target = _pending[index];
        target.Handler.RestorePrediction(seq);
        target.Handler.ForgetPrediction(seq);
        _pending.RemoveAt(index);
        apply?.Invoke();
        foreach (var p in later) p.Handler.ReapplyPrediction(p.Seq);
    }

    // ── debug UI ────────────────────────────────────────────────────────────
    public string DebugName => "Commands";

    public void DrawDebugUi()
    {
        ImGui.Text($"Handlers: {_byId.Count}   Pending predictions: {_pending.Count}");
        ImGui.Text($"Applied: {Stats.Applied}   Sent: {Stats.Sent}   Received: {Stats.Received}");
        ImGui.Text($"Rejected here: {Stats.Rejected}   Ours rejected: {Stats.RejectedHere}");
        if (LastRejection is { } r) ImGui.TextDisabled($"Last rejected: {r}");
    }
}

public sealed class CommandStats
{
    public long Applied, Sent, Received, Rejected, RejectedHere;
}

/// <summary>Command IDs on the wire.</summary>
public static class CommandIds
{
    public const ushort EditVoxels = 1;
    public const ushort SetLever = 2;
    public const ushort SetWheel = 3;
    public const ushort SetGridLocked = 4;
    public const ushort RightGrid = 5;
    public const ushort SetMoveMode = 6;
    public const ushort SpawnGrid = 7;
    public const ushort SpawnPlayer = 8;
    public const ushort DespawnEntity = 9;
}
