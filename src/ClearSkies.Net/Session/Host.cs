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
    /// <summary>None until it has joined; <see cref="PeerId.Host"/> for the hosting machine's (the authority).</summary>
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

    public bool Joined => Peer != PeerId.None;
    public bool IsAuthority => Peer == PeerId.Host;
}

/// <summary>An entity as the Host keeps it: in the save, or loaded (simulated by the authority). Everything but its kind,
/// where it is and its last event number is its Description, which only the authority reads; the Host reads a player's,
/// for where they stand on their ship.</summary>
public sealed class HostEntity
{
    public required EntityId Id;
    public ushort Kind;
    /// <summary>Its latest Description (null: in the save, not read yet).</summary>
    public byte[]? Data;
    /// <summary>The authority's last event the Description includes.</summary>
    public uint EventNumber;
    /// <summary>Where it is (null: a global entity, in every view) and which way it faces, as last described or
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
    /// <summary>The tick <see cref="Data"/> was last described (or read from the save, while not loaded): within that
    /// tick it's still current, so it can be sent as it is.</summary>
    public uint DescribedTick = uint.MaxValue;

    public bool IsPlayer => ControllingPeer != PeerId.None;
}

/// <summary>
/// The Host: coordinates and persists, and simulates nothing (it has no ECS world). Participants connect to it, the
/// hosting machine's over an in-process link (it has authority over every entity, so far), everyone else's over the
/// network; it relays every message between them, as none talk to each other. It keeps a record of every entity
/// (<see cref="HostEntity"/>), loaded or in the save, and each Participant's View Volume, and from those:
/// <list type="bullet">
/// <item>loads what comes into any view (the save's Description, spawned on the authority first, then everyone else),
/// and releases what leaves every view (the authority describes it a last time, for the save, and despawns it);</item>
/// <item>streams to each Participant everything loaded (a fresh Description, asked of the authority) and has it forget
/// what's released; events and snapshots go only to Participants that have their entity;</item>
/// <item>lets players in (spawning their Character where they left off, on their ship as it is now) and out (released
/// like anything else, even in someone's view);</item>
/// <item>saves: the authority describes everything every <see cref="AutosaveSeconds"/> and on exit, written in one
/// transaction.</item>
/// </list>
/// </summary>
public sealed class Host : ISystem, IDisposable
{
    /// <summary>How far a Participant sees entities (its View Volume's radius): an entity loads within this of any view,
    /// and is released past this × <see cref="Hysteresis"/> from every view, so nothing near an edge flickers.</summary>
    public const float ViewRadius = 1000f;
    public const float Hysteresis = 1.1f;
    public const float AutosaveSeconds = 300f;
    private const int MaxLoadsPerTick = 4;

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
    private readonly List<(HostEntity Entity, byte[] Data, Vector3? Position)> _saving = new();
    private uint _idNext, _idEnd;
    private float _sinceSave;

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
        foreach (var stored in db.ReadEntityIndex())
            _entities[stored.Id] = new HostEntity { Id = stored.Id, Kind = stored.Kind, Position = stored.Position };
        transport.Connected += OnConnected;
        transport.Disconnected += OnDisconnected;
        transport.Received += OnReceived;
    }

    public (Vector3 Position, float Yaw, float Pitch) NewPlayerSpawn { get; }

    /// <summary>Called inside a save's transaction, to write edited terrain (until terrain chunks are described like
    /// entities, the authority's world writes them itself).</summary>
    public Action? SaveChunks { get; set; }

    public IReadOnlyCollection<HostPeer> Peers => _peers.Values;
    /// <summary>Participants that have joined (been welcomed).</summary>
    public IEnumerable<HostPeer> Joined => _peers.Values.Where(p => p.Joined);
    public IReadOnlyDictionary<EntityId, HostEntity> Entities => _entities;
    public HostTransport Transport => _transport;

    /// <summary>The hosting machine's Participant, which has authority over every entity.</summary>
    public HostPeer? Authority => _peers.Values.FirstOrDefault(p => p.IsAuthority && p.Ready);

    /// <summary>Whether anyone but the hosting machine is in.</summary>
    public bool OthersConnected => _peers.Values.Any(p => p.Joined && !p.IsAuthority);

    /// <summary>Saves finished this session (each one's Descriptions written).</summary>
    public int Saves { get; private set; }
    public bool Saving { get; private set; }
    public long Loads { get; private set; }
    public long Releases { get; private set; }

    private HostPeer? PeerById(PeerId id) => _peers.Values.FirstOrDefault(p => p.Peer == id);

    /// <summary>Once a tick, first: everything that arrived, then what comes into and leaves each view, and the autosave.</summary>
    public void Update(float dt)
    {
        _transport.Poll();
        Stream();
        _sinceSave += dt;
        if (_sinceSave >= AutosaveSeconds) SaveAll();
    }

    /// <summary>Last in the hosting machine's tick: passes on at once what its Participant sent this tick (its events and
    /// snapshots), rather than a tick later.</summary>
    public ISystem Relay => _relay ??= new LambdaSystem(_transport.Poll);
    private ISystem? _relay;

    // ── connections ─────────────────────────────────────────────────────────

    private void OnConnected(ConnectionId connection) => _peers[connection] = new HostPeer { Connection = connection };

    private void OnDisconnected(ConnectionId connection, string reason)
    {
        if (!_peers.Remove(connection, out var peer) || !peer.Joined) return;
        Console.WriteLine($"[net] {peer.Name} ({peer.Peer}) left: {reason}");
        // Their Character goes through the handshake like anything else, even in someone's view: its last Description
        // is where they rejoin.
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
        if (peer.Joined) { Console.WriteLine($"[net] ignoring a second hello from {peer.Name} ({peer.Peer})"); return; }
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
            else if (peer.Joined) OnMessage(peer, kind, ref r, packet);
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
            case MessageKind.SaveDone when peer.IsAuthority:
                WriteSave();
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
        foreach (var p in Joined)
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
        foreach (var p in Joined)
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
                foreach (var p in Joined)
                    if (p.Requested.Remove(e.Id) && e.Loaded && !e.Releasing) SendSpawn(p, e);
                break;
            case DescribedReason.Released:
                OnReleased(e);
                break;
            case DescribedReason.Save:
                _saving.Add((e, e.Data, e.Position));
                break;
        }
    }

    private void OnReleased(HostEntity e)
    {
        e.Loaded = e.Releasing = false;
        e.DescribedTick = _clock.Tick;
        _db.InTransaction(() => Write(e, e.Data!, e.Position));
        foreach (var p in Joined)
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
        _db.InTransaction(() => _db.DeleteEntity(id));
        foreach (var p in _peers.Values) { p.Known.Remove(id); p.Requested.Remove(id); }
    }

    // ── views ───────────────────────────────────────────────────────────────

    /// <summary>Where an entity is in the world: a player on their ship as the ship is now.</summary>
    public Vector3 WorldPosition(HostEntity e)
    {
        if (e.IsPlayer && !e.Support.IsNone && _entities.TryGetValue(e.Support, out var ship) && ship.Position is { } on)
            return on + Vector3.Transform(e.LocalPosition, ship.Rotation);
        return e.Position ?? Vector3.Zero;
    }

    /// <summary>Whether <paramref name="peer"/> sees <paramref name="e"/>: within its View Volume, or (if it
    /// <paramref name="already"/> has it) a little past. A global entity is in every view.</summary>
    private bool Sees(HostPeer peer, HostEntity e, bool already)
    {
        if (e.Position is null && !e.IsPlayer) return true;
        if (peer.ViewRadius <= 0) return false;
        return Vector3.Distance(WorldPosition(e), peer.ViewCentre) <= (already ? peer.ViewRadius * Hysteresis : peer.ViewRadius);
    }

    private bool AnyoneSees(HostEntity e, bool already)
    {
        foreach (var p in Welcomed) if (p.Ready && Sees(p, e, already)) return true;
        return false;
    }

    /// <summary>
    /// Each tick: entities that left every view are released, stored ones that came into a view are loaded (spawned on
    /// the authority, from the save's Description), and each Participant is sent what has loaded since (a fresh
    /// Description, asked of the authority unless it's already this tick's). Nothing happens before the authority is
    /// here, and nothing is loaded or released while nobody has a view.
    /// </summary>
    private void Stream()
    {
        if (Authority is not { } authority) return;

        // With no view anywhere (nobody in yet), there's nothing to load around and nothing is released.
        bool viewed = Welcomed.Any(p => p.Ready && p.ViewRadius > 0);
        if (viewed)
            foreach (var e in _entities.Values)
                if (e.Loaded && !e.Releasing && !e.IsPlayer && !AnyoneSees(e, already: true)) Release(e);

        int loads = 0;
        foreach (var e in _entities.Values)
        {
            if (loads >= MaxLoadsPerTick || !viewed) break;
            if (e.Loaded || e.Releasing || !(AnyoneSees(e, already: false) || e.IsPlayer)) continue;
            if (e.Data is null)
            {
                // The index and the entities table change together (saved and deleted together), so the row is there.
                var row = _db.ReadEntity(e.Id) ?? throw new InvalidOperationException($"Stored entity {e.Id} has no row in the save.");
                (e.Kind, e.Data) = (row.Kind, row.Data);
            }
            e.Loaded = true;
            e.DescribedTick = _clock.Tick; // the save's, and nothing newer exists anywhere
            SendSpawn(authority, e);
            loads++;
            Loads++;
        }

        foreach (var p in Joined)
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

    /// <summary>Asks the authority to describe everything; written in one transaction once it has (see
    /// <see cref="Saving"/>).</summary>
    public void SaveAll()
    {
        _sinceSave = 0;
        if (Authority is not { } authority || Saving) return;
        Saving = true;
        _saving.Clear();
        Send(authority.Connection, new SignalMessage(MessageKind.SaveRequest));
    }

    private void WriteSave()
    {
        if (!Saving) return;
        _db.InTransaction(() =>
        {
            foreach (var (e, data, position) in _saving) Write(e, data, position);
            SaveChunks?.Invoke();
            _db.NextFreeId = _ids.NextFree;
        });
        Console.WriteLine($"[save] saved {_saving.Count} entities and players");
        _saving.Clear();
        Saving = false;
        Saves++;
    }

    /// <summary>Writes one Description: a player's to the players table, anything else's to the entities table.</summary>
    private void Write(HostEntity e, byte[] data, Vector3? position)
    {
        if (e.IsPlayer) _db.WritePlayer(e.Player, e.Name, data);
        else _db.WriteEntity(e.Id, e.Kind, position, data);
        _db.NextFreeId = _ids.NextFree;
    }

    // ── sending ─────────────────────────────────────────────────────────────

    private IEnumerable<ConnectionId> Others(HostPeer peer) => Joined.Where(p => p != peer).Select(p => p.Connection);

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
