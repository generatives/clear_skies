using System.Diagnostics;

namespace ClearSkies.Net.Transport;

/// <summary>
/// An in-process network for tests and for trying multiplayer without sockets: a host endpoint and any number of
/// client endpoints, with adjustable latency, jitter and packet loss. Reliable packets always arrive, in order, after
/// the latency; unreliable ones may be dropped or arrive out of order. Time comes from <see cref="Now"/> (milliseconds),
/// real time by default or set by a test.
/// </summary>
public sealed class LoopbackNetwork
{
    private readonly Random _random;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private LoopbackTransport? _host;
    private int _nextConnection = 1;

    public LoopbackNetwork(int seed = 1) => _random = new Random(seed);

    /// <summary>One-way delay in milliseconds, plus up to <see cref="JitterMs"/> more.</summary>
    public double LatencyMs { get; set; }
    public double JitterMs { get; set; }

    /// <summary>Chance an unreliable packet is lost, 0 to 1.</summary>
    public double LossChance { get; set; }

    /// <summary>The current time in milliseconds. Real time unless a test sets <see cref="ManualTime"/>.</summary>
    public double Now => ManualTime ?? _clock.Elapsed.TotalMilliseconds;
    public double? ManualTime { get; set; }

    public LoopbackTransport Listen()
    {
        if (_host != null) throw new InvalidOperationException("Already listening.");
        return _host = new LoopbackTransport(this, isHost: true);
    }

    public LoopbackTransport Connect()
    {
        var host = _host ?? throw new InvalidOperationException("Nobody is listening.");
        var client = new LoopbackTransport(this, isHost: false);
        int id = _nextConnection++;
        var hostSide = new ConnectionId(id);
        var clientSide = new ConnectionId(0);
        client.Link(clientSide, host, hostSide);
        host.Link(hostSide, client, clientSide);
        client.QueueConnected(clientSide);
        host.QueueConnected(hostSide);
        return client;
    }

    internal double Delay(Channel channel, out bool dropped)
    {
        dropped = channel == Channel.Unreliable && _random.NextDouble() < LossChance;
        return LatencyMs + (JitterMs > 0 ? _random.NextDouble() * JitterMs : 0);
    }
}

public sealed class LoopbackTransport : ITransport
{
    private readonly LoopbackNetwork _network;
    private readonly Dictionary<ConnectionId, (LoopbackTransport Remote, ConnectionId RemoteId, double LastReliable)> _links = new();
    private readonly List<(double At, long Order, ConnectionId From, byte[] Data, Channel Channel)> _inbox = new();
    private readonly Queue<ConnectionId> _connected = new();
    private readonly Queue<(ConnectionId, string)> _disconnected = new();
    private long _order;

    internal LoopbackTransport(LoopbackNetwork network, bool isHost)
    {
        _network = network;
        IsHost = isHost;
    }

    public bool IsHost { get; }
    public TransportStats Stats { get; } = new();

    public event Action<ConnectionId>? Connected;
    public event Action<ConnectionId, string>? Disconnected;
    public event ReceiveHandler? Received;

    internal void Link(ConnectionId local, LoopbackTransport remote, ConnectionId remoteId) => _links[local] = (remote, remoteId, 0);
    internal void QueueConnected(ConnectionId id) => _connected.Enqueue(id);

    public void Send(ConnectionId to, ReadOnlySpan<byte> data, Channel channel)
    {
        if (!_links.TryGetValue(to, out var link)) return;
        Stats.Sent(data.Length, channel);
        double at = _network.Now + _network.Delay(channel, out bool dropped);
        if (dropped) return;
        if (channel == Channel.Reliable)
        {
            at = System.Math.Max(at, link.LastReliable); // in order
            _links[to] = (link.Remote, link.RemoteId, at);
        }
        link.Remote.Deliver(link.RemoteId, data.ToArray(), channel, at);
    }

    private void Deliver(ConnectionId from, byte[] data, Channel channel, double at) => _inbox.Add((at, _order++, from, data, channel));

    public void Disconnect(ConnectionId connection, string reason)
    {
        if (!_links.Remove(connection, out var link)) return;
        link.Remote._links.Remove(link.RemoteId);
        link.Remote._disconnected.Enqueue((link.RemoteId, reason));
        _disconnected.Enqueue((connection, reason));
    }

    public void Poll()
    {
        while (_connected.TryDequeue(out var c)) Connected?.Invoke(c);
        double now = _network.Now;
        _inbox.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Order.CompareTo(b.Order));
        int due = 0;
        while (due < _inbox.Count && _inbox[due].At <= now) due++;
        var ready = _inbox.GetRange(0, due);
        _inbox.RemoveRange(0, due);
        foreach (var (_, _, from, data, channel) in ready)
        {
            if (!_links.ContainsKey(from)) continue; // disconnected meanwhile
            Stats.Received(data.Length, channel);
            Received?.Invoke(from, data, channel);
        }
        while (_disconnected.TryDequeue(out var d)) Disconnected?.Invoke(d.Item1, d.Item2);
    }

    public void Dispose()
    {
        foreach (var c in _links.Keys.ToList()) Disconnect(c, "closed");
    }
}
