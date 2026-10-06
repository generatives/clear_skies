using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Gui;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Physics.Support;

/// <summary>
/// Each tick after physics: keeps every character's <see cref="Support"/> up to date.
/// <list type="bullet">
/// <item>Standing on a <see cref="Supportable"/> body: that's the support, switching on the same tick.</item>
/// <item>Standing on the static world: no support.</item>
/// <item>Airborne: the support is kept while the body is still above it (a ray straight down hits it first), and
/// released <see cref="ReleaseSeconds"/> after it last was, or at once if the supporting entity no longer exists. One
/// whose body isn't here yet is kept as long.</item>
/// </list>
/// The support becomes the character's air-control reference, so jumping on a moving deck doesn't leave the player
/// behind. While standing on a support, the view turns with it: its change in heading turns the view's yaw, and its
/// change in slope along the way the player faces tilts the pitch, so a player at the helm keeps facing the same way
/// relative to the ship. Roll isn't followed. Free-flying players (<see cref="FreeFlying"/>) have no support.
/// </summary>
public sealed class SupportSystem : ISystem, IDebugUiSystem
{
    /// <summary>Seconds an airborne body can be away from its support before it's released.</summary>
    public const float ReleaseSeconds = 0.5f;

    private readonly EntitySet _bodies;
    private readonly EntitySet _characters; // flying or not, for the debug panel
    private readonly PhysicsWorld _physics;

    public SupportSystem(World world, PhysicsWorld physics)
    {
        _bodies = world.GetEntities().With<Support>().With<CharacterControllerComponent>().With<Transform>().Without<FreeFlying>().AsSet();
        _characters = world.GetEntities().With<Support>().With<CharacterControllerComponent>().AsSet();
        _physics = physics;
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _bodies.GetEntities())
        {
            ref var support = ref e.Get<Support>();
            ref var cc = ref e.Get<CharacterControllerComponent>();
            var characterBody = cc.Character.BodyHandle;

            bool standing = false;
            if (_physics.Characters.TryGetStandingBody(characterBody, out var standingBody))
            {
                var info = _physics.Colliders[standingBody];
                if (info.Entity.IsAlive && info.Entity.Has<Supportable>())
                {
                    Switch(ref support, info.Entity);
                    standing = true;
                }
                else Release(ref support);
            }
            else if (_physics.Characters.IsStanding(characterBody))
                Release(ref support); // on the static world
            else if (support.HasSupporter && support.Supporter.Has<PhysicsBodyComponent>())
            {
                var supporterBody = support.Supporter.Get<PhysicsBodyComponent>().Body;
                support.TimeAway = _physics.Characters.IsAbove(characterBody, supporterBody) ? 0f : support.TimeAway + dt;
                if (support.TimeAway > ReleaseSeconds) Release(ref support);
            }
            else if (support.HasSupporter)
            {
                // Its body isn't here yet (a ship's copy arriving with the player on it, just as a client joins): kept a
                // while, riding along where they were on it, so the copy carries them on once it's placed
                // (RemoteBodyProxySystem) rather than leaving them behind as it moves off.
                support.TimeAway += dt;
                if (support.TimeAway > ReleaseSeconds) Release(ref support);
                else if (support.Supporter.Has<Transform>())
                {
                    ref readonly var st = ref support.Supporter.Get<Transform>();
                    var at = ToNumerics(st.Position) + Vector3.Transform(support.LocalPosition, ToNumerics(st.Rotation));
                    Players.Teleport(e, new Vector3D<float>(at.X, at.Y, at.Z));
                }
            }
            else
                Release(ref support); // the supporter no longer exists

            if (support.HasSupporter && support.Supporter.Has<Transform>())
            {
                ref readonly var st = ref support.Supporter.Get<Transform>();
                var supporterRotation = ToNumerics(st.Rotation);
                if (standing && support.WasStanding && e.Has<MouseLookComponent>())
                    FollowSupport(ref e.Get<MouseLookComponent>(), ref e.Get<Transform>(), support.SupporterRotation, supporterRotation);
                support.SupporterRotation = supporterRotation;

                // The body's pose in the supporter's space.
                var inverse = Quaternion.Inverse(supporterRotation);
                ref readonly var t = ref e.Get<Transform>();
                support.LocalPosition = Vector3.Transform(ToNumerics(t.Position) - ToNumerics(st.Position), inverse);
                support.LocalRotation = inverse * ToNumerics(t.Rotation);
            }
            else
            {
                ref readonly var t = ref e.Get<Transform>();
                support.LocalPosition = ToNumerics(t.Position);
                support.LocalRotation = ToNumerics(t.Rotation);
            }
            support.WasStanding = standing;

            _physics.Characters.SetAirReference(characterBody,
                support.HasSupporter && support.Supporter.Has<PhysicsBodyComponent>() ? support.Supporter.Get<PhysicsBodyComponent>().Body : null);
        }
    }

    private static void Switch(ref Support support, Entity supporter)
    {
        if (support.Supporter != supporter)
        {
            support.Supporter = supporter;
            support.WasStanding = false; // no turn to follow from the first tick on it
        }
        support.TimeAway = 0f;
    }

    private static void Release(ref Support support)
    {
        support.Supporter = default;
        support.TimeAway = 0f;
        support.WasStanding = false;
    }

    /// <summary>Turns the view by however far the support turned since last tick.</summary>
    private static void FollowSupport(ref MouseLookComponent look, ref Transform t, Quaternion previous, Quaternion orientation)
    {
        // Yaw: the support's change in heading (the way its bow points, seen from above), in full.
        float yaw = WrapAngle(Heading(orientation) - Heading(previous));

        // Pitch: the support's change in slope along the way the player now faces, within the usual look limits.
        float newYaw = look.Yaw + yaw;
        var facing = new Vector3(-MathF.Sin(newYaw), 0f, -MathF.Cos(newYaw));
        float limit = MathF.PI / 2f - 0.01f;
        float pitch = System.Math.Clamp(look.Pitch + SlopeAlong(orientation, facing) - SlopeAlong(previous, facing),
                                        -limit, limit) - look.Pitch;
        look.TurnWith(yaw, pitch);

        t.Rotation = look.BodyRotation;
    }

    /// <summary>Which way a body's own -Z points seen from above: radians about world up, 0 towards -Z, anticlockwise
    /// (the same sense as <see cref="MouseLookComponent.Yaw"/>).</summary>
    private static float Heading(Quaternion orientation)
    {
        var forward = Vector3.Transform(-Vector3.UnitZ, orientation);
        return MathF.Atan2(-forward.X, -forward.Z);
    }

    /// <summary>How steeply a body's deck climbs along horizontal unit direction <paramref name="facing"/>, in radians
    /// (positive uphill): its own up tilts back, away from the way the deck climbs.</summary>
    private static float SlopeAlong(Quaternion orientation, Vector3 facing)
    {
        var up = Vector3.Transform(Vector3.UnitY, orientation);
        return MathF.Asin(System.Math.Clamp(-Vector3.Dot(up, facing), -1f, 1f));
    }

    private static float WrapAngle(float angle) => MathF.IEEERemainder(angle, 2f * MathF.PI);

    private static Vector3 ToNumerics(Vector3D<float> v) => new(v.X, v.Y, v.Z);
    private static Quaternion ToNumerics(Quaternion<float> q) => new(q.X, q.Y, q.Z, q.W);

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Character";

    public void DrawDebugUi()
    {
        foreach (ref readonly Entity e in _characters.GetEntities())
        {
            ref readonly var cc = ref e.Get<CharacterControllerComponent>();
            bool freeFly = e.Has<FreeFlying>();
            ImGui.Text($"Mode: {(freeFly ? "FreeFly" : "Walking")} (V to toggle)");
            if (freeFly) continue;
            ImGui.Text($"Supported: {cc.Character.Supported}");
            var v = cc.Character.LinearVelocity;
            ImGui.Text($"Velocity: ({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})");
            ref readonly var support = ref e.Get<Support>();
            ImGui.Text(support.HasSupporter
                ? $"Support: entity {support.Supporter}, away {support.TimeAway:0.00}s, local ({support.LocalPosition.X:0.0}, {support.LocalPosition.Y:0.0}, {support.LocalPosition.Z:0.0})"
                : "Support: none (world space)");
        }
    }
}
