using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Input;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Full airship flight pipeline (Milestone 5 Phases 5.3/5.4), per dynamic grid each tick: first a
/// desired-velocity control law (self-leveling always; forward/right/vertical/yaw velocity targets from
/// player input when the grid carries <see cref="PilotedComponent"/> (set by <see cref="GridPilotSystem"/>
/// while it's piloting that grid), zero otherwise — the grid just holds position/heading), then
/// immediately realizing that desired force/torque by
/// distributing it across the grid's Fan/Buoyant blocks (or applying it directly in "free propulsion"
/// debug mode). Previously two systems (a control system computing desired force/torque into a
/// <c>DynamicGrid</c> field, and a propulsion system consuming it next) — merged into one because nothing
/// outside this pipeline ever reads the value in between, and having two separate systems for "decide
/// what the ship should do" vs. "make it happen" was easy to mix up despite being two halves of the same
/// per-tick computation. Runs before <see cref="PhysicsWorld"/> steps, so impulses are integrated the
/// same tick they're computed.
///
/// Propulsion allocation is a two-pass proportional scheme, not an exact solver: Pass 1 sums each Fan's
/// *positive* alignment with the desired force and with the desired torque separately (a Fan facing the
/// wrong way contributes 0, not a negative share). Pass 2 gives each Fan exactly its proportional share
/// of that total — <c>(this fan's alignment / total positive alignment) × desired magnitude</c> — so the
/// fleet's total output reconstructs the desired force/torque once, not once per fan. This fixes two
/// failure modes a naive per-fan dot product had: (1) N similarly-facing Fans each independently claiming
/// their full raw share delivered N× the request — fine for error-driven terms (the error just shrinks
/// faster) but a permanent bias for a *constant* feedforward term (gravity/Buoyant cancellation) that
/// never shrinks to correct for it; (2) dividing by *total* Fan count regardless of orientation starved a
/// ship with Fans in several different directions (e.g. some for lift, some for forward thrust) — a
/// handful of correctly-aligned lift Fans got diluted by every unrelated forward-facing Fan on the same
/// ship, so a 12-Fan ship with only 4 Fans actually oriented for lift only ever got ~1/12th of what those
/// 4 could truly deliver. Scores against the *unnormalized* force/torque vectors, not a single blended
/// direction — collapsing e.g. "forward + hold-altitude" into one normalized direction let a large
/// forward demand drown out a much smaller vertical one. Buoyant blocks apply constant passive lift,
/// unaffected by any of this.
///
/// Scans every voxel of every loaded chunk *twice* each tick (once per allocation pass) to find
/// Fan/Buoyant blocks — a naive O(voxels) cost accepted for this prototype milestone (small ships),
/// matching this project's "naive-correct first" pattern elsewhere (e.g. the GPU lighting flood).
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
    private readonly PhysicsWorld    _physics;
    private readonly InputManager    _input;

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

    private float _forwardGain  = 3f;
    private float _rightGain    = 3f;
    private float _verticalGain = 3f;
    private float _yawGain      = 2f;

    // ── propulsion tuning ───────────────────────────────────────────────────
    private float _fanMaxForce  = 100f;
    private float _buoyantForce = 25f;

    // When true, every grid gets its control-law force/torque applied directly (no Fan/Buoyant blocks
    // required) — a debug shortcut for testing control feel.
    private bool _freePropulsion;

    // Diagnostics — last Update()'s counters, shown in DrawDebugUi to make "is this system even
    // finding/running anything" observable instead of guessed at.
    private int _lastFanCount, _lastBuoyantCount, _lastGridsProcessed, _lastFreePropelled;

    // Diagnostics for the LAST unlocked grid processed each tick (fine for single-ship debugging):
    // requested vertical force vs. what was actually delivered (Buoyant + Fan combined, force units,
    // not impulse) — distinguishes "the allocation math is wrong" (delivered far short of a target this
    // ship's Fans should easily reach) from "this ship's Fans just aren't oriented/powerful enough"
    // (delivered ≈ everything available, still short of desired).
    private float _lastDesiredForceY, _lastDeliveredForceY;

    public AirshipFlightSystem(World world, PhysicsWorld physics, InputManager input)
    {
        _grids   = world.GetEntities().With<DynamicGridComponent>().AsSet();
        _physics = physics;
        _input   = input;
    }

    public void Update(float dt)
    {
        int fanCount = 0, buoyantCount = 0, gridsProcessed = 0, freePropelled = 0;

        foreach (ref readonly Entity e in _grids.GetEntities())
        {
            var grid = e.Get<DynamicGridComponent>().Grid;
            // Kinematic (Locked) grids skip gravity/impulses entirely via Bepu's own integrator, and an
            // empty grid has no body to steer — nothing to fly in either case.
            if (!grid.BodyCreated || grid.Locked) continue;

            float mass = _physics.GetBodyMass(grid.Body);
            if (mass <= 0f) continue; // shouldn't happen for an unlocked body, but guard the degenerate case

            gridsProcessed++;

            // ── control law: this tick's desired force/torque ──────────────────
            bool piloted = e.Has<PilotedComponent>();

            var (pos, rot) = _physics.GetBodyPose(grid.Body);
            var linVel = _physics.GetBodyLinearVelocity(grid.Body);
            var angVel = _physics.GetBodyAngularVelocity(grid.Body);

            var worldUp = Vector3.UnitY;
            var forward = Vector3.Transform(new Vector3(0, 0, -1), rot);
            var right   = Vector3.Transform(new Vector3(1, 0, 0), rot);
            var gridUp  = Vector3.Transform(worldUp, rot);

            // Self-level: torque rotating gridUp toward worldUp, damped by the pitch/roll component of
            // angular velocity only — the component along worldUp (yaw) is left for the yaw term below.
            var tiltTorque = _levelGain * Vector3.Cross(gridUp, worldUp);
            var angVelPitchRoll = angVel - Vector3.Dot(angVel, worldUp) * worldUp;
            tiltTorque -= _levelDamp * angVelPitchRoll;

            float desiredYawRate = piloted ? YawInput() * _yawRateTarget : 0f;
            float currentYawRate = Vector3.Dot(angVel, worldUp);
            var yawTorque = _yawGain * (desiredYawRate - currentYawRate) * worldUp;

            float desiredForwardSpeed = piloted ? ForwardInput() * _forwardSpeedTarget : 0f;
            float currentForwardSpeed = Vector3.Dot(linVel, forward);
            var forwardForce = _forwardGain * (desiredForwardSpeed - currentForwardSpeed) * forward;

            float desiredRightSpeed = piloted ? RightInput() * _rightSpeedTarget : 0f;
            float currentRightSpeed = Vector3.Dot(linVel, right);
            var rightForce = _rightGain * (desiredRightSpeed - currentRightSpeed) * right;

            float desiredVerticalSpeed = piloted ? VerticalInput() * _verticalSpeedTarget : 0f;
            float currentVerticalSpeed = Vector3.Dot(linVel, worldUp);
            // Feedforward: a pure proportional term can never fully cancel a constant disturbance like
            // gravity — it settles at whatever small velocity error happens to produce enough force to
            // balance it, and holds that terminal drift forever instead of reaching true zero. Cancel
            // BOTH known constants directly (acceleration-space, consistent with the P-term above,
            // before the mass multiply below turns the whole sum into a real force): gravity, and this
            // grid's own Buoyant lift (BuoyantBlockCount × per-block force ÷ mass = its acceleration
            // contribution) — leaving out Buoyant was why the ship started drifting *up* once gravity
            // alone got cancelled. So the P-term only has to correct whatever's left over.
            float buoyantAccel = grid.BuoyantBlockCount * _buoyantForce / mass;
            var verticalForce = _verticalGain * (desiredVerticalSpeed - currentVerticalSpeed) * worldUp
                               - _physics.Gravity - buoyantAccel * worldUp;

            // Mass-scaled so a given gain produces the same ACCELERATION regardless of how heavy the
            // grid is (F = m·a) — torque uses the same scalar as an approximation (real rotational
            // inertia is a tensor, not a scalar, but this is close enough for a prototype and keeps
            // yaw/self-level similarly mass-independent in feel).
            var desiredForce  = (forwardForce + rightForce + verticalForce) * mass;
            var desiredTorque = (tiltTorque + yawTorque) * mass;

            // ── propulsion: realize desiredForce/Torque via Fan/Buoyant blocks ──
            if (_freePropulsion)
            {
                _physics.ApplyLinearImpulse(grid.Body, desiredForce * dt);
                _physics.ApplyAngularImpulse(grid.Body, desiredTorque * dt);
                freePropelled++;
                // Falls through to the block scan below, which — while free propulsion is on — only
                // still applies real Buoyant lift; Fan blocks are skipped there since desiredForce/
                // Torque was already applied directly above and allocating it across Fans too would
                // double it up.
            }

            var com = grid.CenterOfMass;
            float desiredForceMag  = desiredForce.Length();
            float desiredTorqueMag = desiredTorque.Length();

            // Pass 1: total positive alignment "capacity" across all Fans, force and torque tracked
            // separately. Skipped in free-propulsion mode — nothing reads it there.
            float totalForceAlign = 0f, totalTorqueAlign = 0f;
            if (!_freePropulsion)
            {
                foreach (var (chunkPos, entry) in grid.All)
                {
                    if (!entry.Data.HasAnySolid()) continue;
                    var silkOrigin = chunkPos.WorldOrigin;
                    var chunkOrigin = new Vector3(silkOrigin.X, silkOrigin.Y, silkOrigin.Z);

                    for (int lz = 0; lz < ChunkData.Size; lz++)
                    for (int ly = 0; ly < ChunkData.Size; ly++)
                    for (int lx = 0; lx < ChunkData.Size; lx++)
                    {
                        if (entry.Data.Get(lx, ly, lz) != BlockId.Fan) continue;

                        var (forceAlign, torqueAlign) = FanAlignment(
                            entry, lx, ly, lz, chunkOrigin, com, rot, desiredForce, desiredTorque);
                        totalForceAlign  += MathF.Max(0f, forceAlign);
                        totalTorqueAlign += MathF.Max(0f, torqueAlign);
                    }
                }
            }

            // Pass 2: apply Buoyant lift (always) and each Fan's proportional share (skipped in
            // free-propulsion mode — desiredForce/Torque was already applied directly above).
            float deliveredForceY = 0f;
            foreach (var (chunkPos, entry) in grid.All)
            {
                if (!entry.Data.HasAnySolid()) continue;
                var silkOrigin = chunkPos.WorldOrigin;
                var chunkOrigin = new Vector3(silkOrigin.X, silkOrigin.Y, silkOrigin.Z);

                for (int lz = 0; lz < ChunkData.Size; lz++)
                for (int ly = 0; ly < ChunkData.Size; ly++)
                for (int lx = 0; lx < ChunkData.Size; lx++)
                {
                    var id = entry.Data.Get(lx, ly, lz);
                    if (id != BlockId.Fan && id != BlockId.Buoyant) continue;

                    if (id == BlockId.Buoyant)
                    {
                        buoyantCount++;
                        var worldOffset = LocalOffset(lx, ly, lz, chunkOrigin, com, rot);
                        _physics.ApplyLinearImpulse(grid.Body, Vector3.UnitY * (_buoyantForce * dt), worldOffset);
                        deliveredForceY += _buoyantForce;
                        continue;
                    }

                    fanCount++;
                    if (_freePropulsion) continue;

                    var (forceAlign, torqueAlign) = FanAlignment(
                        entry, lx, ly, lz, chunkOrigin, com, rot, desiredForce, desiredTorque);

                    float forceShare  = totalForceAlign  > 1e-3f ? MathF.Max(0f, forceAlign)  / totalForceAlign  : 0f;
                    float torqueShare = totalTorqueAlign > 1e-3f ? MathF.Max(0f, torqueAlign) / totalTorqueAlign : 0f;

                    float thrust = System.Math.Clamp(
                        forceShare * desiredForceMag + torqueShare * desiredTorqueMag, -_fanMaxForce, _fanMaxForce);
                    if (MathF.Abs(thrust) < 1e-3f) continue;

                    var thrustDir = ThrustDirection(entry, lx, ly, lz, rot);
                    var fanOffset = LocalOffset(lx, ly, lz, chunkOrigin, com, rot);
                    _physics.ApplyLinearImpulse(grid.Body, thrustDir * (thrust * dt), fanOffset);
                    deliveredForceY += thrustDir.Y * thrust;
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

    private float ForwardInput()
    {
        float v = 0f;
        if (_input.IsKeyDown(Key.W)) v += 1f;
        if (_input.IsKeyDown(Key.S)) v -= 1f;
        return v;
    }

    private float RightInput()
    {
        float v = 0f;
        if (_input.IsKeyDown(Key.D)) v += 1f;
        if (_input.IsKeyDown(Key.A)) v -= 1f;
        return v;
    }

    private float VerticalInput()
    {
        float v = 0f;
        if (_input.IsKeyDown(Key.Space)) v += 1f;
        if (_input.IsKeyDown(Key.ShiftLeft) || _input.IsKeyDown(Key.ShiftRight)) v -= 1f;
        return v;
    }

    private float YawInput()
    {
        float v = 0f;
        if (_input.IsKeyDown(Key.Q)) v += 1f;
        if (_input.IsKeyDown(Key.E)) v -= 1f;
        return v;
    }

    // World-space offset from centre of mass for a chunk-local voxel — the same rigid transform
    // GridTransformSystem uses for chunk meshes: world = bodyPos + R·(localCentre - centreOfMass).
    private static Vector3 LocalOffset(int lx, int ly, int lz, Vector3 chunkOrigin, Vector3 com, Quaternion rot)
    {
        var localCentre = new Vector3(chunkOrigin.X + lx + 0.5f, chunkOrigin.Y + ly + 0.5f, chunkOrigin.Z + lz + 0.5f);
        return Vector3.Transform(localCentre - com, rot);
    }

    // A Fan's thrust FORCE ON THE SHIP is opposite its stored Facing — Facing is the exhaust/visual
    // direction (also which face gets the glowing Top texture, since Fan's Top is oriented to Facing),
    // and the reaction (Newton's third law) pushes the ship the other way, like a rocket nozzle: exhaust
    // down, ship goes up. Applying force *along* Facing instead of against it was a sign bug present
    // since Fan thrust was first implemented — a Fan facing down (intended as a lift thruster, exhausting
    // downward) was actually pushing the ship further down, fighting the very lift it was built to
    // provide. Free propulsion mode never touches Facing at all (it applies the control law's force
    // directly), which is why it tested fine while Fan-block-allocated thrust didn't.
    private static Vector3 ThrustDirection(ChunkEntry entry, int lx, int ly, int lz, Quaternion rot)
    {
        var facingVec = entry.Data.GetFacing(lx, ly, lz).ToVector();
        var exhaustDir = Vector3.Transform(new Vector3(facingVec.X, facingVec.Y, facingVec.Z), rot);
        return -exhaustDir;
    }

    // A Fan voxel's alignment with the desired force (dot of its thrust direction with it) and with the
    // desired torque (dot of its lever-arm cross product with it, normalized by lever length).
    private static (float forceAlign, float torqueAlign) FanAlignment(
        ChunkEntry entry, int lx, int ly, int lz, Vector3 chunkOrigin, Vector3 com, Quaternion rot,
        Vector3 desiredForce, Vector3 desiredTorque)
    {
        var worldOffset = LocalOffset(lx, ly, lz, chunkOrigin, com, rot);
        var thrustDir = ThrustDirection(entry, lx, ly, lz, rot);

        float leverLength = MathF.Max(worldOffset.Length(), 0.5f);
        float forceAlign  = Vector3.Dot(thrustDir, desiredForce);
        float torqueAlign = Vector3.Dot(Vector3.Cross(worldOffset, thrustDir) / leverLength, desiredTorque);
        return (forceAlign, torqueAlign);
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    // Drawn as a section inside AirshipDebugPanel's combined "Airship" window, not its own panel.
    public void DrawDebugUi()
    {
        ImGui.Text("Self-level (always on)");
        ImGui.SliderFloat("Level gain", ref _levelGain, 0f, 20f);
        ImGui.SliderFloat("Level damping", ref _levelDamp, 0f, 10f);
        ImGui.Separator();
        ImGui.Text("Velocity targets (while piloted)");
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
    }
}
