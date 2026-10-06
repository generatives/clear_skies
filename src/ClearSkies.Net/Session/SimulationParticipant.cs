using System.Diagnostics;
using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Sync;
using DefaultEcs;
using EngineSession = ClearSkies.Engine.Entities.Session;

namespace ClearSkies.Net.Session;

/// <summary>
/// A SimulationParticipant: an ECS world kept in step with the game, on every machine that plays, the hosting one's
/// included. It tells the Host things through an <see cref="IHost"/> (on the hosting machine, the Host itself; on any
/// other, a <see cref="RemoteHost"/>), and the Host tells it things as its <see cref="IParticipant"/>. The Host spawns
/// into it what's in its View Volume (which it keeps the Host told of, around its player) and has it forget what
/// leaves; each spawn waits here, with its events, until what it needs has loaded (<see cref="SpawnQueue"/>). It's also the command router: commands go to their authority, events to everyone who applies them, always
/// through the Host.
/// <para>The hosting machine's is the authority (<see cref="PeerId.Host"/>): it simulates every entity, players
/// included (moving each by their machine's input, <see cref="RemoteInputs"/>), decides every command, and describes
/// entities for the Host: ones it makes, ones the Host asks for, ones the Host releases (then despawns them), and
/// everything when the Host saves; and it describes each terrain chunk it edits, before the edit's event goes anywhere.
/// It shares the Host's clock. Any other simulates only its own player, predicting it
/// (<see cref="OwnPlayerPrediction"/>), and keeps its clock on the Host's (<see cref="ClockSync"/>). Its player spawns
/// only once that has settled (<see cref="ClockSync.Ready"/>, for at most <see cref="ClockSync.MaxSettleMs"/>), so prediction
/// starts on the Host's timeline.</para>
/// </summary>
public sealed class SimulationParticipant : IParticipant, ISystem, ICommandRouter, IDisposable
{
    /// <summary>How often the View Volume is sent, in ticks.</summary>
    public const int ViewTicks = 30;

    private readonly IHost _host;
    private readonly HostChunkStore? _chunks;
    private readonly SpawnQueue _spawns;
    private readonly RemoteInputs? _inputs;
    private readonly EntitySet _localPlayers;
    private readonly EntitySet _describable;
    private readonly EntitySet _created;
    private readonly HashSet<EntityId> _fromHost = new();
    private readonly List<EntityId> _deleted = new();
    private readonly List<EntityId> _toRelease = new();
    private readonly List<EntityId> _toDescribe = new();
    private readonly HashSet<ChunkPosition> _editedChunks = new();
    private readonly Stopwatch _realTime = Stopwatch.StartNew();
    private bool _idRequested;
    private int _viewTicks;
    private bool _viewSent;
    private bool _left;

    /// <param name="host">What it tells the Host by, which <paramref name="welcome"/> came from. Whoever makes it then has
    /// the Host tell it things (see <see cref="Join(Host, Hello, EngineSession, CommandSystem, EntityRegistry, World, ITickClock, Func{Vector3, bool})"/>).</param>
    /// <param name="terrainReady">Whether the terrain around a point has loaded here, with colliders.</param>
    /// <param name="chunks">Where its terrain streaming loads edited chunks from: the Host, through this (none: it streams
    /// no terrain).</param>
    public SimulationParticipant(IHost host, Welcome welcome, EngineSession session, CommandSystem commands, EntityRegistry registry,
                                 World world, ITickClock clock, Func<Vector3, bool> terrainReady, HostChunkStore? chunks = null)
    {
        _host = host;
        _chunks = chunks;
        chunks?.Connect(host, welcome.EditedChunks);
        Session = session;
        Commands = commands;
        Registry = registry;
        World = world;
        Clock = clock;
        commands.Router = this;
        TimeSource = () => _realTime.Elapsed.TotalMilliseconds;
        SpawnPoint = welcome.Spawn;
        session.Join(welcome.Peer);
        registry.AddIdBlock(welcome.IdFirst, welcome.IdCount);
        if (!IsAuthority)
        {
            ClockSync = new ClockSync(clock) { Settling = true };
            ClockSync.SnapTo(welcome.HostTick);
        }
        else _inputs = new RemoteInputs(world, registry);
        _spawns = new SpawnQueue(world, registry, commands, session, terrainReady) { LocalReady = () => ClockReady };
        _spawns.Spawning += id => _fromHost.Add(id);
        _localPlayers = world.GetEntities().With<LocalPlayer>().With<Transform>().AsSet();
        _describable = world.GetEntities().With<EntityId>().With<OwnPresence>().Without<Chunk>().AsSet();
        _created = world.GetEntities().With<EntityId>().With<OwnPresence>().Without<Chunk>().WhenAdded<OwnPresence>().AsSet();
        commands.Applied += OnApplied;
    }

    /// <summary>The hosting machine's, joining <paramref name="host"/> directly: the authority. Throws with the Host's
    /// reason if it refuses.</summary>
    public static SimulationParticipant Join(Host host, Hello hello, EngineSession session, CommandSystem commands, EntityRegistry registry,
                                             World world, ITickClock clock, Func<Vector3, bool> terrainReady, HostChunkStore? chunks = null)
    {
        var joined = host.Join(hello, local: true, out var welcome, out var refusal)
                     ?? throw new InvalidOperationException($"The host refused: {refusal}");
        var participant = new SimulationParticipant(new LocalHost(host, joined), welcome, session, commands, registry, world, clock,
                                                     terrainReady, chunks);
        joined.Participant = participant;
        return participant;
    }

    /// <summary>Another machine's, joining the Host <paramref name="host"/> connects to, once it has welcomed us.</summary>
    public static SimulationParticipant Join(RemoteHost host, EngineSession session, CommandSystem commands, EntityRegistry registry,
                                             World world, ITickClock clock, Func<Vector3, bool> terrainReady, HostChunkStore? chunks = null)
    {
        var participant = new SimulationParticipant(host, host.Welcome, session, commands, registry, world, clock, terrainReady, chunks);
        host.Participant = participant;
        return participant;
    }

    public EngineSession Session { get; }
    public CommandSystem Commands { get; }
    public EntityRegistry Registry { get; }
    public World World { get; }
    public ITickClock Clock { get; }

    /// <summary>Receives and sends body snapshots (set by the body sync system).</summary>
    public BodySync? Bodies { get; set; }

    /// <summary>A client's prediction of its own player, which hears what the host makes of it (set by the prediction).</summary>
    public OwnPlayerPrediction? Prediction { get; set; }

    /// <summary>Real time in milliseconds, for clock sync. Settable so tests can run on simulated time.</summary>
    public Func<double> TimeSource { get; set; }

    public double NowMs => TimeSource();

    /// <summary>Where its player first spawns (or, for a dedicated host, nowhere in particular).</summary>
    public Vector3 SpawnPoint { get; }

    /// <summary>The hosting machine's: authority over every entity.</summary>
    public bool IsAuthority => Session.LocalPeer == PeerId.Host;

    /// <summary>Keeps this machine's clock on the Host's (none on the authority, which shares it).</summary>
    public ClockSync? ClockSync { get; }

    private bool ClockReady => ClockSync?.Ready ?? true;

    /// <summary>Other machines' input, applied to the players they play (the authority only).</summary>
    public RemoteInputs? Inputs => _inputs;

    /// <summary>Spawns waiting for what they need.</summary>
    public SpawnQueue Spawns => _spawns;

    /// <summary>Whether its own player has spawned.</summary>
    public bool Joined { get; private set; }

    /// <summary>What joining is waiting on, for a loading screen: the clock, the world around the spawn, then the Host.</summary>
    public string JoinStatus { get; private set; } = "Syncing with the host";

    /// <summary>Whether anyone else is in the game (pilot mode and the flight sliders are single-player only).</summary>
    public bool OthersConnected => !IsAuthority || OthersHere();

    /// <summary>On the authority: whether anyone else has joined (set by whoever runs the Host).</summary>
    public Func<bool> OthersHere { get; set; } = () => false;

    /// <summary>Whether it has a View Volume: false for a host with nobody playing on it.</summary>
    public bool Viewing { get; set; } = true;

    /// <summary>Raised when the Host lets it go, with why.</summary>
    public event Action<string>? Ended;

    /// <summary>First in the tick (after what arrived): what the Host asked of the authority, players' next inputs (on the
    /// authority), spawns that are ready, and what's owed to the Host.</summary>
    public void Update(float dt)
    {
        AnswerHost();
        ReportDeleted();
        AnnounceCreated();
        _inputs?.Update();
        _spawns.Update();
        UpdateJoined();
        SyncClock();
        if (!_viewSent || (Viewing && ++_viewTicks >= ViewTicks)) SendView();
        RequestIdsIfLow();
    }

    /// <summary>On the authority: what it despawned last tick, which the Host stops keeping.</summary>
    private void ReportDeleted()
    {
        foreach (var id in _deleted) _host.EntityDeleted(id);
        _deleted.Clear();
    }

    private void UpdateJoined()
    {
        if (!Joined && _localPlayers.Count > 0) Console.WriteLine($"[net] in the game, as {Session.LocalPeer}");
        Joined = _localPlayers.Count > 0;
        if (!Joined) JoinStatus = !ClockReady ? "Syncing with the host" : _spawns.All.Any(p => p.Local) ? "Loading the world" : "Waiting for the host";
    }

    private void SyncClock()
    {
        if (ClockSync is not { } sync) return;
        sync.Update(NowMs);
        if (sync.ShouldPing(NowMs)) _host.Ping(NowMs);
    }

    private void RequestIdsIfLow()
    {
        if (_idRequested || Registry.IdsLeft >= EntityRegistry.BlockSize / 4) return;
        _host.RequestIdBlock();
        _idRequested = true;
    }

    /// <summary>The View Volume: around its player, or where its player will spawn until then (none, but sent once, as
    /// the Host's sign this is up, if not <see cref="Viewing"/>).</summary>
    private void SendView()
    {
        _viewTicks = 0;
        _viewSent = true;
        var centre = SpawnPoint;
        foreach (ref readonly var e in _localPlayers.GetEntities())
        {
            var t = e.Get<Transform>().Position;
            centre = new Vector3(t.X, t.Y, t.Z);
        }
        _host.SetView(centre, Viewing ? Host.ViewRadius : 0);
    }

    // ── what the Host tells it ──────────────────────────────────────────────

    public void Spawn(in SpawnMessage spawn) => _spawns.Add(spawn);

    /// <summary>Out of view: the copy goes (it's still in the game; it comes back as a spawn if it comes back into view).</summary>
    public void Forget(EntityId id)
    {
        if (_spawns.Cancel(id) is null && Registry.TryGet(id, out var e)) Hierarchy.DestroyRecursive(e);
        Commands.ForgetEvents(id);
    }

    public void ReceiveEvent(in EventMessage evt)
    {
        if (!_spawns.Hold(evt.Meta, evt.Handler, evt.Payload)) Commands.ReceiveEvent(evt.Meta, evt.Handler, evt.Payload.ToArray());
    }

    public void ReceiveCommand(in CommandMessage command)
    {
        if (command.To == Session.LocalPeer) Commands.ReceiveCommand(command.From, command.Handler, command.Seq, command.Payload.ToArray());
    }

    public void Rejected(in Rejection rejection) => Commands.ReceiveRejection(rejection.Authority, rejection.Seq);

    public void ReceiveFrame(uint tick, IReadOnlyList<BodySnapshot> snapshots) => Bodies?.ReceiveFrame(tick, snapshots);

    public void Pong(in TimePong pong) => ClockSync?.OnPong(pong, NowMs);

    public void IdBlock(uint first, uint count)
    {
        Registry.AddIdBlock(first, count);
        _idRequested = false;
    }

    public void PlayerNotice(in PlayerNotice notice) => Console.WriteLine($"[net] {notice.Name} {(notice.Joined ? "joined" : "left")}");

    public void Disconnected(string reason)
    {
        Console.WriteLine($"[net] disconnected from the host: {reason}");
        _left = true;
        Ended?.Invoke(reason);
    }

    public void ReceiveInput(in PlayerInputMessage input)
    {
        if (_inputs is not null && Registry.TryGet(input.Player, out var player)) _inputs.Receive(player, input);
    }

    /// <summary>Answered in its next update (<see cref="AnswerHost"/>), from its world as it is then.</summary>
    public void Release(EntityId id) { if (IsAuthority) _toRelease.Add(id); }

    /// <inheritdoc cref="Release"/>
    public void Describe(EntityId id) { if (IsAuthority) _toDescribe.Add(id); }

    public void ChunkData(in ChunkMessage chunk) => _chunks?.Received(chunk);

    /// <summary>Answered at once, so the save on exit needs no more ticks.</summary>
    public void Save()
    {
        if (!IsAuthority) return;
        foreach (var e in Commands.Describe(_describable.GetEntities().ToArray())) _host.EntitySaved(Description(MessageKind.Saved, e));
        foreach (var p in _spawns.All) _host.EntitySaved(Description(MessageKind.Saved, p));
        _host.SaveDone();
    }

    // ── entities coming and going ───────────────────────────────────────────

    /// <summary>What the Host asked of the authority since its last update: Descriptions, and releases.</summary>
    private void AnswerHost()
    {
        foreach (var id in _toDescribe)
        {
            if (_spawns.Find(id) is { } pending) _host.EntityDescribed(Description(MessageKind.Described, pending));
            else if (Registry.TryGet(id, out var e)) _host.EntityDescribed(Description(MessageKind.Described, Commands.Describe(e)));
        }
        _toDescribe.Clear();
        foreach (var id in _toRelease) ReleaseNow(id);
        _toRelease.Clear();
    }

    /// <summary>The Host has released it: its last Description, then it's despawned.</summary>
    private void ReleaseNow(EntityId id)
    {
        if (_spawns.Cancel(id) is { } pending)
        {
            _host.EntityReleased(Description(MessageKind.Released, pending));
            return;
        }
        if (!Registry.TryGet(id, out var e)) return;
        _host.EntityReleased(Description(MessageKind.Released, Commands.Describe(e)));
        Commands.Send(new DespawnEntity { Entity = id, KeepStored = true });
    }

    /// <summary>On the authority: what it made since the last tick, which the Host hasn't heard of.</summary>
    private void AnnounceCreated()
    {
        foreach (ref readonly var e in _created.GetEntities())
        {
            var id = e.Get<EntityId>();
            if (_fromHost.Remove(id) || !IsAuthority) continue;
            _host.EntityCreated(Description(MessageKind.Created, Commands.Describe(e)));
        }
        _created.Complete();
    }

    private void OnApplied(CommandHandlerBase handler, EventMeta meta, object evt)
    {
        if (IsAuthority && evt is EditVoxels edit && edit.Volume == EntityRegistry.WorldVolume) DescribeChunks(edit);
        if (evt is not DespawnEntity despawn) return;
        Commands.ForgetEvents(despawn.Entity);
        // Gone for good (deleted, broken up), not released: out of the save too. Told next tick, after its event.
        if (IsAuthority && !despawn.KeepStored) _deleted.Add(despawn.Entity);
    }

    /// <summary>On the authority, as it applies a terrain edit (before its event goes anywhere): each chunk it changed,
    /// for the Host to keep.</summary>
    private void DescribeChunks(EditVoxels edit)
    {
        if (Registry.Find(EntityRegistry.WorldVolume) is not { } root) return;
        var volume = root.Get<ChunkGrid>().Volume;
        _editedChunks.Clear();
        foreach (var op in edit.Ops)
        {
            var lo = ChunkPosition.FromVoxel(op.Min);
            var hi = ChunkPosition.FromVoxel(op.Max);
            for (int z = lo.Z; z <= hi.Z; z++)
            for (int y = lo.Y; y <= hi.Y; y++)
            for (int x = lo.X; x <= hi.X; x++)
                _editedChunks.Add(new ChunkPosition(x, y, z));
        }
        foreach (var pos in _editedChunks)
            if (volume.GetData(pos) is { } data)
                _host.ChunkEdited(new ChunkMessage(MessageKind.ChunkEdited, pos, StaticWorldSerializer.ToBytes(data)));
    }

    private DescriptionMessage Description(MessageKind message, EntityDescription d)
    {
        Vector3? position = d.Entity.Has<Transform>() ? Where(d.Entity) : null;
        return new DescriptionMessage(message, d.Id, d.Kind, Commands.LastEventNumber(Session.LocalPeer, d.Id), position, d.Data);
    }

    private static DescriptionMessage Description(MessageKind message, SpawnQueue.Pending p) =>
        new(message, p.Id, p.Kind, p.EventNumber, p.Position, p.Data);

    /// <summary>Where an entity is: its body if it has one (a grid's centre of mass, which may be well away from its block
    /// origin), else its Transform.</summary>
    private static Vector3 Where(Entity e)
    {
        ref readonly var t = ref e.Get<Transform>();
        return e.Has<PhysicsBodyComponent>() ? e.Get<PhysicsBodyComponent>().BodyPosition(t) : new Vector3(t.Position.X, t.Position.Y, t.Position.Z);
    }

    // ── what it sends: everything goes through the Host ─────────────────────

    public void SendCommand(PeerId authority, ushort handlerId, uint seq, ReadOnlySpan<byte> payload) =>
        _host.SendCommand(new CommandMessage(authority, Session.LocalPeer, handlerId, seq, payload));

    /// <summary>Events go to the Host, which passes them on; but spawns never do: the Host spawns entities from their
    /// Descriptions (one it makes here is described next tick, see <see cref="AnnounceCreated"/>).</summary>
    public void BroadcastEvent(ushort handlerId, in EventMeta meta, ReadOnlySpan<byte> payload)
    {
        if (Commands.HandlerFor(handlerId) is ISpawnHandler) return;
        _host.SendEvent(new EventMessage(handlerId, meta, payload));
    }

    public void SendRejection(PeerId to, uint seq) => _host.Reject(new Rejection(to, Session.LocalPeer, seq));

    /// <summary>Snapshots of the bodies it simulates (see <see cref="BodySync"/>).</summary>
    internal void SendFrame(uint tick, IReadOnlyList<BodySnapshot> snapshots) => _host.SendFrame(tick, snapshots);

    /// <summary>The player's latest inputs, for its authority to move them by (see <see cref="OwnPlayerPrediction"/>).</summary>
    internal void SendInput(in PlayerInputMessage message) => _host.SendInput(message);

    public void Dispose()
    {
        if (_left) return;
        _left = true;
        _host.Leave("left");
    }
}
