# Milestone 3 — Physics & Dynamic Voxels

Implements `plan.md` Milestone 3. Introduces BepuPhysics2 and the dynamic voxel grid system that
airships (Milestone 6) are built on. Physics deliberately comes before lighting (Milestone 4) so the
lighting system can be built against real dynamic grids from the start — see `lighting_design.md`,
whose Tier-2 model assumes each dynamic grid owns its blocks in a **local, axis-aligned frame**. This
plan keeps that property front-of-mind: a dynamic grid stores blocks in grid-local coordinates and
only its single rigid-body pose changes as it moves.

**Exit criterion (from `plan.md`):** the player can spawn a dynamic grid, watch it fall and collide
with terrain and with other grids, and edit it by clicking to add/remove blocks.

---

## Guiding constraint: share everything between static and dynamic voxels

`plan.md` 3.2 is explicit: *"A lot of rendering, meshing, chunk data storage, and ECS components
should be shared between dynamic and static chunks. The dynamic chunks should be editable the same as
static chunks."*

Today that sharing does not exist — `ChunkManager` is hardwired to be *the* static world. So
**Phase 3.0 is a refactor** that extracts a reusable voxel container, after which both the static
world and each dynamic grid are instances of the same type. Every later phase depends on it.

### What is already reusable as-is

| Component | Reuse verdict |
|-----------|---------------|
| `ChunkData` (32³ `BlockId[]`, `Get/Set/HasAnySolid`) | **As-is.** Already frame-agnostic — no world coupling. |
| `GreedyMesher` (`Mesh(chunk, 6 neighbours)`) | **As-is.** Emits chunk-local vertices `[0,32]`; caller's `Transform` places them. |
| `Transform` (`Position`, `Rotation`, `Scale`, `ToMatrix` = T·R·S) | **As-is.** Already supports rotation — a rotated grid chunk just needs its `Transform` set. |
| `RenderSystem` (draws every `MeshRenderer` via `Transform.ToMatrix()`) | **As-is.** Works for rotated chunks with zero changes. |
| `MeshRenderer`, `ChunkEntry`, `VoxelRaycaster` (DDA) | Reused, lightly generalized (see below). |

### What must change

`ChunkManager` currently bundles three responsibilities: (a) a chunk dictionary with neighbour-dirty
bookkeeping and mesh handoff, (b) world-space block access via `Decompose`, (c) being a singleton for
the streamed static world. We split (a) into a shared base and keep (c) as a thin static-world layer.

---

## Phase 3.0 — Extract the shared voxel container

### New type: `ChunkVolume` (`src/ClearSkies.Engine/Voxels/ChunkVolume.cs`)

Hosts everything generic about "a set of 32³ chunks with meshes and entities." Lift the body of the
current `ChunkManager` almost verbatim:

```csharp
public class ChunkVolume
{
    protected readonly Dictionary<ChunkPosition, ChunkEntry> _chunks = new();
    protected readonly World _world;

    public ChunkVolume(World world) => _world = world;

    public int  LoadedCount               { get; }
    public bool IsLoaded(ChunkPosition p);
    public ChunkData? GetData(ChunkPosition p);
    internal ChunkEntry? GetEntry(ChunkPosition p);
    internal IEnumerable<KeyValuePair<ChunkPosition, ChunkEntry>> All { get; }

    // Volume-LOCAL block access (was SetBlockWorld/GetBlockWorld/Decompose in ChunkManager).
    public BlockId GetBlock(int x, int y, int z);
    public virtual void SetBlock(int x, int y, int z, BlockId id);  // virtual: dynamic grid overrides to auto-grow + mark shape dirty

    protected (ChunkPosition, int lx, int ly, int lz) Decompose(int x, int y, int z); // MathF.Floor — already negative-safe
    public void SetMesh(ChunkPosition p, GpuMesh mesh);
    protected void MarkNeighboursDirty(ChunkPosition p);
    protected void TryMark(ChunkPosition p);

    // Hook so dynamic grids can position chunk entities differently from the static world.
    protected virtual void PlaceChunkEntity(Entity e, ChunkPosition p);
}
```

Key generalization: `SetBlock` becomes **virtual**, and chunk creation is factored into a protected
`EnsureChunk(ChunkPosition)` that the dynamic grid can call to **auto-grow** (create a chunk on demand
when an edit lands outside existing chunks — supports `plan.md`'s "number of chunks will expand to
fit"). `ChunkPosition` already supports negative coordinates, so no change there.

### `StaticWorld : ChunkVolume` (rename of today's `ChunkManager`)

Adds only the static-world specifics:
- `Load(ChunkPosition, IWorldGenerator)` — runs the generator, sets the chunk entity `Transform` to
  `pos.WorldOrigin` (no rotation). This is `PlaceChunkEntity`'s static implementation.
- `Unload(ChunkPosition)` — unchanged.
- Public `GetBlockWorld/SetBlockWorld` kept as thin aliases to `GetBlock/SetBlock` so
  `BlockInteractionSystem` and `VoxelRaycaster` need minimal edits.

Mechanical rename impact: `ChunkLoadSystem`, `ChunkMeshSystem`, `BlockInteractionSystem`,
`VoxelRaycaster`, and `Program.cs` reference `ChunkManager`. **Decision: rename `ChunkManager` →
`StaticWorld`** (project-wide), since dynamic grids arrive this same milestone and "manager" becomes
ambiguous once `DynamicGrid` exists alongside it.

### `ChunkMeshSystem` generalization

Today it iterates `_manager.All`. Generalize it to iterate **a list of `ChunkVolume`s** (the static
world plus every dynamic grid). The greedy-mesh neighbour lookups (`GetData(pos.Offset(...))`) stay
within a single volume — correct, because a dynamic grid's chunks are only adjacent to each other, and
cross-volume face culling is intentionally not done (grids are separate objects). Register dynamic
grids with the mesh system as they spawn; unregister on despawn.

### Deliverable / check

After 3.0 the game behaves **identically** — pure refactor. Verify: build, fly the existing sky world,
break/place blocks. No visual or behavioural change.

---

## Phase 3.1 — Physics integration (BepuPhysics2)

### Dependency

Add to `src/ClearSkies.Engine/ClearSkies.Engine.csproj`:

```xml
<PackageReference Include="BepuPhysics" Version="2.4.0" />  <!-- pulls BepuUtilities 2.4.0 -->
```

BepuPhysics2 pulls in `BepuUtilities`. It requires `<AllowUnsafeBlocks>` — already enabled.

### New: `PhysicsWorld` (`src/ClearSkies.Engine/Physics/PhysicsWorld.cs`)

Thin wrapper owning the Bepu `Simulation` and its `BufferPool`. Bepu requires two user-supplied
callback structs:

- `INarrowPhaseCallbacks` — material properties (friction, restitution) and collision filtering. Start
  permissive: everything collides; friction ≈ 1, restitution ≈ 0.
- `IPoseIntegratorCallbacks` — applies gravity `(0, -10, 0)` per substep and (optionally) linear/angular
  damping so grids settle.

```csharp
public sealed class PhysicsWorld : IDisposable
{
    public Simulation Simulation { get; }
    private readonly BufferPool _pool = new();

    public PhysicsWorld() {
        Simulation = Simulation.Create(_pool,
            new VoxelNarrowPhaseCallbacks { /* friction 1, restitution 0 */ },
            new VoxelPoseCallbacks { Gravity = new Vector3(0, -10, 0), LinearDamping = 0.03f, AngularDamping = 0.03f },
            new SolveDescription(velocityIterationCount: 8, substepCount: 1));
    }

    public void Step(float dt) => Simulation.Timestep(dt, /* threadDispatcher: */ null);
    public void Dispose() { Simulation.Dispose(); _pool.Clear(); }
}
```

Note: Bepu uses `System.Numerics.Vector3`/`Quaternion`, while the engine uses `Silk.NET.Maths`
`Vector3D<float>`/`Quaternion<float>`. A small `PhysicsConv` helper (`Physics/PhysicsConv.cs`) provides
`ToBepu`/`ToSilk` conversions — used at every physics↔ECS boundary.

> **As built (2.4.0 specifics):** the pose integrator uses Bepu's SIMD-wide `IntegrateVelocity`
> (`Vector3Wide`/`QuaternionWide`/`BodyInertiaWide`/`BodyVelocityWide`); the narrow-phase callback types
> live in `BepuPhysics.CollisionDetection`. The engine's `ClearSkies.Engine.Math` namespace shadows
> `System.Math`, so the damping `Clamp` must be written `System.Math.Clamp`.

### New: `PhysicsSystem` (`src/ClearSkies.Engine/ECS/PhysicsSystem.cs`), `SystemStage.Logic`

Fixed-timestep accumulator (the field `Time.FixedStep = 1/60` already exists):

```csharp
_accumulator += dt;
while (_accumulator >= FixedStep) {
    _physics.Step(FixedStep);
    _accumulator -= FixedStep;
}
```

Run this **before** `PlayerGridControlSystem` (3.4) and before the grid-pose sync (3.2) within Logic.
Wire `PhysicsWorld` creation and `PhysicsSystem` registration into `Program.cs`, and dispose in
`EngineHost.Dispose` (add `PhysicsWorld` as a host-owned object, mirroring `Renderer`).

### Static terrain → Bepu statics

Each loaded chunk gets a Bepu **static** collidable built from its solid blocks. A naive per-block box
compound is far too many shapes for streamed terrain, so decompose occupancy into merged boxes first:

- **`VoxelBoxDecomposer`** (`src/ClearSkies.Engine/Voxels/VoxelBoxDecomposer.cs`): greedy 3-D box
  merging over a `ChunkData` (the 3-D analogue of `GreedyMesher`'s 2-D quad merge) → a list of
  `(center, size)` boxes in chunk-local block units. Reuse the merge intuition already proven in
  `GreedyMesher`.

> **As built — per-box statics, not a compound.** Rather than one `Compound`/`BigCompound` static per
> chunk, each merged box is added as its own Bepu `Static` (`Box` shape at `pos.WorldOrigin + center`).
> Box statics have unambiguous teardown (`Statics.Remove` + `Shapes.Remove`), whereas a compound
> requires manually removing each child shape, disposing the children buffer, then removing the compound
> shape — fiddly and easy to leak. Greedy merging keeps the box count low, and statics are cheap (only
> in the broadphase, never simulated). Revisit `BigCompound` only if the global static count becomes a
> broadphase concern.
>
> **As built — handles owned by `StaticColliderSystem`, not `ChunkEntry`.** `ChunkEntry` carries only
> the `NeedsRecollide` flag (no Bepu types). The system holds a
> `Dictionary<ChunkPosition, List<StaticHandle>>` and reconciles it against the loaded set each frame,
> so chunk unloads need no physics coupling in `StaticWorld.Unload`.

**Caller: a dedicated `StaticColliderSystem`, not `ChunkMeshSystem`.** Collision build is deliberately
*not* folded into the mesh system, for three reasons:

1. **Different dirty-propagation rules.** Meshing marks the edited chunk **plus its 6 neighbours** dirty
   (greedy face-culling depends on what is across the border). A chunk's collision boxes depend only on
   its **own** occupancy — neighbour edits never change them. So collision needs a narrower trigger and
   therefore its own flag: add `bool NeedsRecollide` to `ChunkEntry`, set wherever `NeedsRemesh` is set
   (`ChunkVolume.SetBlock`, `StaticWorld.Load`) **but without the neighbour propagation**.
2. **Independent clearing.** `ChunkMeshSystem` already clears `NeedsRemesh` when it remeshes; a shared
   flag would let whichever system runs first consume the signal before the other sees it.
3. **Separation & cost.** Keeps `ChunkMeshSystem` free of a `PhysicsWorld` dependency, mirrors the
   dynamic-grid side (its colliders are owned by `GridShapeSystem`, Phase 3.2), and lets collision use a
   CPU-only per-frame budget independent of the GPU-upload-bound `MeshesPerFrame = 2`.

`StaticColliderSystem` (`SystemStage.Logic`) iterates the static world's chunks and, for each with
`NeedsRecollide`:
- remove any existing handles for that chunk from its dictionary;
- `HasAnySolid()` false → clear the flag, nothing to add;
- otherwise → `VoxelBoxDecomposer` → add one box static per merged box, store the handle list, clear
  the flag.
- Throttled to `CollidersPerFrame = 4`. After the build pass, **reconcile**: any tracked
  `ChunkPosition` no longer loaded has its statics released — this is how unloads are handled, with no
  physics reference inside `StaticWorld`.

### Validation test (3.1 exit)

Add a temporary debug action (e.g. key `B`) that drops a single dynamic box body above the island.
Confirm it falls under gravity, lands on terrain, and rests. This validates gravity, the fixed step,
and terrain statics before grids exist. (Render it by parenting a cube `MeshRenderer` whose `Transform`
is synced from the body pose — same sync mechanism Phase 3.2 generalizes.)

---

## Phase 3.2 — Dynamic voxel grid

### New: `DynamicGrid` (`src/ClearSkies.Engine/Voxels/DynamicGrid.cs`), `: ChunkVolume`

A dynamic grid is **one logical object** = a `ChunkVolume` of blocks in grid-local space + a single
Bepu dynamic body. Its blocks never move relative to each other (Tier-2 motion invariance); only the
body pose changes.

```csharp
public sealed class DynamicGrid : ChunkVolume
{
    public Entity     Root        { get; }      // carries DynamicGridComponent
    public BodyHandle Body         { get; internal set; }
    public bool       ShapeDirty   { get; private set; } = true;

    public override void SetBlock(int x, int y, int z, BlockId id)
    {
        EnsureChunk(ContainingChunk(x, y, z));   // auto-grow: create chunk if edit lands outside
        base.SetBlock(x, y, z, id);
        ShapeDirty = true;                         // collision shape + mass must be rebuilt
    }

    protected override void PlaceChunkEntity(Entity e, ChunkPosition p)
    {
        // Initial local placement; GridTransformSystem overwrites world pose each frame.
        var t = Transform.Identity;
        t.Position = p.WorldOrigin;   // grid-LOCAL origin (relative to grid frame)
        e.Set(t);
    }
}
```

### New component: `DynamicGridComponent` (in `Components.cs`)

```csharp
public struct DynamicGridComponent { public DynamicGrid Grid; }
```

### Pose sync — `GridTransformSystem` (`SystemStage.Logic`, after `PhysicsSystem`)

Each frame, for every dynamic grid: read its Bepu body pose, then set **every chunk entity's**
`Transform` so its local offset is rigidly carried by the grid pose:

```
gridPos = ToSilk(body.Pose.Position)
gridRot = ToSilk(body.Pose.Orientation)
for each chunk entity at local origin Lo:
    t.Rotation = gridRot
    t.Position = gridPos + Vec.Rotate(gridRot, Lo - CenterOfMass)   // CoM offset, see below
```

`RenderSystem` then draws each chunk with `Transform.ToMatrix()` (T·R·S) — **no renderer change**.
`Vec.Rotate` already exists.

### Collision shape & mass — `GridShapeSystem` (`SystemStage.Logic`, before `PhysicsSystem`)

When `grid.ShapeDirty`:
1. Run `VoxelBoxDecomposer` over **all** the grid's chunks (in grid-local space) → merged boxes.
2. Build a Bepu `Compound`/`BigCompound`. Bepu's `CompoundBuilder` computes the **center of mass** and
   combined **inertia**; capture the CoM offset — this is the `CenterOfMass` used by
   `GridTransformSystem` so render offsets and physics rotation origin stay consistent (Bepu rotates a
   body about its CoM).
3. If the body doesn't exist yet, create it (`BodyDescription.CreateDynamic(pose, inertia, collidable,
   activity)`); otherwise update the shape, inertia, and re-wake it. Release the previous shape's
   `BufferPool` allocation to avoid leaks.
4. Clear `ShapeDirty`.

**This is the milestone's hardest correctness knot:** keeping the three frames consistent — block-local
coordinates, the Bepu shape (recentered on CoM), and the rendered chunk transforms. Pin down the
convention early: *blocks are authored in grid-local space; the shape is built recentered by `-CoM`;
render offsets subtract the same `CoM`.* Write a one-block-grid test first (CoM = block center) and a
two-block test (CoM between them) before anything bigger.

### Meshing

Register each `DynamicGrid` with `ChunkMeshSystem` (Phase 3.0 made it multi-volume). Greedy meshing,
upload, and `MeshRenderer` assignment are identical to static chunks. Editing a grid marks chunks dirty
exactly like the static path — satisfying "editable the same as static chunks."

### Spawn helper

`DynamicGridFactory.SpawnSingleBlock(world, physics, meshSystem, position, BlockId.Stone)` — creates
the `DynamicGrid`, sets one block at local `(0,0,0)`, registers it for meshing, and lets
`GridShapeSystem` build the body next frame. Used by 3.4's spawn key.

---

## Phase 3.3 — Grid–world & grid–grid interaction

Mostly emergent once 3.1 (terrain statics + stepping simulation) and 3.2 (grid dynamic bodies) coexist
in one `Simulation`. Tasks:

- **Collision filtering:** confirm `VoxelNarrowPhaseCallbacks.AllowContactGeneration` permits
  grid↔static and grid↔grid (default allow-all is fine for M3). Add a speculative-margin sanity pass if
  fast grids tunnel; bump `substepCount` if needed.
- **Resting/sleeping:** ensure grids come to rest on terrain (the linear/angular damping from 3.1 plus
  Bepu's sleeping). Verify they wake on contact and on player force (3.4).
- **Tests:**
  1. Spawn a grid above terrain → it falls, contacts, and rests on the surface (grid–world).
  2. Spawn two grids so one lands on the other → they stack/settle without interpenetration
     (grid–grid).

No new systems; this phase is configuration + validation. If contacts look wrong, the usual suspects
are CoM/shape misalignment from 3.2 (re-verify the convention) or an inverted inertia tensor.

---

## Phase 3.4 — Player interaction for testing

### New: `PlayerGridControlSystem` (`src/ClearSkies.Engine/ECS/PlayerGridControlSystem.cs`, `Logic`)

Applies to **all** dynamic grids (per `plan.md`). Reads `InputManager` (it already exposes
`IsKeyDown`/`WasKeyPressed`):

| Input | Effect |
|-------|--------|
| Arrow keys | Apply horizontal force (X/Z) to every grid body — `body.ApplyLinearImpulse` or accumulate a force each substep |
| Page Up / Page Down | Apply vertical (+Y / −Y) force |
| End | Stop motion: zero linear & angular velocity on every grid |
| (spawn key, e.g. `G`) | `DynamicGridFactory.SpawnSingleBlock` at a point in front of the camera |

Forces wake sleeping bodies (set `Awake = true` before applying). Use a tunable force magnitude
constant; impulses scale with `dt` if applied per-frame, or use Bepu's continuous force accumulation.

### Editing dynamic grids by clicking (raycast against grids)

The exit criterion requires editing a grid by clicking. Today `VoxelRaycaster.Cast` and
`BlockInteractionSystem` only test the static world in world space. Extend selection to grids:

- **Generalize `VoxelRaycaster`** to take a `ChunkVolume` and an optional pose. For a dynamic grid,
  transform the ray into grid-local space with the inverse body pose (`origin' = Rot⁻¹·(origin −
  gridPos) + CoM`, `dir' = Rot⁻¹·dir`), then run the **existing** DDA unchanged in local space. The hit
  block/normal come back in local coordinates.
- **`BlockInteractionSystem`** casts against the static world *and* each dynamic grid, then picks the
  **nearest** hit (compare hit distances in world space). Break/place then calls `SetBlock` on whichever
  volume was hit — the static path is unchanged, and the grid path goes through `DynamicGrid.SetBlock`
  (auto-grow + `ShapeDirty`). Place position uses the hit normal exactly as today.
- The existing face-highlight overlay already draws via a `Transform`; when the hit is on a grid, set
  the highlight entity's `Transform` to the grid pose composed with the local face — reuses the same
  overlay mesh.

### 3.4 exit / milestone exit

Run the full loop: press `G` to spawn a stone grid in front of the camera → it falls and rests on the
island → click to add/remove blocks (grid re-meshes and its collision shape rebuilds) → use arrows /
PageUp / PageDown to push it around and `End` to stop → spawn a second grid and watch them collide.

---

## File-change summary

| Phase | New files | Modified files |
|-------|-----------|----------------|
| 3.0 | `Voxels/ChunkVolume.cs` | `ChunkManager.cs`→`StaticWorld.cs` (rename + reparent), `ChunkMeshSystem.cs` (multi-volume), `ChunkLoadSystem.cs`, `BlockInteractionSystem.cs`, `VoxelRaycaster.cs`, `Program.cs` (rename refs) |
| 3.1 | `Physics/PhysicsWorld.cs` (+ callback structs), `Physics/PhysicsConv.cs`, `Voxels/VoxelBoxDecomposer.cs`, `ECS/PhysicsSystem.cs`, `ECS/StaticColliderSystem.cs`, `ECS/DebugDropSystem.cs` | `ClearSkies.Engine.csproj` (Bepu 2.4.0), `EngineHost.cs` (own+dispose `PhysicsWorld`), `ChunkEntry.cs` (`NeedsRecollide` flag only), `ChunkVolume.cs` (set `NeedsRecollide`, no neighbour propagation), `Program.cs` |
| 3.2 | `Voxels/DynamicGrid.cs`, `Voxels/DynamicGridFactory.cs`, `ECS/GridTransformSystem.cs`, `ECS/GridShapeSystem.cs` | `Components.cs` (`DynamicGridComponent`), `ChunkMeshSystem.cs` (register grids), `Program.cs` |
| 3.3 | — | `Physics/` callbacks (filtering/tuning), `Program.cs` (system order) |
| 3.4 | `ECS/PlayerGridControlSystem.cs` | `VoxelRaycaster.cs` (volume + pose), `BlockInteractionSystem.cs` (multi-volume nearest hit), `Program.cs` |

---

## Risk register

| Risk | Mitigation |
|------|-----------|
| **Frame-convention bugs** (block-local vs CoM-recentered shape vs render offset) — the single biggest risk | Fix the convention in writing before coding 3.2; validate with 1-block then 2-block grids before larger ones |
| Terrain static count explodes for streamed chunks | `VoxelBoxDecomposer` greedy merge; `BigCompound`; only build for chunks with solids (reuse `HasAnySolid`) |
| Bepu ↔ Silk.NET math type mismatch (`System.Numerics` vs `Silk.NET.Maths`) | Centralize in `PhysicsConv`; never pass raw types across the boundary |
| Rebuilding a grid's shape every edit is costly | Debounce via `ShapeDirty` (rebuild once per frame max); fine for M3 scale |
| Fast grids tunnel through thin terrain | Bepu speculative margins + raise `substepCount`; defer full CCD |
| Refactor (3.0) regresses the working static world | 3.0 is pure refactor — verify identical behaviour before starting 3.1 |

## Sequencing

3.0 → 3.1 → 3.2 → 3.3 → 3.4, strictly in order (each depends on the previous). 3.0 must leave the game
behaving identically before physics is layered on. Recommend committing after 3.0 and after 3.1's
falling-box test, since those are the two points where a clean baseline is most valuable.
