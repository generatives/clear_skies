# Milestone 4 — Lighting (GPU)

Implements `plan.md` Milestone 4. **The authoritative design is `lighting_design_details.md`** — read it
first; this plan implements its uniform GPU pipeline (inject + flood, one mechanism for sun and lamps,
every volume shading every other). It **supersedes** the earlier three-tier CPU design
(`lighting_design.md`) and the CPU phasing this file previously contained.

The headline change from the CPU approach: **light is GPU-resident and sampled in the fragment shader**,
so meshes never re-bake on light change; shadow-map rendering, light injection, and BFS flood all run as
GPU passes. We trade global motion-invariance for a uniform system that includes real sun shadows and
cross-grid light, and recover steady-state cost later via culling (Phase 4.6).

**Exit criterion (from `plan.md`):** placed lamps illuminate surroundings; AO visible in corners;
sunlight shades underground spaces and undersides; a lamp on a dynamic grid casts light and shadows onto
nearby static terrain and other grids; a grid casts a sun shadow on the ground.

---

## Starting point — what exists from the CPU attempt

Earlier work built a CPU lighting stack (per-voxel `LightData`, a `LightEngine` BFS, a `LightSystem`,
per-vertex baked light with light in the greedy-mesh merge key, and a per-fragment N·L sun term). The
pivot **replaces** the CPU flood and the per-vertex bake. Disposition:

| Existing | Disposition in the GPU plan |
|---|---|
| `BlockDef.LightEmission` / `Opacity`, `BlockId.Lamp` | **Keep** — drive GPU injection + opacity buffer. |
| `ChunkVolume` / `DynamicGrid` / chunk structure | **Keep** — substrate for per-chunk GPU buffers. |
| Per-fragment N·L term + sun-direction uniform (WGSL) | **Keep** — combined with the new sun shadow map. |
| `LightData` (CPU per-voxel store) | **Remove** from the render path (keep only if gameplay needs CPU light queries). |
| `LightEngine` (CPU BFS + remove/relight) | **Remove** — replaced by GPU flood. |
| `LightSystem` (CPU init/relight per frame) | **Replace** with a GPU lighting system. |
| Per-vertex light attribute in `Vertex` | **Remove** — fragment samples the light buffer. |
| Light in the greedy-mesh merge key | **Remove** — meshing becomes light-independent. |

---

## Guiding principles (from `lighting_design_details.md`)

1. **One GPU mechanism for everything.** Sun and lamps, static world and every grid: depth map →
   inject → flood. No per-situation algorithms.
2. **Sample light in the fragment shader, never per-vertex.** Greedy meshing stays valid; meshing is
   light-independent; smooth lighting returns via filtering.
3. **Naive-correct before fast.** v1 clears and re-floods everything at 1–2 Hz; no incremental removal.
   Culling and change-detection come last (Phase 4.6) and are what restore steady-state cost.
4. **Max-relaxation flood** (`new = max(self, neighbours−1)`, opacity-blocked) — order-independent,
   GPU-robust. Global ping-pong, one pass per dispatch in v1 (no shared memory — 32³ exceeds WebGPU's
   16 KB workgroup-storage limit).
5. **Sky = ambient exposure (soft) + direct sun (sharp shadow map)**, two contributions, reconciled
   with the N·L term and an ambient floor.

---

## Phase 4.0 — GPU compute & voxel-data infrastructure

No visible lighting; lays the substrate every later phase needs.

- **Compute pipeline support in `Renderer` / `GpuContext`:** create compute pipelines, storage
  buffers/textures, compute bind groups, and dispatch. This is net-new — the renderer is render-only
  today.
- **Per-chunk GPU residency.** For every loaded chunk (static and grid): an **opacity buffer** derived
  from block data, and a **light buffer** (sky + block channels), allocated double-buffered for
  ping-pong. Define the layout (storage buffer vs 3D texture) per the storage-vs-sampled tension in the
  design doc.
- **Sync.** Upload opacity (and block identity for emitters) on chunk load and on every `SetBlock`
  edit; keep GPU data consistent with CPU edits. Decide CPU-upload vs a GPU pass deriving opacity from a
  block-id buffer.
- **Deliverable:** buffers exist, upload correctly, and a trivial compute dispatch round-trips data.
  Game looks identical (nothing samples light yet).

## Phase 4.1 — Fragment-shader light sampling (rendering path)

Move light out of the mesh and into the fragment shader.

- **Strip light from meshing:** remove the per-vertex light attribute and light from the greedy merge
  key (`GreedyMesher` merges on `BlockId` again). Vertices carry chunk-local position; the fragment
  derives its voxel coordinate from interpolated position + face normal.
- **Fragment samples the chunk light buffer**, offset to the **air-side** voxel by the normal, smoothed
  by filtering/averaging, **opacity-masked** so light does not bleed through walls. Cross-chunk borders
  resolved via a volume-wide light resource indexed by voxel coordinate (not 27 per-draw bindings).
- **Validate** by feeding a constant/full-bright (or a CPU-seeded test pattern) buffer: the world looks
  identical to today, proving the sampling path before any flood exists.
- **Deliverable:** meshes are lit by sampling GPU light buffers; no re-mesh occurs on a buffer change.

## Phase 4.2 — GPU BFS flood + block light (lamps)

The core flood, for block/dynamic light first (no shadow maps yet).

- **Compute flood:** global ping-pong storage buffers, one max-relaxation pass per dispatch, ~15 passes,
  opacity-blocked. Cross-chunk bounded to ~2 rounds (radius 15 < chunk 32).
- **Injection:** seed each `Lamp` voxel at its emission level into the block channel each cycle.
- **Cycle:** clear → inject → flood from scratch at 1–2 Hz; double-buffered handoff to the render read.
- **Deliverable:** lamps light their surroundings with correct intra-volume wall occlusion and smooth
  falloff; placing/removing a lamp or block updates lighting within ~1 cycle, with **no re-mesh**.

## Phase 4.3 — Sky & sun

Add the sky/sun channel as ambient exposure + direct shadow-mapped sun.

- **Ambient sky exposure:** flood from cells open to the sky (soft hemisphere fill) into the sky channel.
- **Direct sun shadow map:** render scene depth from the sun over the posed scene; inject direct sun
  into voxels that pass the depth test; flood. v1 may use one coarse map; note cascades for later.
- **Shader combine:** sky term = ambient ⊕ direct-sun, scaled by the existing N·L with an ambient floor;
  final = `max(skyTerm, blockTerm)`. Reconcile/retire any redundancy with the old N·L-only path.
- **Deliverable:** open surfaces bright, undersides/caves shaded, and a static-world overhang casts a
  real sun shadow.

## Phase 4.4 — Cross-grid & dynamic lights (unified)

Light crossing volume boundaries, using the *same* inject+flood path.

- Each grid floods its own local-space light volume. A light from another volume (a lamp on another
  grid, or the world-space sun) is depth-tested against that light's shadow map and injected into the
  **target** volume's buffer, then flooded locally.
- **All grids shade all grids** (naive, unbounded for now) and grids cast sun shadows on terrain.
- **Deliverable (milestone headline):** a lamp on a ship lights and shadows nearby terrain and a second
  ship; a ship casts a sun shadow on the island below; moving the ship moves the lit/shadowed regions.

## Phase 4.5 — Ambient occlusion

- AO via the fragment path: either a **separate baked AO channel** sampled like light, or **in-shader**
  from the opacity neighbourhood (or SSAO). Must use the same air-side, opacity-masked sampling.
- **Deliverable:** soft darkening in inner corners and block junctions, on terrain and grids alike.

## Phase 4.6 — Culling & change detection (recover steady-state cost)

Brings the naive "re-flood everything at 2 Hz" cost back down.

- Skip chunks/volumes with no light change and no nearby moved light (dirty tracking).
- **Radius-gate shadow maps** via the physics broadphase — a ship alone in open sky casts none.
- Bake-and-cache static-world and static-grid lighting; only re-flood disturbed regions.
- Optional: incremental injection/removal once full-recompute is no longer affordable.
- **Deliverable:** a static scene costs ~zero ongoing lighting; cost scales with change and with lights
  actually crossing boundaries. Verify via a debug counter (floods/shadow-maps per cycle).

---

## File-change summary

| Phase | New | Modified / Removed |
|-------|-----|--------------------|
| 4.0 | `Rendering/WebGpu/ComputePipeline.cs` (or extend `Renderer`), GPU buffer wrappers for storage/light/opacity | `Renderer.cs` (compute support, storage resources), `ChunkVolume`/`ChunkEntry` (GPU residency + upload-on-edit), `GpuContext` (compute features) |
| 4.1 | light-sampling WGSL | `GreedyMesher.cs` (drop light from merge key + vertex), `Vertex.cs` (drop light attribute, keep local pos), `Renderer.cs` (fragment light sampling, bind light buffer), `ChunkMeshSystem.cs` (no light sampler) |
| 4.2 | `Voxels/GpuLightFlood.cs` (flood compute), `ECS/GpuLightSystem.cs` (drives cycles) | remove CPU `LightEngine`/`LightData` from the path; `LightSystem.cs` replaced |
| 4.3 | `Rendering/WebGpu/SunShadowPass.cs` (depth-from-sun) | flood/inject extended for sky+sun; WGSL sky combine |
| 4.4 | `Rendering/WebGpu/LightShadowPass.cs` (per-light depth), cross-volume injection | `PhysicsWorld.cs` (broadphase queries for which volumes a light reaches) |
| 4.5 | — | AO channel/sampling in flood + WGSL |
| 4.6 | `ECS/LightCullingSystem.cs` (dirty/radius gate) | broadphase radius gate, static bake/cache |

---

## Risk register

| Risk | Mitigation |
|------|-----------|
| **Per-vertex light breaks greedy meshing** (interpolation artifact) | Sample light in the **fragment** shader from a voxel-indexed buffer; meshing stays light-independent (Phase 4.1) |
| **32³ exceeds WebGPU's 16 KB workgroup storage** | v1 flood uses **global** ping-pong, no shared memory; shared-memory tiling deferred and only on 16³ tiles |
| Filtering bleeds light through opaque walls | Opacity-mask samples / air-side offset by normal; pin the sampling convention before coding |
| Incremental light **removal** on GPU is hard | v1 is **clear → inject → flood from scratch**; no removal until 4.6 |
| Ping-pong vs checkerboard confusion | Double-buffer only (read A / write B / swap); **no** checkerboard |
| Shadowed-but-sky-open areas go black | Sky = **ambient exposure + direct sun**, not direct-only; keep N·L + ambient floor |
| Single sun map too low-res over a large world | Camera-focused frustum now; **cascaded shadow maps** later |
| GPU opacity/block data drifts from CPU edits | Upload on load + every `SetBlock`; assert consistency in a debug mode |
| Render samples a half-flooded buffer | Double-buffered/fenced handoff between the 1–2 Hz flood and the per-frame render |
| **Naive cost** (re-flood world + grids + sun map at 2 Hz) too high before culling exists | Validate the v1 budget explicitly; pull radius-gating forward from 4.6 if needed |
| Compute infrastructure is net-new and large | Phase 4.0 isolates it; round-trip a trivial dispatch before building lighting on top |

---

## Sequencing

4.0 → 4.1 → 4.2 → 4.3 → 4.4 → 4.5 → 4.6, in order. 4.0 (compute infra) and 4.1 (fragment sampling) are
prerequisites with no visible change. 4.2 delivers the first real lighting (lamps). 4.3 adds sky/sun
and the first sun shadows. 4.4 is the milestone headline (cross-grid + grid ground shadows). 4.5 adds
AO. 4.6 makes it affordable. Recommend committing after 4.1 (sampling path proven), after 4.3 (static
lighting + sun shadows), and after 4.4 (cross-grid).

Note: this is **more up-front work** than the CPU plan it replaces — the payoff is uniformity, real sun
shadows, free cross-grid light, and no re-mesh on light change. The first visible lighting (4.2) is
further out than the CPU path's was; that is the accepted cost of the re-architecture.
