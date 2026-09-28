using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Serialization;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Session;
using ClearSkies.Net.Transport;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Net.Sync;

/// <summary>
/// Body sync: every second tick (30 Hz), each machine snapshots the bodies it owns, relative to their support, with
/// streamed values (a player's look), and sends them unreliably: clients to the host, which forwards each client's to
/// the others along with its own. Receivers buffer them per entity (<see cref="RemoteBody"/>) and draw them about
/// 100 ms behind (<see cref="RemoteBodySystem"/>). A lost packet is simply replaced by the next one. Runs last in the
/// tick, after support.
/// </summary>
public sealed class BodySync : ISystem, IDebugUiSystem
{
    public const int SnapshotsPerPacket = 20;

    private readonly NetSession _net;
    private readonly PhysicsWorld _physics;
    private readonly EntitySet _players;
    private readonly EntitySet _grids;
    private readonly List<BodySnapshot> _own = new();
    private readonly Dictionary<uint, (PeerId Owner, uint Tick, BodySnapshot Snapshot)> _relay = new();
    private readonly NetWriter _writer = new(2048);
    private readonly Dictionary<uint, uint> _shapeVersions = new(); // grid → event number of its last shape change
    private readonly List<(PeerId From, uint Tick, BodySnapshot Snapshot)> _parked = new();
    private long _snapshotsSent, _snapshotsReceived;

    public BodySync(NetSession net, World world, PhysicsWorld physics)
    {
        _net = net;
        _physics = physics;
        net.Bodies = this;
        // A grid's shape changes (its blocks, and with them its centre of mass, which is the body's origin) with its
        // EditVoxels and SpawnGrid events: its snapshots say which one they follow, so a receiver uses them only once
        // it has applied that one too.
        net.Commands.Applied += (handler, meta, _) =>
        {
            // Numbers are per authority, so only the ones we decide count: a grid just handed to us has none yet, and
            // its snapshots go straight in.
            if (handler.Id is Engine.Commands.CommandIds.EditVoxels or Engine.Commands.CommandIds.SpawnGrid)
            {
                if (meta.Authority == net.Session.LocalPeer) _shapeVersions[meta.Target] = meta.EventNumber;
                else _shapeVersions.Remove(meta.Target);
            }
        };
        _players = world.GetEntities().With<NetId>().With<NetOwner>().With<Player>().With<Transform>().AsSet();
        _grids = world.GetEntities().With<NetId>().With<NetOwner>().With<PhysicsBodyComponent>().With<Transform>().AsSet();
    }

    public PhysicsWorld Physics => _physics;

    /// <summary>Whether grids are synced too (on by default; players always are).</summary>
    public bool SyncGrids { get; set; } = true;

    public void Update(float dt)
    {
        FlushParked();
        if (_net.Clock.Tick % 2 != 0) return;
        _own.Clear();
        foreach (ref readonly var e in _players.GetEntities())
            if (e.Get<NetOwner>().IsLocal) _own.Add(PlayerSnapshot(e));
        if (SyncGrids)
            foreach (ref readonly var e in _grids.GetEntities())
                if (e.Get<NetOwner>().IsLocal) _own.Add(GridSnapshot(e));

        uint tick = _net.Clock.Tick;
        switch (_net)
        {
            case ClientSession client:
                SendFrames(tick, _own, packet => client.SendToHost(packet, Channel.Unreliable));
                break;
            case HostSession host:
                foreach (var peer in host.Joined)
                {
                    // Only what's in their window.
                    var frame = new List<BodySnapshot>(_own.Count);
                    foreach (var s in _own) if (peer.Known.Contains(s.Entity)) frame.Add(s);
                    foreach (var (id, relayed) in _relay)
                        if (relayed.Owner != peer.Peer && peer.Known.Contains(id)) frame.Add(relayed.Snapshot);
                    SendFrames(tick, frame, packet => host.SendUnreliable(peer.Peer, packet));
                }
                _relay.Clear();
                break;
        }
    }

    private delegate void PacketSender(ReadOnlySpan<byte> packet);

    private void SendFrames(uint tick, List<BodySnapshot> snapshots, PacketSender send)
    {
        for (int start = 0; start < snapshots.Count; start += SnapshotsPerPacket)
        {
            int count = System.Math.Min(SnapshotsPerPacket, snapshots.Count - start);
            _writer.Clear();
            _writer.WriteByte((byte)MessageKind.StateFrame);
            _writer.WriteUInt32(tick);
            _writer.WriteUInt16((ushort)count);
            for (int i = 0; i < count; i++) snapshots[start + i].Write(_writer);
            send(_writer.Written);
            _snapshotsSent += count;
        }
    }

    private BodySnapshot PlayerSnapshot(Entity e)
    {
        var s = new BodySnapshot { Entity = e.Get<NetId>().Value, Epoch = e.Get<NetOwner>().Epoch, Rotation = Quaternion.Identity };
        ref readonly var t = ref e.Get<Transform>();
        s.Position = new Vector3(t.Position.X, t.Position.Y, t.Position.Z);
        if (e.Has<Support>() && e.Get<Support>() is { HasSupporter: true } support && support.Supporter.Has<NetId>())
        {
            s.Support = support.Supporter.Get<NetId>().Value;
            s.Position = support.LocalPosition;
        }
        if (e.Has<CharacterControllerComponent>() && !e.Has<FreeFlying>())
            s.LinearVelocity = e.Get<CharacterControllerComponent>().Character.LinearVelocity;
        if (e.Has<MouseLookComponent>())
        {
            ref readonly var look = ref e.Get<MouseLookComponent>();
            s.Look = new LookAngles(MathF.IEEERemainder(look.Yaw, 2 * MathF.PI), look.Pitch);
            s.Flags |= SnapshotFlags.HasLook;
        }
        return s;
    }

    private BodySnapshot GridSnapshot(Entity e)
    {
        var body = e.Get<PhysicsBodyComponent>().Body;
        var (p, q) = _physics.GetBodyPose(body);
        uint id = e.Get<NetId>().Value;
        return new BodySnapshot
        {
            Entity = id,
            Epoch = e.Get<NetOwner>().Epoch,
            ShapeVersion = _shapeVersions.GetValueOrDefault(id),
            Position = p,
            Rotation = q,
            LinearVelocity = _physics.GetBodyLinearVelocity(body),
            AngularVelocity = _physics.GetBodyAngularVelocity(body),
        };
    }

    /// <summary>A frame from <paramref name="from"/>: buffer each snapshot on its entity, and (on the host) keep it to pass on.</summary>
    public void ReceiveFrame(PeerId from, ref NetReader r)
    {
        uint tick = r.ReadUInt32();
        int count = r.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            var s = BodySnapshot.Read(ref r);
            _snapshotsReceived++;
            if (!_net.Registry.TryGet(s.Entity, out var e)) continue; // not spawned here (yet)
            if (e.Has<NetOwner>() && e.Get<NetOwner>().IsLocal) continue; // ours: we're the truth
            if (e.Has<NetOwner>() && Older(s.Epoch, e.Get<NetOwner>().Epoch)) continue; // from before it changed hands
            if (_net is HostSession && from != PeerId.Host) _relay[s.Entity] = (from, tick, s);
            if (!ShapeApplied(e, s)) { _parked.Add((from, tick, s)); continue; } // its edit hasn't arrived yet
            Buffer(e, tick, s);
        }
    }

    private static void Buffer(Entity e, uint tick, in BodySnapshot s)
    {
        if (!e.Has<RemoteBody>()) e.Set(new RemoteBody { Buffer = new SnapshotBuffer() });
        e.Get<RemoteBody>().Buffer.Add(tick, s);
    }

    /// <summary>Whether this machine has applied the shape change a grid snapshot follows. With no event from its owner
    /// applied here at all (it arrived as a description, or has just changed hands) there's nothing to wait for.</summary>
    private bool ShapeApplied(Entity e, in BodySnapshot s)
    {
        if (s.ShapeVersion == 0 || !e.Has<NetOwner>()) return true;
        uint applied = _net.Commands.LastEventNumber(e.Get<NetOwner>().Owner, s.Entity);
        return applied == 0 || applied >= s.ShapeVersion;
    }

    /// <summary>Epochs wrap: <paramref name="a"/> is older than <paramref name="b"/> if it's behind by less than half the range.</summary>
    public static bool Older(ushort a, ushort b) => a != b && (ushort)(b - a) < 0x8000;

    /// <summary>An entity's body as this machine has it now (for handing it over): its physics body if it has one,
    /// else its Transform with the velocity of its last snapshot.</summary>
    public BodySnapshot SnapshotOf(Entity e)
    {
        if (e.Has<PhysicsBodyComponent>()) return GridSnapshot(e);
        ref readonly var t = ref e.Get<Transform>();
        var s = new BodySnapshot
        {
            Entity = e.Get<NetId>().Value,
            Epoch = e.Has<NetOwner>() ? e.Get<NetOwner>().Epoch : (ushort)0,
            Position = new Vector3(t.Position.X, t.Position.Y, t.Position.Z),
            Rotation = new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W),
        };
        if (e.Has<RemoteBody>() && e.Get<RemoteBody>().Buffer is { Latest: { } latest } buffer && latest.Support == 0)
        {
            // Where its owner has it now (as best we know), not where it's drawn, 100 ms behind.
            if (buffer.At(_net.Clock.Tick) is { } now) (s.Position, s.Rotation) = (now.Position, now.Rotation);
            s.LinearVelocity = latest.LinearVelocity;
            s.AngularVelocity = latest.AngularVelocity;
        }
        return s;
    }

    /// <summary>Snapshots held back for a shape change that has now been applied go into their buffers.</summary>
    private void FlushParked()
    {
        for (int i = _parked.Count - 1; i >= 0; i--)
        {
            var (_, tick, s) = _parked[i];
            if (!_net.Registry.TryGet(s.Entity, out var e)) { _parked.RemoveAt(i); continue; }
            if (!ShapeApplied(e, s)) continue;
            Buffer(e, tick, s);
            _parked.RemoveAt(i);
        }
        if (_parked.Count > 256) _parked.RemoveRange(0, _parked.Count - 256);
    }

    public int Parked => _parked.Count;

    public string DebugName => "Body sync";

    public void DrawDebugUi() => ImGui.Text($"Snapshots sent {_snapshotsSent:N0}, received {_snapshotsReceived:N0}; bodies owned here {_own.Count}");
}

/// <summary>
/// Every frame: draws bodies owned elsewhere from their snapshots, <see cref="InterpolationDelay"/> ticks behind the
/// host's current tick, interpolated in their support's space (so a player standing on a moving ship stays on its
/// deck). Players face the way they look.
/// </summary>
public sealed class RemoteBodySystem : ISystem
{
    private readonly EntitySet _remote;
    private readonly NetRegistry _registry;
    private readonly ITickClock _clock;

    public RemoteBodySystem(World world, NetRegistry registry, ITickClock clock)
    {
        _remote = world.GetEntities().With<RemoteBody>().With<Transform>().AsSet();
        _registry = registry;
        _clock = clock;
    }

    /// <summary>How far behind the host's tick remote bodies are drawn, in ticks: 6 (100 ms) by default.</summary>
    public double InterpolationDelay { get; set; } = 6;

    /// <summary>The tick remote bodies are drawn at this frame. Things simulated here are drawn between the last two
    /// ticks (a tick behind, see TickInterpolationSystem), so remote bodies are too, to line up with them.</summary>
    public double RenderTick => _clock.Tick + (double)_clock.Alpha - 1 - InterpolationDelay;

    /// <summary>The tick followers' bodies are placed at in tick <see cref="ITickClock.Tick"/>.</summary>
    public double PhysicsTick => _clock.Tick - InterpolationDelay;

    public void Update(float dt)
    {
        double renderTick = RenderTick;
        foreach (ref readonly var e in _remote.GetEntities())
        {
            if (e.Has<NetOwner>() && e.Get<NetOwner>().IsLocal) continue;
            if (e.Get<RemoteBody>().Buffer.At(renderTick) is not { } s) continue;
            var (position, rotation) = ToWorld(s.Support, s.Position, s.Rotation);
            if (e.Has<Support>())
            {
                // What it stands on, as its owner has it: kept so a description of it (a leaving player's save) is
                // relative to the ship it's on.
                ref var support = ref e.Get<Support>();
                support.Supporter = s.Support != 0 && _registry.TryGet(s.Support, out var supporter) ? supporter : default;
                support.LocalPosition = support.HasSupporter ? s.Position : position;
                support.LocalRotation = support.HasSupporter ? s.Rotation : rotation;
            }
            ref var t = ref e.Get<Transform>();
            t.Position = new Vector3D<float>(position.X, position.Y, position.Z);
            if (e.Has<Player>())
            {
                if (s.HasLook && e.Has<MouseLookComponent>())
                {
                    ref var look = ref e.Get<MouseLookComponent>();
                    (look.Yaw, look.Pitch) = (s.Look.Yaw, s.Look.Pitch);
                    t.Rotation = Quaternion<float>.CreateFromYawPitchRoll(s.Look.Yaw, 0, 0); // the body turns, the head nods
                }
            }
            else t.Rotation = new Quaternion<float>(rotation.X, rotation.Y, rotation.Z, rotation.W);
        }
    }

    /// <summary>A pose in a support's space, in world space (as the support is drawn now).</summary>
    public (Vector3 Position, Quaternion Rotation) ToWorld(uint support, Vector3 position, Quaternion rotation)
    {
        if (support == 0 || !_registry.TryGet(support, out var s) || !s.Has<Transform>()) return (position, rotation);
        ref readonly var st = ref s.Get<Transform>();
        var sr = new Quaternion(st.Rotation.X, st.Rotation.Y, st.Rotation.Z, st.Rotation.W);
        return (new Vector3(st.Position.X, st.Position.Y, st.Position.Z) + Vector3.Transform(position, sr), sr * rotation);
    }
}
