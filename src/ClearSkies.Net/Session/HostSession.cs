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
}

/// <summary>
/// The host's side of the session: single-player is a host with the transport off. Welcomes clients; spawns every
/// player, its own and joining clients', once what they need is here (<see cref="PendingSpawns"/>: their ship, and
/// the terrain around them, loaded here for its own and by the client for theirs), first sending a client the world as
/// it is then (every live entity, described); relays
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
    private readonly IPlayerDirectory _directory;
    private readonly EntitySet _players;
    private readonly PendingSpawns _spawns;

    /// <param name="terrainReady">Whether the terrain around a point has loaded here, with colliders: the host's own
    /// player waits for it (none: it doesn't wait).</param>
    public HostSession(ITransport? transport, EngineSession session, CommandSystem commands, EntityRegistry registry, World world,
                       ITickClock clock, EntityIdAllocator ids, ulong seed, ulong generationChecksum, IPlayerDirectory directory,
                       Func<Vector3, bool>? terrainReady = null)
        : base(transport, session, commands, registry, world, clock)
    {
        _spawns = new PendingSpawns(world, registry, terrainReady);
        _ids = ids;
        _seed = seed;
        _checksum = generationChecksum;
        _directory = directory;
        _players = world.GetEntities().With<Player>().AsSet();
        _describable = world.GetEntities().With<EntityId>().With<OwnPresence>().Without<Chunk>().AsSet();
    }

    public IReadOnlyCollection<RemotePeer> Peers => _peers.Values;
    public override bool OthersConnected => _peers.Values.Any(p => p.State != PeerState.Connected);

    public IEnumerable<RemotePeer> Joined => _peers.Values.Where(p => p.State == PeerState.Joined);
    private IEnumerable<ConnectionId> JoinedConnections => Joined.Select(p => p.Connection);

    private RemotePeer? PeerById(PeerId id) => _peers.Values.FirstOrDefault(p => p.Peer == id);

    /// <summary>Players waiting to spawn.</summary>
    public int PendingSpawns => _spawns.Count;

    /// <summary>Everything that arrived, then whoever is ready spawns: still first in the tick, so a joining client's
    /// world is described as the last tick left it (see <see cref="SendWorld"/>).</summary>
    public override void Update(float dt)
    {
        base.Update(dt);
        _spawns.Update();
    }

    /// <summary>Spawns the host's own player once the world they need has loaded here (their ship, and the terrain
    /// around them with colliders).</summary>
    public void SpawnWhenReady(PlayerDescription description) =>
        _spawns.Add(description, Session.LocalPeer, local: true,
                    d => Commands.Send(new Spawn<PlayerDescription> { Description = d }));

    // ── connections ─────────────────────────────────────────────────────────

    protected override void OnConnected(ConnectionId connection) =>
        _peers[connection] = new RemotePeer { Connection = connection, Peer = PeerId.None, State = PeerState.Connected };

    protected override void OnDisconnected(ConnectionId connection, string reason)
    {
        if (!_peers.Remove(connection, out var peer) || peer.Peer == PeerId.None) return;
        _spawns.Cancel(peer.Peer);
        Console.WriteLine($"[net] {peer.Name} ({peer.Peer}) left: {reason}");
        // Everything they owned is the host's now (their player, until it's despawned).
        foreach (var e in World.GetEntities().With<NetOwner>().AsEnumerable().ToList())
            if (e.Get<NetOwner>().Owner == peer.Peer) e.Set(Session.LocalOwner((ushort)(e.Get<NetOwner>().Epoch + 1)));
        if (!peer.PlayerEntity.IsNone && Registry.TryGet(peer.PlayerEntity, out var player))
        {
            _directory.Leaving(player);
            Commands.Send(new DespawnEntity { Entity = peer.PlayerEntity, KeepStored = true });
        }
        Send(JoinedConnections, new PlayerNotice(false, peer.Peer, peer.Name));
    }

    private PeerId FreePeerId()
    {
        for (uint i = 2; i < 64; i++)
            if (_peers.Values.All(p => p.Peer.Value != i)) return new PeerId(i);
        return PeerId.None;
    }

    private void Refuse(ConnectionId connection, string reason)
    {
        Send(connection, new DisconnectMessage(reason));
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
                if (peer.State == PeerState.LoadingTerrain) _spawns.ClientReady(peer.Peer);
                break;
            case MessageKind.TimePing:
            {
                var ping = TimePing.Read(ref r);
                Send(from, new TimePong(ping.ClientTimeMs, Clock.Tick, Clock.Alpha), Channel.Unreliable);
                break;
            }
            case MessageKind.Command:
            {
                var m = CommandMessage.Read(ref r);
                if (m.To == Session.LocalPeer) Commands.ReceiveCommand(peer.Peer, m.Handler, m.Seq, m.Payload.ToArray());
                else if (PeerById(m.To) is { } target) Send(target.Connection, m.WithFrom(peer.Peer)); // from whoever sent it, whatever it says
                break;
            }
            case MessageKind.Event:
            {
                // A client deciding something it owns (its own player): apply it here, and pass it on.
                var m = EventMessage.Read(ref r);
                Commands.ReceiveEvent(m.Meta, m.Handler, m.Payload.ToArray());
                foreach (var other in Joined)
                    if (other != peer) Forward(other.Connection, packet);
                break;
            }
            case MessageKind.Rejection:
            {
                var rej = Rejection.Read(ref r);
                if (rej.To == Session.LocalPeer) Commands.ReceiveRejection(peer.Peer, rej.Seq);
                else if (PeerById(rej.To) is { } target) Forward(target.Connection, packet);
                break;
            }
            case MessageKind.StateFrame:
                if (peer.State != PeerState.Joined) break;
                Bodies?.ReceiveFrame(ref r);
                // Clients only hear each other through the host: passed on straight away, as it came (with the tick
                // it was taken on).
                foreach (var other in Joined)
                    if (other != peer) Forward(other.Connection, packet, Channel.Unreliable);
                break;
            case MessageKind.IdBlockRequest:
            {
                var (first, count) = _ids.NextBlock();
                Send(from, new IdBlockMessage(first, count));
                break;
            }
            case MessageKind.Disconnect:
                Transport?.Disconnect(from, DisconnectMessage.Read(ref r).Reason);
                break;
        }
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
        var player = _directory.PlayerFor(name);
        if (_peers.Values.Any(p => p.Player == player && p != peer) || InGame(player) || _spawns.IsWaiting(player))
        {
            Refuse(peer.Connection, $"{name} is already in the game");
            return;
        }

        peer.Peer = id;
        peer.Name = name;
        peer.Player = player;
        peer.State = PeerState.LoadingTerrain;

        // Where they left off if they've played this world before (on their ship as it is now, if it's here), else new
        // at the spawn point. The client loads its terrain there; they spawn once it has, and their ship has loaded.
        var spawn = _directory.NewPlayerSpawn;
        var description = _directory.Saved(player) ?? new PlayerDescription
            { Id = player, FreeFly = true, Position = spawn.Position, Yaw = spawn.Yaw, Pitch = spawn.Pitch };
        description.Name = name;
        peer.Spawn = PlayerFactory.WorldPosition(description, Registry);
        _spawns.Add(description, id, local: false, d => SendWorld(peer, d));
        var (first, count) = _ids.NextBlock();
        Send(peer.Connection, new Welcome(id, first, count, _seed, Clock.Tick, peer.Spawn));
        Console.WriteLine($"[net] {name} joining as {id}");
    }

    /// <summary>
    /// The client has terrain, and their ship is here: send them the world as it is now, then spawn their player for
    /// everyone. Runs first in the tick (in the session's update), so every change of the last tick is in the descriptions, and every later event
    /// goes out after them on the same ordered channel: nothing is missed or applied twice. Each is numbered after
    /// every event already sent for its entity (<see cref="CommandSystem.StampEvent"/>).
    /// </summary>
    private void SendWorld(RemotePeer peer, PlayerDescription description)
    {
        foreach (var d in Commands.Describe(_describable.GetEntities().ToArray()))
        {
            var owner = d.Entity.Has<NetOwner>() ? d.Entity.Get<NetOwner>().Owner : Session.LocalPeer;
            Send([peer.Connection], new EventMessage(d.Kind, Commands.StampEvent(d.Id), Commands.SpawnCommand(d.Kind, d.Id, owner, d.Data)));
        }
        peer.State = PeerState.Joined;

        peer.PlayerEntity = Registry.Allocate();
        Commands.Send(new Spawn<PlayerDescription> { Id = peer.PlayerEntity, Owner = peer.Peer, Description = description });

        Send(JoinedConnections, new PlayerNotice(true, peer.Peer, peer.Name));
        Console.WriteLine($"[net] {peer.Name} ({peer.Peer}) joined");
    }

    // ── routing ─────────────────────────────────────────────────────────────

    public override void SendCommand(PeerId authority, ushort handlerId, uint seq, ReadOnlySpan<byte> payload)
    {
        if (PeerById(authority) is not { } peer) return; // gone: the command lapses
        Send(peer.Connection, new CommandMessage(authority, Session.LocalPeer, handlerId, seq, payload));
    }

    public override void BroadcastEvent(ushort handlerId, in EventMeta meta, ReadOnlySpan<byte> payload)
    {
        Send(JoinedConnections, new EventMessage(handlerId, meta, payload));
    }

    public override void SendRejection(PeerId to, uint seq)
    {
        if (PeerById(to) is not { } peer) return;
        Send(peer.Connection, new Rejection(to, Session.LocalPeer, seq));
    }

    /// <summary>Sends an unreliable packet to one joined client (body snapshots).</summary>
    internal void SendUnreliable(PeerId to, ReadOnlySpan<byte> packet)
    {
        if (PeerById(to) is { State: PeerState.Joined } peer) Forward(peer.Connection, packet, Channel.Unreliable);
    }
}
