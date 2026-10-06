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

/// <summary>A Participant that has joined, as the Host keeps track of it.</summary>
public sealed class JoinedParticipant
{
    internal JoinedParticipant(PeerId peer, string name)
    {
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
}

/// <summary>The Host on this machine, as one Participant here calls it (where <see cref="RemoteHost"/> is the Host on
/// another machine): each call reaches the Host as from that Participant.</summary>
public sealed class LocalHost(Host host, JoinedParticipant from) : IHost
{
    public void Ping(double clientTimeMs) => host.Ping(from, clientTimeMs);
    public void RequestIdBlock() => host.RequestIdBlock(from);
    public void SetView(Vector3 centre, float radius) => host.SetView(from, centre, radius);
    public void SendCommand(in CommandMessage command) => host.SendCommand(from, command);
    public void SendEvent(in EventMessage evt) => host.SendEvent(from, evt);
    public void Reject(in Rejection rejection) => host.Reject(rejection);
    public void SendInput(in PlayerInputMessage input) => host.SendInput(from, input);
    public void SendFrame(uint tick, IReadOnlyList<BodySnapshot> snapshots) => host.SendFrame(from, tick, snapshots);
    public void EntityCreated(in DescriptionMessage description) => host.EntityCreated(from, description);
    public void EntityDescribed(in DescriptionMessage description) => host.EntityDescribed(from, description);
    public void EntityReleased(in DescriptionMessage description) => host.EntityReleased(from, description);
    public void EntityDeleted(EntityId id) => host.EntityDeleted(from, id);
    public void EntitySaved(in DescriptionMessage description) => host.EntitySaved(from, description);
    public void SaveDone() => host.SaveDone(from);
    public void Leave(string reason) => host.Leave(from, reason);
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
/// The Host: coordinates and persists, and simulates nothing (it has no ECS world). Participants join it
/// (<see cref="Join"/>), the hosting machine's directly (it has authority over every entity, so far), everyone else's
/// over the network (<see cref="HostNetwork"/>). Each call on it says which Participant it's from (the hosting machine's
/// makes them through a <see cref="LocalHost"/>, an <see cref="IHost"/>), and the Host calls each through its
/// <see cref="IParticipant"/>. Everything between them goes
/// through the Host, as none talk to each other. It keeps a record of every entity (<see cref="HostEntity"/>), loaded
/// or in the save, and each Participant's View Volume, and from those:
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
public sealed class Host : ISystem
{
    /// <summary>How far a Participant sees entities (its View Volume's radius): an entity loads within this of any view,
    /// and is released past this × <see cref="Hysteresis"/> from every view, so nothing near an edge flickers.</summary>
    public const float ViewRadius = 1000f;
    public const float Hysteresis = 1.1f;
    public const float AutosaveSeconds = 300f;
    private const int MaxLoadsPerTick = 4;

    private readonly SaveDatabase _db;
    private readonly EntityIdAllocator _ids;
    private readonly ulong _seed, _checksum;
    private readonly ITickClock _clock;
    private readonly List<JoinedParticipant> _joined = new();
    private readonly Dictionary<EntityId, HostEntity> _entities = new();
    private readonly List<BodySnapshot> _frame = new();
    private readonly List<EntityId> _scratch = new();
    private readonly List<(HostEntity Entity, byte[] Data, Vector3? Position)> _saving = new();
    private uint _idNext, _idEnd;
    private float _sinceSave;

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
        foreach (var stored in db.ReadEntityIndex())
            _entities[stored.Id] = new HostEntity { Id = stored.Id, Kind = stored.Kind, Position = stored.Position };
    }

    public (Vector3 Position, float Yaw, float Pitch) NewPlayerSpawn { get; }

    /// <summary>Called inside a save's transaction, to write edited terrain (until terrain chunks are described like
    /// entities, the authority's world writes them itself).</summary>
    public Action? SaveChunks { get; set; }

    /// <summary>The Participants that have joined.</summary>
    public IReadOnlyList<JoinedParticipant> Participants => _joined;
    public IReadOnlyDictionary<EntityId, HostEntity> Entities => _entities;

    /// <summary>The hosting machine's Participant, which has authority over every entity.</summary>
    public JoinedParticipant? Authority => _joined.FirstOrDefault(p => p.IsAuthority && p.Ready);

    /// <summary>Whether anyone but the hosting machine is in.</summary>
    public bool OthersConnected => _joined.Any(p => !p.IsAuthority);

    /// <summary>Saves finished this session (each one's Descriptions written).</summary>
    public int Saves { get; private set; }
    public bool Saving { get; private set; }
    public long Loads { get; private set; }
    public long Releases { get; private set; }

    private JoinedParticipant? JoinedAs(PeerId id) => _joined.FirstOrDefault(p => p.Peer == id);

    /// <summary>Once a tick, after what arrived over the network: what comes into and leaves each view, and the
    /// autosave.</summary>
    public void Update(float dt)
    {
        Stream();
        _sinceSave += dt;
        if (_sinceSave >= AutosaveSeconds) SaveAll();
    }

    // ── joining and leaving ─────────────────────────────────────────────────

    /// <summary>It's leaving, and why.</summary>
    public void Leave(JoinedParticipant joined, string reason)
    {
        if (!_joined.Remove(joined)) return;
        Console.WriteLine($"[net] {joined.Name} ({joined.Peer}) left: {reason}");
        // Their Character goes through the handshake like anything else, even in someone's view: its last Description
        // is where they rejoin.
        if (_entities.TryGetValue(joined.PlayerEntity, out var player))
        {
            player.Leaving = true;
            if (player.Loaded) Release(player);
            else _entities.Remove(player.Id);
        }
        foreach (var p in Others(joined)) p.Participant.PlayerNotice(new PlayerNotice(false, joined.Peer, joined.Name));
    }

    private PeerId FreePeerId()
    {
        for (uint i = 2; i < 64; i++)
            if (_joined.All(p => p.Peer.Value != i)) return new PeerId(i);
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
    /// over the network. Whoever lets it in then sets what the Host tells it by (<see cref="JoinedParticipant.Participant"/>),
    /// and makes each of its calls on the Host as from what this returns.
    /// </summary>
    public JoinedParticipant? Join(in Hello hello, bool local, out Welcome welcome, out string refusal)
    {
        welcome = default;
        refusal = "";
        if (hello.Version != ProtocolVersion.Current) { refusal = $"Version mismatch: host {ProtocolVersion.Current}, you {hello.Version}"; return null; }
        if (hello.GenerationChecksum != _checksum) { refusal = "World generation differs from the host's (different game build?)"; return null; }
        var id = local && _joined.All(p => !p.IsAuthority) ? PeerId.Host : FreePeerId();
        if (id == PeerId.None) { refusal = "The game is full"; return null; }
        string name = hello.Name.Trim();
        if (name.Length == 0 && id == PeerId.Host)
        {
            // The hosting machine with nobody playing there (a dedicated host): the authority, with no view of its own.
            var (idFirst, idCount) = _ids.NextBlock();
            welcome = new Welcome(id, idFirst, idCount, _seed, (uint)_clock.Now, default);
            var host = new JoinedParticipant(id, "host");
            _joined.Add(host);
            return host;
        }
        if (name.Length == 0) { refusal = "A player name is needed"; return null; }
        var player = _db.PlayerFor(name);
        if (_joined.Any(p => p.Player == player) || _entities.Values.Any(e => e.Player == player && e.IsPlayer))
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

        var joined = new JoinedParticipant(id, name) { Player = player, PlayerEntity = character.Id };
        joined.ViewCentre = WorldPosition(character);
        joined.ViewRadius = ViewRadius;
        var (first, count) = _ids.NextBlock();
        welcome = new Welcome(id, first, count, _seed, (uint)_clock.Now, joined.ViewCentre);
        foreach (var p in _joined) p.Participant.PlayerNotice(new PlayerNotice(true, id, name));
        _joined.Add(joined);
        Console.WriteLine($"[net] {name} joining as {id}");
        return joined;
    }

    // ── what Participants send ──────────────────────────────────────────────

    public void Ping(JoinedParticipant from, double clientTimeMs)
    {
        double now = _clock.Now; // not Tick: on a frame running several ticks, that's behind real time
        from.Participant.Pong(new TimePong(clientTimeMs, (uint)now, (float)(now - Math.Floor(now))));
    }

    public void RequestIdBlock(JoinedParticipant from)
    {
        var (first, count) = _ids.NextBlock();
        from.Participant.IdBlock(first, count);
    }

    public void SetView(JoinedParticipant from, Vector3 centre, float radius)
    {
        (from.ViewCentre, from.ViewRadius) = (centre, MathF.Min(radius, ViewRadius));
        from.Ready = true;
    }

    /// <summary>To its authority, from whoever sent it, whatever it says.</summary>
    public void SendCommand(JoinedParticipant from, in CommandMessage command)
    {
        if (JoinedAs(command.To) is { } to) to.Participant.ReceiveCommand(command.WithFrom(from.Peer));
    }

    public void Reject(in Rejection rejection)
    {
        if (JoinedAs(rejection.To) is { } to) to.Participant.Rejected(rejection);
    }

    /// <summary>Only from the machine that plays them, on to their authority.</summary>
    public void SendInput(JoinedParticipant from, in PlayerInputMessage input)
    {
        if (input.Player == from.PlayerEntity && Authority is { } authority) authority.Participant.ReceiveInput(input);
    }

    /// <summary>An event goes to everyone who has its entity, and back to whoever sent the command (to settle its
    /// prediction); nothing for an entity released or being released (its last Description has it). One for something
    /// the Host doesn't keep (the terrain) goes to everyone.</summary>
    public void SendEvent(JoinedParticipant from, in EventMessage evt)
    {
        var meta = evt.Meta;
        if (_entities.TryGetValue(meta.Target, out var entity) && (!entity.Loaded || entity.Releasing)) return;
        foreach (var p in _joined)
            if (p != from && (entity is null || p.Known.Contains(meta.Target) || p.Peer == meta.Origin))
                p.Participant.ReceiveEvent(evt);
    }

    /// <summary>The authority's snapshots: where its entities are now, then each Participant's share, as frames of
    /// their own.</summary>
    public void SendFrame(JoinedParticipant from, uint tick, IReadOnlyList<BodySnapshot> snapshots)
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
        foreach (var p in _joined)
        {
            if (p == from) continue;
            _frame.Clear();
            foreach (var s in snapshots) if (p.Known.Contains(s.Entity) && _entities.TryGetValue(s.Entity, out var e) && e.Loaded) _frame.Add(s);
            if (_frame.Count > 0) p.Participant.ReceiveFrame(tick, _frame);
        }
    }

    // ── descriptions ────────────────────────────────────────────────────────

    /// <summary>The authority made an entity (anyone else's is ignored, as are all of these).</summary>
    public void EntityCreated(JoinedParticipant from, in DescriptionMessage d)
    {
        if (!from.IsAuthority) return;
        if (!_entities.TryGetValue(d.Id, out var e))
        {
            _entities[d.Id] = e = new HostEntity { Id = d.Id, Loaded = true };
            Authority?.Known.Add(d.Id);
        }
        Record(e, d, created: true);
    }

    /// <summary>For the Participants that asked: sent on, unless it's been released meanwhile.</summary>
    public void EntityDescribed(JoinedParticipant from, in DescriptionMessage d)
    {
        if (!from.IsAuthority) return;
        if (!_entities.TryGetValue(d.Id, out var e)) return; // forgotten meanwhile (deleted)
        Record(e, d, created: false);
        foreach (var p in _joined)
            if (p.Requested.Remove(e.Id) && e.Loaded && !e.Releasing) SendSpawn(p, e);
    }

    public void EntityReleased(JoinedParticipant from, in DescriptionMessage d)
    {
        if (!from.IsAuthority) return;
        if (!_entities.TryGetValue(d.Id, out var e)) return;
        Record(e, d, created: false);
        OnReleased(e);
    }

    /// <summary>One of everything the authority describes when the Host saves (see <see cref="SaveAll"/>).</summary>
    public void EntitySaved(JoinedParticipant from, in DescriptionMessage d)
    {
        if (!from.IsAuthority || !Saving || !_entities.TryGetValue(d.Id, out var e)) return;
        Record(e, d, created: false);
        _saving.Add((e, e.Data!, e.Position));
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
        foreach (var p in _joined)
        {
            p.Requested.Remove(e.Id);
            if (p.Known.Remove(e.Id) && !p.IsAuthority) p.Participant.Forget(e.Id);
        }
        if (e.IsPlayer) _entities.Remove(e.Id); // a player's Character exists only while they play
        Releases++;
    }

    public void EntityDeleted(JoinedParticipant from, EntityId id)
    {
        if (!from.IsAuthority) return;
        if (!_entities.Remove(id)) return;
        _db.InTransaction(() => _db.DeleteEntity(id));
        foreach (var p in _joined) { p.Known.Remove(id); p.Requested.Remove(id); }
    }

    // ── views ───────────────────────────────────────────────────────────────

    /// <summary>Where an entity is in the world: a player on their ship as the ship is now.</summary>
    public Vector3 WorldPosition(HostEntity e)
    {
        if (e.IsPlayer && !e.Support.IsNone && _entities.TryGetValue(e.Support, out var ship) && ship.Position is { } on)
            return on + Vector3.Transform(e.LocalPosition, ship.Rotation);
        return e.Position ?? Vector3.Zero;
    }

    /// <summary>Whether <paramref name="joined"/> sees <paramref name="e"/>: within its View Volume, or (if it
    /// <paramref name="already"/> has it) a little past. A global entity is in every view.</summary>
    private bool Sees(JoinedParticipant joined, HostEntity e, bool already)
    {
        if (e.Position is null && !e.IsPlayer) return true;
        if (joined.ViewRadius <= 0) return false;
        return Vector3.Distance(WorldPosition(e), joined.ViewCentre) <= (already ? joined.ViewRadius * Hysteresis : joined.ViewRadius);
    }

    private bool AnyoneSees(HostEntity e, bool already)
    {
        foreach (var p in _joined) if (p.Ready && Sees(p, e, already)) return true;
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
        bool viewed = _joined.Any(p => p.Ready && p.ViewRadius > 0);
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

        foreach (var p in _joined)
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

    /// <summary>Spawns <paramref name="e"/> on <paramref name="joined"/> from its Description: owned by the authority, and
    /// a player played by their machine.</summary>
    private void SendSpawn(JoinedParticipant joined, HostEntity e)
    {
        var owner = e.IsPlayer ? e.ControllingPeer : PeerId.Host;
        joined.Known.Add(e.Id);
        joined.Participant.Spawn(new SpawnMessage(e.Id, e.Kind, owner, e.EventNumber, WorldPosition(e), e.Data));
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

    /// <summary>Asks the authority to describe everything; written in one transaction once it has (see
    /// <see cref="Saving"/>).</summary>
    public void SaveAll()
    {
        _sinceSave = 0;
        if (Authority is not { } authority || Saving) return;
        Saving = true;
        _saving.Clear();
        authority.Participant.Save();
    }

    /// <summary>The authority has described everything: written in one transaction.</summary>
    public void SaveDone(JoinedParticipant from)
    {
        if (!from.IsAuthority || !Saving) return;
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

    private IEnumerable<JoinedParticipant> Others(JoinedParticipant joined) => _joined.Where(p => p != joined);
}
