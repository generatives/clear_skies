using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;
using System.Numerics;
using Quaternion = System.Numerics.Quaternion;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Post-physics: while a camera is in Walking mode (not FreeFly, not grid-following), reads its
/// character capsule's new pose (after this tick's Simulation.Timestep, including the ported
/// BepuPhysics2 character-controller constraint — see Physics/Characters/) and writes the
/// first-person eye position into <see cref="Transform"/>. Mirrors the existing
/// PhysicsTransformSyncSystem/GridPilotSystem post-physics pose-readback precedent.
///
/// While the character stands on a ship, the view turns with it: each tick the ship's change in heading turns the
/// view's yaw by the same amount, and its change in slope along the way the player faces tilts the view's pitch by
/// the same amount (still within the usual look limits), so a player standing at the helm keeps facing the same way
/// relative to the ship as it turns, and the horizon stays put relative to the deck as it pitches. Roll isn't
/// followed: the view never rolls. It only follows while the character is standing on the ship, i.e. supported, on a
/// surface within the character's maximum slope; jumping or sliding off, the view stays where it is.
/// </summary>
public sealed class CharacterCameraSyncSystem : ISystem, IDebugUiSystem
{
    private readonly EntitySet _cameras;

    public CharacterCameraSyncSystem(World world)
    {
        _cameras = world.GetEntities()
            .With<Transform>().With<CharacterControllerComponent>().With<CharacterModeComponent>()
            .AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            if (e.Has<CameraGridFollowComponent>()) continue;
            if (e.Get<CharacterModeComponent>().FreeFly) continue;

            ref var cc = ref e.Get<CharacterControllerComponent>();
            ref var t = ref e.Get<Transform>();
            var eye = cc.Character.GetEyePosition(cc.EyeHeight);
            t.Position = new Vector3D<float>(eye.X, eye.Y, eye.Z);

            if (e.Has<MouseLookComponent>()) FollowShip(ref cc, ref t, ref e.Get<MouseLookComponent>());
        }
    }

    /// <summary>Turns the view by however far the ship the character stands on turned since last tick.</summary>
    private static void FollowShip(ref CharacterControllerComponent cc, ref Transform t, ref MouseLookComponent look)
    {
        if (!cc.Character.TryGetSupportBody(out var body, out var orientation))
        {
            cc.RideBody = null;
            return;
        }

        if (cc.RideBody == body)
        {
            var previous = cc.RideOrientation;

            // Yaw: the ship's change in heading (the way its bow points, seen from above), in full.
            look.Yaw += WrapAngle(Heading(orientation) - Heading(previous));

            // Pitch: the ship's change in slope along the way the player now faces.
            var facing = new Vector3(-MathF.Sin(look.Yaw), 0f, -MathF.Cos(look.Yaw));
            look.Pitch += SlopeAlong(orientation, facing) - SlopeAlong(previous, facing);
            float limit = MathF.PI / 2f - 0.01f;
            look.Pitch = System.Math.Clamp(look.Pitch, -limit, limit);

            t.Rotation = Quaternion<float>.CreateFromYawPitchRoll(look.Yaw, look.Pitch, 0f);
        }

        cc.RideBody        = body;
        cc.RideOrientation = orientation;
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

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Character";

    public void DrawDebugUi()
    {
        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            ref readonly var cc = ref e.Get<CharacterControllerComponent>();
            bool freeFly = e.Get<CharacterModeComponent>().FreeFly;
            ImGui.Text($"Mode: {(freeFly ? "FreeFly" : "Walking")} (V to toggle)");
            if (!freeFly)
            {
                ImGui.Text($"Supported: {cc.Character.Supported}");
                var v = cc.Character.LinearVelocity;
                ImGui.Text($"Velocity: ({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})");
            }
        }
    }
}
