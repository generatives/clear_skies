using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Transport;

namespace ClearSkies.Net.Session;

/// <summary>
/// The Host's end of the network: Participants on other machines join through it. Each connection says hello, and once
/// the Host lets it in, every message from it is a call on its <see cref="HostPeer"/> (as an <see cref="IHost"/>), and
/// every call the Host makes on it goes back as a message. A system, first in the hosting machine's tick: its update
/// hands on everything that arrived since the last.
/// </summary>
public sealed class RemoteParticipants : ISystem, IDisposable
{
    private readonly Host _host;
    private readonly ITransport _transport;
    private readonly Dictionary<ConnectionId, Connection> _connections = new();

    public RemoteParticipants(Host host, ITransport transport)
    {
        _host = host;
        _transport = transport;
        transport.Connected += c => _connections[c] = new Connection(this, c);
        transport.Disconnected += OnDisconnected;
        transport.Received += OnReceived;
    }

    public TransportStats Stats => _transport.Stats;

    /// <summary>First in the tick: everything that arrived.</summary>
    public void Update(float dt) => _transport.Poll();

    private void OnDisconnected(ConnectionId connection, string reason)
    {
        if (_connections.Remove(connection, out var c)) ((IHost?)c.Peer)?.Leave(reason);
    }

    private void OnReceived(ConnectionId from, ReadOnlySpan<byte> packet, Channel channel)
    {
        if (packet.Length == 0 || !_connections.TryGetValue(from, out var c)) return;
        var r = new NetReader(packet);
        var kind = (MessageKind)r.ReadByte();
        try
        {
            if (kind == MessageKind.Hello) c.Hello(Hello.Read(ref r));
            else if (c.Peer is { } peer) Dispatch(c, peer, kind, ref r);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            Console.WriteLine($"[net] bad {kind} from {c.Peer?.Name ?? from.ToString()}: {e.Message}");
        }
    }

    private static void Dispatch(Connection c, IHost peer, MessageKind kind, ref NetReader r)
    {
        switch (kind)
        {
            case MessageKind.TimePing: peer.Ping(TimePing.Read(ref r).ClientTimeMs); break;
            case MessageKind.IdBlockRequest: peer.RequestIdBlock(); break;
            case MessageKind.ViewVolume:
            {
                var view = ViewVolumeMessage.Read(ref r);
                peer.SetView(view.Centre, view.Radius);
                break;
            }
            case MessageKind.Command: peer.SendCommand(CommandMessage.Read(ref r)); break;
            case MessageKind.Event: peer.SendEvent(EventMessage.Read(ref r)); break;
            case MessageKind.Rejection: peer.Reject(Rejection.Read(ref r)); break;
            case MessageKind.PlayerInput: peer.SendInput(PlayerInputMessage.Read(ref r)); break;
            case MessageKind.StateFrame:
            {
                uint tick = BodySync.ReadFrame(ref r, c.Frame);
                peer.SendFrame(tick, c.Frame);
                break;
            }
            case MessageKind.Created: peer.EntityCreated(DescriptionMessage.Read(kind, ref r)); break;
            case MessageKind.Described: peer.EntityDescribed(DescriptionMessage.Read(kind, ref r)); break;
            case MessageKind.Released: peer.EntityReleased(DescriptionMessage.Read(kind, ref r)); break;
            case MessageKind.Deleted: peer.EntityDeleted(EntityMessage.Read(kind, ref r).Id); break;
            case MessageKind.Disconnect: c.Disconnect(DisconnectMessage.Read(ref r).Reason); break;
        }
    }

    public void Dispose() => _transport.Dispose();

    /// <summary>One Participant over the network: what the Host tells it, as messages.</summary>
    private sealed class Connection(RemoteParticipants owner, ConnectionId id) : IParticipant
    {
        private readonly NetWriter _writer = new(1024);
        public readonly List<BodySnapshot> Frame = new();

        /// <summary>Its record at the Host, once it has joined.</summary>
        public HostPeer? Peer;

        public void Hello(in Hello hello)
        {
            if (Peer is not null) { Console.WriteLine($"[net] ignoring a second hello from {Peer.Name} ({Peer.Peer})"); return; }
            Peer = owner._host.Join(hello, local: false, out var welcome, out var refusal);
            if (Peer is null)
            {
                Disconnected(refusal);
                return;
            }
            Peer.Participant = this;
            Send(welcome);
        }

        /// <summary>It's leaving.</summary>
        public void Disconnect(string reason) => owner._transport.Disconnect(id, reason);

        public void Spawn(in SpawnMessage spawn)
        {
            _writer.Clear();
            spawn.Write(_writer);
            Send();
        }

        public void Forget(EntityId entity) => Send(new EntityMessage(MessageKind.Forget, entity));
        public void ReceiveEvent(in EventMessage evt) { _writer.Clear(); evt.Write(_writer); Send(); }
        public void ReceiveCommand(in CommandMessage command) { _writer.Clear(); command.Write(_writer); Send(); }
        public void Rejected(in Rejection rejection) => Send(rejection);

        public void ReceiveFrame(uint tick, IReadOnlyList<BodySnapshot> snapshots) =>
            BodySync.WriteFrames(_writer, tick, snapshots, packet => owner._transport.Send(id, packet, Channel.Unreliable));

        public void Pong(in TimePong pong) => Send(pong, Channel.Unreliable);
        public void IdBlock(uint first, uint count) => Send(new IdBlockMessage(first, count));
        public void PlayerNotice(in PlayerNotice notice) => Send(notice);

        public void Disconnected(string reason)
        {
            Send(new DisconnectMessage(reason));
            owner._transport.Disconnect(id, reason);
        }

        public void ReceiveInput(in PlayerInputMessage input) => Send(input, Channel.Unreliable);
        public void Release(EntityId entity) => Send(new EntityMessage(MessageKind.Release, entity));
        public void Describe(EntityId entity) => Send(new EntityMessage(MessageKind.DescribeRequest, entity));

        private void Send<T>(in T message, Channel channel = Channel.Reliable) where T : struct, IMessage
        {
            _writer.Clear();
            message.Write(_writer);
            Send(channel);
        }

        private void Send(Channel channel = Channel.Reliable) => owner._transport.Send(id, _writer.Written, channel);
    }
}
