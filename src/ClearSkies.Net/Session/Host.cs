using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Serialization;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Transport;

namespace ClearSkies.Net.Session;

/// <summary>A Participant, as the Host sees it.</summary>
public sealed class HostPeer
{
    public ConnectionId Connection;
    /// <summary>None until welcomed; <see cref="PeerId.Host"/> for the hosting machine's (the authority).</summary>
    public PeerId Peer = PeerId.None;
    public string Name = "";
    public PlayerId Player;
    public EntityId PlayerEntity;

    /// <summary>The space it wants to see (see <see cref="ViewVolumeMessage"/>).</summary>
    public Vector3 ViewCentre;
    public float ViewRadius;

    /// <summary>The entities it has (it's sent events and snapshots for these only), and those being described for it.</summary>
    public readonly HashSet<EntityId> Known = new();
    public readonly HashSet<EntityId> Requested = new();

    /// <summary>Its Participant is up (its first View Volume has come): nothing is streamed to it before, as nothing
    /// sent before then would reach it.</summary>
    public bool Ready;

    public bool Welcomed => Peer != PeerId.None;
    public bool IsAuthority => Peer == PeerId.Host;
}

/// <summary>An entity as the Host keeps it: one the authority has, or a player about to spawn there. Everything but its kind,
/// where it is and its last event number is its Description, which only the authority reads; the Host reads a player's,
/// for where they stand on their ship.</summary>
public sealed class HostEntity
{
    public required EntityId Id;
    public ushort Kind;
    /// <summary>Its latest Description.</summary>
    public byte[]? Data;
    /// <summary>The authority's last event the Description includes.</summary>
    public uint EventNumber;
    /// <summary>Where it is (null: a global entity) and which way it faces, as last described or
    /// snapshotted.</summary>
    public Vector3? Position;
    public Quaternion Rotation = Quaternion.Identity;
    /// <summary>A player standing on a ship: the ship, and where on it (its block space).</summary>
    public EntityId Support;
    public Vector3 LocalPosition;
    /// <summary>A player: who plays them (None for anything else), and who they are.</summary>
    public PeerId ControllingPeer;
    public PlayerId Player;
    public string Name = "";
    /// <summary>Spawned on the authority (and so maybe elsewhere).</summary>
    public bool Loaded;
    /// <summary>Released, waiting for its last Description.</summary>
    public bool Releasing;
    /// <summary>A player whose machine left: they go once released.</summary>
    public bool Leaving;
    /// <summary>The tick <see cref="Data"/> was last described (or read from the save, for a player): within that
    /// tick it's still current, so it can be sent as it is.</summary>
    public uint DescribedTick = uint.MaxValue;

    public bool IsPlayer => ControllingPeer != PeerId.None;
}

/// <summary>
/// The Host: coordinates, and simulates nothing (it has no ECS world). Participants connect to it, the hosting machine's
/// over an in-process link (it has authority over every entity, so far), everyone else's over the network; it relays
/// every message between them, as none talk to each other. It keeps a record of every entity the authority has
/// (<see cref="HostEntity"/>), as the authority announces them (made, loaded from the save, despawned), and from those:
/// <list type="bullet">
/// <item>streams to each Participant every entity (a fresh Description, asked of the authority) and has it forget what's
/// released; events and snapshots go only to Participants that have their entity;</item>
/// <item>lets players in (spawning their Character where they left off, on their ship as it is now) and out (released:
/// the authority describes them a last time, and the Host writes that to the save).</item>
/// </list>
/// The authority's world loads, unloads and saves everything else itself.
/// </summary>
public sealed class Host : ISystem, IDisposable
{
    /// <summary>How far a Participant sees entities (its View Volume's radius).</summary>
    public const float ViewRadius = 1000f;

    private readonly HostTransport _transport;
    private readonly SaveDatabase _db;
    private readonly EntityIdAllocator _ids;
    private readonly ulong _seed, _checksum;
    private readonly ITickClock _clock;
    private readonly Dictionary<ConnectionId, HostPeer> _peers = new();
    private readonly Dictionary<EntityId, HostEntity> _entities = new();
    private readonly NetWriter _writer = new(1024);
    private readonly List<BodySnapshot> _snapshots = new();
    private readonly List<BodySnapshot> _frame = new();
    private readonly List<EntityId> _scratch = new();
    private uint _idNext, _idEnd;

    /// <param name="newPlayerSpawn">Where a new player's Character spawns (its capsule's centre), and which way they face.</param>
    public Host(HostTransport transport, SaveDatabase db, ITickClock clock, ulong seed, ulong generationChecksum,
                (Vector3 Position, float Yaw, float Pitch) newPlayerSpawn)
    {
        _transport = transport;
        _db = db;
        _clock = clock;
        _seed = seed;
        _checksum = generationChecksum;
        NewPlayerSpawn = newPlayerSpawn;
        // Entity IDs come in blocks from the save's next free ID, so they never repeat across sessions.
        _ids = new EntityIdAllocator(db.NextFreeId);
        transport.Connected += OnConnected;
        transport.Disconnected += OnDisconnected;
        transport.Received += OnReceived;
    }

    public (Vector3 Position, float Yaw, float Pitch) NewPlayerSpawn { get; }

    /// <summary>Where entity IDs come from, so the save can record the next free one.</summary>
    public EntityIdAllocator Ids => _ids;

    public IReadOnlyCollection<HostPeer> Peers => _peers.Values;
    public IEnumerable<HostPeer> Welcomed => _peers.Values.Where(p => p.Welcomed);
    public IReadOnlyDictionary<EntityId, HostEntity> Entities => _entities;
    public HostTransport Transport => _transport;

    /// <summary>The hosting machine's Participant, which has authority over every entity.</summary>
    public HostPeer? Authority => _peers.Values.FirstOrDefault(p => p.IsAuthority && p.Ready);

    /// <summary>Whether anyone but the hosting machine is in.</summary>
    public bool OthersConnected => _peers.Values.Any(p => p.Welcomed && !p.IsAuthority);

    /// <summary>Players released this session (as they left).</summary>
    public long Releases { get; private set; }

    private HostPeer? PeerById(PeerId id) => _peers.Values.FirstOrDefault(p => p.Peer == id);

    /// <summary>Once a tick, first: everything that arrived, then what each Participant is owed.</summary>
    public void Update(float dt)
    {
        _transport.Poll();
        Stream();
    }

    /// <summary>Last in the hosting machine's tick: passes on at once what its Participant sent this tick (its events and
    /// snapshots), rather than a tick later.</summary>
    public ISystem Relay => _relay ??= new LambdaSystem(_transport.Poll);
    private ISystem? _relay;

    // ── connections ─────────────────────────────────────────────────────────

    private void OnConnected(ConnectionId connection) => _peers[connection] = new HostPeer { Connection = connection };

    private void OnDisconnected(ConnectionId connection, string reason)
    {
        if (!_peers.Remove(connection, out var peer) || !peer.Welcomed) return;
        Console.WriteLine($"[net] {peer.Name} ({peer.Peer}) left: {reason}");
        // Their Character is released: its last Description is where they rejoin.
        if (_entities.TryGetValue(peer.PlayerEntity, out var player))
        {
            player.Leaving = true;
            if (player.Loaded) Release(player);
            else _entities.Remove(player.Id);
        }
        Send(Others(peer), new PlayerNotice(false, peer.Peer, peer.Name));
    }

    private void Refuse(ConnectionId connection, string reason)
    {
        Send(connection, new DisconnectMessage(reason));
        _transport.Disconnect(connection, reason);
    }

    private PeerId FreePeerId()
    {
        for (uint i = 2; i < 64; i++)
            if (_peers.Values.All(p => p.Peer.Value != i)) return new PeerId(i);
        return PeerId.None;
    }

    private EntityId AllocateId()
    {
        if (_idNext == _idEnd)
        {
            var (first, count) = _ids.NextBlock();
            (_idNext, _idEnd) = (first, first + count);
        }
        return new EntityId(_idNext++);
    }

    private void OnHello(HostPeer peer, Hello hello)
    {
        if (peer.Welcomed) { Console.WriteLine($"[net] ignoring a second hello from {peer.Name} ({peer.Peer})"); return; }
        if (hello.Version != ProtocolVersion.Current) { Refuse(peer.Connection, $"Version mismatch: host {ProtocolVersion.Current}, you {hello.Version}"); return; }
        if (hello.GenerationChecksum != _checksum) { Refuse(peer.Connection, "World generation differs from the host's (different game build?)"); return; }
        // The hosting machine's Participant is the authority: known by the link it came on, never by anything it says.
        var id = HostTransport.IsLocal(peer.Connection) && _peers.Values.All(p => !p.IsAuthority) ? PeerId.Host : FreePeerId();
        if (id == PeerId.None) { Refuse(peer.Connection, "The game is full"); return; }
        string name = hello.Name.Trim();
        if (name.Length == 0 && id == PeerId.Host)
        {
            // The hosting machine with nobody playing there (a dedicated host): the authority, with no view of its own.
            peer.Peer = id;
            peer.Name = "host";
            var (idFirst, idCount) = _ids.NextBlock();
            Send(peer.Connection, new Welcome(id, idFirst, idCount, _seed, (uint)_clock.Now, default));
            return;
        }
        if (name.Length == 0) { Refuse(peer.Connection, "A player name is needed"); return; }
        var player = _db.PlayerFor(name);
        if (_peers.Values.Any(p => p.Player == player && p != peer) || _entities.Values.Any(e => e.Player == player && e.IsPlayer))
        {
            Refuse(peer.Connection, $"{name} is already in the game");
            return;
        }

        // Where they left off if they've played this world before (on their ship as it is now), else new at the spawn.
        var description = _db.ReadPlayer(player) is { } saved
            ? DescriptionBytes.Read<PlayerDescription>(saved)
            : new PlayerDescription { Id = player, FreeFly = true, Position = NewPlayerSpawn.Position, Yaw = NewPlayerSpawn.Yaw, Pitch = NewPlayerSpawn.Pitch };
        description.Id = player;
        description.Name = name;
        var character = new HostEntity
        {
            Id = AllocateId(), Kind = CommandIds.SpawnPlayer, Data = DescriptionBytes.Of(description), Position = description.Position,
            Support = description.Support, LocalPosition = description.LocalPosition, ControllingPeer = id, Player = player, Name = name,
        };
        _entities[character.Id] = character;

        peer.Peer = id;
        peer.Name = name;
        peer.Player = player;
        peer.PlayerEntity = character.Id;
        peer.ViewCentre = WorldPosition(character);
        peer.ViewRadius = ViewRadius;
        var (first, count) = _ids.NextBlock();
        Send(peer.Connection, new Welcome(id, first, count, _seed, (uint)_clock.Now, peer.ViewCentre));
        Send(Others(peer), new PlayerNotice(true, id, name));
        Console.WriteLine($"[net] {name} joining as {id}");
    }

    // ── messages ────────────────────────────────────────────────────────────

    private void OnReceived(ConnectionId from, ReadOnlySpan<byte> packet, Channel channel)
    {
        if (packet.Length == 0 || !_peers.TryGetValue(from, out var peer)) return;
        var r = new NetReader(packet);
        var kind = (MessageKind)r.ReadByte();
        try
        {
            if (kind == MessageKind.Hello) OnHello(peer, Hello.Read(ref r));
            else if (peer.Welcomed) OnMessage(peer, kind, ref r, packet);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            Console.WriteLine($"[net] bad {kind} from {peer.Name}: {e.Message}");
        }
    }

    private void OnMessage(HostPeer peer, MessageKind kind, ref NetReader r, ReadOnlySpan<byte> packet)
    {
        switch (kind)
        {
            case MessageKind.TimePing:
            {
                double now = _clock.Now; // not Tick: on a frame running several ticks, that's behind real time
                Send(peer.Connection, new TimePong(TimePing.Read(ref r).ClientTimeMs, (uint)now, (float)(now - Math.Floor(now))), Channel.Unreliable);
                break;
            }
            case MessageKind.IdBlockRequest:
            {
                var (first, count) = _ids.NextBlock();
                Send(peer.Connection, new IdBlockMessage(first, count));
                break;
            }
            case MessageKind.ViewVolume:
            {
                var view = ViewVolumeMessage.Read(ref r);
                (peer.ViewCentre, peer.ViewRadius) = (view.Centre, MathF.Min(view.Radius, ViewRadius));
                peer.Ready = true;
                break;
            }
            case MessageKind.Command:
            {
                // To its authority, from whoever sent it, whatever it says.
                var m = CommandMessage.Read(ref r);
                if (PeerById(m.To) is { } to) SendCommand(to.Connection, m.WithFrom(peer.Peer));
                break;
            }
            case MessageKind.Rejection:
                if (PeerById(Rejection.Read(ref r).To) is { } rejected) _transport.Send(rejected.Connection, packet, Channel.Reliable);
                break;
            case MessageKind.Event:
                OnEvent(peer, EventMessage.Read(ref r).Meta, packet);
                break;
            case MessageKind.PlayerInput:
                // Only from the machine that plays them, on to their authority.
                if (PlayerInputMessage.Read(ref r).Player == peer.PlayerEntity && Authority is { } authority)
                    _transport.Send(authority.Connection, packet, Channel.Unreliable);
                break;
            case MessageKind.StateFrame:
                OnFrame(peer, ref r);
                break;
            case MessageKind.Described when peer.IsAuthority:
                OnDescribed(DescribedMessage.Read(ref r));
                break;
            case MessageKind.Deleted when peer.IsAuthority:
                OnDeleted(EntityMessage.Read(kind, ref r).Id);
                break;
            case MessageKind.Disconnect:
                _transport.Disconnect(peer.Connection, DisconnectMessage.Read(ref r).Reason);
                break;
        }
    }

    /// <summary>An event goes to everyone who has its entity, and back to whoever sent the command (to settle its
    /// prediction); nothing for an entity released or being released (its last Description has it). One for something
    /// the Host doesn't keep (the terrain) goes to everyone.</summary>
    private void OnEvent(HostPeer from, in EventMeta meta, ReadOnlySpan<byte> packet)
    {
        if (_entities.TryGetValue(meta.Target, out var entity) && (!entity.Loaded || entity.Releasing)) return;
        foreach (var p in Welcomed)
            if (p != from && (entity is null || p.Known.Contains(meta.Target) || p.Peer == meta.Origin))
                _transport.Send(p.Connection, packet, Channel.Reliable);
    }

    /// <summary>The authority's snapshots: where its entities are now, then each Participant's share, as frames of
    /// their own.</summary>
    private void OnFrame(HostPeer from, ref NetReader r)
    {
        uint tick = r.ReadUInt32();
        int count = r.ReadUInt16();
        _snapshots.Clear();
        for (int i = 0; i < count; i++)
        {
            var s = BodySnapshot.Read(ref r);
            if (!_entities.TryGetValue(s.Entity, out var e) || !e.Loaded) continue;
            _snapshots.Add(s);
            if (e.IsPlayer)
            {
                e.Support = s.Support;
                if (s.Support.IsNone) e.Position = s.Position;
                else e.LocalPosition = s.Position;
            }
            else if (e.Position is not null) (e.Position, e.Rotation) = (s.Position, s.Rotation);
        }
        foreach (var p in Welcomed)
        {
            if (p == from) continue;
            _frame.Clear();
            foreach (var s in _snapshots) if (p.Known.Contains(s.Entity)) _frame.Add(s);
            BodySync.WriteFrames(_writer, tick, _frame, packet => _transport.Send(p.Connection, packet, Channel.Unreliable));
        }
    }

    // ── descriptions ────────────────────────────────────────────────────────

    private void OnDescribed(in DescribedMessage m)
    {
        if (!_entities.TryGetValue(m.Id, out var e))
        {
            if (m.Reason != DescribedReason.Created) return; // forgotten meanwhile (deleted)
            _entities[m.Id] = e = new HostEntity { Id = m.Id, Loaded = true };
            Authority?.Known.Add(m.Id);
        }
        e.Kind = m.Kind;
        e.Data = m.Data.ToArray();
        e.EventNumber = m.EventNumber;
        e.DescribedTick = _clock.Tick;
        if (e.IsPlayer)
        {
            var d = DescriptionBytes.Read<PlayerDescription>(m.Data);
            (e.Support, e.LocalPosition) = d.FreeFly ? (EntityId.None, default) : (d.Support, d.LocalPosition);
            e.Position = d.Position;
        }
        else if (e.Position is not null || m.Reason == DescribedReason.Created) e.Position = m.Position; // a global one stays global

        switch (m.Reason)
        {
            case DescribedReason.Requested:
                foreach (var p in Welcomed)
                    if (p.Requested.Remove(e.Id) && e.Loaded && !e.Releasing) SendSpawn(p, e);
                break;
            case DescribedReason.Released:
                OnReleased(e);
                break;
        }
    }

    private void OnReleased(HostEntity e)
    {
        e.Loaded = e.Releasing = false;
        e.DescribedTick = _clock.Tick;
        _db.InTransaction(() => Write(e, e.Data!, e.Position));
        foreach (var p in Welcomed)
        {
            p.Requested.Remove(e.Id);
            if (p.Known.Remove(e.Id) && !p.IsAuthority) Send(p.Connection, new EntityMessage(MessageKind.Forget, e.Id));
        }
        if (e.IsPlayer) _entities.Remove(e.Id); // a player's Character exists only while they play
        Releases++;
    }

    private void OnDeleted(EntityId id)
    {
        if (!_entities.Remove(id)) return;
        foreach (var p in _peers.Values) { p.Known.Remove(id); p.Requested.Remove(id); }
    }

    // ── streaming ───────────────────────────────────────────────────────────

    /// <summary>Where an entity is in the world: a player on their ship as the ship is now.</summary>
    public Vector3 WorldPosition(HostEntity e)
    {
        if (e.IsPlayer && !e.Support.IsNone && _entities.TryGetValue(e.Support, out var ship) && ship.Position is { } on)
            return on + Vector3.Transform(e.LocalPosition, ship.Rotation);
        return e.Position ?? Vector3.Zero;
    }

    /// <summary>
    /// Each tick: players not yet spawned are (on the authority, from their Description), and each Participant is sent
    /// what it doesn't have yet (a fresh Description, asked of the authority unless it's already this tick's). Nothing
    /// happens before the authority is here.
    /// </summary>
    private void Stream()
    {
        if (Authority is not { } authority) return;

        foreach (var e in _entities.Values)
        {
            if (e.Loaded || !e.IsPlayer) continue;
            e.Loaded = true;
            e.DescribedTick = _clock.Tick; // the save's, and nothing newer exists anywhere
            SendSpawn(authority, e);
        }

        foreach (var p in Welcomed)
        {
            if (p.IsAuthority || !p.Ready) continue;
            _scratch.Clear();
            foreach (var e in _entities.Values)
            {
                if (p.Known.Contains(e.Id) || !e.Loaded || e.Releasing || p.Requested.Contains(e.Id)) continue;
                if (e.DescribedTick == _clock.Tick) SendSpawn(p, e);
                else
                {
                    p.Requested.Add(e.Id);
                    _scratch.Add(e.Id);
                }
            }
            foreach (var id in _scratch) Send(authority.Connection, new EntityMessage(MessageKind.DescribeRequest, id));
        }
    }

    /// <summary>Spawns <paramref name="e"/> on <paramref name="peer"/> from its Description: owned by the authority, and
    /// a player played by their machine.</summary>
    private void SendSpawn(HostPeer peer, HostEntity e)
    {
        var owner = e.IsPlayer ? e.ControllingPeer : PeerId.Host;
        _writer.Clear();
        new SpawnMessage(e.Id, e.Kind, owner, e.EventNumber, WorldPosition(e), e.Data).Write(_writer);
        _transport.Send(peer.Connection, _writer.Written, Channel.Reliable);
        peer.Known.Add(e.Id);
    }

    /// <summary>Asks the authority for an entity's last Description; it despawns it, and the Host saves it and tells
    /// everyone else to forget it (see <see cref="OnReleased"/>).</summary>
    private void Release(HostEntity e)
    {
        if (Authority is not { } authority) return;
        e.Releasing = true;
        Send(authority.Connection, new EntityMessage(MessageKind.Release, e.Id));
    }

    // ── saving ──────────────────────────────────────────────────────────────

    /// <summary>Writes one Description: a player's to the players table, anything else's to the entities table.</summary>
    private void Write(HostEntity e, byte[] data, Vector3? position)
    {
        if (e.IsPlayer) _db.WritePlayer(e.Player, e.Name, data);
        else _db.WriteEntity(e.Id, e.Kind, position, data);
        _db.NextFreeId = _ids.NextFree;
    }

    // ── sending ─────────────────────────────────────────────────────────────

    private IEnumerable<ConnectionId> Others(HostPeer peer) => Welcomed.Where(p => p != peer).Select(p => p.Connection);

    private void Send<T>(ConnectionId to, in T message, Channel channel = Channel.Reliable) where T : struct, IMessage
    {
        _writer.Clear();
        message.Write(_writer);
        _transport.Send(to, _writer.Written, channel);
    }

    private void Send<T>(IEnumerable<ConnectionId> to, in T message) where T : struct, IMessage
    {
        _writer.Clear();
        message.Write(_writer);
        foreach (var c in to) _transport.Send(c, _writer.Written, Channel.Reliable);
    }

    private void SendCommand(ConnectionId to, in CommandMessage message)
    {
        _writer.Clear();
        message.Write(_writer);
        _transport.Send(to, _writer.Written, Channel.Reliable);
    }

    public void Dispose() => _transport.Dispose();
}
