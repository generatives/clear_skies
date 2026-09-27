using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Net.Sync;

/// <summary>Another player's servo copy: a capsule body pulled towards where their snapshots put them.</summary>
public struct ServoBody
{
    public BepuPhysics.BodyHandle Body;
}

/// <summary>
/// Each tick before the physics step: moves this machine's physics copies of bodies owned elsewhere to where their
/// snapshots put them, <see cref="RemoteBodySystem.InterpolationDelay"/> ticks behind.
/// <list type="bullet">
/// <item>A grid near the local player (<see cref="PhysicsMode.KinematicFollower"/>) has a kinematic body. It's given
/// the velocity that carries it to its snapshot pose over the step, so a player standing on it rides it, and its
/// deck's velocity is right for jumping and air control. Far off (a join, a hitch) it's placed there directly.</item>
/// <item>Another player within load range (<see cref="PhysicsMode.ServoFollower"/>) gets a capsule that collides with
/// grids only. A force-limited servo pulls it towards their snapshot position, so it has weight on a ship this machine
/// simulates; more than <see cref="ServoSnapDistance"/> away, it's teleported.</item>
/// </list>
/// </summary>
public sealed class FollowerSystem : ISystem, IDebugUiSystem
{
    public const float ServoSnapDistance = 2f;
    public const float GridSnapDistance = 8f;
    /// <summary>The most the servo changes a copy's velocity per second (it can't shove ships around harder than that).</summary>
    public const float ServoMaxAcceleration = 300f;
    private const float PlayerMass = 2f;

    private readonly PhysicsWorld _physics;
    private readonly RemoteBodySystem _remote;
    private readonly EntitySet _grids;
    private readonly EntitySet _players;
    private readonly EntitySet _servoed;
    private readonly HashSet<Entity> _placed = new();
    private readonly List<BepuPhysics.BodyHandle> _orphaned = new();
    private int _teleports;

    public FollowerSystem(World world, PhysicsWorld physics, RemoteBodySystem remote)
    {
        _physics = physics;
        _remote = remote;
        _grids = world.GetEntities().With<DynamicGrid>().With<PhysicsBodyComponent>().With<RemoteBody>().With<PhysicsPresence>().AsSet();
        _players = world.GetEntities().With<Player>().With<RemoteBody>().With<Transform>().AsSet();
        _servoed = world.GetEntities().With<ServoBody>().AsSet();
        world.SubscribeEntityDisposed((in Entity e) =>
        {
            if (e.Has<ServoBody>()) _orphaned.Add(e.Get<ServoBody>().Body);
            _placed.Remove(e);
        });
    }

    public void Update(float dt)
    {
        foreach (var body in _orphaned) _physics.RemoveBodyAndShape(body);
        _orphaned.Clear();

        double tick = _remote.PhysicsTick;
        foreach (ref readonly var e in _grids.GetEntities())
        {
            if (e.Get<PhysicsPresence>().Mode != PhysicsMode.KinematicFollower) { _placed.Remove(e); continue; }
            if (e.Get<RemoteBody>().Buffer.At(tick) is not { } s) continue;
            var (target, rotation) = _remote.ToWorld(s.Support, s.Position, s.Rotation);
            FollowKinematic(e, e.Get<PhysicsBodyComponent>().Body, target, rotation, dt);
        }

        // Servo copies come and go with the presence layer.
        foreach (var e in _servoed.GetEntities().ToArray())
            if (!e.Has<PhysicsPresence>() || e.Get<PhysicsPresence>().Mode != PhysicsMode.ServoFollower || e.Has<CharacterControllerComponent>())
            {
                _physics.RemoveBodyAndShape(e.Get<ServoBody>().Body);
                e.Remove<ServoBody>();
            }
        foreach (ref readonly var e in _players.GetEntities())
        {
            if (!e.Has<PhysicsPresence>() || e.Get<PhysicsPresence>().Mode != PhysicsMode.ServoFollower || e.Has<CharacterControllerComponent>()) continue;
            if (e.Get<RemoteBody>().Buffer.At(tick) is not { } s) continue;
            var (target, _) = _remote.ToWorld(s.Support, s.Position, s.Rotation);
            if (!e.Has<ServoBody>())
            {
                e.Set(new ServoBody { Body = _physics.AddFollowerCapsule(target, 0.3f, 1.0f, PlayerMass, new ColliderInfo(ColliderKind.Follower, e)) });
                continue;
            }
            Servo(e.Get<ServoBody>().Body, target, dt);
        }
    }

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

    private void Servo(BepuPhysics.BodyHandle body, Vector3 target, float dt)
    {
        var (position, _) = _physics.GetBodyPose(body);
        if (Vector3.Distance(position, target) > ServoSnapDistance)
        {
            _physics.SetBodyPose(body, target, Quaternion.Identity);
            _physics.SetBodyLinearVelocity(body, Vector3.Zero);
            _teleports++;
            return;
        }
        // The velocity that reaches the target over the step, having gravity's pull this step already in hand.
        var desired = (target - position) / dt - _physics.Gravity * dt;
        var current = _physics.GetBodyLinearVelocity(body);
        var change = desired - current;
        float max = ServoMaxAcceleration * dt;
        if (change.Length() > max) change = Vector3.Normalize(change) * max;
        _physics.SetBodyLinearVelocity(body, current + change);
    }

    public string DebugName => "Followers";

    public void DrawDebugUi() =>
        ImGui.Text($"Kinematic grid copies placed: {_placed.Count}, servo player copies: {_servoed.Count}, teleports: {_teleports}");
}
