# Milestone 5 — First Airship Systems

Implements `plan.md` Milestone 5. Layers game logic on top of the dynamic voxel grids built in
Milestone 3 (`DynamicGrid`, `ChunkVolume`, BepuPhysics2 compound bodies) so a player-built grid of
blocks can actually fly: a debug "take control" mode with two camera modes, lift/thrust blocks that
apply real physics forces, an automatic stabilizer + player steering control layer, and a tuning pass
on mass/lift/drag so it feels weighty rather than twitchy.

**Exit criterion (from `plan.md`):** build an airship in-world, power it up, and pilot it between two
islands. The multiplayer half of `plan.md`'s exit-criterion sentence ("with a second player aboard
seeing the ride in sync") is **stale text** — Multiplayer is Milestone 6, which comes *after* this
one, and no Milestone 5 phase mentions networking. This plan scopes Milestone 5 to single-player
piloting only; Milestone 6 will network whatever this milestone builds.

---

## Context

Milestones 1–4 gave us static + dynamic voxel chunks, BepuPhysics2 rigid bodies for dynamic grids
(with compound-box collision rebuilt from block occupancy), and GPU lighting. Milestone 5 is
explicitly a **prototype/debug-level** pass at flight (per `plan.md`'s own framing: "there isn't a
power network, limited fuel, many block types, etc." yet) — the fuller economy in
`airship_block_system.md` (energy costs, batteries, controllers, solar/furnace generation) and the
extended block roster in `description.md` (Jets, Magic Levitator, Height/Heading/Speed Controllers,
Pilot Block) are the long-term target, not this milestone's scope. Milestone 5 implements the minimal
subset needed to prove the airship loop end-to-end: weight-bearing blocks, one thrust block (`Fan`,
matching `plan.md`'s own phase wording) and one passive-lift block (`Buoyant`), debug-UI-tunable force
constants instead of a real power system, and an automatic+manual control layer — deferring
energy/power entirely to a later milestone.

Scope decisions baked into this plan:
- Phase 5.3's "debug UI to control force generated, per block" means **per block type** (one global
  tuning slider per block type, e.g. "Fan max thrust"), not a UI per placed block instance — simpler,
  and matches the stated prototype scope regardless of how big a ship gets.
- Block rotation is stored **densely, directly in voxel data** (every voxel carries a facing, not a
  sparse per-chunk lookup) — simplicity over the small memory cost.
- Ship control (Phase 5.4) is a **desired-velocity system**, not raw force taps: `W`/`S` command a
  stable forward/back velocity, `Space` commands a stable upward velocity, `Q`/`E` command yaw
  (rotation around the vertical axis) — no pitch/roll input axes are needed; pitch/roll is handled
  entirely by the always-on self-leveling stabilizer.

The pieces `plan.md` calls out as missing (block rotation, "block entity" extended state) are
genuinely absent from the codebase today. This plan extends, rather than duplicates, these existing
systems: `PlayerGridControlSystem` (existing arrow-key impulse code), `GridSelection` /
`SelectedGridComponent` (existing "which grid" selection), `GridShapeSystem` (existing compound-rebuild
+ centre-of-mass calculation), and the `IDebugUiSystem` / `EngineHost.AddSystem` pattern for new ImGui
panels.

---

## Starting point — what exists today

| Area | File(s) | Relevant state |
|---|---|---|
| Dynamic grid | `Voxels/DynamicGrid.cs`, `Voxels/ChunkVolume.cs`, `Voxels/DynamicGridFactory.cs` | Grid = `ChunkVolume` + `BodyHandle` + `CenterOfMass` + `ShapeDirty`. `SetBlock` grows chunks and marks shape dirty. |
| Blocks | `Voxels/BlockId.cs` (byte enum), `Voxels/BlockDef.cs`, `Voxels/BlockRegistry.cs` | `BlockDef` has Id/Name/Color/IsSolid/LightEmission/Opacity/Texture — **no weight, no rotation, no per-instance state.** One byte per voxel in `ChunkData`. |
| Physics | `Physics/PhysicsWorld.cs`, `ECS/GridShapeSystem.cs`, `ECS/GridTransformSystem.cs` | `BuildDynamicCompound` uses **uniform density = box volume** (block type doesn't affect mass yet). `ApplyLinearImpulse` applies at the body's centre — **no offset/torque-inducing overload, no angular velocity getter, no kinematic/lock toggle.** CoM is already recomputed correctly on every shape rebuild. |
| Player→grid control (M3 Phase 3.4) | `ECS/PlayerGridControlSystem.cs` | Arrow keys / `I`/`K` apply linear impulses; `;` calls `StopBody`. Only applies to the currently `SelectedGridComponent` grid, not all grids. Direct precedent to extend for piloting. |
| Grid/block selection | `ECS/GridSelection.cs`, `ECS/Components.cs` (`SelectedGridComponent`) | One grid selected at a time; already used by debug UI and save/load. Reuse as "grid you'd take control of." |
| Camera | `ECS/PlayerInputSystem.cs`, `ECS/Components.cs` (`CameraComponent`, `FreeFlyController`), `Rendering/Camera.cs`, `ECS/CameraUtil.cs` | Single active camera (`CameraComponent.Active`) driven by a free-fly controller. **No camera modes, no attach-to-entity concept** — new work. |
| ECS/system scheduling | `Core/ISystem.cs`, `Core/EngineHost.cs`, `Game/Program.cs` | `AddSystem(system, stage)` with `SystemStage {Input, Logic, PreRender, Render}`; systems implementing `IDebugUiSystem` are auto-registered into the ImGui "Systems" menu — use this for every new system, never hardcode into `EngineHost`. |
| ImGui debug panels | `Gui/IDebugUiSystem.cs`, `Gui/ImGuiController.cs` | Implement `DebugName` + `DrawDebugUi()`; no existing `SliderFloat` example, but the panel plumbing is ready. |
| Save/load | `Voxels/DynamicGridSerializer.cs` (`CSGD` + version), `Voxels/StaticWorldSerializer.cs` (`CSCD` + version), `ECS/GridPersistenceSystem.cs` | Both formats currently persist **only a flat `(x,y,z,blockId)` list / raw `BlockId[]`** — no rotation, no extended state, no physics/spawn state. Both already have a version byte in their header, so extending the format is a version bump, not a break. |
| Block rotation / "block entity" state | — | **Confirmed absent everywhere in `src/`.** Genuinely new work, as `plan.md`'s Phase 5.3 notes itself. Rotation will be added as a dense per-voxel field; per-instance "block entity" extended state is not needed by this milestone's blocks (see Phase 5.3) and is deferred until a future block genuinely requires persisted per-instance state. |

---

## Phase 5.1 — Debug Control

- Add a small pilot-mode component (e.g. `GridPilotComponent { TargetGrid: Entity?, CameraMode }` with
  `CameraMode { ThirdPerson, Locked }`) on the player entity.
- New `GridPilotSystem` (Logic + PreRender stages, implements `IDebugUiSystem` for a status panel):
  - A key press while a grid is `SelectedGridComponent` (reusing `GridSelection`, no new selection
    mechanism) toggles piloting that grid; another key press (or re-toggle) returns to free-fly.
  - **Third-person mode:** camera Transform = grid's body Transform + a fixed offset behind/above,
    looking at the grid — same math shape as `GridTransformSystem`'s pose sync, just applied to the
    camera's `Transform` instead of a chunk's.
  - **Locked mode:** camera Transform is rigidly parented to the grid's Transform (camera moves and
    rotates exactly with the ship) — a fixed local offset composed with the body pose each frame.
  - While piloting, deactivate `FreeFlyController` input handling (`PlayerInputSystem`'s camera-look
    branch) and switch `CameraComponent.Active` to the pilot camera; the existing single-active-camera
    convention (`CameraUtil.TryGetActive`) means this is a flag flip, not a rendering change.
- **Lock grid:** extend `PlayerGridControlSystem` (or the new pilot system) with a key that freezes the
  targeted grid — zero its velocity and prevent further impulses/gravity from moving it. Investigate
  whether BepuPhysics2 supports toggling a body kinematic in place; if not, fall back to re-zeroing
  velocity and countering gravity every physics tick while "locked" is set (a simple, testable
  approximation).
- **Right grid (reset rotation):** directly set the body's pose rotation to identity via the existing
  `PhysicsWorld.SetBodyPose`, keeping current position — no new physics API needed.

## Phase 5.3 — Lift & Thrust Blocks

- **Per-block weight → real mass distribution:**
  - Add `Weight` to `BlockDef`; register values from `airship_block_system.md` (Wood=1, Metal=3,
    Stone=6, Fan=3, Buoyant=block-count-based per its own rule).
  - Change `PhysicsWorld.BuildDynamicCompound` / `GridShapeSystem` to derive each box's mass from its
    blocks' `Weight` instead of uniform volume density, so `CenterOfMass` (already recomputed on every
    rebuild) reflects real, asymmetric ship construction — this is the mechanism Phase 5.5 needs, and
    it already exists structurally; only the density input changes.
- **New block types:** `BlockId.Fan` (thrust along the block's facing direction) and
  `BlockId.Buoyant` (passive upward force, scaling with block count, no energy cost in this milestone).
- **Block rotation (new, dense storage):** widen `ChunkData`'s per-voxel storage from a single
  `BlockId` byte to `BlockId` + `Facing` (e.g. a parallel dense `Facing[]` array, or pack both into one
  `ushort` per voxel) — every voxel carries a facing, defaulting to a fixed value for non-oriented
  blocks. Simpler than a sparse lookup; no new per-chunk collection type, no lookup path to keep in
  sync with block placement/removal.
- **No persisted per-block "entity" state needed for this milestone's blocks:** `Fan`/`Buoyant`
  behaviour is fully determined by `BlockId` + `Facing` plus the control system's live per-tick
  computation (Phase 5.4) — there is nothing to persist beyond rotation. The general "block entity"
  mechanism `plan.md` anticipates is deferred until a future block (e.g. a Controller/Battery in a
  later milestone) genuinely needs saved per-instance state.
- **Force application:** add an offset-aware overload to `PhysicsWorld.ApplyLinearImpulse` (impulse +
  world offset from the body's centre of mass) so applying thrust at a `Fan` block's actual world
  position induces the correct torque for free — no separate angular-impulse path needed for this.
- **New `AirshipPropulsionSystem`** (Logic stage): per loaded `DynamicGrid`, scan chunks for `Fan`/
  `Buoyant` blocks (via the existing per-chunk block scan pattern already used for shape/collision
  rebuilds), compute each block's world position + facing, and apply an impulse scaled by
  `dt × per-type debug-tunable max-force constant × that block's current thrust fraction` (`Buoyant`'s
  fraction is always 1.0 — passive; `Fan`'s fraction comes from Phase 5.4's allocation step each tick).
  Implements `IDebugUiSystem` to expose one `ImGui.SliderFloat` per block **type** (not per instance)
  for max thrust/lift.
- **Save/load:** bump the version byte in `DynamicGridSerializer` / `StaticWorldSerializer` and extend
  the per-voxel record to include `Facing`; old saves without it load with a default facing.

## Phase 5.4 — Control System

Desired-velocity control, not raw force taps: the player (or the idle default) states a target
velocity per axis, and the control system solves for whatever `Fan` thrust makes the ship track it.
Self-leveling (pitch/roll) is always on, independent of piloting, since it has no player-facing input.

- Add a mode flag per grid (e.g. on `DynamicGridComponent`): `Auto` (default) vs `PlayerPiloted` (set
  by Phase 5.1's pilot toggle) — this only changes *where the forward/vertical/yaw velocity targets
  come from*, not whether self-leveling runs.
- **New `AirshipControlSystem`** (Logic stage, runs before `AirshipPropulsionSystem` each tick and
  writes the thrust fraction it reads):
  - **Self-level (always on):** read the grid's current tilt (rotation vs upright) and angular velocity
    (needs a small `PhysicsWorld.GetAngularVelocity` addition alongside the existing `GetBodyPose`);
    compute a corrective pitch/roll torque via `-Kp·tiltError - Kd·angularVelocity`. This runs
    regardless of `Auto`/`PlayerPiloted`.
  - **Velocity targets:**
    - `PlayerPiloted`: read `W`/`S` (forward/back, ship-local axis) and `Space` (world-up) via
      `InputManager`, mapped to a target linear velocity on those two axes (0 when no key held, so the
      ship holds station rather than drifting); read `Q`/`E` mapped to a target yaw angular velocity
      (0 when neither held). Reuses `InputManager`'s existing key-state API; supersedes
      `PlayerGridControlSystem`'s old direct-impulse arrow-key path for grids in this mode.
    - `Auto` (not piloted): all velocity targets are 0 — the ship holds position/heading and only
      self-levels. (Auto ≠ autopilot-navigates-somewhere; that's out of scope for this milestone.)
  - **Velocity tracking → desired force/torque:** for each of the three controlled axes (forward,
    vertical, yaw), compute `error = targetVelocity − currentVelocity` (linear velocity for
    forward/vertical, angular velocity for yaw) and scale by a proportional gain to get a desired
    force (forward/vertical) or torque (yaw) contribution; sum yaw with the self-level pitch/roll
    torque into one desired torque vector, and combine forward+vertical into one desired force vector.
  - **Allocation to blocks:** distribute the desired force vector + desired torque vector across the
    grid's `Fan` blocks by their facing and lever arm from CoM (each fan's contribution to a given axis
    is proportional to the dot product of its facing with that axis, weighted by lever arm for the
    torque terms) — a direct proportional allocation is sufficient for this prototype, not a full
    solver. Each `Fan` block's resulting thrust fraction (clamped to [-1, 1] or [0, 1] depending on
    whether a fan is reversible) is **transient, recomputed every tick** — held in an in-memory
    per-grid list/map for that tick only, not persisted on `ChunkEntry` — and consumed immediately by
    `AirshipPropulsionSystem` in the same tick. No new save-format changes beyond Phase 5.3's facing.

## Phase 5.5 — Airship Feel & Tuning

- Verify centre-of-mass now follows real block placement (Phase 5.3's weight change) by building a
  deliberately asymmetric test ship and confirming it tilts toward the heavy side, and that Auto mode
  (Phase 5.4) corrects it.
- Expose linear/angular damping constants (check BepuPhysics2 body-creation defaults in
  `PhysicsWorld.AddDynamicBody`; add explicit damping if absent) alongside the Phase 5.3 debug sliders
  so lift/drag/damping can be tuned together without a rebuild.
- No new systems — this phase is tuning constants already exposed by 5.1–5.4's debug UI, plus manual
  playtesting per the verification section below.

---

## File-change summary

| Phase | New | Modified / Removed |
|---|---|---|
| 5.1 | `ECS/GridPilotSystem.cs` (+ `GridPilotComponent` in `Components.cs`) | `ECS/PlayerInputSystem.cs` (suppress free-fly while piloting), `ECS/PlayerGridControlSystem.cs` (lock/right-grid keys), `Physics/PhysicsWorld.cs` (kinematic-lock or zero-velocity path), `Game/Program.cs` (register system) |
| 5.3 | `Voxels/Facing.cs` (facing enum), `ECS/AirshipPropulsionSystem.cs` | `Voxels/BlockId.cs`/`BlockDef.cs`/`BlockRegistry.cs` (+Weight, +Fan/+Buoyant), `Voxels/ChunkData.cs` (dense per-voxel `Facing` alongside `BlockId`), `Physics/PhysicsWorld.cs` (offset impulse overload, weight-based compound mass), `ECS/GridShapeSystem.cs` (mass source), `Voxels/DynamicGridSerializer.cs` + `StaticWorldSerializer.cs` (version bump, persist facing), `Game/Program.cs` |
| 5.4 | `ECS/AirshipControlSystem.cs` | `ECS/Components.cs` (grid mode flag), `ECS/PlayerGridControlSystem.cs` (superseded for piloted grids by velocity-target input reading), `Physics/PhysicsWorld.cs` (`GetAngularVelocity`), `Game/Program.cs` |
| 5.5 | — | `Physics/PhysicsWorld.cs` (damping constants), existing debug UI sliders (tuning only) |

---

## Risk register

| Risk | Mitigation |
|---|---|
| BepuPhysics2 may not expose a clean "kinematic toggle" for locking a grid in place | Fall back to re-zeroing velocity + counteracting gravity every physics tick while locked; testable approximation, no engine fork needed |
| Per-block thrust impulses applied at an offset could destabilize the simulation if too large or discontinuous | Clamp per-tick impulse magnitude; ramp thrust-fraction changes rather than stepping instantly |
| Dense per-voxel `Facing` field costs memory across every chunk, most of it unused (only `Fan` cares) | Accepted as a deliberate simplicity/memory trade-off — a single extra byte per voxel is cheap relative to `BlockId` itself, and avoids a lookup path entirely |
| Save-format version bump could orphan existing saves | Both serializers already carry a version byte; old saves simply load with a default facing, matching the existing versioning convention |
| Forward/vertical/yaw force allocation fighting the always-on self-level torque on the same `Fan` blocks | Solve one combined desired force+torque vector per tick (self-level torque summed in before allocation), not two independent passes — avoids double-counting or oscillation between the two |
| Velocity-tracking controller oscillates or overshoots (classic P-controller ringing) | Start with a proportional-only gain and only add the derivative/damping term if playtesting (Phase 5.5) shows overshoot; tune gains via the same debug-slider mechanism as thrust constants |
| Camera-mode switching fighting the existing free-fly input/ImGui-capture flags (`UiWantsMouse/Keyboard`) | Reuse the existing single-active-camera convention (`CameraComponent.Active`); piloting just changes which Transform drives it and disables free-fly input, no new capture logic |

---

## Sequencing

5.1 → 5.3 → 5.4 → 5.5 (`plan.md`'s own numbering has no Phase 5.2 — that gap is pre-existing in
`plan.md`, not introduced here). 5.1 (pilot camera + lock/right) touches almost entirely different
files from 5.3 (blocks/physics mass) and could be built first or in parallel with the start of 5.3, but
5.3's thrust blocks are a hard prerequisite for 5.4's control layer, and 5.5 is tuning on top of both.
Recommend committing after 5.1 (piloting works even with only `StopBody`-style movement), after 5.3
(hand-tuned Fan/Buoyant blocks visibly move a ship), after 5.4 (auto-stabilize + player steering both
work through one shared allocation path), and after 5.5 (final feel pass).

## Verification

- **5.1:** Select a grid, press the pilot key, confirm camera enters third-person mode following the
  grid; toggle to locked mode and confirm the camera rides rigidly with the ship through a manual
  impulse (existing arrow-key controls); test lock (ship stays put under gravity) and right (ship
  snaps upright from a tilted rest state).
- **5.3:** Place `Fan` blocks facing various directions and a few `Buoyant` blocks on a test grid;
  adjust the new debug sliders and confirm the ship visibly accelerates/lifts proportionally; save,
  reload, and confirm block facings survive.
- **5.4:** Build an asymmetric ship, leave it in `Auto` mode, and confirm it self-levels while holding
  position; enter `PlayerPiloted` mode and confirm `W`/`S` hold a stable forward/back speed, `Space`
  holds a stable climb speed, `Q`/`E` hold a stable yaw rate, and releasing all keys brings the ship to
  a level hover rather than drifting — all via `Fan` blocks, not a raw CoM impulse.
- **5.5:** Fly an asymmetric test ship between two islands manually; confirm it feels weighty
  (resists snap-turns, settles rather than oscillating) and that Auto mode recovers from a manual push.
- No automated test suite exists for gameplay feel — verification here is manual, in-engine, matching
  how Milestones 1–4 were validated per their own plans.
