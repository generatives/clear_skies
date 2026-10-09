using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Full airship flight pipeline (Milestone 5 Phases 5.3/5.4), per dynamic grid each tick: first a
/// control law deciding the force and torque the ship wants, then
/// immediately realizing that desired force/torque by
/// distributing it across the grid's Fan/Buoyant blocks (or applying it directly in "free propulsion"
/// debug mode). Previously two systems (a control system computing desired force/torque into a
/// <c>DynamicGrid</c> field, and a propulsion system consuming it next) — merged into one because nothing
/// outside this pipeline ever reads the value in between, and having two separate systems for "decide
/// what the ship should do" vs. "make it happen" was easy to mix up despite being two halves of the same
/// per-tick computation. Runs before <see cref="PhysicsWorld"/> steps, so impulses are integrated the
/// same tick they're computed.
///
/// The control law always self-levels (pitch/roll) and cancels gravity and the ship's Buoyant lift (feedforward). Past
/// that it has two modes:
/// <list type="bullet">
/// <item>Piloted (the grid carries <see cref="PilotedComponent"/>, set by <see cref="GridPilotSystem"/>): forward,
/// right, vertical and yaw velocity targets from the keyboard. The ship's own controls are ignored.</item>
/// <item>Otherwise, the ship's own controls (<see cref="ShipControls"/>, which its levers and wheels show and set).
/// Each thrust axis' setting, squared so it ramps up (fine near upright), asks for that fraction of a tunable maximum
/// force along the axis. Nothing tracks a speed: the ship speeds up until air resistance (below) matches the force.
/// The turn setting asks for turning force (torque about world up) the same way, clockwise to starboard: the ship's
/// turn speeds up until air resistance on its rotation matches, and coasts to a stop when the turn is centred.</item>
/// </list>
///
/// Air resistance against the ship's movement through the (moving) air is <see cref="AirResistanceSystem"/>'s. This
/// system keeps an extra drag torque against the spin, growing with the square of the turn rate, so a steady wheel
/// torque gives a top turn rate and a ship left alone stops turning (to be retuned now that the air also damps spin).
/// It's part of the world, not the controls: applied directly, not through the Fans.
///
/// Propulsion allocation solves for Fan thrusts rather than sharing the demand out: it finds each Fan's thrust,
/// between 0 and the Fan's max (Fans only push), so that together they produce the desired force and torque as
/// closely as possible (a box-constrained least-squares problem; see <see cref="AllocateThrust"/>). When the Fans
/// can meet the demand they meet it exactly, whatever mix of directions it has and however many Fans point each
/// way. When they can't, torque comes first (see <see cref="AllocateKeepingTorque"/>): the ship gets less force rather
/// than tipping over. An earlier proportional scheme gave each Fan <c>(its alignment / total alignment) × |demand|</c>, which is
/// only exact when a single group of Fans is working: thrusting forward on a ship with more lift Fans than forward
/// Fans handed the lift group too much of the bigger total, so ships climbed whenever they accelerated. When the
/// demand is out of reach (Fans at max, or none pointing the needed way) the solve gives the closest achievable
/// force and torque.
///
/// Buoyant blocks apply constant passive lift at their own cells. The control law cancels both what that lift
/// adds (feedforward): its force alongside gravity, and its torque about the centre of mass, which otherwise
/// leaves a ship with off-centre Buoyant blocks settled at a tilt.
///
/// Fan and Buoyant blocks are entity blocks (see <see cref="Fan"/>, <see cref="Buoyant"/>): each tick starts by
/// grouping their entities by the volume they belong to (<see cref="BlockRef.Volume"/>), so a ship's blocks are
/// found by walking its own lists instead of scanning its voxels. Fans and Buoyant blocks on the static world are
/// ignored — only grids fly.
///
/// Debug "free propulsion" mode (the "Free propulsion" checkbox in <see cref="DrawDebugUi"/>) applies the
/// control law's force/torque directly at the grid's centre of mass instead of allocating it across Fan
/// blocks, so a ship can be flown/stabilized for testing without needing any Fan blocks — but Buoyant
/// blocks still apply their own passive lift either way (it's a separate, always-on mechanic, not part of
/// the Fan allocation this mode is replacing).
/// </summary>
public sealed class AirshipFlightSystem : ISystem
{
    private readonly EntitySet       _grids;
    private readonly EntitySet       _fans;
    private readonly EntitySet       _buoyants;

    /// <summary>This tick's Fan and Buoyant blocks of one volume, by cell and orientation.</summary>
    private sealed class ShipBlocks
    {
        public readonly List<BlockRef> Fans        = new();
        public readonly List<Entity>   FanEntities = new(); // Fans' entities, in the same order
        public readonly List<BlockRef> Buoyants    = new();
    }
    private readonly Dictionary<ChunkVolume, ShipBlocks> _blocksByVolume = new();
    private static readonly ShipBlocks NoBlocks = new();

    // AllocateThrust scratch, one entry per Fan of the grid being allocated; grown as needed.
    private Vector3[] _fanDirs    = new Vector3[16];
    private Vector3[] _fanOffsets = new Vector3[16];
    private Vector3[] _fanTorques = new Vector3[16];
    private float[]   _fanThrusts = new float[16];

    // Allocation solve: at most this many passes over the Fans, stopping early once a whole pass changes no Fan's
    // thrust by more than the tolerance (force units).
    private const int   AllocationMaxPasses = 50;
    private const float AllocationTolerance = 0.01f;
    private readonly PhysicsWorld    _physics;
    private readonly EntitySet       _players;

    // ── control law tuning ──────────────────────────────────────────────────
    // Self-level (pitch/roll) — always on.
    private float _levelGain = 6f;
    private float _levelDamp = 2f;

    // Velocity targets (piloted only) + tracking gains (always). Gains are acceleration-space (1/s) —
    // the resulting force gets multiplied by the grid's actual mass below, so the resulting acceleration
    // (and therefore stability: a pure-P velocity loop is stable for gain*dt < 2, independent of mass
    // once force is mass-scaled) is the same for a light single block or a heavy ship at the same gain.
    // Un-scaled force previously meant "gain" was really "force," so a value tuned for a heavy ship
    // (e.g. 200) becomes wildly unstable on a lighter one — this is why the ship diverged/kept climbing
    // rather than actually failing to collide with anything.
    private float _forwardSpeedTarget  = 8f;
    private float _rightSpeedTarget    = 8f;
    private float _verticalSpeedTarget = 5f;
    private float _yawRateTarget       = 1.2f; // rad/s

    // Not piloted: the force (N) full thrust asks for, along its axis.
    private float _leverMaxForce = 500f;

    // Not piloted: the torque (N·m) full turn asks for, about world up.
    private float _wheelMaxTorque = 500f;

    // Extra air resistance on rotation: drag torque = this × turn rate², against the spin. With the wheel's full torque T,
    // top turn rate is √(T / this): 1 rad/s at the defaults.
    private float _angularDragCoefficient = 500f;

    private float _forwardGain  = 3f;
    private float _rightGain    = 3f;
    private float _verticalGain = 3f;
    private float _yawGain      = 2f;

    // ── propulsion tuning ───────────────────────────────────────────────────
    private float _fanMaxForce  = 1000f;
    private float _buoyantForce = 25f;

    // When true, every grid gets its control-law force/torque applied directly (no Fan/Buoyant blocks
    // required) — a debug shortcut for testing control feel.
    private bool _freePropulsion;

    /// <summary>Applies the control law's force and torque directly, with no Fan blocks (see the debug panel's checkbox).</summary>
    public bool FreePropulsion { get => _freePropulsion; set => _freePropulsion = value; }

    // Diagnostics — last Update()'s counters, shown in DrawDebugUi to make "is this system even
    // finding/running anything" observable instead of guessed at.
    private int _lastFanCount, _lastBuoyantCount, _lastGridsProcessed, _lastFreePropelled;

    // Diagnostics for the LAST unlocked grid processed each tick (fine for single-ship debugging):
    // requested vertical force vs. what was actually delivered (Buoyant + Fan combined, force units,
    // not impulse) — distinguishes "the allocation math is wrong" (delivered far short of a target this
    // ship's Fans should easily reach) from "this ship's Fans just aren't oriented/powerful enough"
    // (delivered ≈ everything available, still short of desired).
    private float _lastDesiredForceY, _lastDeliveredForceY;

    // What the last allocated grid's Fans couldn't deliver (desired minus delivered): zero while its Fans can meet
    // the demand, so anything else here means Fans at max or none pointing the needed way.
    private Vector3 _lastUnmetForce, _lastUnmetTorque;

    public AirshipFlightSystem(World world, PhysicsWorld physics)
    {
        _grids    = world.GetEntities().With<DynamicGrid>().With<ChunkGrid>().With<PhysicsBodyComponent>().AsSet();
        _fans     = world.GetEntities().With<Fan>().With<BlockRef>().AsSet();
        _buoyants = world.GetEntities().With<Buoyant>().With<BlockRef>().AsSet();
        _physics = physics;
        _players = world.GetEntities().With<LocalPlayer>().With<PlayerInput>().AsSet();
    }

    public void Update(float dt)
    {
        int fanCount = 0, buoyantCount = 0, gridsProcessed = 0, freePropelled = 0;
        GroupBlocksByVolume();

        foreach (ref readonly Entity e in _grids.GetEntities())
        {
            var volume = e.Get<ChunkGrid>().Volume;
            var dynamicGrid = e.Get<DynamicGrid>();
            var blocks = _blocksByVolume.GetValueOrDefault(volume, NoBlocks);
            // Only the owner flies a grid; everyone else follows its body sync (which brings its Fans' thrust too).
            if (e.Has<Entities.NetOwner>() && !e.Get<Entities.NetOwner>().IsLocal) continue;
            foreach (var fan in blocks.FanEntities) fan.Get<Fan>().Thrust = 0f; // off unless allocated thrust below
            // Kinematic (Locked) grids skip gravity/impulses entirely via Bepu's own integrator — nothing
            // to fly. (An empty grid has no body yet, so it isn't in _grids at all.)
            if (dynamicGrid.Locked) continue;
            var body = e.Get<PhysicsBodyComponent>().Body;

            float mass = _physics.GetBodyMass(body);
            if (mass <= 0f) continue; // shouldn't happen for an unlocked body, but guard the degenerate case

            gridsProcessed++;

            // ── control law: this tick's desired force/torque ──────────────────
            bool piloted = e.Has<PilotedComponent>();

            var (pos, rot) = _physics.GetBodyPose(body);
            var linVel = _physics.GetBodyLinearVelocity(body);
            var angVel = _physics.GetBodyAngularVelocity(body);

            var worldUp = Vector3.UnitY;
            var forward = Vector3.Transform(new Vector3(0, 0, -1), rot);
            var right   = Vector3.Transform(new Vector3(1, 0, 0), rot);
            var gridUp  = Vector3.Transform(worldUp, rot);

            // Self-level: torque rotating gridUp toward worldUp, damped by the pitch/roll component of
            // angular velocity only — the component along worldUp (yaw) is left for the yaw term below.
            var tiltTorque = _levelGain * Vector3.Cross(gridUp, worldUp);
            var angVelPitchRoll = angVel - Vector3.Dot(angVel, worldUp) * worldUp;
            tiltTorque -= _levelDamp * angVelPitchRoll;

            // Where the velocity targets come from: the keyboard while piloted, the ship's own controls otherwise.
            var controls = piloted
                ? PilotInput()
                : OwnControls(e.Has<ShipControls>() ? e.Get<ShipControls>() : default);

            float desiredYawRate = controls.W * _yawRateTarget;
            float currentYawRate = Vector3.Dot(angVel, worldUp);
            var yawTorque = _yawGain * (desiredYawRate - currentYawRate) * worldUp;

            float desiredForwardSpeed = controls.X * _forwardSpeedTarget;
            float currentForwardSpeed = Vector3.Dot(linVel, forward);
            var forwardForce = _forwardGain * (desiredForwardSpeed - currentForwardSpeed) * forward;

            float desiredRightSpeed = controls.Y * _rightSpeedTarget;
            float currentRightSpeed = Vector3.Dot(linVel, right);
            var rightForce = _rightGain * (desiredRightSpeed - currentRightSpeed) * right;

            float desiredVerticalSpeed = controls.Z * _verticalSpeedTarget;
            float currentVerticalSpeed = Vector3.Dot(linVel, worldUp);
            // Feedforward: a pure proportional term can never fully cancel a constant disturbance like
            // gravity — it settles at whatever small velocity error happens to produce enough force to
            // balance it, and holds that terminal drift forever instead of reaching true zero. Cancel
            // BOTH known constants directly (acceleration-space, consistent with the P-term above,
            // before the mass multiply below turns the whole sum into a real force): gravity, and this
            // grid's own Buoyant lift (its Buoyant block count × per-block force ÷ mass = its acceleration
            // contribution) — leaving out Buoyant was why the ship started drifting *up* once gravity
            // alone got cancelled. So the P-term only has to correct whatever's left over.
            float buoyantAccel = blocks.Buoyants.Count * _buoyantForce / mass;
            var verticalForce = _verticalGain * (desiredVerticalSpeed - currentVerticalSpeed) * worldUp
                               - _physics.Gravity - buoyantAccel * worldUp;

            // Mass-scaled so a given gain produces the same ACCELERATION regardless of how heavy the
            // grid is (F = m·a) — torque uses the same scalar as an approximation (real rotational
            // inertia is a tensor, not a scalar, but this is close enough for a prototype and keeps
            // yaw/self-level similarly mass-independent in feel).
            // Piloted, yaw tracks a rate target; otherwise the wheel's torque, with no rate tracking (air resistance on
            // rotation sets the top turn rate).
            var desiredTorque = piloted
                ? (tiltTorque + yawTorque) * mass
                : tiltTorque * mass + controls.W * _wheelMaxTorque * worldUp;
            Vector3 desiredForce;
            if (piloted)
            {
                desiredForce = (forwardForce + rightForce + verticalForce) * mass;
            }
            else
            {
                // The levers' force along the ship's own axes, on top of cancelling gravity and Buoyant lift; no
                // speed tracking (air resistance sets the top speed).
                var leverForce = (controls.X * forward + controls.Y * right + controls.Z * gridUp) * _leverMaxForce;
                desiredForce = leverForce - (_physics.Gravity + buoyantAccel * worldUp) * mass;
            }

            // Extra air resistance on rotation, straight onto the body: part of the world, not something the Fans deliver.
            // (AirResistanceSystem applies drag against the ship's movement through the air.)
            float spin = angVel.Length();
            if (spin > 1e-4f)
                _physics.ApplyAngularImpulse(body, -_angularDragCoefficient * spin * angVel * dt);

            // Feedforward, like the Buoyant force above: cancel the torque this grid's Buoyant lift adds about its
            // centre of mass, so the self-level term isn't left fighting it with a steady tilt.
            var com = PhysicsConv.ToBepu(e.Get<PhysicsBodyComponent>().Offset); // the body sits at the centre of mass
            var buoyantLift = Vector3.UnitY * _buoyantForce;
            foreach (var buoyant in blocks.Buoyants)
                desiredTorque -= Vector3.Cross(LocalOffset(buoyant, com, rot), buoyantLift);

            // ── propulsion: realize desiredForce/Torque via Fan/Buoyant blocks ──
            if (_freePropulsion)
            {
                _physics.ApplyLinearImpulse(body, desiredForce * dt);
                _physics.ApplyAngularImpulse(body, desiredTorque * dt);
                freePropelled++;
                // Falls through to the block scan below, which — while free propulsion is on — only
                // still applies real Buoyant lift; Fan blocks are skipped there since desiredForce/
                // Torque was already applied directly above and allocating it across Fans too would
                // double it up.
            }

            // Buoyant lift (always, free propulsion or not), then the Fans' solved thrusts (skipped in
            // free-propulsion mode — desiredForce/Torque was already applied directly above).
            float deliveredForceY = 0f;
            foreach (var buoyant in blocks.Buoyants)
            {
                buoyantCount++;
                _physics.ApplyLinearImpulse(body, buoyantLift * dt, LocalOffset(buoyant, com, rot));
                deliveredForceY += _buoyantForce;
            }

            fanCount += blocks.Fans.Count;
            if (!_freePropulsion && blocks.Fans.Count > 0)
            {
                (_lastUnmetForce, _lastUnmetTorque) = AllocateKeepingTorque(blocks.Fans, com, rot, desiredForce, desiredTorque);
                for (int i = 0; i < blocks.Fans.Count; i++)
                {
                    float thrust = _fanThrusts[i];
                    blocks.FanEntities[i].Get<Fan>().Thrust = thrust / MathF.Max(_fanMaxForce, 1e-3f);
                    if (thrust < 1e-3f) continue;
                    _physics.ApplyLinearImpulse(body, _fanDirs[i] * (thrust * dt), _fanOffsets[i]);
                    deliveredForceY += _fanDirs[i].Y * thrust;
                }
            }

            _lastDesiredForceY   = desiredForce.Y;
            _lastDeliveredForceY = deliveredForceY;
        }

        _lastFanCount        = fanCount;
        _lastBuoyantCount    = buoyantCount;
        _lastGridsProcessed  = gridsProcessed;
        _lastFreePropelled   = freePropelled;
    }

    /// <summary>A ship's own controls, as fractions (-1 to 1) of the full force and torque: forward, right and vertical
    /// from its thrust settings, and yaw (anticlockwise from above, like the pilot's keys) from its turn, clockwise to
    /// starboard. Each is squared, keeping its sign, so it ramps up: half-way asks for a quarter.</summary>
    private static Vector4 OwnControls(in ShipControls c) =>
        new(Ramp(c.Forward), Ramp(c.Right), Ramp(c.Up), -Ramp(c.Turn));

    private static float Ramp(float setting)
    {
        setting = System.Math.Clamp(setting, -1f, 1f);
        return setting * MathF.Abs(setting);
    }

    /// <summary>The pilot's keys, as (forward, right, up, yaw left): from the local player's input this tick.</summary>
    private Vector4 PilotInput()
    {
        foreach (ref readonly Entity p in _players.GetEntities())
        {
            ref readonly var input = ref p.Get<PlayerInput>();
            return new Vector4(input.Axis(PlayerButtons.Forward, PlayerButtons.Back),
                               input.Axis(PlayerButtons.Right, PlayerButtons.Left),
                               input.Axis(PlayerButtons.Up, PlayerButtons.Down),
                               input.Axis(PlayerButtons.Previous, PlayerButtons.Next));
        }
        return Vector4.Zero;
    }

    /// <summary>Rebuilds <see cref="_blocksByVolume"/> from this tick's Fan and Buoyant entities. Lists are reused
    /// across ticks; a volume left with neither is dropped so a despawned ship's lists don't linger.</summary>
    private void GroupBlocksByVolume()
    {
        foreach (var blocks in _blocksByVolume.Values)
        {
            blocks.Fans.Clear();
            blocks.FanEntities.Clear();
            blocks.Buoyants.Clear();
        }

        foreach (ref readonly Entity e in _fans.GetEntities())
        {
            ref readonly var block = ref e.Get<BlockRef>();
            var blocks = BlocksOf(block.Volume);
            blocks.Fans.Add(block);
            blocks.FanEntities.Add(e);
        }
        foreach (ref readonly Entity e in _buoyants.GetEntities())
        {
            ref readonly var block = ref e.Get<BlockRef>();
            BlocksOf(block.Volume).Buoyants.Add(block);
        }

        foreach (var (volume, blocks) in _blocksByVolume)
            if (blocks.Fans.Count == 0 && blocks.Buoyants.Count == 0) _blocksByVolume.Remove(volume);
    }

    private ShipBlocks BlocksOf(ChunkVolume volume)
    {
        if (!_blocksByVolume.TryGetValue(volume, out var blocks))
            _blocksByVolume[volume] = blocks = new ShipBlocks();
        return blocks;
    }

    // World-space offset from centre of mass for a block's cell centre — the same rigid transform
    // ChunkVolume.VoxelToWorld maps voxels with: world = bodyPos + R·(localCentre - centreOfMass).
    private static Vector3 LocalOffset(in BlockRef block, Vector3 com, Quaternion rot)
    {
        var p = block.Position;
        var localCentre = new Vector3(p.X + 0.5f, p.Y + 0.5f, p.Z + 0.5f);
        return Vector3.Transform(localCentre - com, rot);
    }

    // A Fan's thrust FORCE ON THE SHIP is opposite where its top points — the top is the exhaust/visual
    // direction (also which face gets the glowing Top texture),
    // and the reaction (Newton's third law) pushes the ship the other way, like a rocket nozzle: exhaust
    // down, ship goes up. Applying force *along* the top instead of against it was a sign bug present
    // since Fan thrust was first implemented — a Fan facing down (intended as a lift thruster, exhausting
    // downward) was actually pushing the ship further down, fighting the very lift it was built to
    // provide. Free propulsion mode never touches orientation at all (it applies the control law's force
    // directly), which is why it tested fine while Fan-block-allocated thrust didn't.
    private static Vector3 ThrustDirection(in BlockRef fan, Quaternion rot)
    {
        var up = fan.Orientation.Up.ToVector();
        var exhaustDir = Vector3.Transform(new Vector3(up.X, up.Y, up.Z), rot);
        return -exhaustDir;
    }

    /// <summary>
    /// <see cref="AllocateThrust"/>, but putting torque first: when the Fans can't deliver the whole force and torque
    /// (typically at their limits), it asks for as large a fraction of <paramref name="force"/> as still leaves the
    /// torque met as well as it can be with no force asked for at all. Plain least squares would trade some torque for
    /// more force, and the lost torque is the self-levelling and yaw control: asking an off-balance set of Fans for
    /// more lift than they have tips the ship over (nose up and over backwards) instead of just lifting less. The
    /// fraction is found by bisection, re-solving each time; the last solve is the one left in the scratch arrays.
    /// </summary>
    private (Vector3 UnmetForce, Vector3 UnmetTorque) AllocateKeepingTorque(
        List<BlockRef> fans, Vector3 com, Quaternion rot, Vector3 force, Vector3 torque)
    {
        var full = AllocateThrust(fans, com, rot, force, torque);
        float torqueTolerance = TorqueTolerance + 0.01f * torque.Length();
        if (full.UnmetTorque.Length() <= torqueTolerance) return full;

        // The torque that can't be met whatever force is asked for; allow that much, plus the tolerance.
        float floor = AllocateThrust(fans, com, rot, Vector3.Zero, torque).UnmetTorque.Length() + torqueTolerance;
        if (full.UnmetTorque.Length() <= floor) return AllocateThrust(fans, com, rot, force, torque);

        float lo = 0f, hi = 1f; // lo: a fraction known to keep the torque; hi: one known not to
        for (int i = 0; i < TorqueBisections; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (AllocateThrust(fans, com, rot, force * mid, torque).UnmetTorque.Length() <= floor) lo = mid;
            else hi = mid;
        }
        var kept = AllocateThrust(fans, com, rot, force * lo, torque);
        return (kept.UnmetForce + force * (1f - lo), kept.UnmetTorque);
    }

    // AllocateKeepingTorque: torque misses below this (plus 1% of the asked-for torque) count as met; and how many times
    // it halves the range of force fractions it searches.
    private const float TorqueTolerance  = 0.05f;
    private const int   TorqueBisections = 8;

    /// <summary>
    /// Solves for each of <paramref name="fans"/>' thrusts, into <see cref="_fanThrusts"/> (with each Fan's world
    /// thrust direction and offset from the centre of mass in <see cref="_fanDirs"/> / <see cref="_fanOffsets"/>):
    /// thrusts between 0 and <see cref="_fanMaxForce"/> whose combined force and torque about the centre of mass come
    /// as close as possible to <paramref name="force"/> and <paramref name="torque"/>. Returns what they fall short by.
    ///
    /// Box-constrained least squares, by coordinate descent: each step sets one Fan's thrust to whatever best reduces
    /// the remaining force/torque error, clamped to its limits, and passes repeat until nothing changes. Converges to
    /// the exact answer when one exists. Torque is weighted by 1/L, L the Fans' RMS distance from the centre of mass,
    /// so a unit of thrust counts about the same in the force and torque terms; unweighted, the large lever arms of a
    /// big ship make torque dominate every step and the force error shrinks so slowly that a pass limit stops it
    /// short (a steady sink or climb).
    /// </summary>
    private (Vector3 UnmetForce, Vector3 UnmetTorque) AllocateThrust(
        List<BlockRef> fans, Vector3 com, Quaternion rot, Vector3 force, Vector3 torque)
    {
        int n = fans.Count;
        if (_fanThrusts.Length < n)
        {
            int size = System.Math.Max(n, _fanThrusts.Length * 2);
            _fanDirs = new Vector3[size]; _fanOffsets = new Vector3[size]; _fanTorques = new Vector3[size];
            _fanThrusts = new float[size];
        }

        float sumSq = 0f;
        for (int i = 0; i < n; i++)
        {
            _fanDirs[i]    = ThrustDirection(fans[i], rot);
            _fanOffsets[i] = LocalOffset(fans[i], com, rot);
            sumSq += _fanOffsets[i].LengthSquared();
        }
        float torqueWeight = 1f / MathF.Max(MathF.Sqrt(sumSq / n), 0.5f);

        // Weighted torque per unit thrust, and the remaining (unmet) force and weighted torque.
        for (int i = 0; i < n; i++)
        {
            _fanTorques[i] = Vector3.Cross(_fanOffsets[i], _fanDirs[i]) * torqueWeight;
            _fanThrusts[i] = 0f;
        }
        var unmetForce  = force;
        var unmetTorque = torque * torqueWeight;

        for (int pass = 0; pass < AllocationMaxPasses; pass++)
        {
            float largestChange = 0f;
            for (int i = 0; i < n; i++)
            {
                var d = _fanDirs[i];
                var t = _fanTorques[i];
                float step = (Vector3.Dot(d, unmetForce) + Vector3.Dot(t, unmetTorque)) / (d.LengthSquared() + t.LengthSquared());
                float thrust = System.Math.Clamp(_fanThrusts[i] + step, 0f, _fanMaxForce);
                float change = thrust - _fanThrusts[i];
                if (change == 0f) continue;

                _fanThrusts[i] = thrust;
                unmetForce  -= d * change;
                unmetTorque -= t * change;
                largestChange = MathF.Max(largestChange, MathF.Abs(change));
            }
            if (largestChange < AllocationTolerance) break;
        }
        return (unmetForce, unmetTorque / torqueWeight);
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    // Drawn as a section inside AirshipDebugPanel's combined "Airship" window, not its own panel.
    public void DrawDebugUi()
    {
        ImGui.Text("Self-level (always on)");
        ImGui.SliderFloat("Level gain", ref _levelGain, 0f, 20f);
        ImGui.SliderFloat("Level damping", ref _levelDamp, 0f, 10f);
        ImGui.Separator();
        ImGui.Text("Ship's controls (not piloted)");
        ImGui.SliderFloat("Full lever force (N)", ref _leverMaxForce, 0f, 20000f);
        ImGui.TextDisabled("Air resistance: see the Air resistance and Wind panels");
        ImGui.SliderFloat("Full wheel torque (N·m)", ref _wheelMaxTorque, 0f, 20000f);
        ImGui.SliderFloat("Extra rotation air resistance", ref _angularDragCoefficient, 0f, 5000f);
        ImGui.Text($"Top turn rate at full wheel: {MathF.Sqrt(_wheelMaxTorque / MathF.Max(_angularDragCoefficient, 1e-4f)):0.00} rad/s");
        ImGui.Separator();
        ImGui.Text("Top speeds (keyboard while piloted)");
        ImGui.SliderFloat("Forward speed", ref _forwardSpeedTarget, 0f, 30f);
        ImGui.SliderFloat("Right speed", ref _rightSpeedTarget, 0f, 30f);
        ImGui.SliderFloat("Vertical speed", ref _verticalSpeedTarget, 0f, 30f);
        ImGui.SliderFloat("Yaw rate", ref _yawRateTarget, 0f, 5f);
        ImGui.Separator();
        ImGui.Text("Tracking gains");
        ImGui.SliderFloat("Forward gain", ref _forwardGain, 0f, 20f);
        ImGui.SliderFloat("Right gain", ref _rightGain, 0f, 20f);
        ImGui.SliderFloat("Vertical gain", ref _verticalGain, 0f, 20f);
        ImGui.SliderFloat("Yaw gain", ref _yawGain, 0f, 20f);
        ImGui.Separator();
        ImGui.Checkbox("Free propulsion (no blocks needed)", ref _freePropulsion);
        ImGui.BeginDisabled(_freePropulsion);
        ImGui.SliderFloat("Fan max thrust", ref _fanMaxForce, 0f, 20000f);
        ImGui.SliderFloat("Buoyant lift", ref _buoyantForce, 0f, 20000f);
        ImGui.EndDisabled();
        ImGui.Separator();
        ImGui.Text($"Last tick: {_lastGridsProcessed} unlocked grid(s) processed");
        ImGui.Text($"Free-propelled: {_lastFreePropelled}");
        ImGui.Text($"Fan blocks seen: {_lastFanCount}   Buoyant blocks seen: {_lastBuoyantCount}");
        ImGui.Text($"Vertical (last grid): desired={_lastDesiredForceY:0.0}  delivered={_lastDeliveredForceY:0.0}");
        ImGui.Text($"Unmet by Fans (last grid): force={_lastUnmetForce.Length():0.0}  torque={_lastUnmetTorque.Length():0.0}");
    }
}
