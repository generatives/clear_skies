using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace ClearSkies.Net.Transport;

/// <summary>The real transport: UDP through LiteNetLib, with its reliable-ordered and unreliable delivery.</summary>
public sealed class LiteNetTransport : ITransport
{
    private const string ConnectionKey = "ClearSkies";

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private readonly Dictionary<int, NetPeer> _peers = new();

    private LiteNetTransport()
    {
        _manager = new NetManager(_listener) { AutoRecycle = true, DisconnectTimeout = 10_000, ChannelsCount = 1 };
        _listener.ConnectionRequestEvent += request => request.AcceptIfKey(ConnectionKey);
        _listener.PeerConnectedEvent += peer =>
        {
            _peers[peer.Id] = peer;
            Connected?.Invoke(new ConnectionId(peer.Id));
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            _peers.Remove(peer.Id);
            Disconnected?.Invoke(new ConnectionId(peer.Id), info.Reason.ToString());
        };
        _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            var data = reader.GetRemainingBytesSpan();
            var ch = method == DeliveryMethod.ReliableOrdered ? Channel.Reliable : Channel.Unreliable;
            Stats.Received(data.Length, ch);
            Received?.Invoke(new ConnectionId(peer.Id), data, ch);
        };
    }

    /// <summary>Listens for clients on <paramref name="port"/>.</summary>
    public static LiteNetTransport Host(int port)
    {
        var t = new LiteNetTransport();
        if (!t._manager.Start(port)) throw new SocketException((int)SocketError.AddressAlreadyInUse);
        return t;
    }

    /// <summary>Connects to a host at <paramref name="address"/>:<paramref name="port"/>; <see cref="Connected"/> is raised from
    /// <see cref="Poll"/> once it answers.</summary>
    public static LiteNetTransport Join(string address, int port)
    {
        var t = new LiteNetTransport();
        t._manager.Start();
        t._manager.Connect(address, port, ConnectionKey);
        return t;
    }

    public TransportStats Stats { get; } = new();
    public event Action<ConnectionId>? Connected;
    public event Action<ConnectionId, string>? Disconnected;
    public event ReceiveHandler? Received;

    public void Send(ConnectionId to, ReadOnlySpan<byte> data, Channel channel)
    {
        if (!_peers.TryGetValue(to.Value, out var peer)) return;
        Stats.Sent(data.Length, channel);
        peer.Send(data, 0, channel == Channel.Reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
    }

    public void Disconnect(ConnectionId connection, string reason)
    {
        if (_peers.TryGetValue(connection.Value, out var peer)) peer.Disconnect(System.Text.Encoding.UTF8.GetBytes(reason));
    }

    public void Poll() => _manager.PollEvents();

    public void Dispose() => _manager.Stop();
}
