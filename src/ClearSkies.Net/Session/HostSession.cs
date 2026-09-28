using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Transport;
using DefaultEcs;
using EngineSession = ClearSkies.Engine.Entities.Session;

namespace ClearSkies.Net.Session;

public enum PeerState
{
    /// <summary>Connected; no Hello yet.</summary>
    Connected,
    /// <summary>Welcomed; loading terrain around their spawn.</summary>
    LoadingTerrain,
    /// <summary>Terrain loaded; the host is describing every entity for them this tick.</summary>
    Snapshot,
    /// <summary>In the game: receives every event and snapshot.</summary>
    Joined,
}

/// <summary>A client, as the host sees it.</summary>
public sealed class RemotePeer
{
    public ConnectionId Connection;
    public PeerId Peer;
    public PeerState State;
    public string Name = "";
    public PlayerId Player;
    public uint PlayerEntity;
    public Vector3 Spawn;
    public readonly List<(ushort Handler, byte[] Payload, uint Target)> PendingSnapshot = new();

    /// <summary>The entities this client has: those in its load window. It's sent events and snapshots for these only.</summary>
    public readonly HashSet<uint> Known = new();

    /// <summary>Entities being described for this client (they've just come into its window).</summary>
    public readonly HashSet<uint> Requested = new();
}

/// <summary>
/// The host's side of the session: single-player is a host with the transport off. Welcomes clients, sends each the
/// world as it is when they've loaded their terrain (every live entity, described), then spawns their player; relays
/// commands, events and body snapshots between clients; answers clock pings; and when a client leaves, saves and
/// despawns their player.
/// </summary>
public sealed class HostSession : NetSession
{
    private readonly Dictionary<ConnectionId, RemotePeer> _peers = new();
    private readonly EntitySet _describable;
    private readonly NetIdAllocator _ids;
    private readonly ulong _seed;
    private readonly ulong _checksum;
    private readonly Func<PlayerId, (byte[]? SavedSpawn, Vector3 Position)> _spawnFor;

    /// <param name="spawnFor">A joining player's saved SpawnPlayer command (if they've played this world before) and
    /// where they'll spawn.</param>
    public HostSession(ITransport? transport, EngineSession session, CommandSystem commands, NetRegistry registry, World world,
                       ITickClock clock, NetIdAllocator ids, ulong seed, ulong generationChecksum,
                       Func<PlayerId, (byte[]? SavedSpawn, Vector3 Position)> spawnFor)
        : base(transport, session, commands, registry, world, clock)
    {
        _ids = ids;
        _seed = seed;
        _checksum = generationChecksum;
        _spawnFor = spawnFor;
        _describable = world.GetEntities().With<NetId>().With<OwnPresence>().Without<Chunk>().AsSet();
        commands.Descriptions.Described += OnDescribed;
        commands.DescribedAll += OnDescribedAll;
    }

    public IReadOnlyCollection<RemotePeer> Peers => _peers.Values;

    /// <summary>A client has the entities within this distance of its player, and forgets them past
    /// <see cref="ForgetWindow"/>: the same load window entities stream by.</summary>
    public float LoadWindow { get; set; } = 1000f;
    public float ForgetWindow { get; set; } = 1100f;
    public long ChunksSent { get; private set; }
    public override bool OthersConnected => _peers.Values.Any(p => p.State != PeerState.Connected);

    /// <summary>Called when a leaving player's entity should go: describe for storage first if there's a save.</summary>
    public Action<Entity>? PlayerLeaving { get; set; }

    /// <summary>Every built-on terrain chunk, sent to a joining client so it fetches them rather than generate them.</summary>
    public Func<IEnumerable<ChunkPosition>>? EditedChunks { get; set; }

    /// <summary>A terrain chunk's current data (as loaded here, or from the save), for a client fetching it.</summary>
    public Func<ChunkPosition, byte[]?>? ChunkSource { get; set; }

    /// <summary>Which way a new player faces when they first spawn (yaw, pitch).</summary>
    public (float Yaw, float Pitch) NewPlayerLook { get; set; }

    public IEnumerable<RemotePeer> Joined => _peers.Values.Where(p => p.State == PeerState.Joined);

    internal RemotePeer? PeerById(PeerId id) => _peers.Values.FirstOrDefault(p => p.Peer == id);

    // ── connections ─────────────────────────────────────────────────────────

    protected override void OnConnected(ConnectionId connection) =>
        _peers[connection] = new RemotePeer { Connection = connection, Peer = PeerId.None, State = PeerState.Connected };

    protected override void OnDisconnected(ConnectionId connection, string reason)
    {
        if (!_peers.Remove(connection, out var peer) || peer.Peer == PeerId.None) return;
        Console.WriteLine($"[net] {peer.Name} ({peer.Peer}) left: {reason}");
        // Everything they owned is the host's now (their player, until it's despawned).
        if (Ownership is { } ownership) ownership.PeerLeft(peer.Peer);
        else
            foreach (var e in World.GetEntities().With<NetOwner>().AsEnumerable().ToList())
                if (e.Get<NetOwner>().Owner == peer.Peer) e.Set(Session.LocalOwner((ushort)(e.Get<NetOwner>().Epoch + 1)));
        if (peer.PlayerEntity != 0 && Registry.TryGet(peer.PlayerEntity, out var player))
        {
            if (PlayerLeaving is { } leaving) leaving(player);
            else Commands.Send(new DespawnEntity { Entity = peer.PlayerEntity, KeepStored = true });
        }
        Writer.Clear();
        new PlayerNotice(false, peer.Peer, peer.Name).Write(Writer);
        foreach (var other in Joined) Send(other.Connection);
    }

    private PeerId FreePeerId()
    {
        for (uint i = 2; i < 64; i++)
            if (_peers.Values.All(p => p.Peer.Value != i)) return new PeerId(i);
        return PeerId.None;
    }

    private void Refuse(ConnectionId connection, string reason)
    {
        Writer.Clear();
        new DisconnectMessage(reason).Write(Writer);
        Send(connection);
        Transport?.Disconnect(connection, reason);
    }

    // ── messages ────────────────────────────────────────────────────────────

    protected override void OnMessage(ConnectionId from, MessageKind kind, ref NetReader r, ReadOnlySpan<byte> packet, Channel channel)
    {
        if (!_peers.TryGetValue(from, out var peer)) return;
        switch (kind)
        {
            case MessageKind.Hello: OnHello(peer, Hello.Read(ref r)); break;
            case MessageKind.TerrainReady:
                if (peer.State == PeerState.LoadingTerrain) BeginSnapshot(peer);
                break;
            case MessageKind.TimePing:
            {
                var ping = TimePing.Read(ref r);
                Writer.Clear();
                new TimePong(ping.ClientTimeMs, Clock.Tick, Clock.Alpha).Write(Writer);
                Send(from, Channel.Unreliable);
                break;
            }
            case MessageKind.Command:
            {
                var h = CommandHeader.Read(ref r);
                if (h.To == Session.LocalPeer) Commands.ReceiveCommand(peer.Peer, h.Handler, h.Seq, r.ReadRaw(r.Remaining).ToArray());
                else if (PeerById(h.To) is { } target) Relay(target, h with { From = peer.Peer }, ref r);
                break;
            }
            case MessageKind.ForwardedCommand:
            {
                // A command that reached a client after its target left them: on to its authority now, from its origin.
                var h = CommandHeader.Read(ref r);
                if (h.To == Session.LocalPeer) Commands.ReceiveCommand(h.From, h.Handler, h.Seq, r.ReadRaw(r.Remaining).ToArray());
                else if (PeerById(h.To) is { } target) Relay(target, h, ref r);
                break;
            }
            case MessageKind.HandoverSnapshot:
                Ownership?.ReceiveHandover(peer.Peer, ref r);
                break;
            case MessageKind.Event:
            {
                // A client deciding something it owns (its own player): apply it here, and pass it on.
                var h = EventHeader.Read(ref r);
                var payload = r.ReadRaw(r.Remaining).ToArray();
                Commands.ReceiveEvent(h.Meta, h.Handler, payload);
                foreach (var other in Joined)
                    if (other != peer && Wants(other, h.Meta)) Send(other.Connection, packet);
                break;
            }
            case MessageKind.Rejection:
            {
                var rej = Rejection.Read(ref r);
                if (rej.To == Session.LocalPeer) Commands.ReceiveRejection(peer.Peer, rej.Seq);
                else if (PeerById(rej.To) is { } target) Send(target.Connection, packet);
                break;
            }
            case MessageKind.StateFrame:
                Bodies?.ReceiveFrame(peer.Peer, ref r);
                break;
            case MessageKind.IdBlockRequest:
            {
                var (first, count) = _ids.NextBlock();
                Writer.Clear();
                new IdBlockMessage(first, count).Write(Writer);
                Send(from);
                break;
            }
            case MessageKind.StateHash:
                Divergence?.ReceiveHash(ref r);
                break;
            case MessageKind.SnapshotRequest:
                Divergence?.ReceiveSnapshotRequest(peer.Peer, ref r);
                break;
            case MessageKind.ChunkRequest:
                // Answered at once, on the reliable channel: the data reflects every edit already relayed to them, and
                // every later edit arrives after it.
                var wanted = ChunkMessages.ReadRequest(ref r);
                Console.WriteLine($"[net] sending {wanted.Count} built-on chunk(s) to {peer.Name}");
                foreach (var pos in wanted)
                {
                    Writer.Clear();
                    ChunkMessages.WriteData(Writer, pos, ChunkSource?.Invoke(pos));
                    Send(from);
                    ChunksSent++;
                }
                break;
            case MessageKind.Disconnect:
                Transport?.Disconnect(from, DisconnectMessage.Read(ref r).Reason);
                break;
        }
    }

    private void Send(ConnectionId to, ReadOnlySpan<byte> packet, Channel channel = Channel.Reliable) => Transport?.Send(to, packet, channel);

    /// <summary>Sends a reliable packet to one client (joined or joining).</summary>
    internal void SendReliable(PeerId to, ReadOnlySpan<byte> packet)
    {
        if (PeerById(to) is { } peer) Send(peer.Connection, packet);
    }

    private void Relay(RemotePeer target, CommandHeader header, ref NetReader rest)
    {
        Writer.Clear();
        header.Write(Writer);
        Writer.WriteRaw(rest.ReadRaw(rest.Remaining));
        Send(target.Connection);
    }

    private void OnHello(RemotePeer peer, Hello hello)
    {
        if (peer.State != PeerState.Connected) return;
        if (hello.Version != ProtocolVersion.Current) { Refuse(peer.Connection, $"Version mismatch: host {ProtocolVersion.Current}, you {hello.Version}"); return; }
        if (hello.GenerationChecksum != _checksum) { Refuse(peer.Connection, "World generation differs from the host's (different game build?)"); return; }
        var id = FreePeerId();
        if (id == PeerId.None) { Refuse(peer.Connection, "The game is full"); return; }
        if (_peers.Values.Any(p => p.Player == hello.Player && p != peer)) { Refuse(peer.Connection, "That player is already in the game"); return; }

        peer.Peer = id;
        peer.Name = hello.Name;
        peer.Player = hello.Player;
        peer.State = PeerState.LoadingTerrain;
        peer.Spawn = _spawnFor(hello.Player).Position;
        var (first, count) = _ids.NextBlock();
        Writer.Clear();
        new Welcome(id, first, count, _seed, Clock.Tick, peer.Spawn, EditedChunks?.Invoke().ToArray() ?? Array.Empty<ChunkPosition>()).Write(Writer);
        Send(peer.Connection);
        Console.WriteLine($"[net] {hello.Name} joining as {id}");
    }

    /// <summary>The client has terrain: describe every live entity for them (sent when describing ends this tick).</summary>
    private void BeginSnapshot(RemotePeer peer)
    {
        peer.State = PeerState.Snapshot;
        foreach (var e in _describable.GetEntities().ToArray())
            if (InWindow(peer, e, LoadWindow)) DescribeRequest.Request(e, DescribePurpose.Send, PeerSet.Of(peer.Peer));
    }

    private void OnDescribed(Description d)
    {
        if ((d.Request.Purpose & DescribePurpose.Send) == 0) return;
        foreach (var peerId in d.Request.SendTo.Peers)
        {
            if (PeerById(peerId) is not { } peer) continue;
            if (peer.State == PeerState.Snapshot) peer.PendingSnapshot.Add((d.HandlerId, d.Payload, d.NetId));
            else if (peer.State == PeerState.Joined)
            {
                // Came into their window, or they asked for a resync: it goes to them as a spawn event.
                Writer.Clear();
                new EventHeader(d.HandlerId, Commands.StampEvent(d.NetId)).Write(Writer);
                Writer.WriteRaw(d.Payload);
                Send(peer.Connection);
                peer.Known.Add(d.NetId);
                Ownership?.SendEpoch(peer, d.Entity);
            }
            peer.Requested.Remove(d.NetId);
        }
    }

    // ── load windows ────────────────────────────────────────────────────────

    /// <summary>Where a client's load window is centred: its player (as this machine has it), or its spawn until then.</summary>
    private Vector3 WindowCentre(RemotePeer peer)
    {
        if (peer.PlayerEntity != 0 && Registry.TryGet(peer.PlayerEntity, out var p) && p.Has<Transform>())
        {
            var t = p.Get<Transform>().Position;
            return new Vector3(t.X, t.Y, t.Z);
        }
        return peer.Spawn;
    }

    /// <summary>Whether an entity is within <paramref name="range"/> of a client's window centre. One with no position (a
    /// global entity) is in every window.</summary>
    private bool InWindow(RemotePeer peer, Entity e, float range)
    {
        if (!e.Has<Transform>()) return true;
        var t = e.Get<Transform>().Position;
        return Vector3.Distance(new Vector3(t.X, t.Y, t.Z), WindowCentre(peer)) <= range;
    }

    /// <summary>Whether a client has an entity (and so gets its events and snapshots).</summary>
    public bool Knows(PeerId peer, uint entity) => PeerById(peer) is { } p && p.Known.Contains(entity);

    /// <summary>Last in each tick: entities coming into a client's window are described for it; ones leaving it are
    /// forgotten there.</summary>
    public override void Flush()
    {
        foreach (var peer in Joined)
        {
            foreach (ref readonly var e in _describable.GetEntities())
            {
                uint id = e.Get<NetId>().Value;
                if (id == peer.PlayerEntity) { peer.Known.Add(id); continue; }
                bool known = peer.Known.Contains(id);
                if (!known && !peer.Requested.Contains(id) && InWindow(peer, e, LoadWindow))
                {
                    peer.Requested.Add(id);
                    DescribeRequest.Request(e, DescribePurpose.Send, PeerSet.Of(peer.Peer));
                }
                else if (known && !InWindow(peer, e, ForgetWindow))
                {
                    peer.Known.Remove(id);
                    Writer.Clear();
                    Writer.WriteByte((byte)MessageKind.Forget);
                    Writer.WriteUInt32(id);
                    Send(peer.Connection);
                }
            }
            peer.Known.RemoveWhere(id => !Registry.IsLive(id));
        }
    }

    /// <summary>Whether an event for <paramref name="target"/> goes to <paramref name="peer"/>: everything about the
    /// terrain goes to everyone; anything else to clients that have it, including one it's just come into view of (a
    /// new spawn) and the client that sent the command.</summary>
    private bool Wants(RemotePeer peer, in EventMeta meta)
    {
        uint target = meta.Target;
        if (target == NetRegistry.WorldVolume) return true;
        if (peer.Known.Contains(target) || meta.Origin == peer.Peer) { peer.Known.Add(target); return true; }
        if (Registry.TryGet(target, out var e) && e.Has<OwnPresence>() && InWindow(peer, e, LoadWindow))
        {
            peer.Known.Add(target);
            return true;
        }
        return false;
    }

    /// <summary>Descriptions are out: send each snapshotting client theirs, then spawn their player for everyone.</summary>
    private void OnDescribedAll()
    {
        foreach (var peer in _peers.Values.Where(p => p.State == PeerState.Snapshot).ToList())
        {
            foreach (var (handler, payload, target) in peer.PendingSnapshot)
            {
                Writer.Clear();
                new EventHeader(handler, Commands.StampEvent(target)).Write(Writer);
                Writer.WriteRaw(payload);
                Send(peer.Connection);
            }
            foreach (var (_, _, target) in peer.PendingSnapshot)
            {
                peer.Known.Add(target);
                if (Ownership is { } ownership && Registry.TryGet(target, out var e) && e.Has<NetOwner>()) ownership.SendEpoch(peer, e);
            }
            peer.PendingSnapshot.Clear();
            peer.State = PeerState.Joined;

            var (saved, position) = _spawnFor(peer.Player);
            SpawnPlayer spawn;
            if (saved is not null)
            {
                var handler = (SpawnPlayerHandler)Commands.HandlerFor(CommandIds.SpawnPlayer)!;
                var reader = new NetReader(saved);
                spawn = handler.Read(ref reader);
                spawn.Owner = peer.Peer;
                spawn.Player.Name = peer.Name;
                if (Registry.IsLive(spawn.Id)) spawn.Id = 0; // somehow still here: give them a fresh entity
            }
            else
                spawn = new SpawnPlayer { Owner = peer.Peer, Player = new PlayerDescription
                    { Id = peer.Player, Name = peer.Name, FreeFly = true, Position = position, Yaw = NewPlayerLook.Yaw, Pitch = NewPlayerLook.Pitch } };
            if (spawn.Id == 0) spawn.Id = Registry.Allocate();
            peer.PlayerEntity = spawn.Id;
            Commands.Send(spawn);

            Writer.Clear();
            new PlayerNotice(true, peer.Peer, peer.Name).Write(Writer);
            foreach (var other in Joined) Send(other.Connection);
            Console.WriteLine($"[net] {peer.Name} ({peer.Peer}) joined");
        }
    }

    // ── routing ─────────────────────────────────────────────────────────────

    public override void SendCommand(PeerId authority, ushort handlerId, uint seq, ReadOnlySpan<byte> payload)
    {
        if (PeerById(authority) is not { } peer) return; // gone: the command lapses
        Writer.Clear();
        new CommandHeader(authority, Session.LocalPeer, handlerId, seq).Write(Writer);
        Writer.WriteRaw(payload);
        Send(peer.Connection);
    }

    public override void BroadcastEvent(ushort handlerId, in EventMeta meta, ReadOnlySpan<byte> payload)
    {
        Writer.Clear();
        new EventHeader(handlerId, meta).Write(Writer);
        Writer.WriteRaw(payload);
        foreach (var peer in Joined)
            if (Wants(peer, meta)) Send(peer.Connection);
        if (handlerId == CommandIds.DespawnEntity)
            foreach (var peer in _peers.Values) peer.Known.Remove(meta.Target);
    }

    public override void ForwardCommand(PeerId authority, PeerId origin, ushort handlerId, uint seq, ReadOnlySpan<byte> payload)
    {
        if (PeerById(authority) is not { } peer)
        {
            SendRejection(origin, seq); // nobody to decide it: the sender undoes its prediction
            return;
        }
        Writer.Clear();
        new CommandHeader(authority, origin, handlerId, seq).Write(Writer);
        Writer.WriteRaw(payload);
        Send(peer.Connection);
    }

    public override void SendRejection(PeerId to, uint seq)
    {
        if (PeerById(to) is not { } peer) return;
        Writer.Clear();
        new Rejection(to, Session.LocalPeer, seq).Write(Writer);
        Send(peer.Connection);
    }

    /// <summary>Sends an unreliable packet to one joined client (body snapshots).</summary>
    internal void SendUnreliable(PeerId to, ReadOnlySpan<byte> packet)
    {
        if (PeerById(to) is { State: PeerState.Joined } peer) Transport?.Send(peer.Connection, packet, Channel.Unreliable);
    }
}
