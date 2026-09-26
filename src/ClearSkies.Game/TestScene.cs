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
    // Fallback spawn if the world has no spawn of its own (no island cluster found nearby).
    private static readonly Vector3D<float> FallbackSpawn = new(16f, 45f, -30f);

    /// <summary>Builds the scene and returns the resolved camera spawn position, so callers (e.g. the
    /// ray-traced lighting prototype's test ship — see the plan doc) can place things relative to it.</summary>
    /// <param name="cameraOverride">Launch option (<c>--camera x,y,z[,yaw,pitch]</c>): puts the camera here instead of
    /// overlooking the nearest island, e.g. to reproduce a view for a screenshot.</param>
    /// <param name="spawnView">Where the camera starts and how it faces (yaw, pitch); <see cref="FallbackSpawn"/> if
    /// null.</param>
    public static Vector3D<float> Build(EngineHost host, ulong worldSeed, float[]? cameraOverride = null,
                                        (Vector3D<float> Position, float Yaw, float Pitch)? spawnView = null)
    {
        var cam = host.World.CreateEntity();
        var camTransform = Transform.Identity;

        float yaw = MathF.PI, pitch = -0.45f;
        if (spawnView is { } view)
        {
            camTransform.Position = view.Position;
            (yaw, pitch) = (view.Yaw, view.Pitch);
        }
        else
        {
            camTransform.Position = FallbackSpawn;
        }
        if (cameraOverride is { Length: >= 3 })
        {
            camTransform.Position = new Vector3D<float>(cameraOverride[0], cameraOverride[1], cameraOverride[2]);
            if (cameraOverride.Length >= 5) (yaw, pitch) = (cameraOverride[3], cameraOverride[4]);
            camTransform.Rotation = Quaternion<float>.CreateFromYawPitchRoll(yaw, pitch, 0f);
        }
        cam.Set(camTransform);
        cam.Set(new CameraComponent { Camera = new Camera(), Active = true });
        cam.Set(new MouseLookComponent
        {
            LookSensitivity = 0.0025f,
            Yaw             = yaw,    // default π: face +Z (yaw=π rotates default -Z forward to +Z)
            Pitch           = pitch,  // default ~26° downward — sees island surface at ~75 units ahead
        });
        cam.Set(new FreeFlyController { MoveSpeed = 10f });

        // Capsule spawns under the camera's eye position (PhysicsConv is internal to
        // ClearSkies.Engine, so convert by hand here — it's just field access).
        var spawnPosition = new Vector3(camTransform.Position.X, camTransform.Position.Y - 0.8f, camTransform.Position.Z);
        var shape = new Capsule(radius: 0.3f, length: 1.0f);
        var character = new PlayerCharacter(host.Physics.Characters, spawnPosition, shape,
            // Light (two Wood blocks' worth): the character pushes off the deck it walks on as hard as it pushes
            // itself, so a heavy character with strong forces shoved and twisted ships as hard as their Fans.
            minimumSpeculativeMargin: 0.01f, mass: 2f,
            // Sharp start/stop: accel = force/mass = 50 m/s², reaches the 5 m/s target in ~0.1s (same
            // cap governs stopping) and gives the motion constraint plenty of headroom to hold the
            // character's velocity to an accelerating support (e.g. a thrusting airship deck) without
            // lagging behind. MaximumVerticalGlueForce raised to match for the vertical half of that grip.
            // Both scale with the mass, so the feel stays the same at any mass.
            maximumHorizontalForce: 100f, maximumVerticalGlueForce: 70f,
            // JumpVelocity paired with PlayerCharacter's default ExtraFallGravity (12, on top of the
            // world's own gentle -6 gravity -> 18 effective while airborne) for a ~1-block peak jump
            // height: v²/(2·g) = 6²/(2·18) = 1.0. Also makes falls heavier/snappier instead of floaty.
            jumpVelocity: 6f, speed: 5f,
            // Full air control: same acceleration and top speed as on the ground, and a gentle brake with no keys
            // held, so you can steer mid-air and let go to avoid overshooting a ledge.
            airControlForceScale: 1f, airControlSpeedScale: 1f, airBrakeScale: 0.5f);
        cam.Set(new CharacterControllerComponent { Character = character, EyeHeight = 0.7f });
        cam.Set(new CharacterModeComponent { FreeFly = true }); // start in FreeFly — zero regression risk vs. today

        host.Input.CursorCaptured = false; // the F1 debug menu starts open, and F1 frees the cursor with it
        return camTransform.Position;
    }
}
