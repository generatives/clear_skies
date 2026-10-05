using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Transport;
using DefaultEcs;
using Silk.NET.Maths;
using EngineSession = ClearSkies.Engine.Entities.Session;

namespace ClearSkies.Net.Session;

/// <summary>
/// A client's side of the session: one connection, to the host. After the Welcome it loads terrain around where the
/// player will spawn (a stand-in terrain interest until the player arrives) and settles its clock on the host's (see
/// <see cref="ClockSync.Settling"/>, for at most <see cref="MaxSettleMs"/>), tells the host, and then receives the
/// world: every entity as a spawn event, then its own player, which the host simulates and this machine predicts
/// (<see cref="OwnPlayerPrediction"/>, which sends the player's input). Everything it sends goes to the host, which
/// applies or relays it. Keeps its clock on the host's with <see cref="ClockSync"/>.
/// </summary>
public sealed class ClientSession : NetSession
{
    private static readonly ConnectionId Host = new(0);

    private readonly Func<Vector3, bool> _terrainLoaded;
    private readonly EntitySet _localPlayers;
    private Entity _anchor;
    private bool _terrainReadySent;
    private double _settleFrom = double.NaN;

    /// <summary>The longest a join waits for the clock to settle, in milliseconds: a jittery connection may never
    /// quite, and it carries on slewing once joined.</summary>
    public const double MaxSettleMs = 10_000;

    /// <param name="welcome">The host's welcome (see <see cref="Connect"/>).</param>
    /// <param name="terrainLoaded">Whether the terrain around a point has loaded (ChunkLoadSystem).</param>
    public ClientSession(ITransport transport, Welcome welcome, EngineSession session, CommandSystem commands, EntityRegistry registry,
                         World world, ITickClock clock, Func<Vector3, bool> terrainLoaded)
        : base(transport, session, commands, registry, world, clock)
    {
        Welcome = welcome;
        _terrainLoaded = terrainLoaded;
        ClockSync = new ClockSync(clock) { Settling = true };
        session.BecomeClient(welcome.Peer);
        registry.AddIdBlock(welcome.IdFirst, welcome.IdCount);
        ClockSync.SnapTo(welcome.HostTick);
        _localPlayers = world.GetEntities().With<LocalPlayer>().AsSet();

        // Stream terrain around the spawn until the player is here to stream around.
        _anchor = world.CreateEntity();
        _anchor.Set(new Transform { Position = new Vector3D<float>(welcome.Spawn.X, welcome.Spawn.Y, welcome.Spawn.Z), Rotation = Quaternion<float>.Identity, Scale = Vector3D<float>.One });
        _anchor.Set(new TerrainInterest { ColliderRadius = EntityPresenceSystem.ColliderRange, DrawRadius = 1000 });
    }

    public Welcome Welcome { get; }
    public ClockSync ClockSync { get; }
    public bool Joined { get; private set; }

    /// <summary>What joining is waiting on, for a loading screen: the world around the spawn, the clock, then the host.</summary>
    public string JoinStatus { get; private set; } = "Loading the world";
    public override bool OthersConnected => true;

    /// <summary>
    /// Connects and says hello, waiting (pumping the transport) for the host's welcome. Throws with the host's reason
    /// if it refuses, or on a timeout.
    /// </summary>
    public static Welcome Connect(ITransport transport, Hello hello, TimeSpan timeout)
    {
        using var join = new JoinRequest(transport, hello);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (join.TryComplete(out var welcome)) return welcome;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The host didn't answer.");
            Thread.Sleep(5);
        }
    }

    public override void Update(float dt)
    {
        base.Update(dt);
        ClockSync.Update();
        if (ClockSync.ShouldPing(NowMs)) Send(Host, new TimePing(NowMs), Channel.Unreliable);
        if (double.IsNaN(_settleFrom)) _settleFrom = NowMs;
        bool clockReady = ClockSync.Settled || NowMs - _settleFrom > MaxSettleMs;
        bool terrainLoaded = !_terrainReadySent && _terrainLoaded(Welcome.Spawn);
        if (!_terrainReadySent) JoinStatus = !terrainLoaded ? "Loading the world" : "Syncing with the host";
        if (terrainLoaded && clockReady)
        {
            JoinStatus = "Waiting for the host";
            Send(Host, new TerrainReady());
            _terrainReadySent = true;
            double waited = (NowMs - _settleFrom) / 1000;
            Console.WriteLine(ClockSync.Settled
                ? $"[net] clock settled after {waited:0.0} s: {ClockSync.Offset:+0.00;-0.00} ticks off, round trip {ClockSync.RoundTripMs:0} ms, {ClockSync.Snaps} snaps, {ClockSync.SkippedTicks} dropped ticks put back"
                : $"[net] clock didn't settle in {waited:0.0} s: {ClockSync.Offset:+0.00;-0.00} ticks off, round trip {ClockSync.RoundTripMs:0} ms, {ClockSync.Snaps} snaps, {ClockSync.SkippedTicks} dropped ticks put back; joining anyway");
            ClockSync.EndSettling(NowMs); // from here on, the world is drawn and predicted from it: slew
        }
        if (_anchor.IsAlive && _localPlayers.Count > 0)
        {
            _anchor.Dispose(); // the player streams its own terrain now
            Joined = true;
        }
        if (Registry.IdsLeft < EntityRegistry.BlockSize / 4 && !_idRequested)
        {
            Send(Host, new IdBlockRequest());
            _idRequested = true;
        }
    }

    private bool _idRequested;

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
            case MessageKind.Event:
            {
                var m = EventMessage.Read(ref r);
                Commands.ReceiveEvent(m.Meta, m.Handler, m.Payload.ToArray());
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
            case MessageKind.TimePong:
                ClockSync.OnPong(TimePong.Read(ref r), NowMs);
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

    // ── routing: everything goes through the host ───────────────────────────

    public override void SendCommand(PeerId authority, ushort handlerId, uint seq, ReadOnlySpan<byte> payload) =>
        Send(Host, new CommandMessage(authority, Session.LocalPeer, handlerId, seq, payload));

    public override void BroadcastEvent(ushort handlerId, in EventMeta meta, ReadOnlySpan<byte> payload) =>
        Send([Host], new EventMessage(handlerId, meta, payload));

    public override void SendRejection(PeerId to, uint seq) => Send(Host, new Rejection(to, Session.LocalPeer, seq));

    internal void SendToHost(ReadOnlySpan<byte> packet, Channel channel) => Forward(Host, packet, channel);

    /// <summary>The player's latest inputs, for the host to move them by (see <see cref="OwnPlayerPrediction"/>).</summary>
    internal void SendInput(in PlayerInputMessage message) => Send(Host, message, Channel.Unreliable);

    public override void Dispose()
    {
        Send(Host, new DisconnectMessage("left"));
        Transport?.Poll();
        base.Dispose();
    }
}

/// <summary>A join in progress: says hello once connected, and completes with the host's welcome (or throws with
/// its refusal). Poll it with <see cref="TryComplete"/>.</summary>
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
