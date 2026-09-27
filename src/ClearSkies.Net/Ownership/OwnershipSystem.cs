using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using ClearSkies.Net.Protocol;
using ClearSkies.Net.Session;
using ClearSkies.Net.Sync;
using ClearSkies.Net.Transport;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Net.Ownership;

/// <summary>The ownership messages on the wire.</summary>
public static class OwnershipMessages
{
    public static void WriteRelease(NetWriter w, uint entity, PeerId to, ushort epoch)
    {
        w.WriteByte((byte)MessageKind.OwnershipRelease);
        w.WriteUInt32(entity);
        w.WriteUInt32(to.Value);
        w.WriteUInt16(epoch);
    }

    public static (uint Entity, PeerId To, ushort Epoch) ReadRelease(ref NetReader r) => (r.ReadUInt32(), new PeerId(r.ReadUInt32()), r.ReadUInt16());

    public static void WriteHandover(NetWriter w, uint tick, in BodySnapshot snapshot)
    {
        w.WriteByte((byte)MessageKind.HandoverSnapshot);
        w.WriteUInt32(tick);
        snapshot.Write(w);
    }

    public static (uint Tick, BodySnapshot Snapshot) ReadHandover(ref NetReader r) => (r.ReadUInt32(), BodySnapshot.Read(ref r));

    public static void WriteChanged(NetWriter w, uint entity, PeerId to, ushort epoch, uint tick, BodySnapshot? snapshot)
    {
        w.WriteByte((byte)MessageKind.OwnershipChanged);
        w.WriteUInt32(entity);
        w.WriteUInt32(to.Value);
        w.WriteUInt16(epoch);
        w.WriteUInt32(tick);
        w.WriteBool(snapshot.HasValue);
        snapshot?.Write(w);
    }

    public static (uint Entity, PeerId To, ushort Epoch, uint Tick, BodySnapshot? Snapshot) ReadChanged(ref NetReader r)
    {
        uint entity = r.ReadUInt32();
        var to = new PeerId(r.ReadUInt32());
        ushort epoch = r.ReadUInt16();
        uint tick = r.ReadUInt32();
        BodySnapshot? s = r.ReadBool() ? BodySnapshot.Read(ref r) : null;
        return (entity, to, epoch, tick, s);
    }

    public static void WriteBubbleOwners(NetWriter w, IReadOnlyDictionary<PeerId, PeerId> owners)
    {
        w.WriteByte((byte)MessageKind.BubbleOwners);
        w.WriteVarUInt((uint)owners.Count);
        foreach (var (player, owner) in owners) { w.WriteUInt32(player.Value); w.WriteUInt32(owner.Value); }
    }

    public static Dictionary<PeerId, PeerId> ReadBubbleOwners(ref NetReader r)
    {
        uint count = r.ReadVarUInt();
        if (count > 256) throw new InvalidDataException($"{count} bubble owners.");
        var owners = new Dictionary<PeerId, PeerId>();
        for (int i = 0; i < count; i++) owners[new PeerId(r.ReadUInt32())] = new PeerId(r.ReadUInt32());
        return owners;
    }
}

/// <summary>
/// Moves entities between owners. Only the host decides (its <see cref="BubbleManager"/>, or a player leaving); each
/// assignment has a new epoch. If the host owns the entity (or its owner has gone) it hands it over at once; otherwise it
/// asks the owner to let go, and the owner stops simulating it and sends its body as it was (the handover snapshot).
/// Either way the host then tells everyone who has the entity its new owner, with that snapshot:
/// <list type="bullet">
/// <item>The new owner starts simulating it from the snapshot, carried forward to now, taking the local players
/// standing on it along, and eases its drawn pose over from where it was (<see cref="HandoverBlend"/>).</item>
/// <item>The old owner draws and follows it from snapshots again, starting from its own last one.</item>
/// <item>Everyone else just carries on with the new owner's snapshots.</item>
/// </list>
/// Commands that reach the old owner after it let go are forwarded to the new one (<c>RunRemoteCommand</c>).
/// </summary>
public sealed class OwnershipSystem : ISystem, IDebugUiSystem
{
    /// <summary>How long the host waits for an owner's handover snapshot before going ahead with its own copy.</summary>
    public const int HandoverTimeoutTicks = 60;

    private readonly NetSession _net;
    private readonly EntitySet _characters;
    private readonly Dictionary<uint, (PeerId To, ushort Epoch, uint Since)> _releasing = new(); // host: asked, waiting
    private readonly NetWriter _writer = new();

    public OwnershipSystem(NetSession net, World world)
    {
        _net = net;
        net.Ownership = this;
        _characters = world.GetEntities().With<Support>().With<CharacterControllerComponent>().With<NetOwner>().AsSet();
    }

    public long Handovers { get; private set; }
    public long TimedOut { get; private set; }

    /// <summary>Whether the host is waiting for this entity's owner to let go of it.</summary>
    public bool InHandover(uint entity) => _releasing.ContainsKey(entity);

    public void Update(float dt)
    {
        if (_net is not HostSession host || _releasing.Count == 0) return;
        foreach (var (id, (to, epoch, since)) in _releasing.ToList())
        {
            bool ownerGone = _net.Registry.TryGet(id, out var e) && host.PeerById(e.Get<NetOwner>().Owner) is not { State: PeerState.Joined };
            if (!ownerGone && _net.Clock.Tick - since < HandoverTimeoutTicks) continue;
            _releasing.Remove(id);
            if (!e.IsAlive) continue;
            // The owner never answered (or left): go ahead from our own copy.
            TimedOut++;
            var snapshot = _net.Bodies?.SnapshotOf(e) ?? default;
            Complete(host, e, to, epoch, _net.Clock.Tick, snapshot);
        }
    }

    // ── the host ────────────────────────────────────────────────────────────

    /// <summary>The host: gives <paramref name="e"/> to <paramref name="to"/>.</summary>
    public void Assign(Entity e, PeerId to)
    {
        var host = (HostSession)_net;
        uint id = e.Get<NetId>().Value;
        var owner = e.Get<NetOwner>();
        if (owner.Owner == to || _releasing.ContainsKey(id)) return;
        ushort epoch = (ushort)(owner.Epoch + 1);
        if (owner.IsLocal || host.PeerById(owner.Owner) is not { State: PeerState.Joined })
        {
            var snapshot = _net.Bodies?.SnapshotOf(e) ?? default;
            Complete(host, e, to, epoch, _net.Clock.Tick, snapshot);
            return;
        }
        _releasing[id] = (to, epoch, _net.Clock.Tick);
        _writer.Clear();
        OwnershipMessages.WriteRelease(_writer, id, to, epoch);
        host.SendReliable(owner.Owner, _writer.Written);
    }

    /// <summary>The host: an owner's snapshot as it let go.</summary>
    public void ReceiveHandover(PeerId from, ref NetReader r)
    {
        var (tick, snapshot) = OwnershipMessages.ReadHandover(ref r);
        if (!_releasing.TryGetValue(snapshot.Entity, out var pending) || pending.Epoch != snapshot.Epoch) return;
        _releasing.Remove(snapshot.Entity);
        if (_net.Registry.TryGet(snapshot.Entity, out var e)) Complete((HostSession)_net, e, pending.To, pending.Epoch, tick, snapshot);
    }

    private void Complete(HostSession host, Entity e, PeerId to, ushort epoch, uint tick, in BodySnapshot snapshot)
    {
        var s = snapshot;
        s.Epoch = epoch;
        Change(e, to, epoch, tick, s);
        uint id = e.Get<NetId>().Value;
        _writer.Clear();
        OwnershipMessages.WriteChanged(_writer, id, to, epoch, tick, s);
        foreach (var peer in host.Joined)
            if (peer.Known.Contains(id) || peer.Peer == to) host.SendReliable(peer.Peer, _writer.Written);
    }

    /// <summary>The host: a client has just been sent a description of <paramref name="e"/> (which doesn't carry the
    /// epoch): tell them the epoch too, so they know which snapshots are current.</summary>
    public void SendEpoch(RemotePeer peer, Entity e)
    {
        var owner = e.Get<NetOwner>();
        if (owner.Epoch == 0) return;
        _writer.Clear();
        OwnershipMessages.WriteChanged(_writer, e.Get<NetId>().Value, owner.Owner, owner.Epoch, _net.Clock.Tick, null);
        ((HostSession)_net).SendReliable(peer.Peer, _writer.Written);
    }

    /// <summary>The host: a player has left. What they owned is the host's (their player too, until it's despawned);
    /// the bubble manager moves it on from there.</summary>
    public void PeerLeft(PeerId peer)
    {
        foreach (var e in _net.World.GetEntities().With<NetOwner>().With<NetId>().AsEnumerable().ToList())
        {
            if (e.Get<NetOwner>().Owner != peer) continue;
            _releasing.Remove(e.Get<NetId>().Value);
            if (e.Has<Player>()) e.Set(_net.Session.LocalOwner((ushort)(e.Get<NetOwner>().Epoch + 1)));
            else Assign(e, PeerId.Host);
        }
        // Handovers to them: finish them to the host instead.
        foreach (var (id, pending) in _releasing.ToList())
            if (pending.To == peer) _releasing[id] = pending with { To = PeerId.Host };
    }

    // ── clients ─────────────────────────────────────────────────────────────

    /// <summary>A client: the host wants an entity we own handed to someone else.</summary>
    public void ReceiveRelease(ref NetReader r)
    {
        var (id, to, epoch) = OwnershipMessages.ReadRelease(ref r);
        if (!_net.Registry.TryGet(id, out var e) || !e.Get<NetOwner>().IsLocal) return; // the host times out and carries on
        if (!BodySync.Older(e.Get<NetOwner>().Epoch, epoch)) return;
        var snapshot = _net.Bodies?.SnapshotOf(e) ?? default;
        snapshot.Epoch = epoch;
        uint tick = _net.Clock.Tick;
        Change(e, to, epoch, tick, snapshot);
        _writer.Clear();
        OwnershipMessages.WriteHandover(_writer, tick, snapshot);
        ((ClientSession)_net).SendToHost(_writer.Written, Channel.Reliable);
    }

    /// <summary>A client: an entity's new owner.</summary>
    public void ReceiveChanged(ref NetReader r)
    {
        var (id, to, epoch, tick, snapshot) = OwnershipMessages.ReadChanged(ref r);
        if (!_net.Registry.TryGet(id, out var e) || !e.Has<NetOwner>()) return;
        var owner = e.Get<NetOwner>();
        if (owner.Owner == to && owner.Epoch == epoch) return; // already (we let go of it ourselves)
        if (owner.Epoch != 0 && !BodySync.Older(owner.Epoch, epoch)) return;
        Change(e, to, epoch, tick, snapshot);
    }

    public void ReceiveBubbleOwners(ref NetReader r)
    {
        var owners = OwnershipMessages.ReadBubbleOwners(ref r);
        _net.Session.BubbleOwners.Clear();
        foreach (var (player, owner) in owners) _net.Session.BubbleOwners[player] = owner;
    }

    // ── every machine ───────────────────────────────────────────────────────

    private void Change(Entity e, PeerId to, ushort epoch, uint tick, BodySnapshot? snapshot)
    {
        var before = e.Get<NetOwner>();
        var after = _net.Session.OwnerFor(to, epoch);
        e.Set(after);
        Handovers++;
        if (e.Has<Player>()) return; // players only change hands as they leave
        Console.WriteLine($"[net] entity {e.Get<NetId>().Value} now simulated by {(after.IsLocal ? "us" : to.ToString())} (epoch {epoch})");
        if (!before.IsLocal && after.IsLocal) TakeUp(e, tick, snapshot);
        else if (before.IsLocal && !after.IsLocal)
        {
            // Followed from snapshots again, starting with ours: drawn from there until the new owner's arrive.
            var buffer = new SnapshotBuffer();
            if (snapshot is { } s) buffer.Add(tick, s);
            e.Set(new RemoteBody { Buffer = buffer });
        }
        else if (snapshot is { } s && e.Has<RemoteBody>()) e.Get<RemoteBody>().Buffer.Add(tick, s);
    }

    /// <summary>Starts simulating an entity handed to this machine, from its handover snapshot carried forward to now.</summary>
    private void TakeUp(Entity e, uint tick, BodySnapshot? snapshot)
    {
        if (!e.Has<Transform>()) return;
        ref var t = ref e.Get<Transform>();
        var drawnPosition = t.Position;
        var drawnRotation = t.Rotation;
        var s = snapshot ?? _net.Bodies?.SnapshotOf(e) ?? default;
        if (snapshot is null) tick = _net.Clock.Tick;

        float ahead = System.Math.Clamp((int)(_net.Clock.Tick - tick), 0, 30) / 60f;
        var position = s.Position + s.LinearVelocity * ahead;
        var rotation = Integrate(s.Rotation, s.AngularVelocity, ahead);

        // Where the body is here now (a kinematic copy, or just drawn): anyone of ours standing on it moves with it.
        var (oldPosition, oldRotation) = e.Has<PhysicsBodyComponent>() && _net.Bodies?.Physics is { } physics
            ? physics.GetBodyPose(e.Get<PhysicsBodyComponent>().Body)
            : (new Vector3(t.Position.X, t.Position.Y, t.Position.Z), new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W));
        CarryRiders(e, oldPosition, oldRotation, position, rotation);

        t.Position = new Vector3D<float>(position.X, position.Y, position.Z);
        t.Rotation = new Quaternion<float>(rotation.X, rotation.Y, rotation.Z, rotation.W);
        e.Set(new BodyStateOverride { LinearVelocity = s.LinearVelocity, AngularVelocity = s.AngularVelocity });
        if (e.Has<PhysicsPresence>()) e.Set(new PhysicsPresence { Mode = PhysicsMode.Simulated }); // dynamic from this tick
        if (e.Has<RemoteBody>()) e.Remove<RemoteBody>();

        // Drawn: eased over from where it was drawn.
        var newRotation = t.Rotation;
        e.Set(new HandoverBlend
        {
            Offset = drawnPosition - t.Position,
            Rotation = drawnRotation * Quaternion<float>.Inverse(newRotation),
            Seconds = HandoverBlend.Duration,
        });
    }

    private void CarryRiders(Entity ship, Vector3 oldPosition, Quaternion oldRotation, Vector3 newPosition, Quaternion newRotation)
    {
        if (Vector3.DistanceSquared(oldPosition, newPosition) < 1e-6f && oldRotation == newRotation) return;
        var turn = newRotation * Quaternion.Conjugate(oldRotation);
        foreach (ref readonly var c in _characters.GetEntities())
        {
            if (!c.Get<NetOwner>().IsLocal || c.Get<Support>().Supporter != ship) continue;
            var character = c.Get<CharacterControllerComponent>().Character;
            var at = character.Position;
            var velocity = character.LinearVelocity;
            character.TeleportTo(newPosition + Vector3.Transform(at - oldPosition, turn));
            character.SetVelocity(velocity);
        }
    }

    private static Quaternion Integrate(Quaternion q, Vector3 angularVelocity, float seconds)
    {
        float angle = angularVelocity.Length() * seconds;
        if (angle < 1e-6f) return q;
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(angularVelocity), angle) * q);
    }

    public string DebugName => "Ownership";

    public void DrawDebugUi()
    {
        ImGui.Text($"Ownership changes seen {Handovers}, waiting on {_releasing.Count}, timed out {TimedOut}");
        if (_net.Session.BubbleOwners.Count > 0)
            ImGui.Text("Bubble owners: " + string.Join(", ", _net.Session.BubbleOwners.Select(p => $"{p.Key.Value}→{p.Value.Value}")));
    }
}
