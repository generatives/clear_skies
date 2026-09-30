using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Serialization;
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
    public EntityId PlayerEntity;
    public Vector3 Spawn;
    public readonly List<(ushort Handler, byte[] Payload, EntityId Target)> PendingSnapshot = new();
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
    private readonly EntityIdAllocator _ids;
    private readonly ulong _seed;
    private readonly ulong _checksum;
    private readonly Func<string, PlayerId> _playerFor;
    private readonly Func<PlayerId, (byte[]? SavedSpawn, Vector3 Position)> _spawnFor;
    private readonly EntitySet _players;

    /// <param name="playerFor">The player a joining name is (the save gives each name a player ID).</param>
    /// <param name="spawnFor">A joining player's saved player spawn (if they've played this world before) and
    /// where they'll spawn.</param>
    public HostSession(ITransport? transport, EngineSession session, CommandSystem commands, EntityRegistry registry, World world,
                       ITickClock clock, EntityIdAllocator ids, ulong seed, ulong generationChecksum,
                       Func<string, PlayerId> playerFor, Func<PlayerId, (byte[]? SavedSpawn, Vector3 Position)> spawnFor)
        : base(transport, session, commands, registry, world, clock)
    {
        _ids = ids;
        _seed = seed;
        _checksum = generationChecksum;
        _playerFor = playerFor;
        _spawnFor = spawnFor;
        _players = world.GetEntities().With<Player>().AsSet();
        _describable = world.GetEntities().With<EntityId>().With<OwnPresence>().Without<Chunk>().AsSet();
        commands.Descriptions.Described += OnDescribed;
        commands.DescribedAll += OnDescribedAll;
    }

    public IReadOnlyCollection<RemotePeer> Peers => _peers.Values;
    public override bool OthersConnected => _peers.Values.Any(p => p.State != PeerState.Connected);

    /// <summary>Called when a leaving player's entity should go: describe for storage first if there's a save.</summary>
    public Action<Entity>? PlayerLeaving { get; set; }

    /// <summary>Which way a new player faces when they first spawn (yaw, pitch).</summary>
    public (float Yaw, float Pitch) NewPlayerLook { get; set; }

    public IEnumerable<RemotePeer> Joined => _peers.Values.Where(p => p.State == PeerState.Joined);

    private RemotePeer? PeerById(PeerId id) => _peers.Values.FirstOrDefault(p => p.Peer == id);

    // ── connections ─────────────────────────────────────────────────────────

    protected override void OnConnected(ConnectionId connection) =>
        _peers[connection] = new RemotePeer { Connection = connection, Peer = PeerId.None, State = PeerState.Connected };

    protected override void OnDisconnected(ConnectionId connection, string reason)
    {
        if (!_peers.Remove(connection, out var peer) || peer.Peer == PeerId.None) return;
        Console.WriteLine($"[net] {peer.Name} ({peer.Peer}) left: {reason}");
        // Everything they owned is the host's now (their player, until it's despawned).
        foreach (var e in World.GetEntities().With<NetOwner>().AsEnumerable().ToList())
            if (e.Get<NetOwner>().Owner == peer.Peer) e.Set(Session.LocalOwner((ushort)(e.Get<NetOwner>().Epoch + 1)));
        if (!peer.PlayerEntity.IsNone && Registry.TryGet(peer.PlayerEntity, out var player))
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
            case MessageKind.Event:
            {
                // A client deciding something it owns (its own player): apply it here, and pass it on.
                var h = EventHeader.Read(ref r);
                var payload = r.ReadRaw(r.Remaining).ToArray();
                Commands.ReceiveEvent(h.Meta, h.Handler, payload);
                foreach (var other in Joined)
                    if (other != peer) Send(other.Connection, packet);
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
                if (peer.State != PeerState.Joined) break;
                Bodies?.ReceiveFrame(ref r);
                // Clients only hear each other through the host: passed on straight away, as it came (with the tick
                // it was taken on).
                foreach (var other in Joined)
                    if (other != peer) Transport?.Send(other.Connection, packet, Channel.Unreliable);
                break;
            case MessageKind.IdBlockRequest:
            {
                var (first, count) = _ids.NextBlock();
                Writer.Clear();
                new IdBlockMessage(first, count).Write(Writer);
                Send(from);
                break;
            }
            case MessageKind.Disconnect:
                Transport?.Disconnect(from, DisconnectMessage.Read(ref r).Reason);
                break;
        }
    }

    private void Send(ConnectionId to, ReadOnlySpan<byte> packet, Channel channel = Channel.Reliable) => Transport?.Send(to, packet, channel);

    private void Relay(RemotePeer target, CommandHeader header, ref NetReader rest)
    {
        Writer.Clear();
        header.Write(Writer);
        Writer.WriteRaw(rest.ReadRaw(rest.Remaining));
        Send(target.Connection);
    }

    /// <summary>A player already here: the host's own, or a joined client's.</summary>
    private bool InGame(PlayerId player)
    {
        foreach (ref readonly var e in _players.GetEntities())
            if (e.Get<Player>().Id == player) return true;
        return false;
    }

    private void OnHello(RemotePeer peer, Hello hello)
    {
        if (peer.State != PeerState.Connected)
        {
            Console.WriteLine($"[net] ignoring a second hello from {peer.Name} ({peer.Peer}), already {peer.State}");
            return;
        }
        if (hello.Version != ProtocolVersion.Current) { Refuse(peer.Connection, $"Version mismatch: host {ProtocolVersion.Current}, you {hello.Version}"); return; }
        if (hello.GenerationChecksum != _checksum) { Refuse(peer.Connection, "World generation differs from the host's (different game build?)"); return; }
        var id = FreePeerId();
        if (id == PeerId.None) { Refuse(peer.Connection, "The game is full"); return; }
        string name = hello.Name.Trim();
        if (name.Length == 0) { Refuse(peer.Connection, "A player name is needed"); return; }
        var player = _playerFor(name);
        if (_peers.Values.Any(p => p.Player == player && p != peer) || InGame(player))
        {
            Refuse(peer.Connection, $"{name} is already in the game");
            return;
        }

        peer.Peer = id;
        peer.Name = name;
        peer.Player = player;
        peer.State = PeerState.LoadingTerrain;
        peer.Spawn = _spawnFor(player).Position;
        var (first, count) = _ids.NextBlock();
        Writer.Clear();
        new Welcome(id, first, count, _seed, Clock.Tick, peer.Spawn).Write(Writer);
        Send(peer.Connection);
        Console.WriteLine($"[net] {name} joining as {id}");
    }

    /// <summary>The client has terrain: describe every live entity for them (sent when describing ends this tick).</summary>
    private void BeginSnapshot(RemotePeer peer)
    {
        peer.State = PeerState.Snapshot;
        foreach (var e in _describable.GetEntities().ToArray())
            DescribeRequest.Request(e, DescribePurpose.Send, PeerSet.Of(peer.Peer));
    }

    private void OnDescribed(Description d)
    {
        if ((d.Request.Purpose & DescribePurpose.Send) == 0) return;
        foreach (var peerId in d.Request.SendTo.Peers)
            if (PeerById(peerId) is { } peer) peer.PendingSnapshot.Add((d.HandlerId, d.Payload, d.Id));
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
            peer.PendingSnapshot.Clear();
            peer.State = PeerState.Joined;

            var (saved, position) = _spawnFor(peer.Player);
            Spawn<PlayerDescription> spawn;
            if (saved is not null)
            {
                var handler = (SpawnPlayerHandler)Commands.HandlerFor(CommandIds.SpawnPlayer)!;
                var reader = new NetReader(saved);
                spawn = handler.Read(ref reader);
                spawn.Owner = peer.Peer;
                spawn.Description.Name = peer.Name;
                if (Registry.IsLive(spawn.Id)) spawn.Id = EntityId.None; // somehow still here: give them a fresh entity
            }
            else
                spawn = new Spawn<PlayerDescription> { Owner = peer.Peer, Description = new PlayerDescription
                    { Id = peer.Player, Name = peer.Name, FreeFly = true, Position = position, Yaw = NewPlayerLook.Yaw, Pitch = NewPlayerLook.Pitch } };
            if (spawn.Id.IsNone) spawn.Id = Registry.Allocate();
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
        foreach (var peer in Joined) Send(peer.Connection);
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
