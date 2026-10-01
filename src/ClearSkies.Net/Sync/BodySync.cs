using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Session;
using ClearSkies.Net.Transport;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Net.Sync;

/// <summary>
/// Body sync: every second tick (30 Hz), each machine snapshots the bodies it owns, relative to their support, with
/// streamed values (a player's look), and sends them unreliably: clients to the host, which passes each client's on to
/// the others as they arrive (HostSession), and sends its own to everyone. Receivers buffer them per entity
/// (<see cref="RemoteBody"/>, which every synced body owned elsewhere has from when its owner is set) and draw them about
/// 100 ms behind (<see cref="RemoteBodySystem"/>). A lost packet is simply replaced by the next one. Runs last in the
/// tick, after support.
/// <para>A grid's pose, and a pose on a grid, is its block space's (its Transform's), which no edit moves; its body
/// sits at its centre of mass inside that, which edits do move, and when an edit reaches each machine isn't when any
/// snapshot does.</para>
/// </summary>
public sealed class BodySync : ISystem, IDebugUiSystem
{
    public const int SnapshotsPerPacket = 20;

    private readonly NetSession _net;
    private readonly PhysicsWorld _physics;
    private readonly EntitySet _players;
    private readonly EntitySet _grids;
    private readonly EntitySet _remote;
    private readonly List<BodySnapshot> _own = new();
    private readonly NetWriter _writer = new(2048);
    private long _snapshotsSent, _snapshotsReceived;

    public BodySync(NetSession net, World world, PhysicsWorld physics)
    {
        _net = net;
        _physics = physics;
        net.Bodies = this;
        _remote = world.GetEntities().With<RemoteBody>().AsSet();
        // Snapshots' lateness is measured against our clock: when clock sync snaps it, they move with it.
        if (net is ClientSession client)
            client.ClockSync.Snapped += ticks =>
            {
                foreach (ref readonly var e in _remote.GetEntities()) e.Get<RemoteBody>().Buffer.ShiftClock(ticks);
            };
        _players = world.GetEntities().With<EntityId>().With<NetOwner>().With<Player>().With<Transform>().AsSet();
        _grids = world.GetEntities().With<EntityId>().With<NetOwner>().With<PhysicsBodyComponent>().With<Transform>().AsSet();
        // Spawned owned elsewhere, or taken over by the host when its owner leaves.
        world.SubscribeComponentAdded((in Entity e, in NetOwner owner) => SetRemote(e, owner));
        world.SubscribeComponentChanged((in Entity e, in NetOwner _, in NetOwner owner) => SetRemote(e, owner));
    }

    /// <summary>Whether grids are synced too (on by default; players always are).</summary>
    public bool SyncGrids { get; init; } = true;

    /// <summary>A synced body owned elsewhere is drawn from its snapshots; one owned here is the truth.</summary>
    private void SetRemote(Entity e, in NetOwner owner)
    {
        bool synced = e.Has<Player>() || (SyncGrids && e.Has<DynamicGrid>());
        bool remote = synced && !owner.IsLocal;
        if (remote && !e.Has<RemoteBody>()) e.Set(new RemoteBody { Buffer = new SnapshotBuffer(), Jumped = true });
        else if (!remote && e.Has<RemoteBody>()) e.Remove<RemoteBody>();
    }

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
                SendFrames(tick, _own, packet =>
                {
                    foreach (var peer in host.Joined) host.SendUnreliable(peer.Peer, packet);
                });
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
        var s = new BodySnapshot { Entity = e.Get<EntityId>(), Epoch = e.Get<NetOwner>().Epoch, Rotation = Quaternion.Identity };
        ref readonly var t = ref e.Get<Transform>();
        s.Position = new Vector3(t.Position.X, t.Position.Y, t.Position.Z);
        if (e.Has<Support>() && e.Get<Support>() is { HasSupporter: true } support && support.Supporter.Has<EntityId>())
        {
            s.Support = support.Supporter.Get<EntityId>();
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
        ref readonly var pb = ref e.Get<PhysicsBodyComponent>();
        var body = pb.Body;
        var (p, q) = _physics.GetBodyPose(body);
        return new BodySnapshot
        {
            Entity = e.Get<EntityId>(),
            Epoch = e.Get<NetOwner>().Epoch,
            Position = PhysicsConv.ToBepu(pb.EntityPosition(p, q)), // its block space, which edits don't move
            Rotation = q,
            LinearVelocity = _physics.GetBodyLinearVelocity(body),
            AngularVelocity = _physics.GetBodyAngularVelocity(body),
        };
    }

    /// <summary>A frame of snapshots: buffer each on its entity.</summary>
    public void ReceiveFrame(ref NetReader r)
    {
        uint tick = r.ReadUInt32();
        int count = r.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            var s = BodySnapshot.Read(ref r);
            _snapshotsReceived++;
            if (!_net.Registry.TryGet(s.Entity, out var e)) continue; // not spawned here (yet)
            if (!e.Has<RemoteBody>()) continue; // ours: we're the truth
            e.Get<RemoteBody>().Buffer.Add(tick, s, _net.Clock.Tick);
        }
    }

    public string DebugName => "Body sync";

    public void DrawDebugUi() => ImGui.Text($"Snapshots sent {_snapshotsSent:N0}, received {_snapshotsReceived:N0}; bodies owned here {_own.Count}");
}

/// <summary>
/// Each tick, after physics: puts bodies owned elsewhere where their snapshots say they were
/// <see cref="SnapshotBuffer.Delay"/> ticks ago, each as little behind as keeps a newer snapshot in hand, in their
/// support's space (so a player standing on a moving ship stays on its deck). Players face the way they look. That's
/// their Transform on this machine, the pose their physics copies are moved to (<see cref="FollowerSystem"/>); they're
/// drawn between ticks like anything simulated here (TickInterpolationSystem), which comes to the same thing as
/// sampling the snapshots every frame, <see cref="ITickClock.Alpha"/> of a tick later.
/// </summary>
public sealed class RemoteBodySystem : ISystem
{
    private readonly EntitySet _remote;
    private readonly EntityRegistry _registry;
    private readonly ITickClock _clock;

    public RemoteBodySystem(World world, EntityRegistry registry, ITickClock clock)
    {
        _remote = world.GetEntities().With<RemoteBody>().With<Transform>().AsSet();
        _registry = registry;
        _clock = clock;
    }

    /// <summary>Extra delay, in ticks, on top of what each body needs (a debug setting).</summary>
    public double Margin { get; set; }

    /// <summary>The tick <paramref name="buffer"/>'s body is at in tick <see cref="ITickClock.Tick"/>.</summary>
    public double SampleTick(SnapshotBuffer buffer) => _clock.Tick - buffer.Delay;

    /// <summary>The longest and shortest delay remote bodies are drawn with, in ticks (for the network panel).</summary>
    public (double Least, double Most) Delays { get; private set; }

    public void Update(float dt)
    {
        double least = double.MaxValue, most = 0;
        // Grids before players: a player standing on a ship is placed on the ship as it is this tick.
        for (int pass = 0; pass < 2; pass++)
        foreach (ref readonly var e in _remote.GetEntities())
        {
            if (e.Has<Player>() != (pass == 1)) continue;
            ref var remote = ref e.Get<RemoteBody>();
            var buffer = remote.Buffer;
            if (buffer.Count == 0) continue; // nothing heard yet
            if (buffer.At(SampleTick(buffer)) is { } s)
            {
                var (position, rotation) = ToWorld(s.Support, s.Position, s.Rotation);
                ref var t = ref e.Get<Transform>();
                t.Position = new Vector3D<float>(position.X, position.Y, position.Z);
                if (e.Has<Player>())
                {
                    // What they stand on, as their machine has it (their own SupportSystem keeps it), so that describing
                    // them here (saving them as they leave, say) keeps it too.
                    if (e.Has<Support>()) e.Get<Support>().Supporter = TryGetSupport(s.Support, out var on) ? on : default;
                    if (s.HasLook && e.Has<MouseLookComponent>())
                    {
                        ref var look = ref e.Get<MouseLookComponent>();
                        (look.Yaw, look.Pitch) = (s.Look.Yaw, s.Look.Pitch);
                        t.Rotation = look.BodyRotation; // the body turns, the head nods
                    }
                }
                else t.Rotation = new Quaternion<float>(rotation.X, rotation.Y, rotation.Z, rotation.W);
                // Its first pose, or one after the delay jumped: appear there, don't slide there over the tick.
                if (remote.Jumped && e.Has<InterpolatedTransform>()) e.Get<InterpolatedTransform>().Teleport();
                remote.Jumped = false;
            }

            // For the next tick, which the physics copies are moved to before this runs again.
            buffer.Margin = Margin;
            if (buffer.UpdateDelay(1)) remote.Jumped = true;
            (least, most) = (System.Math.Min(least, buffer.Delay), System.Math.Max(most, buffer.Delay));
        }
        Delays = most > 0 ? (least, most) : (0, 0);
    }

    /// <summary>A pose in a support's space (its block space if it's a grid), in world space, with the support as it
    /// is now.</summary>
    /// <summary>The entity a snapshot's position is relative to, if it has one here.</summary>
    public bool TryGetSupport(EntityId support, out Entity entity)
    {
        entity = default;
        return !support.IsNone && _registry.TryGet(support, out entity);
    }

    public (Vector3 Position, Quaternion Rotation) ToWorld(EntityId support, Vector3 position, Quaternion rotation)
    {
        if (support.IsNone || !_registry.TryGet(support, out var s) || !s.Has<Transform>()) return (position, rotation);
        ref readonly var st = ref s.Get<Transform>();
        var sr = new Quaternion(st.Rotation.X, st.Rotation.Y, st.Rotation.Z, st.Rotation.W);
        return (new Vector3(st.Position.X, st.Position.Y, st.Position.Z) + Vector3.Transform(position, sr), sr * rotation);
    }
}
