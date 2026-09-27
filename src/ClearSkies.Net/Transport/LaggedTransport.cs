using System.Diagnostics;

namespace ClearSkies.Net.Transport;

/// <summary>
/// Wraps any transport to add artificial latency, jitter and packet loss to what this machine sends, because a local
/// connection hides almost every networking bug. Reliable packets are only delayed (in order); unreliable ones may
/// also be dropped. All zero by default: a plain pass-through.
/// </summary>
public sealed class LaggedTransport : ITransport
{
    private readonly ITransport _inner;
    private readonly Random _random = new(7);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(double At, long Order, ConnectionId To, byte[] Data, Channel Channel)> _outbox = new();
    private readonly Dictionary<ConnectionId, double> _lastReliable = new();
    private long _order;

    public LaggedTransport(ITransport inner)
    {
        _inner = inner;
        _inner.Connected += c => Connected?.Invoke(c);
        _inner.Disconnected += (c, r) => Disconnected?.Invoke(c, r);
        _inner.Received += (c, d, ch) => Received?.Invoke(c, d, ch);
    }

    /// <summary>Extra one-way delay on what's sent, in milliseconds (half the round trip it adds when both ends set it).</summary>
    public double LatencyMs { get; set; }
    public double JitterMs { get; set; }
    public double LossChance { get; set; }

    public TransportStats Stats => _inner.Stats;
    public event Action<ConnectionId>? Connected;
    public event Action<ConnectionId, string>? Disconnected;
    public event ReceiveHandler? Received;

    public void Send(ConnectionId to, ReadOnlySpan<byte> data, Channel channel)
    {
        if (LatencyMs <= 0 && JitterMs <= 0 && LossChance <= 0 && _outbox.Count == 0)
        {
            _inner.Send(to, data, channel);
            return;
        }
        if (channel == Channel.Unreliable && _random.NextDouble() < LossChance) return;
        double at = _clock.Elapsed.TotalMilliseconds + LatencyMs + (JitterMs > 0 ? _random.NextDouble() * JitterMs : 0);
        if (channel == Channel.Reliable)
        {
            at = System.Math.Max(at, _lastReliable.GetValueOrDefault(to));
            _lastReliable[to] = at;
        }
        _outbox.Add((at, _order++, to, data.ToArray(), channel));
    }

    public void Disconnect(ConnectionId connection, string reason) => _inner.Disconnect(connection, reason);

    public void Poll()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (_outbox.Count > 0)
        {
            _outbox.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Order.CompareTo(b.Order));
            int due = 0;
            while (due < _outbox.Count && _outbox[due].At <= now) due++;
            foreach (var p in _outbox.GetRange(0, due)) _inner.Send(p.To, p.Data, p.Channel);
            _outbox.RemoveRange(0, due);
        }
        _inner.Poll();
    }

    public void Dispose() => _inner.Dispose();
}
