using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Characters;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Net.Sync;

/// <summary>
/// Each tick before the physics step: moves this machine's physics copies of ships simulated elsewhere to where their
/// snapshots put them this tick (<see cref="RemoteBodySystem"/> sets their Transforms there after the step). A grid near
/// the local player (<see cref="PhysicsMode.KinematicFollower"/>) has a kinematic body. It's given the velocity that
/// carries it to its snapshot pose over the step, so the local player, predicted here, rides it, and its deck's velocity
/// is right for jumping and air control. Just arrived, or after a hitch (further off than its own velocity explains: see
/// <see cref="HitchDistance"/>), it's placed there directly, carrying the local player aboard. Other players have no
/// physics copy: the host simulates them, and they're drawn from their snapshots.
/// </summary>
public sealed class FollowerSystem : ISystem, IDebugUiSystem
{
    /// <summary>A copy that would have to move further than this in a tick beyond its own velocity, or turn more than
    /// <see cref="HitchAngle"/>, has had a hitch (the host stalled, the delay jumped): it's placed there outright, with
    /// the local player aboard, rather than yanked there through them.</summary>
    public const float HitchDistance = 0.25f;
    public const float HitchAngle = 0.05f;

    private readonly PhysicsWorld _physics;
    private readonly RemoteBodySystem _remote;
    private readonly EntitySet _grids;
    private readonly EntitySet _riders;
    private readonly HashSet<Entity> _placed = new();
    private int _teleports;

    public FollowerSystem(World world, PhysicsWorld physics, RemoteBodySystem remote)
    {
        _physics = physics;
        _remote = remote;
        _grids = world.GetEntities().With<DynamicGrid>().With<PhysicsBodyComponent>().With<RemoteBody>().With<PhysicsPresence>().AsSet();
        _riders = world.GetEntities().With<CharacterControllerComponent>().With<Support>().With<Transform>().Without<FreeFlying>().AsSet();
        world.SubscribeEntityDisposed((in Entity e) => _placed.Remove(e));
    }

    public void Update(float dt)
    {
        foreach (ref readonly var e in _grids.GetEntities())
        {
            if (e.Get<PhysicsPresence>().Mode != PhysicsMode.KinematicFollower) { _placed.Remove(e); continue; }
            var buffer = e.Get<RemoteBody>().Buffer;
            if (buffer.At(_remote.SampleTick(buffer)) is not { } s)
            {
                // Nothing heard yet (a copy just arrived): held where it was spawned until it can be placed, rather than
                // drifting off along its spawn velocity from under whoever stands on it.
                if (!_placed.Contains(e)) HoldStill(e.Get<PhysicsBodyComponent>().Body);
                continue;
            }
            var (origin, rotation) = _remote.ToWorld(s.Support, s.Position, s.Rotation);
            ref readonly var pb = ref e.Get<PhysicsBodyComponent>();
            FollowKinematic(e, pb.Body, pb.BodyPosition(origin, rotation), rotation, s.Velocity, dt);
        }
    }

    private void FollowKinematic(Entity e, BepuPhysics.BodyHandle body, Vector3 target, Quaternion rotation, Vector3 velocity, float dt)
    {
        var (position, orientation) = _physics.GetBodyPose(body);
        // The rotation from where it is to where it should be.
        var delta = Quaternion.Normalize(rotation * Quaternion.Conjugate(orientation));
        if (delta.W < 0) delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
        float angle = 2f * MathF.Acos(System.Math.Clamp(delta.W, -1f, 1f));
        bool hitch = Vector3.Distance(target - position, velocity * dt) > HitchDistance || angle > HitchAngle;
        if (!_placed.Contains(e) || hitch)
        {
            // Placed straight there (just arrived, or a hitch), moving as it was then, with whoever is aboard: a step
            // short, as following leaves it, so the step brings it to the target rather than past it.
            var placed = target - velocity * dt;
            _physics.SetBodyPose(body, placed, rotation);
            _physics.SetBodyLinearVelocity(body, velocity);
            _physics.SetBodyAngularVelocity(body, Vector3.Zero);
            CarryRiders(e, position, orientation, placed, rotation, velocity);
            _placed.Add(e);
            _teleports++;
            return;
        }
        _physics.SetBodyLinearVelocity(body, (target - position) / dt);
        // The rotation as an angular velocity over the step.
        float sin = MathF.Sqrt(MathF.Max(0f, 1f - delta.W * delta.W));
        var axis = sin > 1e-5f ? new Vector3(delta.X, delta.Y, delta.Z) / sin : Vector3.Zero;
        _physics.SetBodyAngularVelocity(body, axis * (angle / dt));
    }

    private void HoldStill(BepuPhysics.BodyHandle body)
    {
        _physics.SetBodyLinearVelocity(body, Vector3.Zero);
        _physics.SetBodyAngularVelocity(body, Vector3.Zero);
    }

    /// <summary>Moves the local players on <paramref name="ship"/> with it, where it's just been placed: to the same
    /// spot on its deck, moving with it. (Not as they were moving on it: what it was doing before it was placed may be
    /// nothing like what it's doing now, e.g. a copy just made, at rest.)</summary>
    private void CarryRiders(Entity ship, Vector3 fromPosition, Quaternion fromRotation,
                             Vector3 toPosition, Quaternion toRotation, Vector3 toVelocity)
    {
        var inverse = Quaternion.Inverse(fromRotation);
        foreach (ref readonly var rider in _riders.GetEntities())
        {
            if (rider.Get<Support>().Supporter != ship) continue;
            ref var character = ref rider.Get<CharacterControllerComponent>().Character;
            var onDeck = Vector3.Transform(character.Position - fromPosition, inverse);
            var at = toPosition + Vector3.Transform(onDeck, toRotation);
            Players.Teleport(rider, new Silk.NET.Maths.Vector3D<float>(at.X, at.Y, at.Z));
            character.SetVelocity(toVelocity);
            if (rider.Has<InterpolatedTransform>()) rider.Get<InterpolatedTransform>().Teleport();
        }
    }

    public string DebugName => "Followers";

    public void DrawDebugUi() =>
        ImGui.Text($"Kinematic grid copies placed: {_placed.Count}, teleports: {_teleports}");
}
