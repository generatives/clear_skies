using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Applies the desired force/torque <see cref="AirshipControlSystem"/> computed this tick (Milestone 5
/// Phase 5.3/5.4) by distributing it across a grid's Fan blocks — a two-pass proportional allocation,
/// not an exact solver: sufficient for a closed feedback loop that corrects itself every tick.
///
/// Pass 1 sums each Fan's *positive* alignment with the desired force and with the desired torque
/// separately (a Fan facing the wrong way contributes 0, not a negative share). Pass 2 gives each Fan
/// exactly its proportional share of that total — <c>(this fan's alignment / total positive
/// alignment) × desired magnitude</c> — so the fleet's total output reconstructs the desired
/// force/torque once, not once per fan. This fixes two failure modes a naive per-fan dot product had:
/// (1) N similarly-facing Fans each independently claiming their full raw share delivered N× the
/// request — fine for error-driven terms (the error just shrinks faster) but a permanent bias for a
/// *constant* feedforward term (gravity/Buoyant cancellation) that never shrinks to correct for it;
/// (2) dividing by *total* Fan count regardless of orientation starved a ship with Fans in several
/// different directions (e.g. some for lift, some for forward thrust) — a handful of correctly-aligned
/// lift Fans got diluted by every unrelated forward-facing Fan on the same ship, so a 12-Fan ship with
/// only 4 Fans actually oriented for lift only ever got ~1/12th of what those 4 could truly deliver.
///
/// Deliberately scores against the *unnormalized* force/torque vectors, not a single blended direction —
/// collapsing e.g. "forward + hold-altitude" into one normalized direction let a large forward demand
/// drown out a much smaller vertical one. Buoyant blocks apply constant passive lift, unaffected by any
/// of this. Runs after <see cref="AirshipControlSystem"/> and before <see cref="PhysicsWorld"/> steps,
/// so impulses are integrated the same tick they're computed.
///
/// Scans every voxel of every loaded chunk *twice* each tick (once per pass) to find Fan/Buoyant blocks
/// — a naive O(voxels) cost accepted for this prototype milestone (small ships), matching this
/// project's "naive-correct first" pattern elsewhere (e.g. the GPU lighting flood).
///
/// Debug "free propulsion" mode (the "Free propulsion" checkbox in <see cref="DrawDebugUi"/>) applies
/// every grid's desired force/torque directly at its centre of mass instead of allocating it across
/// Fan blocks, so a ship can be flown/stabilized for testing without needing any Fan blocks — but
/// Buoyant blocks still apply their own passive lift either way (it's a separate, always-on mechanic,
/// not part of the Fan allocation this mode is replacing).
/// </summary>
public sealed class AirshipPropulsionSystem : ISystem, IDebugUiSystem
{
    private readonly EntitySet    _grids;
    private readonly PhysicsWorld _physics;

    private float _fanMaxForce  = 100f;
    private float _buoyantForce = 25f;

    /// <summary>Current per-block Buoyant lift force — read by AirshipControlSystem to feedforward-
    /// cancel total Buoyant lift (BuoyantBlockCount × this) alongside gravity in the vertical hold.</summary>
    public float BuoyantForcePerBlock => _buoyantForce;

    // When true, every grid gets its AirshipControlSystem-computed force/torque applied directly (no
    // Fan/Buoyant blocks required) — a debug shortcut for testing control feel.
    private bool _freePropulsion;

    // Diagnostics — last Update()'s counters, shown in DrawDebugUi to make "is this system even
    // finding/running anything" observable instead of guessed at.
    private int _lastFanCount, _lastBuoyantCount, _lastGridsProcessed, _lastFreePropelled;

    // Diagnostics for the LAST unlocked grid processed each tick (fine for single-ship debugging):
    // requested vertical force vs. what was actually delivered (Buoyant + Fan combined, force units,
    // not impulse) — distinguishes "the allocation math is wrong" (delivered far short of a target
    // this ship's Fans should easily reach) from "this ship's Fans just aren't oriented/powerful
    // enough" (delivered ≈ everything available, still short of desired).
    private float _lastDesiredForceY, _lastDeliveredForceY;

    public AirshipPropulsionSystem(World world, PhysicsWorld physics)
    {
        _grids   = world.GetEntities().With<DynamicGridComponent>().AsSet();
        _physics = physics;
    }

    public void Update(float dt)
    {
        int fanCount = 0, buoyantCount = 0, gridsProcessed = 0, freePropelled = 0;

        foreach (ref readonly Entity e in _grids.GetEntities())
        {
            var grid = e.Get<DynamicGridComponent>().Grid;
            if (!grid.BodyCreated || grid.Locked) continue;
            gridsProcessed++;

            if (_freePropulsion)
            {
                _physics.ApplyLinearImpulse(grid.Body, grid.DesiredForce * dt);
                _physics.ApplyAngularImpulse(grid.Body, grid.DesiredTorque * dt);
                freePropelled++;
                // Falls through to the block scan below, which — while free propulsion is on — only
                // still applies real Buoyant lift; Fan blocks are skipped there since DesiredForce/
                // Torque was already applied directly above and allocating it across Fans too would
                // double it up.
            }

            var (pos, rot) = _physics.GetBodyPose(grid.Body);
            var com = grid.CenterOfMass;
            var desiredForce  = grid.DesiredForce;
            var desiredTorque = grid.DesiredTorque;
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
            // free-propulsion mode — DesiredForce/Torque was already applied directly above).
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

            _lastDesiredForceY  = desiredForce.Y;
            _lastDeliveredForceY = deliveredForceY;
        }

        _lastFanCount = fanCount;
        _lastBuoyantCount = buoyantCount;
        _lastGridsProcessed = gridsProcessed;
        _lastFreePropelled = freePropelled;
    }

    // World-space offset from centre of mass for a chunk-local voxel — the same rigid transform
    // GridTransformSystem uses for chunk meshes: world = bodyPos + R·(localCentre - centreOfMass).
    private static Vector3 LocalOffset(int lx, int ly, int lz, Vector3 chunkOrigin, Vector3 com, Quaternion rot)
    {
        var localCentre = new Vector3(chunkOrigin.X + lx + 0.5f, chunkOrigin.Y + ly + 0.5f, chunkOrigin.Z + lz + 0.5f);
        return Vector3.Transform(localCentre - com, rot);
    }

    // A Fan's thrust FORCE ON THE SHIP is opposite its stored Facing — Facing is the exhaust/visual
    // direction (also which face gets the glowing Top texture, since Fan's Top is oriented to
    // Facing), and the reaction (Newton's third law) pushes the ship the other way, like a rocket
    // nozzle: exhaust down, ship goes up. Applying
    // force *along* Facing instead of against it was a sign bug present since Fan thrust was first
    // implemented — a Fan facing down (intended as a lift thruster, exhausting downward) was actually
    // pushing the ship further down, fighting the very lift it was built to provide. Free propulsion
    // mode never touches Facing at all (it applies AirshipControlSystem's force directly), which is
    // why it tested fine while Fan-block-allocated thrust didn't.
    private static Vector3 ThrustDirection(ChunkEntry entry, int lx, int ly, int lz, Quaternion rot)
    {
        var facingVec = entry.Data.GetFacing(lx, ly, lz).ToVector();
        var exhaustDir = Vector3.Transform(new Vector3(facingVec.X, facingVec.Y, facingVec.Z), rot);
        return -exhaustDir;
    }

    // A Fan voxel's alignment with the desired force (dot of its thrust direction with it) and with
    // the desired torque (dot of its lever-arm cross product with it, normalized by lever length).
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
    public string DebugName => "Airship Propulsion";

    public void DrawDebugUi()
    {
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
