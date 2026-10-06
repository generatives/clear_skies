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
/// the Host lets it in, every message from it becomes a call on the Host, as from that Participant, and every call the
/// Host makes on it goes back as a message. A system, first in the hosting machine's tick: its update
/// hands on everything that arrived since the last.
/// </summary>
public sealed class HostNetwork : ISystem, IDisposable
{
    private readonly Host _host;
    private readonly ITransport _transport;
    private readonly Dictionary<ConnectionId, RemoteParticipant> _connections = new();

    public HostNetwork(Host host, ITransport transport)
    {
        _host = host;
        _transport = transport;
        transport.Connected += c => _connections[c] = new RemoteParticipant(this, c);
        transport.Disconnected += OnDisconnected;
        transport.Received += OnReceived;
    }

    public TransportStats Stats => _transport.Stats;

    /// <summary>First in the tick: everything that arrived.</summary>
    public void Update(float dt) => _transport.Poll();

    private void OnDisconnected(ConnectionId connection, string reason)
    {
        if (_connections.Remove(connection, out var from) && from.Joined is { } joined) _host.Leave(joined, reason);
    }

    private void OnReceived(ConnectionId from, ReadOnlySpan<byte> packet, Channel channel)
    {
        if (packet.Length == 0 || !_connections.TryGetValue(from, out var participant)) return;
        var r = new NetReader(packet);
        var kind = (MessageKind)r.ReadByte();
        try
        {
            if (kind == MessageKind.Hello) participant.Hello(Hello.Read(ref r));
            else if (participant.Joined is { } joined) Dispatch(participant, joined, kind, ref r);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            Console.WriteLine($"[net] bad {kind} from {participant.Joined?.Name ?? from.ToString()}: {e.Message}");
        }
    }

    private void Dispatch(RemoteParticipant from, JoinedParticipant joined, MessageKind kind, ref NetReader r)
    {
        switch (kind)
        {
            case MessageKind.TimePing: _host.Ping(joined, TimePing.Read(ref r).ClientTimeMs); break;
            case MessageKind.IdBlockRequest: _host.RequestIdBlock(joined); break;
            case MessageKind.ViewVolume:
            {
                var view = ViewVolumeMessage.Read(ref r);
                _host.SetView(joined, view.Centre, view.Radius);
                break;
            }
            case MessageKind.Command: _host.SendCommand(joined, CommandMessage.Read(ref r)); break;
            case MessageKind.Event: _host.SendEvent(joined, EventMessage.Read(ref r)); break;
            case MessageKind.Rejection: _host.Reject(Rejection.Read(ref r)); break;
            case MessageKind.PlayerInput: _host.SendInput(joined, PlayerInputMessage.Read(ref r)); break;
            case MessageKind.StateFrame:
            {
                uint tick = BodySync.ReadFrame(ref r, from.Frame);
                _host.SendFrame(joined, tick, from.Frame);
                break;
            }
            case MessageKind.Created: _host.EntityCreated(joined, DescriptionMessage.Read(kind, ref r)); break;
            case MessageKind.Described: _host.EntityDescribed(joined, DescriptionMessage.Read(kind, ref r)); break;
            case MessageKind.Released: _host.EntityReleased(joined, DescriptionMessage.Read(kind, ref r)); break;
            case MessageKind.Deleted: _host.EntityDeleted(joined, EntityMessage.Read(kind, ref r).Id); break;
            case MessageKind.Disconnect: from.Disconnect(DisconnectMessage.Read(ref r).Reason); break;
        }
    }

    public void Dispose() => _transport.Dispose();

    /// <summary>One Participant over the network: what the Host tells it, as messages.</summary>
    private sealed class RemoteParticipant(HostNetwork owner, ConnectionId id) : IParticipant
    {
        private readonly NetWriter _writer = new(1024);
        public readonly List<BodySnapshot> Frame = new();

        /// <summary>Its record at the Host, once it has joined.</summary>
        public JoinedParticipant? Joined;

        public void Hello(in Hello hello)
        {
            if (Joined is not null) { Console.WriteLine($"[net] ignoring a second hello from {Joined.Name} ({Joined.Peer})"); return; }
            Joined = owner._host.Join(hello, local: false, out var welcome, out var refusal);
            if (Joined is null)
            {
                Disconnected(refusal);
                return;
            }
            Joined.Participant = this;
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
