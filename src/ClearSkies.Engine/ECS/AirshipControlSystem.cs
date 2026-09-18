using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Physics;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Input;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Desired-velocity control law for every dynamic grid (Milestone 5 Phase 5.4), run before
/// <see cref="AirshipPropulsionSystem"/> each tick. Self-leveling (pitch/roll) always runs,
/// independent of piloting. Forward/right/vertical/yaw velocity targets come from player input when the
/// grid is the one <see cref="GridPilotSystem"/> is currently piloting (<c>W</c>/<c>S</c> forward/back,
/// <c>A</c>/<c>D</c> left/right, <c>Space</c>/<c>Shift</c> up/down, <c>Q</c>/<c>E</c> yaw), and are zero otherwise — the
/// grid just holds position/heading. The result is written to <see cref="DynamicGrid.DesiredForce"/> /
/// <see cref="DynamicGrid.DesiredTorque"/> (world space, transient — recomputed every tick, not
/// persisted) for <see cref="AirshipPropulsionSystem"/> to allocate across the grid's Fan blocks.
/// </summary>
public sealed class AirshipControlSystem : ISystem, IDebugUiSystem
{
    private readonly EntitySet       _grids;
    private readonly PhysicsWorld    _physics;
    private readonly InputManager    _input;
    private readonly GridPilotSystem _pilot;
    private readonly AirshipPropulsionSystem _propulsion;

    // Self-level (pitch/roll) — always on.
    private float _levelGain = 6f;
    private float _levelDamp = 2f;

    // Velocity targets (piloted only) + tracking gains (always). Gains are now acceleration-space
    // (1/s) — DesiredForce/Torque get multiplied by the grid's actual mass below, so the resulting
    // acceleration (and therefore stability: a pure-P velocity loop is stable for gain*dt < 2,
    // independent of mass once force is mass-scaled) is the same for a light single block or a heavy
    // ship at the same gain. Un-scaled force previously meant "gain" was really "force," so a value
    // tuned for a heavy ship (e.g. 200) becomes wildly unstable on a lighter one — this is why the
    // ship diverged/kept climbing rather than actually failing to collide with anything.
    private float _forwardSpeedTarget  = 8f;
    private float _rightSpeedTarget    = 8f;
    private float _verticalSpeedTarget = 5f;
    private float _yawRateTarget       = 1.2f; // rad/s

    private float _forwardGain  = 3f;
    private float _rightGain    = 3f;
    private float _verticalGain = 3f;
    private float _yawGain      = 2f;

    public AirshipControlSystem(World world, PhysicsWorld physics, InputManager input, GridPilotSystem pilot,
                                 AirshipPropulsionSystem propulsion)
    {
        _grids      = world.GetEntities().With<DynamicGridComponent>().AsSet();
        _physics    = physics;
        _input      = input;
        _pilot      = pilot;
        _propulsion = propulsion;
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _grids.GetEntities())
        {
            var grid = e.Get<DynamicGridComponent>().Grid;
            if (!grid.BodyCreated)
            {
                grid.DesiredForce  = default;
                grid.DesiredTorque = default;
                continue;
            }

            if (grid.Locked)
            {
                // Kinematic (see DynamicGrid.Locked) — Bepu's own integrator already skips gravity for
                // it entirely, nothing to do here beyond leaving its desired force/torque at zero.
                grid.DesiredForce  = default;
                grid.DesiredTorque = default;
                continue;
            }

            float mass = _physics.GetBodyMass(grid.Body);
            if (mass <= 0f) continue; // shouldn't happen for an unlocked body, but guard the degenerate case

            bool piloted = _pilot.IsPiloting(e);

            var (_, rot) = _physics.GetBodyPose(grid.Body);
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
            float buoyantAccel = grid.BuoyantBlockCount * _propulsion.BuoyantForcePerBlock / mass;
            var verticalForce = _verticalGain * (desiredVerticalSpeed - currentVerticalSpeed) * worldUp
                               - _physics.Gravity - buoyantAccel * worldUp;

            // Mass-scaled so a given gain produces the same ACCELERATION regardless of how heavy the
            // grid is (F = m·a) — torque uses the same scalar as an approximation (real rotational
            // inertia is a tensor, not a scalar, but this is close enough for a prototype and keeps
            // yaw/self-level similarly mass-independent in feel).
            grid.DesiredForce  = (forwardForce + rightForce + verticalForce) * mass;
            grid.DesiredTorque = (tiltTorque + yawTorque) * mass;
        }
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

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Airship Control";

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
    }
}
