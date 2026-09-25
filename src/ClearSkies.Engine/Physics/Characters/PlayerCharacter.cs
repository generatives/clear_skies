using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using ClearSkies.Engine.Input;
using Silk.NET.Input;

namespace ClearSkies.Engine.Physics.Characters;

/// <summary>
/// Game-side adaptation of BepuPhysics2's own Demos/Demos/Characters/CharacterInput.cs (v2.4.0):
/// wraps a <see cref="BodyHandle"/> + <see cref="CharacterControllers"/> registration and reads
/// this project's <see cref="InputManager"/> instead of the Demos framework's Input/Camera types.
/// The physics-side behaviour (support detection, the motion constraint, air control) is
/// untouched — this is purely the input/camera seam.
/// </summary>
public struct PlayerCharacter
{
    private BodyHandle bodyHandle;
    private CharacterControllers characters;
    private float speed;
    private Capsule shape;
    private float extraFallGravity;
    private float airControlForceScale;
    private float airControlSpeedScale;

    public BodyHandle BodyHandle => bodyHandle;

    public PlayerCharacter(CharacterControllers characters, Vector3 initialPosition, Capsule shape,
        float minimumSpeculativeMargin, float mass, float maximumHorizontalForce, float maximumVerticalGlueForce,
        float jumpVelocity, float speed, float maximumSlope = MathF.PI * 0.25f,
        float extraFallGravity = 12f, float airControlForceScale = 0.6f, float airControlSpeedScale = 0.8f)
    {
        this.characters = characters;
        this.extraFallGravity = extraFallGravity;
        this.airControlForceScale = airControlForceScale;
        this.airControlSpeedScale = airControlSpeedScale;
        var shapeIndex = characters.Simulation.Shapes.Add(shape);

        // Characters are dynamic but must not rotate or fall over, so the inverse inertia tensor is
        // left at all zeroes (equivalent to infinite inertia — no torque will ever rotate the capsule).
        bodyHandle = characters.Simulation.Bodies.Add(
            BodyDescription.CreateDynamic(initialPosition, new BodyInertia { InverseMass = 1f / mass },
            new(shapeIndex, minimumSpeculativeMargin, float.MaxValue, ContinuousDetection.Passive), shape.Radius * 0.02f));
        ref var character = ref characters.AllocateCharacter(bodyHandle);
        character.LocalUp = new Vector3(0, 1, 0);
        character.CosMaximumSlope = MathF.Cos(maximumSlope);
        character.JumpVelocity = jumpVelocity;
        character.MaximumVerticalForce = maximumVerticalGlueForce;
        character.MaximumHorizontalForce = maximumHorizontalForce;
        character.MinimumSupportDepth = shape.Radius * -0.01f;
        character.MinimumSupportContinuationDepth = -minimumSpeculativeMargin;
        this.speed = speed;
        this.shape = shape;
    }

    public readonly bool Supported => characters.GetCharacterByBodyHandle(bodyHandle).Supported;
    public readonly Vector3 LinearVelocity => new BodyReference(bodyHandle, characters.Simulation.Bodies).Velocity.Linear;

    /// <summary>The body the character is standing on, when it's one that can move (a ship, not the static world), and
    /// that body's current orientation. Standing means supported: on a surface no steeper than the maximum slope.</summary>
    public readonly bool TryGetSupportBody(out BodyHandle body, out Quaternion orientation)
    {
        ref readonly var character = ref characters.GetCharacterByBodyHandle(bodyHandle);
        if (!character.Supported || character.Support.Mobility == CollidableMobility.Static)
        {
            body = default;
            orientation = Quaternion.Identity;
            return false;
        }
        body = character.Support.BodyHandle;
        orientation = new BodyReference(body, characters.Simulation.Bodies).Pose.Orientation;
        return true;
    }

    /// <summary>Reads WASD + Shift(sprint) + Space(jump) and updates the character's motion goals
    /// for this tick. <paramref name="viewDirectionWorld"/> is the camera's world-space forward
    /// vector (unflattened — the surface-relative projection happens inside CharacterControllers).
    /// <paramref name="frozen"/> ignores the keys (no walking or jumping) while still standing, falling and riding
    /// whatever the character stands on as usual — e.g. while the player is using a lever.</summary>
    public void UpdateCharacterGoals(InputManager input, Vector3 viewDirectionWorld, float simulationTimestepDuration,
                                     bool frozen = false)
    {
        Vector2 movementDirection = default;
        if (!frozen)
        {
            if (input.IsKeyDown(Key.W)) movementDirection += new Vector2(0, 1);
            if (input.IsKeyDown(Key.S)) movementDirection += new Vector2(0, -1);
            if (input.IsKeyDown(Key.A)) movementDirection += new Vector2(-1, 0);
            if (input.IsKeyDown(Key.D)) movementDirection += new Vector2(1, 0);
        }
        var movementDirectionLengthSquared = movementDirection.LengthSquared();
        if (movementDirectionLengthSquared > 0)
            movementDirection /= MathF.Sqrt(movementDirectionLengthSquared);

        ref var character = ref characters.GetCharacterByBodyHandle(bodyHandle);
        character.TryJump = !frozen && input.WasKeyPressed(Key.Space);
        var characterBody = new BodyReference(bodyHandle, characters.Simulation.Bodies);
        var effectiveSpeed = (input.IsKeyDown(Key.ShiftLeft) || input.IsKeyDown(Key.ShiftRight)) ? speed * 1.75f : speed;
        var newTargetVelocity = movementDirection * effectiveSpeed;
        var viewDirection = viewDirectionWorld;

        // Modifying the character's raw data doesn't automatically wake it up — do so explicitly
        // if the goals actually changed, otherwise it won't respond (see BodyActivityDescription).
        if (!characterBody.Awake &&
            ((character.TryJump && character.Supported) ||
             newTargetVelocity != character.TargetVelocity ||
             (newTargetVelocity != Vector2.Zero && character.ViewDirection != viewDirection)))
        {
            characters.Simulation.Awakener.AwakenBody(character.BodyHandle);
        }
        character.TargetVelocity = newTargetVelocity;
        character.ViewDirection = viewDirection;

        // The motion constraint only exists while supported; while airborne we apply gravity/control
        // ourselves. Both run every frame regardless of input, so a falling character with no keys
        // held still gets the extra weight — unlike TargetVelocity/TryJump above, nothing else
        // guarantees the body is awake for this path (e.g. resting motionless against a wall, not
        // "Supported" since the wall fails the slope test, with no jump/target-velocity change to
        // trigger the wake check above), so wake it explicitly before touching its velocity.
        if (!character.Supported)
        {
            if (!characterBody.Awake) characters.Simulation.Awakener.AwakenBody(character.BodyHandle);

            // Extra downward acceleration on top of the world's own (deliberately gentle, -6) gravity,
            // so jumps feel heavy/short rather than floaty. Tuned together with JumpVelocity for a
            // roughly 1-block peak height (see PlayerCharacter's constructor default / TestScene).
            characterBody.Velocity.Linear.Y -= extraFallGravity * simulationTimestepDuration;

            if (movementDirectionLengthSquared > 0)
            {
                QuaternionEx.Transform(character.LocalUp, characterBody.Pose.Orientation, out var characterUp);
                var characterRight = Vector3.Cross(character.ViewDirection, characterUp);
                var rightLengthSquared = characterRight.LengthSquared();
                if (rightLengthSquared > 1e-10f)
                {
                    characterRight /= MathF.Sqrt(rightLengthSquared);
                    var characterForward = Vector3.Cross(characterUp, characterRight);
                    var worldMovementDirection = characterRight * movementDirection.X + characterForward * movementDirection.Y;
                    var currentVelocity = Vector3.Dot(characterBody.Velocity.Linear, worldMovementDirection);
                    var airAccelerationDt = characterBody.LocalInertia.InverseMass * character.MaximumHorizontalForce * airControlForceScale * simulationTimestepDuration;
                    var maximumAirSpeed = effectiveSpeed * airControlSpeedScale;
                    var targetVelocity = MathF.Min(currentVelocity + airAccelerationDt, maximumAirSpeed);
                    var velocityChangeAlongMovementDirection = MathF.Max(0, targetVelocity - currentVelocity);
                    characterBody.Velocity.Linear += worldMovementDirection * velocityChangeAlongMovementDirection;
                }
            }
        }
    }

    /// <summary>First-person eye position: capsule centre + half the capsule's cylindrical length
    /// (top of the capsule) + a small extra rise, minus nothing — no third-person backward offset
    /// (unlike the original demo's debug camera).</summary>
    public readonly Vector3 GetEyePosition(float eyeHeight)
    {
        var characterBody = new BodyReference(bodyHandle, characters.Simulation.Bodies);
        return characterBody.Pose.Position + new Vector3(0, eyeHeight, 0);
    }

    /// <summary>Snaps the capsule to a given world position and zeroes its velocity — used when the
    /// character isn't actively being simulated as "walking" this frame (free-fly or grid-follow
    /// modes), so switching back to Walking always resumes from wherever the camera visually is.</summary>
    public readonly void TeleportTo(Vector3 position)
    {
        var characterBody = new BodyReference(bodyHandle, characters.Simulation.Bodies);
        characterBody.Pose.Position = position;
        characterBody.Velocity.Linear = default;
        characterBody.Velocity.Angular = default;
        characterBody.Awake = true;
    }

    /// <summary>Removes the character's body from the simulation and the character registration.</summary>
    public readonly void Dispose()
    {
        characters.Simulation.Shapes.Remove(new BodyReference(bodyHandle, characters.Simulation.Bodies).Collidable.Shape);
        characters.Simulation.Bodies.Remove(bodyHandle);
        characters.RemoveCharacterByBodyHandle(bodyHandle);
    }
}
