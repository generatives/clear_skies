using System.Numerics;
using BepuPhysics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Serialization;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// One hold between two volumes, kept by the lighter of the two (the rider; see <see cref="AnchorLinks"/>): what it's
/// held to (the heavier grid, or the terrain), where it sits on that, and whose anchors hold it. Who rides doesn't
/// depend on who anchored: a carrier whose anchors grab a dinghy holds it, but the dinghy still rides on the carrier, so
/// it's saved, loaded and placed on the carrier and not the other way round.
/// </summary>
public struct AnchorLink
{
    /// <summary>The grid it's held to, or <see cref="EntityRegistry.WorldVolume"/> for the terrain.</summary>
    public EntityId Target;

    /// <summary>Where the rider's block space (its Transform) is in the target's: in the world, for the terrain.</summary>
    public Vector3 LocalPosition;
    public Quaternion LocalRotation;

    /// <summary>Whose anchors hold it: the rider's own, the target's, or both. It's let go when neither does.</summary>
    public bool HeldByRider, HeldByTarget;

    public readonly bool Held => HeldByRider || HeldByTarget;

    /// <summary>Held to another grid, rather than the terrain.</summary>
    public readonly bool ToGrid => !Target.IsNone && Target != EntityRegistry.WorldVolume;

    /// <summary>The link that keeps a rider at <paramref name="rider"/> on <paramref name="target"/> as it is at
    /// <paramref name="onto"/>.</summary>
    public static AnchorLink Between(EntityId target, in Transform onto, in Transform rider)
    {
        var inverse = Quaternion.Conjugate(PhysicsConv.ToBepu(onto.Rotation));
        return new AnchorLink
        {
            Target = target,
            LocalPosition = Vector3.Transform(PhysicsConv.ToBepu(rider.Position - onto.Position), inverse),
            LocalRotation = Quaternion.Normalize(inverse * PhysicsConv.ToBepu(rider.Rotation)),
        };
    }

    /// <summary>Where the rider's block space is with its target at <paramref name="target"/>.</summary>
    public readonly Transform Place(in Transform target)
    {
        var rotation = PhysicsConv.ToBepu(target.Rotation);
        return new Transform
        {
            Position = target.Position + PhysicsConv.ToSilk(Vector3.Transform(LocalPosition, rotation)),
            Rotation = PhysicsConv.ToSilk(Quaternion.Normalize(rotation * LocalRotation)),
            Scale = Vector3D<float>.One,
        };
    }

    public readonly void Write(NetWriter w)
    {
        Target.Write(w);
        w.WriteVector3(LocalPosition);
        w.WriteQuaternion(LocalRotation);
        w.WriteByte((byte)((HeldByRider ? 1 : 0) | (HeldByTarget ? 2 : 0)));
    }

    public static AnchorLink Read(ref NetReader r)
    {
        var link = new AnchorLink { Target = EntityId.Read(ref r), LocalPosition = r.ReadVector3(), LocalRotation = r.ReadQuaternion() };
        byte held = r.ReadByte();
        (link.HeldByRider, link.HeldByTarget) = ((held & 1) != 0, (held & 2) != 0);
        return link;
    }
}

/// <summary>
/// On a grid held to anything (see <see cref="AnchorSystem"/>): its holds, one per volume it's held to, each to
/// something at least as heavy as it is (or the terrain), so following them never comes back round. The first one to a
/// grid is its support: what it's placed on when it spawns (see SpawnGridHandler), and what the Host loads before it and
/// releases it with, as a player's ship is theirs. In the grid's description, so it survives saving and sending.
/// </summary>
public struct AnchorLinks
{
    public List<AnchorLink> All;

    /// <summary>The grid it rides on, if it's held to one: the first, which is the heaviest it was held to at the time.</summary>
    public static AnchorLink? Support(IReadOnlyList<AnchorLink>? links)
    {
        if (links is null) return null;
        foreach (var link in links) if (link.ToGrid) return link;
        return null;
    }

    public static void Write(NetWriter w, IReadOnlyList<AnchorLink> links)
    {
        w.WriteByte((byte)links.Count);
        foreach (var link in links) link.Write(w);
    }

    public static List<AnchorLink> Read(ref NetReader r)
    {
        int count = r.ReadByte();
        var links = new List<AnchorLink>(count);
        for (int i = 0; i < count; i++) links.Add(AnchorLink.Read(ref r));
        return links;
    }
}

/// <summary>
/// Anchors ships, on the machine that simulates them. When a ship's toggles go on (<see cref="ShipControls.Anchored"/>)
/// and it holds nothing yet, each of its <see cref="Anchor"/> blocks looks for the nearest block that collides, on the
/// terrain or on another grid, within <see cref="Reach"/> of the anchor's centre, and the ship takes hold of every
/// volume one of them found: so one ship can hold several (a carrier's bomb bay, each anchor holding its own bomb).
/// Finding nothing, it turns the toggles back off, so the player sees that nothing caught. Turning them off lets go of
/// everything the ship holds; so does breaking its last anchor.
///
/// Each hold is kept by the lighter of the two (an <see cref="AnchorLink"/> in its <see cref="AnchorLinks"/>; the
/// terrain is heaviest of all), and while it's kept the lighter grid's body is welded (a Bepu
/// <see cref="BepuPhysics.Constraints.Weld"/>) to the other's, where it was when it took hold, so pushing one pushes
/// both as one rigid body would. The terrain has no body (its colliders are statics), so a grid held to it is welded to
/// a pin (<see cref="PhysicsWorld.AddPin"/>) standing where the link puts it; so is one whose other grid has no body
/// here yet, at that grid as it is (a ship spawned aboard another before the other's body exists is held there rather
/// than falling into it), or that's gone, where the ship is. A weld's offset follows each grid's centre of mass as
/// their blocks change, and it's rebuilt whenever either body is. Two kinematic bodies aren't welded (Bepu can't hold
/// either, and drops such a weld), so two locked grids are welded once one is unlocked.
///
/// Runs each tick after the commands and before the physics step, so a weld is in place before the first step either
/// body takes.
/// </summary>
public sealed class AnchorSystem : ISystem, IDisposable, IDebugUiSystem
{
    /// <summary>How far an anchor reaches: from its block's centre to the nearest point of a block it can hold on to.
    /// Anything touching it is half a block away, so it reaches across up to two and a half blocks of air.</summary>
    public const float Reach = 3f;

    // Weld offsets nearer than this to what's set (the centre of mass moving with an edit is far more) aren't re-sent.
    private const float OffsetEpsilon = 1e-4f;

    private readonly PhysicsWorld   _physics;
    private readonly CommandSystem  _commands;
    private readonly EntityRegistry _registry;
    private readonly EntitySet      _ships;
    private readonly EntitySet      _riders;
    private readonly EntitySet      _anchors;
    private readonly EntitySet      _volumes;
    private readonly IDisposable    _bodyRemoved, _disposed;

    private readonly Dictionary<(Entity Rider, EntityId Target), Held> _held = new();
    private readonly HashSet<(Entity, EntityId)> _touched = new();
    private readonly HashSet<Entity> _lettingGo = new(); // let go for want of a hold; the toggles' command is on its way
    private readonly Dictionary<Entity, List<Vector3D<int>>> _anchorsByShip = new();
    private readonly Dictionary<Entity, int> _holding = new(); // how many holds each grid's anchors keep
    private readonly List<Entity> _scratch = new();
    private readonly List<(Entity, EntityId)> _stale = new();
    private string _lastEvent = "";

    /// <summary>How a rider is held to one target: its body, welded to the target's body or to a pin.</summary>
    private sealed class Held
    {
        public BodyHandle Rider, Partner;
        public BodyHandle? Pin;
        public ConstraintHandle Weld;
        public bool Welded;
        public Vector3 Offset;
        public Quaternion Orientation;
    }

    public AnchorSystem(World world, PhysicsWorld physics, CommandSystem commands, EntityRegistry registry)
    {
        _physics  = physics;
        _commands = commands;
        _registry = registry;
        _ships    = world.GetEntities().With<DynamicGrid>().With<ChunkGrid>().With<Transform>().With<ShipControls>().With<EntityId>().AsSet();
        _riders   = world.GetEntities().With<DynamicGrid>().With<AnchorLinks>().With<EntityId>().AsSet();
        _anchors  = world.GetEntities().With<Anchor>().With<BlockRef>().AsSet();
        _volumes  = world.GetEntities().With<ChunkGrid>().With<Transform>().With<EntityId>().AsSet();
        // Bepu removes a body's constraints with it, so a weld on a body going away is already gone.
        _bodyRemoved = world.SubscribeComponentRemoved((in Entity _, in PhysicsBodyComponent pb) => Unweld(pb.Body));
        _disposed = world.SubscribeEntityDisposed((in Entity e) =>
        {
            if (e.Has<PhysicsBodyComponent>()) Unweld(e.Get<PhysicsBodyComponent>().Body);
        });
    }

    /// <summary>Whether <paramref name="rider"/> is welded to <paramref name="target"/> (a grid's or the terrain's ID)
    /// right now, and whether by a pin (for tests and the debug panel).</summary>
    public (bool Welded, bool Pinned) HoldOn(Entity rider, EntityId target) =>
        _held.TryGetValue((rider, target), out var h) ? (h.Welded, h.Pin is not null) : (false, false);

    /// <summary>How many holds <paramref name="ship"/>'s anchors keep, as of the last tick.</summary>
    public int Holding(Entity ship) => _holding.GetValueOrDefault(ship);

    public void Update(float dt)
    {
        GroupAnchors();
        LetGoOfWhatNobodyHolds();
        TakeHold();
        Weld();
    }

    /// <summary>Only the machine that owns a grid anchors it; everyone else follows its body.</summary>
    private static bool Simulates(Entity grid) => !grid.Has<NetOwner>() || grid.Get<NetOwner>().IsLocal;

    private static bool Anchored(Entity grid) => grid.Has<ShipControls>() && grid.Get<ShipControls>().Anchored;

    private void GroupAnchors()
    {
        foreach (var cells in _anchorsByShip.Values) cells.Clear();
        foreach (ref readonly Entity e in _anchors.GetEntities())
        {
            ref readonly var block = ref e.Get<BlockRef>();
            var root = block.Volume.Root;
            if (!_anchorsByShip.TryGetValue(root, out var cells)) _anchorsByShip[root] = cells = new List<Vector3D<int>>();
            cells.Add(block.Position);
        }
        _scratch.Clear();
        foreach (var (root, cells) in _anchorsByShip) if (cells.Count == 0 || !root.IsAlive) _scratch.Add(root);
        foreach (var root in _scratch) _anchorsByShip.Remove(root);
    }

    /// <summary>Drops each grid's holds whose anchors have all let go (their ships' toggles off), and counts what each
    /// grid's anchors still hold. A hold whose holder isn't here (released, or not loaded yet) is kept.</summary>
    private void LetGoOfWhatNobodyHolds()
    {
        _holding.Clear();
        _scratch.Clear();
        foreach (ref readonly Entity rider in _riders.GetEntities())
        {
            if (!Simulates(rider)) continue;
            var links = rider.Get<AnchorLinks>().All;
            for (int i = links.Count - 1; i >= 0; i--)
            {
                var link = links[i];
                var target = link.ToGrid ? _registry.Find(link.Target) : null;
                if (link.HeldByRider && !Anchored(rider)) link.HeldByRider = false;
                if (link.HeldByTarget && target is { } holder && !Anchored(holder)) link.HeldByTarget = false;
                if (!link.Held)
                {
                    links.RemoveAt(i);
                    continue;
                }
                links[i] = link;
                if (link.HeldByRider) _holding[rider] = _holding.GetValueOrDefault(rider) + 1;
                if (link.HeldByTarget && target is { } t) _holding[t] = _holding.GetValueOrDefault(t) + 1;
            }
            if (links.Count == 0) _scratch.Add(rider);
        }
        foreach (var rider in _scratch) rider.Remove<AnchorLinks>();
    }

    /// <summary>Ships whose toggles just went on (anchored, holding nothing) take hold of whatever their anchors reach,
    /// or turn their toggles back off.</summary>
    private void TakeHold()
    {
        foreach (ref readonly Entity ship in _ships.GetEntities())
        {
            if (!Simulates(ship) || !ship.Get<ShipControls>().Anchored)
            {
                _lettingGo.Remove(ship);
                continue;
            }
            if (_lettingGo.Contains(ship) || !ship.Has<PhysicsBodyComponent>()) continue; // its holds are kept meanwhile
            if (!_anchorsByShip.ContainsKey(ship))
            {
                StopAnchoring(ship, "has no anchor");
                continue;
            }
            if (Holding(ship) > 0) continue;

            int held = 0;
            foreach (var target in Reached(ship))
            {
                Hold(ship, target);
                held++;
            }
            if (held == 0) StopAnchoring(ship, "found nothing in reach");
            else
            {
                _holding[ship] = held;
                _lastEvent = $"{ship.Get<EntityId>()} took hold of {held} {(held == 1 ? "thing" : "things")}";
            }
        }
        _lettingGo.RemoveWhere(ship => !ship.IsAlive);
    }

    /// <summary>Turns the ship's toggles off: it isn't holding anything.</summary>
    private void StopAnchoring(Entity ship, string why)
    {
        _lettingGo.Add(ship);
        _commands.Send(new SetShipAnchored { Ship = ship.Get<EntityId>(), Anchored = false });
        _lastEvent = $"{ship.Get<EntityId>()} {why}: toggles off";
    }

    /// <summary><paramref name="ship"/>'s anchors take hold of <paramref name="other"/>: kept by the lighter of the two
    /// (the terrain is heaviest; two the same, the newer rides on the older), joining any hold already between them.</summary>
    private void Hold(Entity ship, Entity other)
    {
        bool shipRides = !other.Has<DynamicGrid>() || Lighter(ship, other);
        var (rider, target) = shipRides ? (ship, other) : (other, ship);
        var targetId = target.Get<EntityId>();
        if (!rider.Has<AnchorLinks>()) rider.Set(new AnchorLinks { All = new List<AnchorLink>() });
        var links = rider.Get<AnchorLinks>().All;
        int i = links.FindIndex(l => l.Target == targetId);
        var link = i >= 0 ? links[i] : AnchorLink.Between(targetId, target.Get<Transform>(), rider.Get<Transform>());
        if (shipRides) link.HeldByRider = true;
        else link.HeldByTarget = true;
        if (i >= 0) links[i] = link;
        else if (link.ToGrid && AnchorLinks.Support(links) is null) links.Insert(0, link); // its first grid: what it rides on
        else links.Add(link);
    }

    /// <summary>Whether <paramref name="a"/> is lighter than <paramref name="b"/> (the same: the newer one).</summary>
    private static bool Lighter(Entity a, Entity b)
    {
        float ma = Mass(a), mb = Mass(b);
        return ma != mb ? ma < mb : a.Get<EntityId>().Value > b.Get<EntityId>().Value;
    }

    /// <summary>A grid's mass from its blocks (a locked one's too: its body is kinematic, but this is what it'd weigh).</summary>
    private static float Mass(Entity grid)
    {
        float inverse = grid.Get<DynamicGrid>().Inertia.InverseMass;
        return inverse > 0f ? 1f / inverse : 0f;
    }

    // ── finding what's in reach ──────────────────────────────────────────────

    /// <summary>The volumes (the terrain, other grids) one of the ship's anchors reaches: for each anchor, the one with
    /// a colliding block nearest it within <see cref="Reach"/>.</summary>
    private HashSet<Entity> Reached(Entity ship)
    {
        var reached = new HashSet<Entity>();
        ref readonly var st = ref ship.Get<Transform>();
        var shipPosition = PhysicsConv.ToBepu(st.Position);
        var shipRotation = PhysicsConv.ToBepu(st.Rotation);
        foreach (var cell in _anchorsByShip[ship])
        {
            var world = shipPosition + Vector3.Transform(new Vector3(cell.X, cell.Y, cell.Z) + new Vector3(0.5f), shipRotation);
            Entity? best = null;
            float bestDistance = Reach;
            foreach (ref readonly Entity volume in _volumes.GetEntities())
            {
                if (volume == ship) continue;
                // A grid is held by its body (or a pin, once held); one with none here can't be taken hold of.
                if (volume.Has<DynamicGrid>() && !volume.Has<PhysicsBodyComponent>()) continue;
                ref readonly var vt = ref volume.Get<Transform>();
                var local = Vector3.Transform(world - PhysicsConv.ToBepu(vt.Position), Quaternion.Conjugate(PhysicsConv.ToBepu(vt.Rotation)));
                float distance = NearestCollidingBlock(volume.Get<ChunkGrid>().Volume, local, bestDistance);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = volume;
                }
            }
            if (best is { } b) reached.Add(b);
        }
        return reached;
    }

    /// <summary>The distance from <paramref name="point"/> (in <paramref name="volume"/>'s block space) to the nearest
    /// block in it that collides, if that's under <paramref name="within"/>; otherwise <paramref name="within"/>.</summary>
    public static float NearestCollidingBlock(ChunkVolume volume, Vector3 point, float within)
    {
        int x0 = (int)MathF.Floor(point.X - within), x1 = (int)MathF.Floor(point.X + within);
        int y0 = (int)MathF.Floor(point.Y - within), y1 = (int)MathF.Floor(point.Y + within);
        int z0 = (int)MathF.Floor(point.Z - within), z1 = (int)MathF.Floor(point.Z + within);
        float best = within;
        for (int z = z0; z <= z1; z++) for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
        {
            if (!BlockRegistry.Get(volume.GetBlock(x, y, z)).Collides) continue;
            var nearest = Vector3.Clamp(point, new Vector3(x, y, z), new Vector3(x + 1, y + 1, z + 1));
            best = MathF.Min(best, Vector3.Distance(point, nearest));
        }
        return best;
    }

    // ── welding ───────────────────────────────────────────────────────────────

    /// <summary>Welds every rider simulated here to each thing it's held to, and drops the welds no hold keeps.</summary>
    private void Weld()
    {
        _touched.Clear();
        foreach (ref readonly Entity rider in _riders.GetEntities())
        {
            if (!Simulates(rider) || !rider.Has<PhysicsBodyComponent>()) continue; // held once its body is here
            foreach (var link in rider.Get<AnchorLinks>().All)
            {
                _touched.Add((rider, link.Target));
                Weld(rider, link);
            }
        }
        _stale.Clear();
        foreach (var key in _held.Keys) if (!_touched.Contains(key)) _stale.Add(key);
        foreach (var key in _stale) Drop(key);
    }

    /// <summary>Welds the rider where the link puts it: to the target's body if it has one here, else to a pin.</summary>
    private void Weld(Entity rider, in AnchorLink link)
    {
        var key = (rider, link.Target);
        ref readonly var pb = ref rider.Get<PhysicsBodyComponent>();
        var target = _registry.Find(link.Target);
        _held.TryGetValue(key, out var held);

        BodyHandle partner;
        Vector3 offset;
        Quaternion orientation;
        if (target is { } t && t != rider && t.Has<PhysicsBodyComponent>())
        {
            // The rider's body in the target's: its block space where the link puts it, then out to its centre of mass,
            // then from the target's centre of mass rather than its block space.
            ref readonly var tpb = ref t.Get<PhysicsBodyComponent>();
            partner = tpb.Body;
            offset = link.LocalPosition + Vector3.Transform(PhysicsConv.ToBepu(pb.Offset), link.LocalRotation) - PhysicsConv.ToBepu(tpb.Offset);
            orientation = link.LocalRotation;
            if (held?.Pin is not null) { Drop(key); held = null; } // the target's body is here now: off the pin
        }
        else
        {
            // A pin where the rider's body belongs: on the terrain, on the target as it stands (or, with that gone
            // altogether, wherever the rider is).
            var (position, rotation) = _physics.GetBodyPose(pb.Body);
            if (target is { } placed && placed != rider && placed.Has<Transform>())
            {
                var at = link.Place(placed.Get<Transform>());
                position = pb.BodyPosition(at);
                rotation = PhysicsConv.ToBepu(at.Rotation);
            }
            else if (held?.Pin is { } kept) (position, rotation) = _physics.GetBodyPose(kept);

            if (held is not null && held.Pin is null) { Drop(key); held = null; } // was on a body that's gone
            if (held?.Pin is { } pin) _physics.SetBodyPose(pin, position, rotation);
            else _held[key] = held = new Held { Pin = _physics.AddPin(position, rotation) };
            partner = held.Pin!.Value;
            offset = Vector3.Zero;
            orientation = Quaternion.Identity;
        }

        held ??= _held[key] = new Held();
        bool bothKinematic = _physics.IsKinematic(pb.Body) && _physics.IsKinematic(partner);
        if (held.Welded && (held.Rider != pb.Body || held.Partner != partner || bothKinematic)) Unweld(held);
        if (bothKinematic) return; // neither moves: welded once one can

        if (!held.Welded)
        {
            held.Rider = pb.Body;
            held.Partner = partner;
            held.Weld = _physics.AddWeld(partner, pb.Body, offset, orientation);
            held.Welded = true;
        }
        else if (Vector3.DistanceSquared(held.Offset, offset) > OffsetEpsilon * OffsetEpsilon ||
                 MathF.Abs(Quaternion.Dot(held.Orientation, orientation)) < 1f - OffsetEpsilon)
        {
            _physics.SetWeld(held.Weld, offset, orientation);
        }
        held.Offset = offset;
        held.Orientation = orientation;
    }

    /// <summary>Removes a hold's weld (if Bepu hasn't already) and its pin.</summary>
    private void Drop((Entity, EntityId) key)
    {
        if (!_held.Remove(key, out var held)) return;
        Unweld(held);
        if (held.Pin is { } pin) _physics.RemoveBody(pin);
    }

    private void Unweld(Held held)
    {
        // Bepu drops a weld whose bodies are both kinematic, and the handle may since have gone to another constraint.
        if (held.Welded && !(_physics.IsKinematic(held.Rider) && _physics.IsKinematic(held.Partner)))
            _physics.RemoveWeld(held.Weld);
        held.Welded = false;
    }

    /// <summary>A body is going: Bepu takes its welds with it.</summary>
    private void Unweld(BodyHandle body)
    {
        foreach (var held in _held.Values)
            if (held.Welded && (held.Rider == body || held.Partner == body)) held.Welded = false;
    }

    public void Dispose()
    {
        _bodyRemoved.Dispose();
        _disposed.Dispose();
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Anchors";

    public void DrawDebugUi()
    {
        int welded = 0, pinned = 0;
        foreach (var held in _held.Values)
        {
            if (held.Welded) welded++;
            if (held.Pin is not null) pinned++;
        }
        ImGui.Text($"Anchor blocks: {_anchors.Count:N0}, on {_anchorsByShip.Count:N0} ships");
        ImGui.Text($"Holds: {_held.Count:N0} ({welded:N0} welded, {pinned:N0} on pins), on {_riders.Count:N0} grids");
        ImGui.TextDisabled(_lastEvent.Length > 0 ? _lastEvent : "Flick a toggle on a ship with an anchor.");
    }
}
