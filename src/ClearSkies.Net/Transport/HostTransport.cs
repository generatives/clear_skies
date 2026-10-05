namespace ClearSkies.Net.Transport;

/// <summary>
/// What the Host listens on: a link to the Participant on its own machine (in-process, see
/// <see cref="LoopbackNetwork"/>), and, when others can join, the network. One transport to the Host, with
/// <see cref="IsLocal"/> telling a connection on the local link (the hosting machine's Participant, which has authority)
/// from one over the network: the Host trusts that only by where it came from, never by anything sent.
/// </summary>
public sealed class HostTransport : ITransport
{
    private readonly ITransport _local;
    private readonly ITransport? _remote;

    public HostTransport(ITransport local, ITransport? remote)
    {
        _local = local;
        _remote = remote;
        local.Connected += c => Connected?.Invoke(Local(c));
        local.Disconnected += (c, reason) => Disconnected?.Invoke(Local(c), reason);
        local.Received += (c, data, channel) => Received?.Invoke(Local(c), data, channel);
        if (remote is null) return;
        remote.Connected += c => Connected?.Invoke(c);
        remote.Disconnected += (c, reason) => Disconnected?.Invoke(c, reason);
        remote.Received += (c, data, channel) => Received?.Invoke(c, data, channel);
    }

    // Local connections are numbered below zero; the network's as it numbers them (from zero up).
    private static ConnectionId Local(ConnectionId c) => new(-1 - c.Value);

    /// <summary>Whether <paramref name="connection"/> is on the local link.</summary>
    public static bool IsLocal(ConnectionId connection) => connection.Value < 0;

    /// <summary>The network transport, if others can join (none: single-player).</summary>
    public ITransport? Remote => _remote;

    public TransportStats Stats => _remote?.Stats ?? _local.Stats;

    public event Action<ConnectionId>? Connected;
    public event Action<ConnectionId, string>? Disconnected;
    public event ReceiveHandler? Received;

    public void Send(ConnectionId to, ReadOnlySpan<byte> data, Channel channel)
    {
        if (IsLocal(to)) _local.Send(Local(to), data, channel);
        else _remote?.Send(to, data, channel);
    }

    public void Disconnect(ConnectionId connection, string reason)
    {
        if (IsLocal(connection)) _local.Disconnect(Local(connection), reason);
        else _remote?.Disconnect(connection, reason);
    }

    public void Poll()
    {
        _local.Poll();
        _remote?.Poll();
    }

    public void Dispose()
    {
        _local.Dispose();
        _remote?.Dispose();
    }
}
