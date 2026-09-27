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
/// The player's character: a capsule body registered with <see cref="CharacterControllers"/> (which handles support
/// detection and the grounded motion constraint), driven by this project's <see cref="InputManager"/>. Started as an
/// adaptation of BepuPhysics2's Demos/Demos/Characters/CharacterInput.cs (v2.4.0); on top of that it owns the
/// game-feel rules: jump buffering and coyote time, extra fall gravity and air control (relative to the ship last
/// stood on), and Minecraft-style crouching that won't walk off edges.
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

    // Crouch (Ctrl), Minecraft-style sneak: slower, a lower eye, and while standing on something the character won't
    // walk off its edge. The centre may hang up to CrouchEdgeOverhang past an edge, on both axes at once at a corner,
    // which puts it CrouchEdgeOverhang·√2 ≈ 0.12 from the corner point (short of the ~0.14 at which the capsule rolls
    // off it). A drop of more than CrouchMaximumDrop counts as an edge.
    private const float CrouchSpeedScale = 0.3f;
    private const float CrouchEyeDrop = 0.3f;
    private const float CrouchEyeDropSpeed = 3f; // blocks per second
    private const float CrouchEdgeOverhang = 0.085f;
    private const float CrouchMaximumDrop = 0.6f;
    private float eyeDrop;

    public BodyHandle BodyHandle => bodyHandle;

    public PlayerCharacter(CharacterControllers characters, Vector3 initialPosition, Capsule shape,
        float minimumSpeculativeMargin, float mass, float maximumHorizontalForce, float maximumVerticalGlueForce,
        float jumpVelocity, float speed, float maximumSlope = MathF.PI * 0.25f,
        float extraFallGravity = 12f, float airControlForceScale = 1f, float airControlSpeedScale = 1f,
        float airBrakeScale = 0.5f, DefaultEcs.Entity entity = default)
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
        eyeDrop = 0;
        var shapeIndex = characters.Simulation.Shapes.Add(shape);

        // Characters are dynamic but must not rotate or fall over, so the inverse inertia tensor is
        // left at all zeroes (equivalent to infinite inertia — no torque will ever rotate the capsule).
        bodyHandle = characters.Simulation.Bodies.Add(
            BodyDescription.CreateDynamic(initialPosition, new BodyInertia { InverseMass = 1f / mass },
            new(shapeIndex, minimumSpeculativeMargin, float.MaxValue, ContinuousDetection.Passive), shape.Radius * 0.02f));
        ref var character = ref characters.AllocateCharacter(bodyHandle, entity);
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

    /// <summary>Reads WASD + Shift(sprint) + Ctrl(crouch) + Space(jump) and updates the character's motion goals
    /// for this frame. <paramref name="viewDirectionWorld"/> is the camera's world-space forward
    /// vector (unflattened — the surface-relative projection happens inside CharacterControllers).
    /// <paramref name="dt"/> is the render frame's duration, not the physics step's: this runs once per frame, and its
    /// timers and air forces advance by it. <paramref name="frozen"/> ignores the keys (no walking or jumping) while still
    /// standing, falling and riding whatever the character stands on as usual — e.g. while the player is using a lever.</summary>
    public void UpdateCharacterGoals(InputManager input, Vector3 viewDirectionWorld, float dt,
                                     bool frozen = false)
    {
        var keys = new CharacterInput();
        if (!frozen)
        {
            if (input.IsKeyDown(Key.W)) keys.Move += new Vector2(0, 1);
            if (input.IsKeyDown(Key.S)) keys.Move += new Vector2(0, -1);
            if (input.IsKeyDown(Key.A)) keys.Move += new Vector2(-1, 0);
            if (input.IsKeyDown(Key.D)) keys.Move += new Vector2(1, 0);
            keys.Sprint = input.IsKeyDown(Key.ShiftLeft) || input.IsKeyDown(Key.ShiftRight);
            keys.Crouch = input.IsKeyDown(Key.ControlLeft) || input.IsKeyDown(Key.ControlRight);
            keys.JumpPressed = input.WasKeyPressed(Key.Space);
        }
        UpdateCharacterGoals(keys, viewDirectionWorld, dt, frozen);
    }

    /// <summary>The keys <see cref="UpdateCharacterGoals(InputManager, Vector3, float, bool)"/> reads, for driving the
    /// character without an <see cref="InputManager"/>. <see cref="Move"/> is (strafe right, forward), any length.</summary>
    public struct CharacterInput
    {
        public Vector2 Move;
        public bool Sprint;
        public bool Crouch;
        public bool JumpPressed;
    }

    public void UpdateCharacterGoals(CharacterInput keys, Vector3 viewDirectionWorld, float dt,
                                     bool frozen = false)
    {
        var movementDirection = keys.Move;
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
        else if (keys.JumpPressed)
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
            timeSinceSupported += dt;

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
                jumpBufferRemaining -= dt;
        }

        var crouching = !frozen && keys.Crouch;
        var eyeDropTarget = crouching ? CrouchEyeDrop : 0f;
        var eyeDropStep = CrouchEyeDropSpeed * dt;
        eyeDrop = eyeDrop < eyeDropTarget ? MathF.Min(eyeDrop + eyeDropStep, eyeDropTarget) : MathF.Max(eyeDrop - eyeDropStep, eyeDropTarget);

        var effectiveSpeed = crouching ? speed * CrouchSpeedScale
            : keys.Sprint ? speed * 1.75f : speed;
        var newTargetVelocity = movementDirection * effectiveSpeed;
        var viewDirection = viewDirectionWorld;
        if (crouching && character.Supported && !character.TryJump && newTargetVelocity != Vector2.Zero)
            newTargetVelocity = KeepAwayFromEdges(character, characterBody, newTargetVelocity, viewDirection, dt);

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
            characterBody.Velocity.Linear.Y -= extraFallGravity * dt;

            // Air control. Holding a direction accelerates along it up to the (sprint-aware) air speed without
            // ever cutting existing speed along it, while sideways drift is bled off so you can steer; with
            // no keys held the character brakes gently, so letting go near a cliff edge stops you short.
            QuaternionEx.Transform(character.LocalUp, characterBody.Pose.Orientation, out var characterUp);
            ref var linear = ref characterBody.Velocity.Linear;
            UpdateAirReferenceVelocity(characterBody.Pose.Position, characterUp, dt);
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
                var velocityChange = airAcceleration * dt;
                var along = Vector3.Dot(horizontal, worldMovementDirection);
                var maximumAirSpeed = effectiveSpeed * airControlSpeedScale;
                var newAlong = MathF.Max(along, MathF.Min(along + velocityChange, maximumAirSpeed));
                var lateral = horizontal - worldMovementDirection * along;
                newHorizontal = worldMovementDirection * newAlong + MoveTowardsZero(lateral, velocityChange);
            }
            else
            {
                newHorizontal = MoveTowardsZero(horizontal, airAcceleration * airBrakeScale * dt);
            }
            linear += newHorizontal - horizontal;
        }
    }

    /// <summary>Crouch edge guard: clips the target velocity, one axis at a time (so the character still slides along an
    /// edge), wherever it would carry the character past an edge. Axes are the support's: world X/Z on terrain, the
    /// ship's own axes on a ship. Velocities are relative to the support, as the motion constraint's target is.</summary>
    private readonly Vector2 KeepAwayFromEdges(in CharacterController character, BodyReference characterBody, Vector2 targetVelocity,
                                               Vector3 viewDirection, float dt)
    {
        QuaternionEx.Transform(character.LocalUp, characterBody.Pose.Orientation, out var up);
        var right = Vector3.Cross(viewDirection, up);
        var rightLengthSquared = right.LengthSquared();
        if (rightLengthSquared < 1e-10f) return targetVelocity;
        right /= MathF.Sqrt(rightLengthSquared);
        var forward = Vector3.Cross(up, right);
        var desired = right * targetVelocity.X + forward * targetVelocity.Y;

        var axisA = Vector3.UnitX;
        var axisB = Vector3.UnitZ;
        var velocity = characterBody.Velocity.Linear;
        if (TryGetSupportBody(out var supportBody, out var supportOrientation))
        {
            axisA = HorizontalAxis(Vector3.Transform(Vector3.UnitX, supportOrientation), up, Vector3.UnitX);
            axisB = Vector3.Normalize(Vector3.Cross(axisA, up));
            velocity -= new BodyReference(supportBody, characters.Simulation.Bodies).Velocity.Linear;
        }

        var position = characterBody.Pose.Position;

        // Already hanging further out than the limit allows (Ctrl pressed after walking out, an overshoot, a ship
        // moving underneath): judge moves against how far out the character already is instead, so it can still
        // slide along the edge or step back, just not go further out. Found to within ~1cm by bisection.
        var overhang = CrouchEdgeOverhang;
        if (!HasGroundNear(position, axisA, axisB, up, overhang))
        {
            float low = overhang, high = shape.Radius;
            if (!HasGroundNear(position, axisA, axisB, up, high))
                return targetVelocity; // nothing within reach underneath: not standing on an edge this guard understands
            while (high - low > 0.01f)
            {
                var middle = (low + high) / 2;
                if (HasGroundNear(position, axisA, axisB, up, middle)) high = middle; else low = middle;
            }
            overhang = high;
        }

        // Look ahead by the distance the character needs to stop — this frame's travel plus braking at the motion
        // constraint's maximum deceleration, from whichever is faster of the target and its actual speed (e.g. still
        // sprinting when Ctrl goes down) — so it halts at the overhang limit rather than after it.
        var deceleration = characterBody.LocalInertia.InverseMass * character.MaximumHorizontalForce;
        var targetA = Vector3.Dot(desired, axisA);
        var stopA = StoppingDistance(targetA, Vector3.Dot(velocity, axisA), dt, deceleration);
        if (targetA != 0 && !HasGroundNear(position + axisA * stopA, axisA, axisB, up, overhang))
        {
            targetA = 0;
            stopA = 0;
        }
        var targetB = Vector3.Dot(desired, axisB);
        var stopB = StoppingDistance(targetB, Vector3.Dot(velocity, axisB), dt, deceleration);
        if (targetB != 0 && !HasGroundNear(position + axisA * stopA + axisB * stopB, axisA, axisB, up, overhang))
            targetB = 0;
        var allowed = axisA * targetA + axisB * targetB;
        return new Vector2(Vector3.Dot(allowed, right), Vector3.Dot(allowed, forward));
    }

    /// <summary>Signed distance along an axis the character covers before it can stop, moving towards
    /// <paramref name="target"/> (a signed speed along the axis) at <paramref name="current"/>.</summary>
    private static float StoppingDistance(float target, float current, float dt, float deceleration)
    {
        if (target == 0) return 0;
        var speed = MathF.Max(MathF.Abs(target), current * MathF.Sign(target));
        return MathF.Sign(target) * (speed * dt + speed * speed / (2 * deceleration));
    }

    private static Vector3 HorizontalAxis(Vector3 axis, Vector3 up, Vector3 fallback)
    {
        axis -= up * Vector3.Dot(axis, up);
        var lengthSquared = axis.LengthSquared();
        return lengthSquared > 1e-6f ? axis / MathF.Sqrt(lengthSquared) : fallback;
    }

    /// <summary>True if there's something to stand on within <see cref="CrouchMaximumDrop"/> below the feet, anywhere
    /// under a square footprint <paramref name="overhang"/> each way from <paramref name="centre"/>, lined up with
    /// the support's axes: a thin plate of that size is swept straight down from just above the feet. Being square and
    /// aligned with the blocks (as Minecraft's is), the overhang allowed at a corner is the same on both axes whichever
    /// way the character arrives or leaves, so it never shifts outward along one edge as it moves away from the other.
    /// The sweep starts clear of the floor; anything it overlaps at the start is a wall the capsule (far wider than the
    /// plate) couldn't be pressed into, so zero-distance hits are ignored.</summary>
    private readonly bool HasGroundNear(Vector3 centre, Vector3 axisA, Vector3 axisB, Vector3 up, float overhang)
    {
        const float startAboveFeet = 0.1f;
        const float plateThickness = 0.02f;
        var feet = centre - up * (shape.HalfLength + shape.Radius);
        var start = feet + up * (startAboveFeet + plateThickness / 2);
        var basis = new Matrix3x3 { X = axisA, Y = up, Z = axisB };
        QuaternionEx.CreateFromRotationMatrix(basis, out var orientation);
        var hitHandler = new AnyHitHandler { Ignore = bodyHandle };
        characters.Simulation.Sweep(new Box(2 * overhang, plateThickness, 2 * overhang), new RigidPose(start, orientation),
            new BodyVelocity(-up), startAboveFeet + CrouchMaximumDrop, characters.Simulation.BufferPool, ref hitHandler);
        return hitHandler.Hit;
    }

    /// <summary>Records whether a sweep hit anything other than the character itself, ignoring overlaps at its start.</summary>
    private struct AnyHitHandler : ISweepHitHandler
    {
        public BodyHandle Ignore;
        public bool Hit;

        public bool AllowTest(CollidableReference collidable) =>
            collidable.Mobility == CollidableMobility.Static || collidable.BodyHandle != Ignore;

        public bool AllowTest(CollidableReference collidable, int child) => true;

        public void OnHit(ref float maximumT, float t, in Vector3 hitLocation, in Vector3 hitNormal, CollidableReference collidable)
        {
            Hit = true;
            maximumT = t;
        }

        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable) { }
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

    /// <summary>First-person eye position: capsule centre + <paramref name="eyeHeight"/>, lowered while crouching
    /// — no third-person backward offset (unlike the original demo's debug camera).</summary>
    public readonly Vector3 GetEyePosition(float eyeHeight)
    {
        var characterBody = new BodyReference(bodyHandle, characters.Simulation.Bodies);
        return characterBody.Pose.Position + new Vector3(0, eyeHeight - eyeDrop, 0);
    }

    /// <summary>Snaps the capsule to a given world position and zeroes its velocity — used when the
    /// character isn't actively being simulated as "walking" this frame (free-fly or grid-follow
    /// modes), so switching back to Walking always resumes from wherever the camera visually is.
    /// Also forgets the jump, air and crouch state, so walking resumes fresh: e.g. the air reference
    /// velocity of a ship left long ago doesn't drag the character after switching back mid-air.</summary>
    public void TeleportTo(Vector3 position)
    {
        jumpBufferRemaining = 0;
        timeSinceSupported = float.MaxValue;
        jumpedSinceSupported = false;
        hasLastSupportBody = false;
        timeNotAboveShip = 0;
        airReferenceVelocity = default;
        eyeDrop = 0;

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
