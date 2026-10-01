using System.Diagnostics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Transport;
using DefaultEcs;
using EngineSession = ClearSkies.Engine.Entities.Session;

namespace ClearSkies.Net.Session;

/// <summary>
/// What host and client sessions share: the transport, packet dispatch, and the engine-side pieces the network feeds
/// (the command system, the registry, the world, the clock). A system, first in each tick: its update handles
/// everything that arrived since the last, and whatever the session does each tick. It's also the command router:
/// commands go to their authority, events to everyone who applies them, always through the host.
/// <para>Messages go out through <see cref="Send{T}"/>, which writes each into one reused buffer and sends it before
/// returning, so nothing else ever sees the buffer half-written.</para>
/// </summary>
public abstract class NetSession : ISystem, ICommandRouter, IDisposable
{
    private readonly NetWriter _writer = new(1024);
    protected readonly Stopwatch RealTime = Stopwatch.StartNew();

    protected NetSession(ITransport? transport, EngineSession session, CommandSystem commands, EntityRegistry registry, World world, ITickClock clock)
    {
        Transport = transport;
        Session = session;
        Commands = commands;
        Registry = registry;
        World = world;
        Clock = clock;
        commands.Router = this;
        TimeSource = () => RealTime.Elapsed.TotalMilliseconds;
        if (transport != null)
        {
            transport.Connected += OnConnected;
            transport.Disconnected += OnDisconnected;
            transport.Received += OnReceived;
        }
    }

    public ITransport? Transport { get; }
    public EngineSession Session { get; }
    public CommandSystem Commands { get; }
    public EntityRegistry Registry { get; }
    public World World { get; }
    public ITickClock Clock { get; }

    /// <summary>Receives and relays body snapshots (set by the body sync system).</summary>
    public BodySync? Bodies { get; set; }

    /// <summary>Real time in milliseconds, for clock sync. Settable so tests can run on simulated time.</summary>
    public Func<double> TimeSource { get; set; }

    public double NowMs => TimeSource();

    /// <summary>Whether anyone else is in the session (pilot mode and the flight sliders are single-player only).</summary>
    public abstract bool OthersConnected { get; }

    /// <summary>Raised when the session ends for this machine (a client disconnected, or refused), with why.</summary>
    public event Action<string>? Ended;
    protected void End(string reason) => Ended?.Invoke(reason);

    /// <summary>Messages received and handed on (for the network panel).</summary>
    public long MessagesIn { get; private set; }

    /// <summary>First in each tick: everything that arrived since the last (handed to <see cref="OnMessage"/>), then
    /// this tick's own work.</summary>
    public virtual void Update(float dt) => Transport?.Poll();

    protected abstract void OnConnected(ConnectionId connection);
    protected abstract void OnDisconnected(ConnectionId connection, string reason);
    protected abstract void OnMessage(ConnectionId from, MessageKind kind, ref NetReader reader, ReadOnlySpan<byte> packet, Channel channel);

    private void OnReceived(ConnectionId from, ReadOnlySpan<byte> data, Channel channel)
    {
        if (data.Length == 0) return;
        MessagesIn++;
        var reader = new NetReader(data);
        var kind = (MessageKind)reader.ReadByte();
        try
        {
            OnMessage(from, kind, ref reader, data, channel);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            Console.WriteLine($"[net] bad {kind} from {from}: {e.Message}");
        }
    }

    /// <summary>Sends <paramref name="message"/> to <paramref name="to"/>.</summary>
    protected void Send<T>(ConnectionId to, in T message, Channel channel = Channel.Reliable) where T : struct, IMessage
    {
        if (Transport is null) return;
        _writer.Clear();
        message.Write(_writer);
        Transport.Send(to, _writer.Written, channel);
    }

    /// <summary>Sends <paramref name="message"/> to each of <paramref name="to"/>, written once.</summary>
    protected void Send<T>(IEnumerable<ConnectionId> to, in T message, Channel channel = Channel.Reliable) where T : struct, IMessage
    {
        if (Transport is null) return;
        _writer.Clear();
        message.Write(_writer);
        foreach (var c in to) Transport.Send(c, _writer.Written, channel);
    }

    protected void Send(ConnectionId to, in CommandMessage message)
    {
        if (Transport is null) return;
        _writer.Clear();
        message.Write(_writer);
        Transport.Send(to, _writer.Written, Channel.Reliable);
    }

    protected void Send(IEnumerable<ConnectionId> to, in EventMessage message)
    {
        if (Transport is null) return;
        _writer.Clear();
        message.Write(_writer);
        foreach (var c in to) Transport.Send(c, _writer.Written, Channel.Reliable);
    }

    /// <summary>Passes on a packet as it came.</summary>
    protected void Forward(ConnectionId to, ReadOnlySpan<byte> packet, Channel channel = Channel.Reliable) => Transport?.Send(to, packet, channel);

    // ── ICommandRouter ──────────────────────────────────────────────────────

    public abstract void SendCommand(PeerId authority, ushort handlerId, uint seq, ReadOnlySpan<byte> payload);
    public abstract void BroadcastEvent(ushort handlerId, in EventMeta meta, ReadOnlySpan<byte> payload);
    public abstract void SendRejection(PeerId to, uint seq);

    public virtual void Dispose() => Transport?.Dispose();
}
