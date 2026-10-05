using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Transport;
using DefaultEcs;
using EngineSession = ClearSkies.Engine.Entities.Session;

namespace ClearSkies.Net.Session;

/// <summary>
/// A Participant: an ECS world kept in step with the game, on every machine that plays, the hosting one's included.
/// One connection, to the Host, which everything it sends goes through. The Host spawns into it what's in its View
/// Volume (which it keeps the Host told of, around its player) and has it forget what leaves; each spawn waits here,
/// with its events, until what it needs has loaded (<see cref="SpawnQueue"/>).
/// <para>The hosting machine's is the authority (<see cref="PeerId.Host"/>): it simulates every entity, players
/// included (moving each by their machine's input, <see cref="RemoteInputs"/>), decides every command, and describes
/// entities for the Host: ones it makes, ones the Host asks for, ones the Host releases (then despawns them), and
/// everything when the Host saves. It shares the Host's clock. Any other simulates only its own player, predicting it
/// (<see cref="OwnPlayerPrediction"/>), and keeps its clock on the Host's (<see cref="ClockSync"/>). Its player spawns
/// only once that has settled (<see cref="ClockSync.Settling"/>, for at most <see cref="MaxSettleMs"/>), so prediction
/// starts on the Host's timeline.</para>
/// </summary>
public sealed class Participant : NetSession
{
    /// <summary>How often the View Volume is sent, in ticks.</summary>
    public const int ViewTicks = 30;

    private static readonly ConnectionId HostConnection = new(0);

    private readonly SpawnQueue _spawns;
    private readonly RemoteInputs? _inputs;
    private readonly EntitySet _localPlayers;
    private readonly EntitySet _describable;
    private readonly EntitySet _created;
    private readonly HashSet<EntityId> _fromHost = new();
    private readonly List<EntityId> _deleted = new();
    private bool _idRequested;
    private int _viewTicks;
    private bool _viewSent;
    private double _settleFrom = double.NaN;
    private bool _clockReady;

    /// <summary>The longest a join waits for the clock to settle, in milliseconds: a jittery connection may never
    /// quite, and it carries on slewing once joined.</summary>
    public const double MaxSettleMs = 10_000;

    /// <param name="welcome">The Host's welcome (see <see cref="Connect"/>).</param>
    /// <param name="terrainReady">Whether the terrain around a point has loaded here, with colliders.</param>
    public Participant(ITransport transport, Welcome welcome, EngineSession session, CommandSystem commands, EntityRegistry registry,
                       World world, ITickClock clock, Func<Vector3, bool> terrainReady)
        : base(transport, session, commands, registry, world, clock)
    {
        Welcome = welcome;
        session.Join(welcome.Peer);
        registry.AddIdBlock(welcome.IdFirst, welcome.IdCount);
        if (!IsAuthority)
        {
            ClockSync = new ClockSync(clock) { Settling = true };
            ClockSync.SnapTo(welcome.HostTick);
        }
        else
        {
            _inputs = new RemoteInputs(world, registry);
            _clockReady = true;
        }
        _spawns = new SpawnQueue(world, registry, commands, session, terrainReady) { LocalReady = () => _clockReady };
        _spawns.Spawning += id => _fromHost.Add(id);
        _localPlayers = world.GetEntities().With<LocalPlayer>().With<Transform>().AsSet();
        _describable = world.GetEntities().With<EntityId>().With<OwnPresence>().Without<Chunk>().AsSet();
        _created = world.GetEntities().With<EntityId>().With<OwnPresence>().Without<Chunk>().WhenAdded<OwnPresence>().AsSet();
        commands.Applied += OnApplied;
    }

    public Welcome Welcome { get; }

    /// <summary>The hosting machine's: authority over every entity.</summary>
    public bool IsAuthority => Session.LocalPeer == PeerId.Host;

    /// <summary>Keeps this machine's clock on the Host's (none on the authority, which shares it).</summary>
    public ClockSync? ClockSync { get; }

    /// <summary>Other machines' input, applied to the players they play (the authority only).</summary>
    public RemoteInputs? Inputs => _inputs;

    /// <summary>Spawns waiting for what they need.</summary>
    public SpawnQueue Spawns => _spawns;

    /// <summary>Whether its own player has spawned.</summary>
    public bool Joined { get; private set; }

    /// <summary>What joining is waiting on, for a loading screen: the clock, the world around the spawn, then the Host.</summary>
    public string JoinStatus { get; private set; } = "Syncing with the host";

    public override bool OthersConnected => !IsAuthority || OthersHere();

    /// <summary>On the authority: whether anyone else has joined (set by whoever runs the Host).</summary>
    public Func<bool> OthersHere { get; set; } = () => false;

    /// <summary>Whether it has a View Volume: false for a host with nobody playing on it.</summary>
    public bool Viewing { get; set; } = true;

    /// <summary>
    /// Connects and says hello, waiting (pumping the transport, and <paramref name="pump"/> meanwhile: the Host, when it's
    /// on this machine) for the Host's welcome. Throws with the Host's reason if it refuses, or on a timeout.
    /// </summary>
    public static Welcome Connect(ITransport transport, Hello hello, TimeSpan timeout, Action? pump = null)
    {
        using var join = new JoinRequest(transport, hello);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (join.TryComplete(out var welcome)) return welcome;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The host didn't answer.");
            if (pump is null) Thread.Sleep(5);
            else pump();
        }
    }

    /// <summary>First in the tick: everything that arrived, then players' next inputs (on the authority), spawns that
    /// are ready, and what's owed to the Host.</summary>
    public override void Update(float dt)
    {
        base.Update(dt);
        foreach (var id in _deleted) Send(HostConnection, new EntityMessage(MessageKind.Deleted, id));
        _deleted.Clear();
        AnnounceCreated();
        _inputs?.Update();
        _spawns.Update();
        if (!Joined && _localPlayers.Count > 0) Console.WriteLine($"[net] in the game, as {Session.LocalPeer}");
        Joined = _localPlayers.Count > 0;
        if (ClockSync is { } sync)
        {
            sync.Update();
            if (sync.ShouldPing(NowMs)) Send(HostConnection, new TimePing(NowMs), Channel.Unreliable);
            if (!_clockReady) Settle(sync);
        }
        if (!Joined) JoinStatus = !_clockReady ? "Syncing with the host" : _spawns.All.Any(p => p.Local) ? "Loading the world" : "Waiting for the host";
        if (!_viewSent || (Viewing && ++_viewTicks >= ViewTicks)) SendView();
        if (Registry.IdsLeft < EntityRegistry.BlockSize / 4 && !_idRequested)
        {
            Send(HostConnection, new IdBlockRequest());
            _idRequested = true;
        }
    }

    /// <summary>Lets its player spawn once the clock has settled, or after <see cref="MaxSettleMs"/> regardless.</summary>
    private void Settle(ClockSync sync)
    {
        if (double.IsNaN(_settleFrom)) _settleFrom = NowMs;
        if (!sync.Settled && NowMs - _settleFrom <= MaxSettleMs) return;
        _clockReady = true;
        double waited = (NowMs - _settleFrom) / 1000;
        Console.WriteLine(sync.Settled
            ? $"[net] clock settled after {waited:0.0} s: {sync.Offset:+0.00;-0.00} ticks off, round trip {sync.RoundTripMs:0} ms, {sync.Snaps} snaps, {sync.SkippedTicks} dropped ticks put back"
            : $"[net] clock didn't settle in {waited:0.0} s: {sync.Offset:+0.00;-0.00} ticks off, round trip {sync.RoundTripMs:0} ms, {sync.Snaps} snaps, {sync.SkippedTicks} dropped ticks put back; joining anyway");
        sync.EndSettling(NowMs); // from here on, its player is predicted from it: slew
    }

    /// <summary>The View Volume: around its player, or where its player will spawn until then (none, but sent once, as
    /// the Host's sign this is up, if not <see cref="Viewing"/>).</summary>
    private void SendView()
    {
        _viewTicks = 0;
        _viewSent = true;
        var centre = Welcome.Spawn;
        foreach (ref readonly var e in _localPlayers.GetEntities())
        {
            var t = e.Get<Transform>().Position;
            centre = new Vector3(t.X, t.Y, t.Z);
        }
        Send(HostConnection, new ViewVolumeMessage(centre, Viewing ? Host.ViewRadius : 0), Channel.Reliable);
    }

    protected override void OnConnected(ConnectionId connection) { }

    protected override void OnDisconnected(ConnectionId connection, string reason)
    {
        Console.WriteLine($"[net] disconnected from the host: {reason}");
        End(reason);
    }

    protected override void OnMessage(ConnectionId from, MessageKind kind, ref NetReader r, ReadOnlySpan<byte> packet, Channel channel)
    {
        switch (kind)
        {
            case MessageKind.Spawn:
                _spawns.Add(SpawnMessage.Read(ref r));
                break;
            case MessageKind.Forget:
                Forget(EntityMessage.Read(kind, ref r).Id);
                break;
            case MessageKind.Event:
            {
                var m = EventMessage.Read(ref r);
                if (!_spawns.Hold(m.Meta, m.Handler, m.Payload)) Commands.ReceiveEvent(m.Meta, m.Handler, m.Payload.ToArray());
                break;
            }
            case MessageKind.Command:
            {
                var m = CommandMessage.Read(ref r);
                if (m.To == Session.LocalPeer) Commands.ReceiveCommand(m.From, m.Handler, m.Seq, m.Payload.ToArray());
                break;
            }
            case MessageKind.Rejection:
            {
                var rej = Rejection.Read(ref r);
                Commands.ReceiveRejection(rej.Authority, rej.Seq);
                break;
            }
            case MessageKind.StateFrame:
                Bodies?.ReceiveFrame(ref r);
                break;
            case MessageKind.PlayerInput when _inputs is not null:
            {
                var m = PlayerInputMessage.Read(ref r);
                if (Registry.TryGet(m.Player, out var player)) _inputs.Receive(player, m);
                break;
            }
            case MessageKind.Release when IsAuthority:
                Release(EntityMessage.Read(kind, ref r).Id);
                break;
            case MessageKind.DescribeRequest when IsAuthority:
                Describe(EntityMessage.Read(kind, ref r).Id, DescribedReason.Requested);
                break;
            case MessageKind.SaveRequest when IsAuthority:
                foreach (var e in Commands.Describe(_describable.GetEntities().ToArray())) SendDescribed(e, DescribedReason.Save);
                foreach (var p in _spawns.All) SendDescribed(p, DescribedReason.Save);
                Send(HostConnection, new SignalMessage(MessageKind.SaveDone));
                break;
            case MessageKind.TimePong:
                ClockSync?.OnPong(TimePong.Read(ref r), NowMs);
                break;
            case MessageKind.IdBlock:
            {
                var block = IdBlockMessage.Read(ref r);
                Registry.AddIdBlock(block.First, block.Count);
                _idRequested = false;
                break;
            }
            case MessageKind.PlayerJoined:
            case MessageKind.PlayerLeft:
            {
                var notice = PlayerNotice.Read(kind == MessageKind.PlayerJoined, ref r);
                Console.WriteLine($"[net] {notice.Name} {(notice.Joined ? "joined" : "left")}");
                break;
            }
            case MessageKind.Disconnect:
                End(DisconnectMessage.Read(ref r).Reason);
                break;
        }
    }

    // ── entities coming and going ───────────────────────────────────────────

    /// <summary>Out of view: the copy goes (it's still in the game; it comes back as a spawn if it comes back into view).</summary>
    private void Forget(EntityId id)
    {
        if (_spawns.Cancel(id) is null && Registry.TryGet(id, out var e)) Hierarchy.DestroyRecursive(e);
        Commands.ForgetEvents(id);
    }

    /// <summary>The Host has released it: its last Description, then it's despawned.</summary>
    private void Release(EntityId id)
    {
        if (_spawns.Cancel(id) is { } pending)
        {
            SendDescribed(pending, DescribedReason.Released);
            return;
        }
        if (!Registry.TryGet(id, out var e)) return;
        SendDescribed(Commands.Describe(e), DescribedReason.Released);
        Commands.Send(new DespawnEntity { Entity = id, KeepStored = true });
    }

    private void Describe(EntityId id, DescribedReason reason)
    {
        if (_spawns.Find(id) is { } pending) SendDescribed(pending, reason);
        else if (Registry.TryGet(id, out var e)) SendDescribed(Commands.Describe(e), reason);
    }

    /// <summary>On the authority: what it made since the last tick, which the Host hasn't heard of.</summary>
    private void AnnounceCreated()
    {
        foreach (ref readonly var e in _created.GetEntities())
        {
            var id = e.Get<EntityId>();
            if (_fromHost.Remove(id) || !IsAuthority) continue;
            SendDescribed(Commands.Describe(e), DescribedReason.Created);
        }
        _created.Complete();
    }

    private void OnApplied(CommandHandlerBase handler, EventMeta meta, object evt)
    {
        if (evt is not DespawnEntity despawn) return;
        Commands.ForgetEvents(despawn.Entity);
        // Gone for good (deleted, broken up), not released: out of the save too. Told next tick, after its event.
        if (IsAuthority && !despawn.KeepStored) _deleted.Add(despawn.Entity);
    }

    private void SendDescribed(EntityDescription d, DescribedReason reason)
    {
        Vector3? position = d.Entity.Has<Transform>() ? Where(d.Entity) : null;
        var m = new DescribedMessage(d.Id, d.Kind, reason, Commands.LastEventNumber(Session.LocalPeer, d.Id), position, d.Data);
        SendDescribed(m);
    }

    private void SendDescribed(SpawnQueue.Pending p, DescribedReason reason) =>
        SendDescribed(new DescribedMessage(p.Id, p.Kind, reason, p.EventNumber, p.Position, p.Data));

    private void SendDescribed(in DescribedMessage m)
    {
        var w = new NetWriter(m.Data.Length + 64);
        m.Write(w);
        Forward(HostConnection, w.Written);
    }

    /// <summary>Where an entity is: its body if it has one (a grid's centre of mass, which may be well away from its block
    /// origin), else its Transform.</summary>
    private static Vector3 Where(Entity e)
    {
        ref readonly var t = ref e.Get<Transform>();
        return e.Has<PhysicsBodyComponent>() ? e.Get<PhysicsBodyComponent>().BodyPosition(t) : new Vector3(t.Position.X, t.Position.Y, t.Position.Z);
    }

    // ── routing: everything goes through the Host ───────────────────────────

    public override void SendCommand(PeerId authority, ushort handlerId, uint seq, ReadOnlySpan<byte> payload) =>
        Send(HostConnection, new CommandMessage(authority, Session.LocalPeer, handlerId, seq, payload));

    /// <summary>Events go to the Host, which passes them on; but spawns never do: the Host spawns entities from their
    /// Descriptions (one it makes here is described next tick, see <see cref="AnnounceCreated"/>).</summary>
    public override void BroadcastEvent(ushort handlerId, in EventMeta meta, ReadOnlySpan<byte> payload)
    {
        if (Commands.HandlerFor(handlerId) is ISpawnHandler) return;
        Send([HostConnection], new EventMessage(handlerId, meta, payload));
    }

    public override void SendRejection(PeerId to, uint seq) => Send(HostConnection, new Rejection(to, Session.LocalPeer, seq));

    internal void SendToHost(ReadOnlySpan<byte> packet, Channel channel) => Forward(HostConnection, packet, channel);

    /// <summary>The player's latest inputs, for its authority to move them by (see <see cref="OwnPlayerPrediction"/>).</summary>
    internal void SendInput(in PlayerInputMessage message) => Send(HostConnection, message, Channel.Unreliable);

    public override void Dispose()
    {
        Send(HostConnection, new DisconnectMessage("left"));
        Transport?.Poll();
        base.Dispose();
    }
}

public sealed class JoinRequest : IDisposable
{
    private readonly ITransport _transport;
    private readonly Hello _hello;
    private Welcome? _welcome;
    private string? _refused;
    private bool _connected, _helloSent;

    public JoinRequest(ITransport transport, Hello hello)
    {
        _transport = transport;
        _hello = hello;
        transport.Connected += OnConnected;
        transport.Disconnected += OnDisconnected;
        transport.Received += OnReceived;
    }

    private void OnConnected(ConnectionId c) => _connected = true;
    private void OnDisconnected(ConnectionId c, string reason) => _refused ??= reason;

    private void OnReceived(ConnectionId c, ReadOnlySpan<byte> data, Channel ch)
    {
        var r = new NetReader(data);
        var kind = (MessageKind)r.ReadByte();
        if (kind == MessageKind.Welcome) _welcome = Welcome.Read(ref r);
        else if (kind == MessageKind.Disconnect) _refused = DisconnectMessage.Read(ref r).Reason;
    }

    /// <summary>Pumps the transport once; true with the welcome once it's here.</summary>
    public bool TryComplete(out Welcome welcome)
    {
        _transport.Poll();
        if (_connected && !_helloSent)
        {
            var w = new NetWriter();
            _hello.Write(w); // before the session exists
            _transport.Send(new ConnectionId(0), w.Written, Channel.Reliable);
            _helloSent = true;
        }
        if (_refused is not null) throw new InvalidOperationException($"The host refused: {_refused}");
        welcome = _welcome ?? default;
        return _welcome is not null;
    }

    public void Dispose()
    {
        _transport.Connected -= OnConnected;
        _transport.Disconnected -= OnDisconnected;
        _transport.Received -= OnReceived;
    }
}
