using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
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
    private float airBrakeScale;

    // Jump forgiveness. A Space press is remembered for JumpBufferTime so it isn't lost when no physics
    // step runs this frame (render rate > the 60Hz fixed step) or the character is a hair off the
    // ground; and a jump still works for CoyoteTime after walking off a ledge.
    private const float JumpBufferTime = 0.15f;
    private const float CoyoteTime = 0.12f;
    private float jumpBufferRemaining;
    private float timeSinceSupported;
    private bool jumpedSinceSupported;

    // The moving body (ship) last stood on, if any: air control steers and brakes relative to its velocity
    // rather than the world's, so jumping on a moving deck doesn't leave the player behind. That only holds
    // while the ship is still underneath (a ray straight down hits it first); after ShipReleaseTime away from
    // it (enough to cross a gap in the deck) the player is released, keeping the ship's velocity at that
    // moment as plain momentum, so falling off the side doesn't get dragged along after the ship.
    private const float ShipReleaseTime = 0.5f;
    private const float ShipCheckDistance = 128f; // blocks (1 block = 1 unit)
    private BodyHandle lastSupportBody;
    private bool hasLastSupportBody;
    private float timeNotAboveShip;
    private Vector3 airReferenceVelocity;

    public BodyHandle BodyHandle => bodyHandle;

    public PlayerCharacter(CharacterControllers characters, Vector3 initialPosition, Capsule shape,
        float minimumSpeculativeMargin, float mass, float maximumHorizontalForce, float maximumVerticalGlueForce,
        float jumpVelocity, float speed, float maximumSlope = MathF.PI * 0.25f,
        float extraFallGravity = 12f, float airControlForceScale = 1f, float airControlSpeedScale = 1f,
        float airBrakeScale = 0.5f)
    {
        this.characters = characters;
        this.extraFallGravity = extraFallGravity;
        this.airControlForceScale = airControlForceScale;
        this.airControlSpeedScale = airControlSpeedScale;
        this.airBrakeScale = airBrakeScale;
        jumpBufferRemaining = 0;
        timeSinceSupported = float.MaxValue;
        jumpedSinceSupported = false;
        lastSupportBody = default;
        hasLastSupportBody = false;
        timeNotAboveShip = 0;
        airReferenceVelocity = default;
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
        var characterBody = new BodyReference(bodyHandle, characters.Simulation.Bodies);

        // TryJump is only ever set here, never cleared: CharacterControllers clears it after the next physics
        // step consumes it. Overwriting it every render frame used to drop presses whenever a frame ran no step.
        if (frozen)
        {
            jumpBufferRemaining = 0;
            character.TryJump = false;
        }
        else if (input.WasKeyPressed(Key.Space))
            jumpBufferRemaining = JumpBufferTime;

        if (character.Supported)
        {
            timeSinceSupported = 0;
            hasLastSupportBody = character.Support.Mobility != CollidableMobility.Static;
            if (hasLastSupportBody) lastSupportBody = character.Support.BodyHandle;
            timeNotAboveShip = 0;
            airReferenceVelocity = default;
            // A pending TryJump means the jump hasn't happened yet (no step since it was requested).
            if (!character.TryJump) jumpedSinceSupported = false;
        }
        else
            timeSinceSupported += simulationTimestepDuration;

        if (jumpBufferRemaining > 0)
        {
            if (character.Supported)
            {
                character.TryJump = true;
                jumpedSinceSupported = true;
                jumpBufferRemaining = 0;
            }
            else if (!jumpedSinceSupported && timeSinceSupported <= CoyoteTime)
            {
                // Just walked off an edge: there's no support left to push off, so set the jump velocity
                // directly (same rule as a grounded jump — reach JumpVelocity, don't add on top of it).
                if (!characterBody.Awake) characters.Simulation.Awakener.AwakenBody(character.BodyHandle);
                ref var velocity = ref characterBody.Velocity.Linear;
                velocity.Y = MathF.Max(velocity.Y, character.JumpVelocity);
                jumpedSinceSupported = true;
                jumpBufferRemaining = 0;
            }
            else
                jumpBufferRemaining -= simulationTimestepDuration;
        }

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

            // Air control. Holding a direction accelerates along it up to the (sprint-aware) air speed without
            // ever cutting existing speed along it, while sideways drift is bled off so you can steer; with
            // no keys held the character brakes gently, so letting go near a cliff edge stops you short.
            QuaternionEx.Transform(character.LocalUp, characterBody.Pose.Orientation, out var characterUp);
            ref var linear = ref characterBody.Velocity.Linear;
            UpdateAirReferenceVelocity(characterBody.Pose.Position, characterUp, simulationTimestepDuration);
            var relative = linear - airReferenceVelocity;
            var horizontal = relative - characterUp * Vector3.Dot(relative, characterUp);
            var airAcceleration = characterBody.LocalInertia.InverseMass * character.MaximumHorizontalForce * airControlForceScale;
            Vector3 newHorizontal;
            var characterRight = Vector3.Cross(character.ViewDirection, characterUp);
            var rightLengthSquared = characterRight.LengthSquared();
            if (movementDirectionLengthSquared > 0 && rightLengthSquared > 1e-10f)
            {
                characterRight /= MathF.Sqrt(rightLengthSquared);
                var characterForward = Vector3.Cross(characterUp, characterRight);
                var worldMovementDirection = characterRight * movementDirection.X + characterForward * movementDirection.Y;
                var velocityChange = airAcceleration * simulationTimestepDuration;
                var along = Vector3.Dot(horizontal, worldMovementDirection);
                var maximumAirSpeed = effectiveSpeed * airControlSpeedScale;
                var newAlong = MathF.Max(along, MathF.Min(along + velocityChange, maximumAirSpeed));
                var lateral = horizontal - worldMovementDirection * along;
                newHorizontal = worldMovementDirection * newAlong + MoveTowardsZero(lateral, velocityChange);
            }
            else
            {
                newHorizontal = MoveTowardsZero(horizontal, airAcceleration * airBrakeScale * simulationTimestepDuration);
            }
            linear += newHorizontal - horizontal;
        }
    }

    /// <summary>Follows the ship last stood on while it's still below the player; releases it (freezing its
    /// velocity as the reference) once the player has been off to the side for <see cref="ShipReleaseTime"/>.</summary>
    private void UpdateAirReferenceVelocity(Vector3 position, Vector3 up, float dt)
    {
        if (!hasLastSupportBody) return;
        var bodies = characters.Simulation.Bodies;
        if (!bodies.BodyExists(lastSupportBody))
        {
            hasLastSupportBody = false;
            return;
        }
        var shipVelocity = new BodyReference(lastSupportBody, bodies).Velocity.Linear;

        var hitHandler = new NearestHitHandler { Ignore = bodyHandle, T = float.MaxValue };
        var down = -up;
        characters.Simulation.RayCast(position, down, ShipCheckDistance, ref hitHandler);
        var aboveShip = hitHandler.T < float.MaxValue &&
                        hitHandler.Hit.Mobility != CollidableMobility.Static && hitHandler.Hit.BodyHandle == lastSupportBody;

        timeNotAboveShip = aboveShip ? 0 : timeNotAboveShip + dt;
        airReferenceVelocity = shipVelocity;
        if (timeNotAboveShip > ShipReleaseTime) hasLastSupportBody = false;
    }

    /// <summary>Records the nearest collidable along a ray, skipping the character's own capsule.</summary>
    private struct NearestHitHandler : IRayHitHandler
    {
        public BodyHandle Ignore;
        public float T;
        public CollidableReference Hit;

        public bool AllowTest(CollidableReference collidable) =>
            collidable.Mobility == CollidableMobility.Static || collidable.BodyHandle != Ignore;

        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
        {
            if (t < T)
            {
                T = t;
                Hit = collidable;
                maximumT = t;
            }
        }
    }

    private static Vector3 MoveTowardsZero(Vector3 v, float amount)
    {
        var length = v.Length();
        return length <= amount ? Vector3.Zero : v * ((length - amount) / length);
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
