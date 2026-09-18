using System.Numerics;
using BepuPhysics.Collidables;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Physics.Characters;
using ClearSkies.Engine.Rendering;
using Silk.NET.Maths;

namespace ClearSkies.Game;

/// <summary>Spawns the player camera (free-fly by default) overlooking the procedural sky world,
/// plus its walking character body (toggle with V — see PlayerMovementSystem).</summary>
public static class TestScene
{
    public static void Build(EngineHost host)
    {
        var cam = host.World.CreateEntity();
        var camTransform = Transform.Identity;
        // Positioned behind the origin, facing +Z so the first loaded chunks (Z>0) are
        // directly in front of the camera. Pitch tilts down to see island tops at ~75 units ahead.
        camTransform.Position = new Vector3D<float>(16f, 45f, -30f);
        cam.Set(camTransform);
        cam.Set(new CameraComponent { Camera = new Camera(), Active = true });
        cam.Set(new MouseLookComponent
        {
            LookSensitivity = 0.0025f,
            Yaw             = MathF.PI,  // face +Z (yaw=π rotates default -Z forward to +Z)
            Pitch           = -0.45f,    // ~26° downward — sees island surface at ~75 units ahead
        });
        cam.Set(new FreeFlyController { MoveSpeed = 10f });

        // Capsule spawns under the camera's eye position (PhysicsConv is internal to
        // ClearSkies.Engine, so convert by hand here — it's just field access).
        var spawnPosition = new Vector3(camTransform.Position.X, camTransform.Position.Y - 0.8f, camTransform.Position.Z);
        var shape = new Capsule(radius: 0.3f, length: 1.0f);
        var character = new PlayerCharacter(host.Physics.Characters, spawnPosition, shape,
            minimumSpeculativeMargin: 0.01f, mass: 10f,
            // Sharp start/stop: accel = force/mass = 50 m/s², reaches the 5 m/s target in ~0.1s (same
            // cap governs stopping) and gives the motion constraint plenty of headroom to hold the
            // character's velocity to an accelerating support (e.g. a thrusting airship deck) without
            // lagging behind. MaximumVerticalGlueForce raised to match for the vertical half of that grip.
            maximumHorizontalForce: 500f, maximumVerticalGlueForce: 350f,
            // JumpVelocity paired with PlayerCharacter's default ExtraFallGravity (12, on top of the
            // world's own gentle -6 gravity -> 18 effective while airborne) for a ~1-block peak jump
            // height: v²/(2·g) = 6²/(2·18) = 1.0. Also makes falls heavier/snappier instead of floaty.
            jumpVelocity: 6f, speed: 5f,
            // Strong (not full) air control, per request — lets you correct your trajectory mid-air.
            airControlForceScale: 0.6f, airControlSpeedScale: 0.8f);
        cam.Set(new CharacterControllerComponent { Character = character, EyeHeight = 0.7f });
        cam.Set(new CharacterModeComponent { FreeFly = true }); // start in FreeFly — zero regression risk vs. today

        host.Input.CursorCaptured = true;
    }
}
