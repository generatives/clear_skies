namespace ClearSkies.Net.Transport;

public enum Channel : byte
{
    /// <summary>Delivered once, in the order sent: commands, events, session messages.</summary>
    Reliable = 0,
    /// <summary>May be lost or arrive out of order: body snapshots, clock pings. Only the latest matters.</summary>
    Unreliable = 1,
}

/// <summary>One connection on a transport (a client, from the host; the host, from a client).</summary>
public readonly record struct ConnectionId(int Value)
{
    public override string ToString() => $"connection {Value}";
}

public delegate void ReceiveHandler(ConnectionId from, ReadOnlySpan<byte> data, Channel channel);

/// <summary>Moves packets between machines. The host listens and has a connection per client; a client has one
/// connection, to the host. Events are raised from <see cref="Poll"/>, on the calling (main) thread.</summary>
public interface ITransport : IDisposable
{
    void Send(ConnectionId to, ReadOnlySpan<byte> data, Channel channel);
    void Disconnect(ConnectionId connection, string reason);
    void Poll();

    event Action<ConnectionId>? Connected;
    event Action<ConnectionId, string>? Disconnected;
    event ReceiveHandler? Received;

    TransportStats Stats { get; }
}

/// <summary>Bytes and packets through a transport, for the network panel.</summary>
public sealed class TransportStats
{
    public long BytesSent, BytesReceived, PacketsSent, PacketsReceived;
    public readonly long[] BytesSentByChannel = new long[2], BytesReceivedByChannel = new long[2];

    internal void Sent(int bytes, Channel channel) { BytesSent += bytes; PacketsSent++; BytesSentByChannel[(int)channel] += bytes; }
    internal void Received(int bytes, Channel channel) { BytesReceived += bytes; PacketsReceived++; BytesReceivedByChannel[(int)channel] += bytes; }
}
