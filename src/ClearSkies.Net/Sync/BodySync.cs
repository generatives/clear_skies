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
    private long _snapshotsSent, _snapshotsReceived;

    public BodySync(NetSession net, World world, PhysicsWorld physics)
    {
        _net = net;
        _physics = physics;
        net.Bodies = this;
        _players = world.GetEntities().With<NetId>().With<NetOwner>().With<Player>().With<Transform>().AsSet();
        _grids = world.GetEntities().With<NetId>().With<NetOwner>().With<PhysicsBodyComponent>().With<Transform>().AsSet();
    }

    /// <summary>Snapshots of grids too (from N2; players only until then).</summary>
    public bool SyncGrids { get; set; }

    public void Update(float dt)
    {
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
                    var frame = new List<BodySnapshot>(_own);
                    foreach (var (id, relayed) in _relay)
                        if (relayed.Owner != peer.Peer) frame.Add(relayed.Snapshot);
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
        if (e.Has<CharacterControllerComponent>() && !(e.Has<CharacterModeComponent>() && e.Get<CharacterModeComponent>().FreeFly))
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
        return new BodySnapshot
        {
            Entity = e.Get<NetId>().Value,
            Epoch = e.Get<NetOwner>().Epoch,
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
            if (!e.Has<RemoteBody>()) e.Set(new RemoteBody { Buffer = new SnapshotBuffer() });
            e.Get<RemoteBody>().Buffer.Add(tick, s);
            if (_net is HostSession && from != PeerId.Host) _relay[s.Entity] = (from, tick, s);
        }
    }

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

    public double RenderTick => _clock.Tick + (double)_clock.Alpha - InterpolationDelay;

    public void Update(float dt)
    {
        double renderTick = RenderTick;
        foreach (ref readonly var e in _remote.GetEntities())
        {
            if (e.Has<NetOwner>() && e.Get<NetOwner>().IsLocal) continue;
            if (e.Get<RemoteBody>().Buffer.At(renderTick) is not { } s) continue;
            var (position, rotation) = ToWorld(s.Support, s.Position, s.Rotation);
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
