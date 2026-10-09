using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;

namespace ClearSkies.Engine.Physics.Characters;

/// <summary>
/// The player's character: a capsule body registered with <see cref="CharacterControllers"/>, which owns its physics —
/// support detection, the grounded motion constraint, jumping (buffered, with coyote time), extra fall gravity and air
/// control relative to the ship last stood on. This side turns the tick's keys into the
/// character's goals each tick (target velocity, view direction, jump requests), and adds Minecraft-style crouching:
/// slower, a lower eye, and a guard that won't walk off edges; and a glider (hold Space while falling, see
/// <see cref="Gliding"/>). Started as an adaptation of BepuPhysics2's
/// Demos/Demos/Characters/CharacterInput.cs (v2.4.0).
/// </summary>
public struct PlayerCharacter
{
    private BodyHandle bodyHandle;
    private CharacterControllers characters;
    private float speed;
    private Capsule shape;

    // A Space press stays pending for JumpBufferTime (see CharacterController.JumpRequestRemaining), so a press slightly
    // before landing still jumps; CoyoteTime lets a jump still push off for a moment after walking off a ledge.
    private const float JumpBufferTime = 0.15f;
    private const float CoyoteTime = 0.12f;

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

    // Glider (Space held while falling, until landing or letting go): AirResistanceSystem swaps the capsule's drag for a
    // wing's (ResistsAir.Glider), turned the way the player looks. The extra fall gravity and air control are off
    // meanwhile, so the air alone holds the character up and steers it (see WingOrientation): look ahead to glide, down
    // to dive, up to climb on the speed picked up. Looking level holds the wing GlideTrim (radians) nose-down, so it
    // glides ahead (about 4 m ahead per 1 m down, at 8 m/s) rather than sinking straight down. The nose is held to at
    // most GlideMaximumAttack above the way the character is going, so a climb runs out of speed and the nose drops
    // (a flat plate held nose-up while slow would glide backwards). Turning the look banks the wing, GlideBankPerTurn
    // radians of bank per radian between the look's heading and the character's, up to GlideMaximumBank, so its push
    // swings the character round to follow.
    private const float GlideTrim = 0.2f;
    private const float GlideMaximumAttack = 0.25f;
    private const float GlideBankPerTurn = 3f, GlideMaximumBank = 1f;
    private bool gliding;
    private Quaternion glideOrientation;
    private float extraFallGravity, airControlForceScale, airBrakeScale; // the character's own, restored on landing

    // To take the capsule out of the simulation and put it back (see Suspend): what its body and character were made from.
    private TypedIndex shapeIndex;
    private float minimumSpeculativeMargin, mass;
    private DefaultEcs.Entity entity;
    private CharacterController settings;
    private bool suspended;

    public BodyHandle BodyHandle => bodyHandle;

    /// <summary>Whether the capsule is out of the simulation (see <see cref="Suspend"/>).</summary>
    public readonly bool Suspended => suspended;

    public PlayerCharacter(CharacterControllers characters, Vector3 initialPosition, Capsule shape,
        float minimumSpeculativeMargin, float mass, float maximumHorizontalForce, float maximumVerticalGlueForce,
        float jumpVelocity, float speed, float maximumSlope = MathF.PI * 0.25f,
        float extraFallGravity = 12f, float airControlForceScale = 1f, float airControlSpeedScale = 1f,
        float airBrakeScale = 0.5f, DefaultEcs.Entity entity = default)
    {
        this.characters = characters;
        eyeDrop = 0;
        shapeIndex = characters.Simulation.Shapes.Add(shape);
        this.minimumSpeculativeMargin = minimumSpeculativeMargin;
        this.mass = mass;
        this.entity = entity;
        this.shape = shape;
        settings = default;
        suspended = false;
        gliding = false;
        glideOrientation = Quaternion.Identity;
        this.extraFallGravity = extraFallGravity;
        this.airControlForceScale = airControlForceScale;
        this.airBrakeScale = airBrakeScale;
        bodyHandle = AddBody(initialPosition);
        ref var character = ref characters.AllocateCharacter(bodyHandle, entity);
        character.LocalUp = new Vector3(0, 1, 0);
        character.CosMaximumSlope = MathF.Cos(maximumSlope);
        character.JumpVelocity = jumpVelocity;
        character.CoyoteTime = CoyoteTime;
        character.ExtraFallGravity = extraFallGravity;
        character.AirControlForceScale = airControlForceScale;
        character.AirControlSpeedScale = airControlSpeedScale;
        character.AirBrakeScale = airBrakeScale;
        character.MaximumVerticalForce = maximumVerticalGlueForce;
        character.MaximumHorizontalForce = maximumHorizontalForce;
        character.MinimumSupportDepth = shape.Radius * -0.01f;
        character.MinimumSupportContinuationDepth = -minimumSpeculativeMargin;
        this.speed = speed;
    }

    private readonly BodyHandle AddBody(Vector3 position) =>
        // Characters are dynamic but must not rotate or fall over, so the inverse inertia tensor is
        // left at all zeroes (equivalent to infinite inertia — no torque will ever rotate the capsule).
        characters.Simulation.Bodies.Add(
            BodyDescription.CreateDynamic(position, new BodyInertia { InverseMass = 1f / mass },
            new(shapeIndex, minimumSpeculativeMargin, float.MaxValue, ContinuousDetection.Passive), shape.Radius * 0.02f));

    /// <summary>Takes the capsule out of the simulation (for free-flying): nothing touches it, supports it or is pushed
    /// by it until <see cref="Resume"/>. Its body handle is meaningless meanwhile.</summary>
    public void Suspend()
    {
        if (suspended) return;
        settings = characters.GetCharacterByBodyHandle(bodyHandle);
        characters.RemoveCharacterByBodyHandle(bodyHandle);
        characters.Simulation.Bodies.Remove(bodyHandle); // with the motion constraint, if it had one
        suspended = true;
    }

    /// <summary>Puts the capsule back into the simulation at <paramref name="position"/>, at rest, with the same settings
    /// and no jump, air or crouch state (see <see cref="TeleportTo"/>). Its body handle may change.</summary>
    public void Resume(Vector3 position)
    {
        if (!suspended) return;
        bodyHandle = AddBody(position);
        ref var character = ref characters.AllocateCharacter(bodyHandle, entity);
        var s = settings;
        s.BodyHandle = bodyHandle;
        s.Supported = false; // no motion constraint to remove
        s.Support = default;
        s.TargetVelocity = default;
        character = s;
        character.ResetJumpAndAirState();
        eyeDrop = 0;
        suspended = false;
        SetGliding(ref character, false);
    }

    /// <summary>Whether the glider is out: <see cref="CharacterInput.Glide"/> held since some moment the character was
    /// airborne and falling, and it hasn't landed since.</summary>
    public readonly bool Gliding => gliding && !suspended;

    /// <summary>Which way the wing faces while <see cref="Gliding"/>: body −z ahead, +y its upper side (see
    /// <see cref="WingOrientation"/>).</summary>
    public readonly Quaternion GlideOrientation => glideOrientation;

    private void SetGliding(ref CharacterController character, bool on)
    {
        gliding = on;
        character.ExtraFallGravity = on ? 0f : extraFallGravity;
        character.AirControlForceScale = on ? 0f : airControlForceScale;
        character.AirBrakeScale = on ? 0f : airBrakeScale;
    }

    public readonly bool Supported => !suspended && characters.GetCharacterByBodyHandle(bodyHandle).Supported;
    public readonly Vector3 LinearVelocity => suspended ? default : new BodyReference(bodyHandle, characters.Simulation.Bodies).Velocity.Linear;

    /// <summary>The body the character is standing on, when it's one that can move (a ship, not the static world), and
    /// that body's current orientation. Standing means supported: on a surface no steeper than the maximum slope.</summary>
    public readonly bool TryGetSupportBody(out BodyHandle body, out Quaternion orientation)
    {
        if (suspended) { body = default; orientation = Quaternion.Identity; return false; }
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

    /// <summary>The keys that drive the character this tick (see <c>PlayerMovementSystem.CharacterKeys</c>).
    /// <see cref="Move"/> is (strafe right, forward), any length.</summary>
    public struct CharacterInput
    {
        public Vector2 Move;
        public bool Sprint;
        public bool Crouch;
        public bool JumpPressed;
        /// <summary>Held to glide (see <see cref="Gliding"/>).</summary>
        public bool Glide;
    }

    /// <summary>Updates the character's goals for this tick from its keys. <paramref name="viewDirectionWorld"/> is the
    /// camera's world-space forward vector (unflattened — the surface-relative projection happens inside
    /// CharacterControllers). <paramref name="dt"/> is the tick's duration (it only eases the crouch eye).
    /// <paramref name="frozen"/> ignores the keys (no walking or jumping) while still standing, falling and riding
    /// whatever the character stands on as usual — e.g. while the player is using a lever.</summary>
    public void UpdateCharacterGoals(CharacterInput keys, Vector3 viewDirectionWorld, float dt, bool frozen = false)
    {
        var movementDirection = frozen ? Vector2.Zero : keys.Move;
        var movementDirectionLengthSquared = movementDirection.LengthSquared();
        if (movementDirectionLengthSquared > 0)
            movementDirection /= MathF.Sqrt(movementDirectionLengthSquared);

        ref var character = ref characters.GetCharacterByBodyHandle(bodyHandle);
        var characterBody = new BodyReference(bodyHandle, characters.Simulation.Bodies);

        if (frozen)
            character.JumpRequestRemaining = 0;
        else if (keys.JumpPressed)
            character.JumpRequestRemaining = JumpBufferTime;

        var crouching = !frozen && keys.Crouch;
        var eyeDropTarget = crouching ? CrouchEyeDrop : 0f;
        var eyeDropStep = CrouchEyeDropSpeed * dt;
        eyeDrop = eyeDrop < eyeDropTarget ? MathF.Min(eyeDrop + eyeDropStep, eyeDropTarget) : MathF.Max(eyeDrop - eyeDropStep, eyeDropTarget);

        var effectiveSpeed = crouching ? speed * CrouchSpeedScale
            : keys.Sprint ? speed * 1.75f : speed;
        var newTargetVelocity = movementDirection * effectiveSpeed;
        var viewDirection = viewDirectionWorld;

        // The glider opens only on the way down (so holding Space through a jump doesn't open it at once) but then
        // stays open, a climb included, until the key is let go or the character lands.
        var glide = !frozen && keys.Glide && !character.Supported &&
                    (gliding || Vector3.Dot(characterBody.Velocity.Linear, character.LocalUp) < 0);
        if (glide != gliding) SetGliding(ref character, glide);
        if (glide) glideOrientation = WingOrientation(viewDirection, characterBody.Velocity.Linear);
        if (crouching && character.Supported && !character.JumpPending && newTargetVelocity != Vector2.Zero)
            newTargetVelocity = KeepAwayFromEdges(character, characterBody, newTargetVelocity, viewDirection, dt);

        // Modifying the character's raw data doesn't automatically wake it up — do so explicitly if the goals changed,
        // otherwise it won't respond (see BodyActivityDescription). Airborne, CharacterControllers moves it every step
        // (extra gravity, air control), which only happens to awake bodies, and nothing else guarantees it's awake (e.g.
        // resting motionless against a wall: not "Supported", since the wall fails the slope test), so wake it then too.
        if (!characterBody.Awake &&
            (character.JumpPending || !character.Supported ||
             newTargetVelocity != character.TargetVelocity ||
             (newTargetVelocity != Vector2.Zero && character.ViewDirection != viewDirection)))
        {
            characters.Simulation.Awakener.AwakenBody(character.BodyHandle);
        }
        character.TargetVelocity = newTargetVelocity;
        character.ViewDirection = viewDirection;
    }

    /// <summary>Drives the character from another player's movement rather than keys: their copy here (see
    /// RemoteBodyProxySystem). <paramref name="velocity"/> is the horizontal velocity to reach, relative to what it stands on
    /// (or, in the air, its air reference), as the keys' target is; <paramref name="jump"/> jumps.</summary>
    public void Drive(Vector3 velocity, bool jump)
    {
        ref var character = ref characters.GetCharacterByBodyHandle(bodyHandle);
        var characterBody = new BodyReference(bodyHandle, characters.Simulation.Bodies);
        if (jump) character.JumpRequestRemaining = JumpBufferTime;
        // Facing -Z, the target's axes are world X (right) and -Z (forward).
        var target = new Vector2(velocity.X, -velocity.Z);
        if (!characterBody.Awake && (character.JumpPending || !character.Supported || target != character.TargetVelocity))
            characters.Simulation.Awakener.AwakenBody(character.BodyHandle);
        character.TargetVelocity = target;
        character.ViewDirection = -Vector3.UnitZ;
    }

    /// <summary>The glider's wing for a look along <paramref name="view"/> while moving at <paramref name="velocity"/>:
    /// turned to the look's heading, pitched to the look (less <see cref="GlideTrim"/>), but never more than
    /// <see cref="GlideMaximumAttack"/> nose-up of the way it's actually going, and banked into the turn.</summary>
    public static Quaternion WingOrientation(Vector3 view, Vector3 velocity)
    {
        var yaw = MathF.Atan2(-view.X, -view.Z);
        var pitch = MathF.Asin(System.Math.Clamp(view.Y / MathF.Max(view.Length(), 1e-6f), -1f, 1f)) - GlideTrim;

        // How the character is actually moving, measured along the look's heading: climbing, gliding or falling, and
        // which way. With nothing left going ahead the path points straight down, which drops the nose: a stall.
        var ahead = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        var forward = MathF.Max(Vector3.Dot(velocity, ahead), 0f);
        var path = MathF.Atan2(velocity.Y, forward);
        pitch = MathF.Min(pitch, path + GlideMaximumAttack);

        // Bank towards the look's heading from the way the character is going, like a glider turning.
        float roll = 0f;
        var horizontal = new Vector2(velocity.X, velocity.Z);
        if (horizontal.LengthSquared() > 1f)
        {
            var heading = MathF.Atan2(-velocity.X, -velocity.Z);
            var turn = MathF.IEEERemainder(yaw - heading, 2 * MathF.PI);
            roll = System.Math.Clamp(turn * GlideBankPerTurn, -GlideMaximumBank, GlideMaximumBank);
        }
        return Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
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

    /// <summary>How far above the capsule's centre the eye is: <paramref name="eyeHeight"/>, less while crouching.</summary>
    public readonly float EyeOffset(float eyeHeight) => eyeHeight - eyeDrop;

    /// <summary>The capsule's centre.</summary>
    public readonly Vector3 Position => new BodyReference(bodyHandle, characters.Simulation.Bodies).Pose.Position;

    /// <summary>First-person eye position: capsule centre + <paramref name="eyeHeight"/>, lowered while crouching
    /// — no third-person backward offset (unlike the original demo's debug camera).</summary>
    public readonly Vector3 GetEyePosition(float eyeHeight)
    {
        var characterBody = new BodyReference(bodyHandle, characters.Simulation.Bodies);
        return characterBody.Pose.Position + new Vector3(0, eyeHeight - eyeDrop, 0);
    }

    /// <summary>Snaps the capsule to a given world position and zeroes its velocity. Also forgets the jump, air and
    /// crouch state, so walking resumes fresh: e.g. the air reference velocity of a ship left long ago doesn't drag the
    /// character. See <see cref="ClearSkies.Engine.ECS.Players.Teleport"/>, which moves the player's Transform with it.</summary>
    public void TeleportTo(Vector3 position)
    {
        if (suspended) return; // put back where the player is when it resumes
        ref var character = ref characters.GetCharacterByBodyHandle(bodyHandle);
        character.ResetJumpAndAirState();
        SetGliding(ref character, false);
        eyeDrop = 0;
        var characterBody = new BodyReference(bodyHandle, characters.Simulation.Bodies);
        characterBody.Pose.Position = position;
        characterBody.Velocity.Linear = default;
        characterBody.Velocity.Angular = default;
        characterBody.Awake = true;
    }

    /// <summary>Moves the character by <paramref name="offset"/>, keeping its velocity and its jump, air and crouch state:
    /// a correction, not a teleport.</summary>
    public readonly void MoveBy(Vector3 offset)
    {
        if (suspended) return;
        var characterBody = new BodyReference(bodyHandle, characters.Simulation.Bodies);
        characterBody.Pose.Position += offset;
        characterBody.Awake = true;
    }

    /// <summary>Sets the character's velocity, waking it.</summary>
    public readonly void SetVelocity(Vector3 velocity)
    {
        if (suspended) return;
        var characterBody = new BodyReference(bodyHandle, characters.Simulation.Bodies);
        characterBody.Velocity.Linear = velocity;
        characterBody.Awake = true;
    }

    /// <summary>Removes the character's body from the simulation and the character registration.</summary>
    public readonly void Dispose()
    {
        characters.Simulation.Shapes.Remove(shapeIndex);
        if (suspended) return;
        characters.Simulation.Bodies.Remove(bodyHandle);
        characters.RemoveCharacterByBodyHandle(bodyHandle);
    }
}
