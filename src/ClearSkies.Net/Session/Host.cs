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

/// <summary>A Participant that has joined, as the Host sees it; and what it calls the Host by, on the hosting machine
/// directly (each call is the Host's, as from this Participant).</summary>
public sealed class HostPeer : IHost
{
    private readonly Host _host;

    internal HostPeer(Host host, PeerId peer, string name)
    {
        _host = host;
        Peer = peer;
        Name = name;
    }

    /// <summary>Who it is: <see cref="PeerId.Host"/> for the hosting machine's (the authority).</summary>
    public PeerId Peer { get; }
    public string Name { get; }
    public PlayerId Player;
    public EntityId PlayerEntity;

    /// <summary>What the Host tells it by (set by whoever let it join, before anything is).</summary>
    public IParticipant Participant { get; set; } = null!;

    /// <summary>The space it wants to see (see <see cref="IHost.SetView"/>).</summary>
    public Vector3 ViewCentre;
    public float ViewRadius;

    /// <summary>The entities it has (it's sent events and snapshots for these only), and those being described for it.</summary>
    public readonly HashSet<EntityId> Known = new();
    public readonly HashSet<EntityId> Requested = new();

    /// <summary>Its Participant is up (its first View Volume has come): nothing is streamed to it before.</summary>
    public bool Ready;

    public bool IsAuthority => Peer == PeerId.Host;

    void IHost.Ping(double clientTimeMs) => _host.Ping(this, clientTimeMs);
    void IHost.RequestIdBlock() => _host.RequestIdBlock(this);
    void IHost.SetView(Vector3 centre, float radius) => _host.SetView(this, centre, radius);
    void IHost.SendCommand(in CommandMessage command) => _host.SendCommand(this, command);
    void IHost.SendEvent(in EventMessage evt) => _host.SendEvent(this, evt);
    void IHost.Reject(in Rejection rejection) => _host.Reject(rejection);
    void IHost.SendInput(in PlayerInputMessage input) => _host.SendInput(this, input);
    void IHost.SendFrame(uint tick, IReadOnlyList<BodySnapshot> snapshots) => _host.SendFrame(this, tick, snapshots);
    void IHost.EntityCreated(in DescriptionMessage description) { if (IsAuthority) _host.EntityCreated(description); }
    void IHost.EntityDescribed(in DescriptionMessage description) { if (IsAuthority) _host.EntityDescribed(description); }
    void IHost.EntityReleased(in DescriptionMessage description) { if (IsAuthority) _host.EntityReleased(description); }
    void IHost.EntityDeleted(EntityId id) { if (IsAuthority) _host.EntityDeleted(id); }
    void IHost.Leave(string reason) => _host.Leave(this, reason);
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
/// The Host: coordinates, and simulates nothing (it has no ECS world). Participants join it (<see cref="Join"/>), the
/// hosting machine's directly (it has authority over every entity, so far), everyone else's over the network
/// (<see cref="RemoteParticipants"/>); each then calls it through its <see cref="HostPeer"/> (an <see cref="IHost"/>),
/// and the Host calls each through its <see cref="IParticipant"/>. Everything between them goes through the Host, as
/// none talk to each other. It keeps a record of every entity the authority has
/// (<see cref="HostEntity"/>), as the authority announces them (made, loaded from the save, despawned), and from those:
/// <list type="bullet">
/// <item>streams to each Participant every entity (a fresh Description, asked of the authority) and has it forget what's
/// released; events and snapshots go only to Participants that have their entity;</item>
/// <item>lets players in (spawning their Character where they left off, on their ship as it is now) and out (released:
/// the authority describes them a last time, and the Host writes that to the save).</item>
/// </list>
/// The authority's world loads, unloads and saves everything else itself.
/// </summary>
public sealed class Host : ISystem
{
    /// <summary>How far a Participant sees entities (its View Volume's radius).</summary>
    public const float ViewRadius = 1000f;

    private readonly SaveDatabase _db;
    private readonly EntityIdAllocator _ids;
    private readonly ulong _seed, _checksum;
    private readonly ITickClock _clock;
    private readonly List<HostPeer> _peers = new();
    private readonly Dictionary<EntityId, HostEntity> _entities = new();
    private readonly List<BodySnapshot> _frame = new();
    private readonly List<EntityId> _scratch = new();
    private uint _idNext, _idEnd;

    /// <param name="newPlayerSpawn">Where a new player's Character spawns (its capsule's centre), and which way they face.</param>
    public Host(SaveDatabase db, ITickClock clock, ulong seed, ulong generationChecksum,
                (Vector3 Position, float Yaw, float Pitch) newPlayerSpawn)
    {
        _db = db;
        _clock = clock;
        _seed = seed;
        _checksum = generationChecksum;
        NewPlayerSpawn = newPlayerSpawn;
        // Entity IDs come in blocks from the save's next free ID, so they never repeat across sessions.
        _ids = new EntityIdAllocator(db.NextFreeId);
    }

    public (Vector3 Position, float Yaw, float Pitch) NewPlayerSpawn { get; }

    /// <summary>Where entity IDs come from, so the save can record the next free one.</summary>
    public EntityIdAllocator Ids => _ids;

    /// <summary>The Participants that have joined.</summary>
    public IReadOnlyList<HostPeer> Peers => _peers;
    public IReadOnlyDictionary<EntityId, HostEntity> Entities => _entities;

    /// <summary>The hosting machine's Participant, which has authority over every entity.</summary>
    public HostPeer? Authority => _peers.FirstOrDefault(p => p.IsAuthority && p.Ready);

    /// <summary>Whether anyone but the hosting machine is in.</summary>
    public bool OthersConnected => _peers.Any(p => !p.IsAuthority);

    /// <summary>Players released this session (as they left).</summary>
    public long Releases { get; private set; }

    private HostPeer? PeerById(PeerId id) => _peers.FirstOrDefault(p => p.Peer == id);

    /// <summary>Once a tick, after what arrived over the network: what each Participant is owed.</summary>
    public void Update(float dt) => Stream();

    // ── joining and leaving ─────────────────────────────────────────────────

    internal void Leave(HostPeer peer, string reason)
    {
        if (!_peers.Remove(peer)) return;
        Console.WriteLine($"[net] {peer.Name} ({peer.Peer}) left: {reason}");
        // Their Character is released: its last Description is where they rejoin.
        if (_entities.TryGetValue(peer.PlayerEntity, out var player))
        {
            player.Leaving = true;
            if (player.Loaded) Release(player);
            else _entities.Remove(player.Id);
        }
        foreach (var p in Others(peer)) p.Participant.PlayerNotice(new PlayerNotice(false, peer.Peer, peer.Name));
    }

    private PeerId FreePeerId()
    {
        for (uint i = 2; i < 64; i++)
            if (_peers.All(p => p.Peer.Value != i)) return new PeerId(i);
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

    /// <summary>
    /// Lets a Participant in, or says why not (<paramref name="refusal"/>). <paramref name="local"/>: it's on this
    /// machine, so it's the authority (the first one only); only code on this machine can say so, never anything sent
    /// over the network. Whoever lets it in then sets what the Host tells it by (<see cref="HostPeer.Participant"/>).
    /// </summary>
    public HostPeer? Join(in Hello hello, bool local, out Welcome welcome, out string refusal)
    {
        welcome = default;
        refusal = "";
        if (hello.Version != ProtocolVersion.Current) { refusal = $"Version mismatch: host {ProtocolVersion.Current}, you {hello.Version}"; return null; }
        if (hello.GenerationChecksum != _checksum) { refusal = "World generation differs from the host's (different game build?)"; return null; }
        var id = local && _peers.All(p => !p.IsAuthority) ? PeerId.Host : FreePeerId();
        if (id == PeerId.None) { refusal = "The game is full"; return null; }
        string name = hello.Name.Trim();
        if (name.Length == 0 && id == PeerId.Host)
        {
            // The hosting machine with nobody playing there (a dedicated host): the authority, with no view of its own.
            var (idFirst, idCount) = _ids.NextBlock();
            welcome = new Welcome(id, idFirst, idCount, _seed, (uint)_clock.Now, default);
            var host = new HostPeer(this, id, "host");
            _peers.Add(host);
            return host;
        }
        if (name.Length == 0) { refusal = "A player name is needed"; return null; }
        var player = _db.PlayerFor(name);
        if (_peers.Any(p => p.Player == player) || _entities.Values.Any(e => e.Player == player && e.IsPlayer))
        {
            refusal = $"{name} is already in the game";
            return null;
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

        var peer = new HostPeer(this, id, name) { Player = player, PlayerEntity = character.Id };
        peer.ViewCentre = WorldPosition(character);
        peer.ViewRadius = ViewRadius;
        var (first, count) = _ids.NextBlock();
        welcome = new Welcome(id, first, count, _seed, (uint)_clock.Now, peer.ViewCentre);
        foreach (var p in _peers) p.Participant.PlayerNotice(new PlayerNotice(true, id, name));
        _peers.Add(peer);
        Console.WriteLine($"[net] {name} joining as {id}");
        return peer;
    }

    // ── what Participants send ──────────────────────────────────────────────

    internal void Ping(HostPeer from, double clientTimeMs)
    {
        double now = _clock.Now; // not Tick: on a frame running several ticks, that's behind real time
        from.Participant.Pong(new TimePong(clientTimeMs, (uint)now, (float)(now - Math.Floor(now))));
    }

    internal void RequestIdBlock(HostPeer from)
    {
        var (first, count) = _ids.NextBlock();
        from.Participant.IdBlock(first, count);
    }

    internal void SetView(HostPeer from, Vector3 centre, float radius)
    {
        (from.ViewCentre, from.ViewRadius) = (centre, MathF.Min(radius, ViewRadius));
        from.Ready = true;
    }

    /// <summary>To its authority, from whoever sent it, whatever it says.</summary>
    internal void SendCommand(HostPeer from, in CommandMessage command)
    {
        if (PeerById(command.To) is { } to) to.Participant.ReceiveCommand(command.WithFrom(from.Peer));
    }

    internal void Reject(in Rejection rejection)
    {
        if (PeerById(rejection.To) is { } to) to.Participant.Rejected(rejection);
    }

    /// <summary>Only from the machine that plays them, on to their authority.</summary>
    internal void SendInput(HostPeer from, in PlayerInputMessage input)
    {
        if (input.Player == from.PlayerEntity && Authority is { } authority) authority.Participant.ReceiveInput(input);
    }

    /// <summary>An event goes to everyone who has its entity, and back to whoever sent the command (to settle its
    /// prediction); nothing for an entity released or being released (its last Description has it). One for something
    /// the Host doesn't keep (the terrain) goes to everyone.</summary>
    internal void SendEvent(HostPeer from, in EventMessage evt)
    {
        var meta = evt.Meta;
        if (_entities.TryGetValue(meta.Target, out var entity) && (!entity.Loaded || entity.Releasing)) return;
        foreach (var p in _peers)
            if (p != from && (entity is null || p.Known.Contains(meta.Target) || p.Peer == meta.Origin))
                p.Participant.ReceiveEvent(evt);
    }

    /// <summary>The authority's snapshots: where its entities are now, then each Participant's share, as frames of
    /// their own.</summary>
    internal void SendFrame(HostPeer from, uint tick, IReadOnlyList<BodySnapshot> snapshots)
    {
        foreach (var s in snapshots)
        {
            if (!_entities.TryGetValue(s.Entity, out var e) || !e.Loaded) continue;
            if (e.IsPlayer)
            {
                e.Support = s.Support;
                if (s.Support.IsNone) e.Position = s.Position;
                else e.LocalPosition = s.Position;
            }
            else if (e.Position is not null) (e.Position, e.Rotation) = (s.Position, s.Rotation);
        }
        foreach (var p in _peers)
        {
            if (p == from) continue;
            _frame.Clear();
            foreach (var s in snapshots) if (p.Known.Contains(s.Entity) && _entities.TryGetValue(s.Entity, out var e) && e.Loaded) _frame.Add(s);
            if (_frame.Count > 0) p.Participant.ReceiveFrame(tick, _frame);
        }
    }

    // ── descriptions ────────────────────────────────────────────────────────

    internal void EntityCreated(in DescriptionMessage d)
    {
        if (!_entities.TryGetValue(d.Id, out var e))
        {
            _entities[d.Id] = e = new HostEntity { Id = d.Id, Loaded = true };
            Authority?.Known.Add(d.Id);
        }
        Record(e, d, created: true);
    }

    /// <summary>For the Participants that asked: sent on, unless it's been released meanwhile.</summary>
    internal void EntityDescribed(in DescriptionMessage d)
    {
        if (!_entities.TryGetValue(d.Id, out var e)) return; // forgotten meanwhile (deleted)
        Record(e, d, created: false);
        foreach (var p in _peers)
            if (p.Requested.Remove(e.Id) && e.Loaded && !e.Releasing) SendSpawn(p, e);
    }

    internal void EntityReleased(in DescriptionMessage d)
    {
        if (!_entities.TryGetValue(d.Id, out var e)) return;
        Record(e, d, created: false);
        OnReleased(e);
    }

    /// <summary>Keeps a Description, and where it says the entity is.</summary>
    private void Record(HostEntity e, in DescriptionMessage d, bool created)
    {
        e.Kind = d.Kind;
        e.Data = d.Data.ToArray();
        e.EventNumber = d.EventNumber;
        e.DescribedTick = _clock.Tick;
        if (e.IsPlayer)
        {
            var player = DescriptionBytes.Read<PlayerDescription>(d.Data);
            (e.Support, e.LocalPosition) = player.FreeFly ? (EntityId.None, default) : (player.Support, player.LocalPosition);
            e.Position = player.Position;
        }
        else if (e.Position is not null || created) e.Position = d.Position; // a global one stays global
    }

    private void OnReleased(HostEntity e)
    {
        e.Loaded = e.Releasing = false;
        e.DescribedTick = _clock.Tick;
        _db.InTransaction(() => Write(e, e.Data!, e.Position));
        foreach (var p in _peers)
        {
            p.Requested.Remove(e.Id);
            if (p.Known.Remove(e.Id) && !p.IsAuthority) p.Participant.Forget(e.Id);
        }
        if (e.IsPlayer) _entities.Remove(e.Id); // a player's Character exists only while they play
        Releases++;
    }

    internal void EntityDeleted(EntityId id)
    {
        if (!_entities.Remove(id)) return;
        foreach (var p in _peers) { p.Known.Remove(id); p.Requested.Remove(id); }
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

        foreach (var p in _peers)
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
            foreach (var id in _scratch) authority.Participant.Describe(id);
        }
    }

    /// <summary>Spawns <paramref name="e"/> on <paramref name="peer"/> from its Description: owned by the authority, and
    /// a player played by their machine.</summary>
    private void SendSpawn(HostPeer peer, HostEntity e)
    {
        var owner = e.IsPlayer ? e.ControllingPeer : PeerId.Host;
        peer.Known.Add(e.Id);
        peer.Participant.Spawn(new SpawnMessage(e.Id, e.Kind, owner, e.EventNumber, WorldPosition(e), e.Data));
    }

    /// <summary>Asks the authority for an entity's last Description; it despawns it, and the Host saves it and tells
    /// everyone else to forget it (see <see cref="OnReleased"/>).</summary>
    private void Release(HostEntity e)
    {
        if (Authority is not { } authority) return;
        e.Releasing = true;
        authority.Participant.Release(e.Id);
    }

    // ── saving ──────────────────────────────────────────────────────────────

    /// <summary>Writes one Description: a player's to the players table, anything else's to the entities table.</summary>
    private void Write(HostEntity e, byte[] data, Vector3? position)
    {
        if (e.IsPlayer) _db.WritePlayer(e.Player, e.Name, data);
        else _db.WriteEntity(e.Id, e.Kind, position, data);
        _db.NextFreeId = _ids.NextFree;
    }

    private IEnumerable<HostPeer> Others(HostPeer peer) => _peers.Where(p => p != peer);
}
