using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Characters;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Net.Sync;

/// <summary>Another player's copy: a character body like theirs, walked towards where their snapshots put them.</summary>
public struct FollowerCharacter
{
    public PlayerCharacter Character;
}

/// <summary>
/// Each tick before the physics step: moves this machine's physics copies of bodies owned elsewhere to where their
/// snapshots put them this tick (<see cref="RemoteBodySystem"/> sets their Transforms there after the step).
/// <list type="bullet">
/// <item>A grid near the local player (<see cref="PhysicsMode.KinematicFollower"/>) has a kinematic body. It's given
/// the velocity that carries it to its snapshot pose over the step, so a player standing on it rides it, and its
/// deck's velocity is right for jumping and air control. Far off (a join, a hitch) it's placed there directly.</item>
/// <item>Another player within load range (<see cref="PhysicsMode.CharacterFollower"/>) gets a character body built as
/// theirs is, standing on grids and terrain (never touching characters). It's walked, not pushed: its target velocity
/// is theirs on what they stand on, plus enough to close the gap to where they are on it, and it jumps when they do.
/// So on a ship this machine simulates, it weighs what they weigh and pushes the deck only as hard as walking can,
/// as a local player does. More than <see cref="SnapDistance"/> away, it's teleported. A free-flying player has no
/// body, so no copy either.</item>
/// </list>
/// </summary>
public sealed class FollowerSystem : ISystem, IDebugUiSystem
{
    public const float SnapDistance = 2f;
    public const float GridSnapDistance = 8f;
    /// <summary>How fast a player's copy closes the gap to where they are: this fraction of it per second, at up to
    /// <see cref="MaxCatchUpSpeed"/> on top of their own speed.</summary>
    public const float CatchUpRate = 10f;
    public const float MaxCatchUpSpeed = 4f;
    /// <summary>Rising this fast off what they stand on, a player has jumped (a jump leaves at 6).</summary>
    private const float JumpSpeed = 3f;

    private readonly PhysicsWorld _physics;
    private readonly RemoteBodySystem _remote;
    private readonly EntitySet _grids;
    private readonly EntitySet _players;
    private readonly EntitySet _copies;
    private readonly HashSet<Entity> _placed = new();
    private readonly List<PlayerCharacter> _orphaned = new();
    private int _teleports;

    public FollowerSystem(World world, PhysicsWorld physics, RemoteBodySystem remote)
    {
        _physics = physics;
        _remote = remote;
        _grids = world.GetEntities().With<DynamicGrid>().With<PhysicsBodyComponent>().With<RemoteBody>().With<PhysicsPresence>().AsSet();
        _players = world.GetEntities().With<Player>().With<RemoteBody>().With<Transform>().AsSet();
        _copies = world.GetEntities().With<FollowerCharacter>().AsSet();
        world.SubscribeEntityDisposed((in Entity e) =>
        {
            if (e.Has<FollowerCharacter>()) _orphaned.Add(e.Get<FollowerCharacter>().Character);
            _placed.Remove(e);
        });
    }

    public void Update(float dt)
    {
        foreach (var character in _orphaned) character.Dispose();
        _orphaned.Clear();

        foreach (ref readonly var e in _grids.GetEntities())
        {
            if (e.Get<PhysicsPresence>().Mode != PhysicsMode.KinematicFollower) { _placed.Remove(e); continue; }
            var buffer = e.Get<RemoteBody>().Buffer;
            if (buffer.At(_remote.SampleTick(buffer)) is not { } s) continue;
            var (origin, rotation) = _remote.ToWorld(s.Support, s.Position, s.Rotation);
            ref readonly var pb = ref e.Get<PhysicsBodyComponent>();
            FollowKinematic(e, pb.Body, pb.BodyPosition(origin, rotation), rotation, dt);
        }

        // Players' copies come and go with the presence layer, and their flying.
        foreach (var e in _copies.GetEntities().ToArray())
            if (!WantsCopy(e, out _))
            {
                e.Get<FollowerCharacter>().Character.Dispose();
                e.Remove<FollowerCharacter>();
            }
        foreach (ref readonly var e in _players.GetEntities())
        {
            if (!WantsCopy(e, out var s)) continue;
            var (target, _) = _remote.ToWorld(s.Support, s.Position, s.Rotation);
            if (!e.Has<FollowerCharacter>())
            {
                var character = PlayerFactory.CreateCharacter(_physics, target, e);
                _physics.Colliders.Allocate(character.BodyHandle) = new ColliderInfo(ColliderKind.Follower, e);
                character.SetVelocity(SupportVelocityAt(s.Support, target));
                e.Set(new FollowerCharacter { Character = character });
                continue;
            }
            Walk(ref e.Get<FollowerCharacter>().Character, e.Get<RemoteBody>().Buffer, s, target);
        }
    }

    /// <summary>Whether a player has a copy here: another machine's, within load range, on foot. <paramref name="s"/>
    /// is where they are now, as their snapshots have it.</summary>
    private bool WantsCopy(Entity e, out SnapshotBuffer.Sample s)
    {
        s = default;
        if (!e.Has<PhysicsPresence>() || e.Get<PhysicsPresence>().Mode != PhysicsMode.CharacterFollower
            || e.Has<CharacterControllerComponent>() || !e.Has<RemoteBody>()) return false;
        var buffer = e.Get<RemoteBody>().Buffer;
        if (buffer.At(_remote.SampleTick(buffer)) is not { } sample) return false;
        s = sample;
        return !s.FreeFlying;
    }

    /// <summary>
    /// Walks a player's copy towards <paramref name="target"/>, where their snapshot <paramref name="s"/> puts them.
    /// Its target velocity is theirs relative to what they stand on (from the snapshot a tick before, in its space, so a
    /// ship moving under them doesn't count), plus a catch-up on the gap. Both are relative to the deck, as the
    /// character's motion is: the deck's own motion carries the copy, as it carries a local player.
    /// </summary>
    private void Walk(ref PlayerCharacter character, SnapshotBuffer buffer, in SnapshotBuffer.Sample s, Vector3 target)
    {
        var position = character.Position;
        var supportVelocity = SupportVelocityAt(s.Support, target);
        if (Vector3.Distance(position, target) > SnapDistance)
        {
            character.TeleportTo(target);
            character.SetVelocity(supportVelocity);
            _teleports++;
            return;
        }
        // Their velocity on what they stand on: the change in their position in its space over a tick, turned as it is here.
        Vector3 velocity = s.Velocity - supportVelocity;
        if (!s.Support.IsNone && buffer.At(_remote.SampleTick(buffer) - 1) is { } before && before.Support == s.Support)
        {
            var (from, _) = _remote.ToWorld(before.Support, before.Position, before.Rotation);
            velocity = (target - from) * 60f;
        }
        var gap = target - position;
        var catchUp = new Vector3(gap.X, 0, gap.Z) * CatchUpRate;
        if (catchUp.Length() > MaxCatchUpSpeed) catchUp = Vector3.Normalize(catchUp) * MaxCatchUpSpeed;
        bool jump = character.Supported && velocity.Y > JumpSpeed;
        character.Drive(new Vector3(velocity.X, 0, velocity.Z) + catchUp, jump);
        _physics.Characters.SetAirReference(character.BodyHandle, SupportBody(s.Support));
    }

    /// <summary>The body of what a player stands on here, if it has one.</summary>
    private BepuPhysics.BodyHandle? SupportBody(EntityId support) =>
        _remote.TryGetSupport(support, out var s) && s.Has<PhysicsBodyComponent>() ? s.Get<PhysicsBodyComponent>().Body : null;

    private void FollowKinematic(Entity e, BepuPhysics.BodyHandle body, Vector3 target, Quaternion rotation, float dt)
    {
        var (position, orientation) = _physics.GetBodyPose(body);
        if (!_placed.Contains(e) || Vector3.Distance(position, target) > GridSnapDistance)
        {
            _physics.SetBodyPose(body, target, rotation);
            _physics.SetBodyLinearVelocity(body, Vector3.Zero);
            _physics.SetBodyAngularVelocity(body, Vector3.Zero);
            _placed.Add(e);
            _teleports++;
            return;
        }
        _physics.SetBodyLinearVelocity(body, (target - position) / dt);
        // The rotation from where it is to where it should be, as an angular velocity over the step.
        var delta = Quaternion.Normalize(rotation * Quaternion.Conjugate(orientation));
        if (delta.W < 0) delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
        float angle = 2f * MathF.Acos(System.Math.Clamp(delta.W, -1f, 1f));
        float sin = MathF.Sqrt(MathF.Max(0f, 1f - delta.W * delta.W));
        var axis = sin > 1e-5f ? new Vector3(delta.X, delta.Y, delta.Z) / sin : Vector3.Zero;
        _physics.SetBodyAngularVelocity(body, axis * (angle / dt));
    }

    /// <summary>How fast the point <paramref name="at"/> of what a player stands on is moving here (zero if they stand on
    /// nothing, or on something with no body here).</summary>
    private Vector3 SupportVelocityAt(EntityId support, Vector3 at)
    {
        if (!_remote.TryGetSupport(support, out var s) || !s.Has<PhysicsBodyComponent>()) return Vector3.Zero;
        var body = s.Get<PhysicsBodyComponent>().Body;
        var (centre, _) = _physics.GetBodyPose(body);
        return _physics.GetBodyLinearVelocity(body) + Vector3.Cross(_physics.GetBodyAngularVelocity(body), at - centre);
    }

    public string DebugName => "Followers";

    public void DrawDebugUi() =>
        ImGui.Text($"Kinematic grid copies placed: {_placed.Count}, player copies: {_copies.Count}, teleports: {_teleports}");
}
