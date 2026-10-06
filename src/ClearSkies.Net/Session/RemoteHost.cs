using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Transport;

namespace ClearSkies.Net.Session;

/// <summary>
/// The Host, as a Participant on another machine has it: each <see cref="IHost"/> call goes over the network as a
/// message, and each message from the Host comes back as a call on its <see cref="Participant"/> property. A system, first in the
/// tick: its update hands on everything that arrived since the last.
/// </summary>
public sealed class RemoteHost : IHost, ISystem, IDisposable
{
    private static readonly ConnectionId HostConnection = new(0);

    private readonly ITransport _transport;
    private readonly NetWriter _writer = new(1024);
    private readonly List<BodySnapshot> _frame = new();
    private bool _left;

    /// <param name="welcome">The Host's welcome (see <see cref="Connect"/>).</param>
    public RemoteHost(ITransport transport, Welcome welcome)
    {
        _transport = transport;
        Welcome = welcome;
        transport.Disconnected += OnDisconnected;
        transport.Received += OnReceived;
    }

    public Welcome Welcome { get; }

    /// <summary>What it hands the Host's messages to (see <see cref="SimulationParticipant.Join(RemoteHost, Engine.Entities.Session, Engine.Commands.CommandSystem, EntityRegistry, DefaultEcs.World, ITickClock, Func{Vector3, bool})"/>).</summary>
    public IParticipant? Participant { get; set; }

    public TransportStats Stats => _transport.Stats;

    /// <summary>
    /// Connects and says hello, waiting (pumping the transport, and <paramref name="pump"/> meanwhile) for the Host's
    /// welcome. Throws with the Host's reason if it refuses, or on a timeout.
    /// </summary>
    public static RemoteHost Connect(ITransport transport, Hello hello, TimeSpan timeout, Action? pump = null)
    {
        using var join = new JoinRequest(transport, hello);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (join.TryComplete(out var welcome)) return new RemoteHost(transport, welcome);
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The host didn't answer.");
            if (pump is null) Thread.Sleep(5);
            else pump();
        }
    }

    /// <summary>First in the tick: everything that arrived.</summary>
    public void Update(float dt) => _transport.Poll();

    // ── to the Host ─────────────────────────────────────────────────────────

    public void Ping(double clientTimeMs) => Send(new TimePing(clientTimeMs), Channel.Unreliable);
    public void RequestIdBlock() => Send(new IdBlockRequest());
    public void SetView(Vector3 centre, float radius) => Send(new ViewVolumeMessage(centre, radius));
    public void SendCommand(in CommandMessage command) { _writer.Clear(); command.Write(_writer); Send(); }
    public void SendEvent(in EventMessage evt) { _writer.Clear(); evt.Write(_writer); Send(); }
    public void Reject(in Rejection rejection) => Send(rejection);
    public void SendInput(in PlayerInputMessage input) => Send(input, Channel.Unreliable);

    public void SendFrame(uint tick, IReadOnlyList<BodySnapshot> snapshots) =>
        BodySync.WriteFrames(_writer, tick, snapshots, packet => _transport.Send(HostConnection, packet, Channel.Unreliable));

    public void EntityCreated(in DescriptionMessage description) => Send(description);
    public void EntityDescribed(in DescriptionMessage description) => Send(description);
    public void EntityReleased(in DescriptionMessage description) => Send(description);
    public void EntityDeleted(EntityId id) => Send(new EntityMessage(MessageKind.Deleted, id));
    public void EntitySaved(in DescriptionMessage description) => Send(description);
    public void SaveDone() => Send(new SignalMessage(MessageKind.SaveDone));
    public void RequestChunk(ChunkPosition pos) { _writer.Clear(); new ChunkMessage(MessageKind.ChunkRequest, pos, default).Write(_writer); Send(); }
    public void ChunkEdited(in ChunkMessage chunk) { _writer.Clear(); chunk.Write(_writer); Send(); }

    public void Leave(string reason)
    {
        if (_left) return;
        _left = true;
        Send(new DisconnectMessage(reason));
        _transport.Poll();
    }

    private void Send<T>(in T message, Channel channel = Channel.Reliable) where T : struct, IMessage
    {
        _writer.Clear();
        message.Write(_writer);
        Send(channel);
    }

    private void Send(in DescriptionMessage message)
    {
        var w = new NetWriter(message.Data.Length + 64);
        message.Write(w);
        _transport.Send(HostConnection, w.Written, Channel.Reliable);
    }

    private void Send(Channel channel = Channel.Reliable) => _transport.Send(HostConnection, _writer.Written, channel);

    // ── from the Host ───────────────────────────────────────────────────────

    private void OnDisconnected(ConnectionId connection, string reason)
    {
        _left = true;
        Participant?.Disconnected(reason);
    }

    private void OnReceived(ConnectionId from, ReadOnlySpan<byte> data, Channel channel)
    {
        if (data.Length == 0 || Participant is not { } p) return;
        var r = new NetReader(data);
        var kind = (MessageKind)r.ReadByte();
        try
        {
            Dispatch(p, kind, ref r);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            Console.WriteLine($"[net] bad {kind} from the host: {e.Message}");
        }
    }

    private void Dispatch(IParticipant p, MessageKind kind, ref NetReader r)
    {
        switch (kind)
        {
            case MessageKind.Spawn: p.Spawn(SpawnMessage.Read(ref r)); break;
            case MessageKind.Forget: p.Forget(EntityMessage.Read(kind, ref r).Id); break;
            case MessageKind.Event: p.ReceiveEvent(EventMessage.Read(ref r)); break;
            case MessageKind.Command: p.ReceiveCommand(CommandMessage.Read(ref r)); break;
            case MessageKind.Rejection: p.Rejected(Rejection.Read(ref r)); break;
            case MessageKind.StateFrame:
            {
                uint tick = BodySync.ReadFrame(ref r, _frame);
                p.ReceiveFrame(tick, _frame);
                break;
            }
            case MessageKind.TimePong: p.Pong(TimePong.Read(ref r)); break;
            case MessageKind.IdBlock:
            {
                var block = IdBlockMessage.Read(ref r);
                p.IdBlock(block.First, block.Count);
                break;
            }
            case MessageKind.PlayerJoined:
            case MessageKind.PlayerLeft:
                p.PlayerNotice(Protocol.PlayerNotice.Read(kind == MessageKind.PlayerJoined, ref r));
                break;
            case MessageKind.PlayerInput: p.ReceiveInput(PlayerInputMessage.Read(ref r)); break;
            case MessageKind.Release: p.Release(EntityMessage.Read(kind, ref r).Id); break;
            case MessageKind.DescribeRequest: p.Describe(EntityMessage.Read(kind, ref r).Id); break;
            case MessageKind.SaveRequest: p.Save(); break;
            case MessageKind.ChunkData: p.ChunkData(ChunkMessage.Read(kind, ref r)); break;
            case MessageKind.Disconnect:
                _left = true;
                p.Disconnected(DisconnectMessage.Read(ref r).Reason);
                break;
        }
    }

    public void Dispose()
    {
        Leave("left");
        _transport.Dispose();
    }
}

/// <summary>Saying hello to the Host over the network, until it welcomes us (or refuses).</summary>
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
            _hello.Write(w);
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
