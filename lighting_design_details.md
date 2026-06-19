# Clear Skies — Lighting System Design (GPU)

**This document is the authoritative lighting design.** It supersedes the three-tier CPU design in
`lighting_design.md` (kept for history). The pivot: instead of special-casing static world / grid
interior / cross-grid with three different CPU mechanisms, run **one uniform GPU pipeline** —
inject + flood — for every light source and every volume.

---

## Why pivot

The CPU tiered design's crown jewel was *motion invariance*: a grid lit itself in its local frame, so
flying was free. But the third tier (cross-grid light) is inherently dynamic, world-space work, and
forcing a bake-into-mesh flood-fill to do it required a large, fragile shadow-map-injection-into-BFS
machine. In practice the tiered approach also fought greedy meshing (light baked per-vertex spreads a
dark corner across a merged quad) and forced a re-mesh on every light change.

The GPU approach is **more total work up front** (it needs compute infrastructure, GPU-resident voxel
data, and shadow mapping) but yields a *uniform* system: sun and lamps use the same path, every grid
can shade every other grid, real sun shadows are included (the old design deferred them), and **meshes
never re-bake when light changes**. We deliberately trade global motion-invariance for uniformity, and
recover steady-state cost later with culling/change-detection (see "What we trade away").

---

## Core model

The vast majority of lighting runs on the GPU: **shadow-map rendering, light injection, and BFS flood
spreading are all GPU passes.** The system runs identically for the sun and for any scene light
(lamp/torch). All voxel grids — the static world and every dynamic grid — can shade all others.

Each loaded chunk (static or grid) holds GPU-resident data:
- a **light buffer** with two channels: **sky/sun** and **block/dynamic** (0–15 each; RGB is a later
  per-channel extension);
- an **opacity buffer** (derived from block data) that the BFS reads to block propagation.

Light is **sampled in the fragment shader** from these buffers — never baked into vertices.

---

## Critical decision: light is sampled in the FRAGMENT shader, not the vertex shader

Sampling light per-vertex is incompatible with greedy meshing. A greedy-merged quad has four vertices
spanning many blocks; computing light at the corners makes the GPU linearly interpolate it across the
whole quad — the exact "a dark corner spreads across a 20-block quad" artifact. The mesh generator
computes light *per face-cell*, which cannot survive being moved to the vertex stage of a merged quad.

Instead, the **fragment shader** looks up the light of the voxel its fragment belongs to, sampling the
chunk light buffer by voxel coordinate. Greedy meshing then stays valid — each fragment looks up its
own voxel independently.

**Consequences (bank these):**
- **Meshing becomes light-independent again.** Light leaves the vertex format and the greedy merge key
  entirely. No re-mesh on light change — the whole point of the pivot.
- **Smooth lighting returns for free.** Per-corner averaging (dropped earlier because it fought greedy
  merge) is recovered via filtering/averaging of the light buffer in the fragment shader.

**Caveats this path introduces (must be handled):**
- The fragment must sample the **air-side** voxel (offset by the face normal), not the solid it is
  drawn on (whose light is 0).
- Filtering/averaging **bleeds across opaque walls** (a lit cell next to a wall blends through it).
  Mask samples by opacity, or accept/mitigate. This is the known hard part of texture lighting.
- WebGPU distinguishes a **storage** resource (compute writes) from a **sampled** texture with
  hardware filtering. Plan to flood into a storage buffer and either copy to a sampled 3D texture each
  cycle or do manual averaging in the fragment shader. Pick one explicitly.

The fragment needs the voxel coordinate: vertices already carry chunk-local position, so the fragment
derives the voxel index from interpolated local position + face normal. It also needs access to the
**current chunk and its neighbours'** light (a face cell at a chunk border averages across the seam) —
addressed via a single volume-wide light resource indexed by voxel coordinate, not per-draw binding of
27 neighbour buffers.

---

## Update pipeline (per cycle)

Run the whole thing on a fixed cadence (v1: **1–2 Hz**, process everything):

1. **Depth maps.** Render a shadow/depth map for each active light source (sun, lamps) against the
   real posed scene geometry in range. Real geometry → occlusion is rotation- and cross-grid-exact.
2. **Injection.** For each affected chunk's light buffer, seed source light: for each candidate voxel,
   depth-test against the light's shadow map and, if visible, write `attenuation(distance)` into the
   light buffer.
3. **Flood (BFS).** Spread the injected seeds through the voxel volume on the GPU (compute shader,
   below), blocked by opacity.

**v1 is clear → inject → flood from scratch each cycle.** This deliberately avoids incremental light
*removal* — the genuinely hard BFS case on GPU (a lamp moving off, a block being placed). Full
recompute sidesteps it entirely. Incremental updates come only later, with change detection.

---

## GPU BFS compute shader

Light propagation is iterative max-relaxation: `new = max(self, max(opacity-passing neighbours) − 1)`.
This is order-independent and idempotent, so it is robust on the GPU regardless of scheduling.

**v1 — naive, correct, no shared memory:**
- **Global ping-pong storage buffers.** Read buffer A, write buffer B, swap. Double-buffering removes
  all read/write hazards — so there is **no need for a checkerboard / "every other block" pattern**
  (checkerboard is the *alternative* to ping-pong for in-place updates; do not use both).
- **One relaxation pass per dispatch**, ~15 passes (max light radius). No internal workgroup loop, no
  workgroup barriers, no convergence reasoning.
- **Cross-chunk spread is bounded to ~2 dispatch rounds.** Light radius is 15 and chunks are 32, so any
  light crosses **at most one** chunk boundary. A lamp at a chunk edge touches only the immediate
  neighbour — "invoke again for cross-chunk" is ~2 rounds, not O(volume diameter). This bound is
  load-bearing for the cost argument.

**Why not the shared-memory single-dispatch scheme (yet):** loading a 32³ chunk into workgroup shared
memory is impossible under WebGPU's guaranteed **16 KB** workgroup-storage limit — 32³ × 1 byte = 32 KB
before the neighbour halo (34³ ≈ 38 KB) or opacity. That optimization (load tile + loop internally with
barriers) is viable only on **16³ tiles** (4 KB) and is deferred to the optimization phase. v1 operates
directly on global memory.

---

## Sky and sun are two contributions, not one

Treating the sun as just another inject-and-flood light gives sharp **direct** sun with occlusion
(what the old design deferred — good). But it does **not** give ambient sky: a cave mouth or an
overhang's underside is dimly lit even where no direct ray reaches. If skylight came *only* from
sun-shadow-map injection + flood, every shadowed-but-sky-open area would be near-black except for
attenuated flood spill.

So the **sky/sun channel carries two contributions**:
- **Ambient sky exposure** (soft) — flood from cells open to the sky; the diffuse hemisphere fill.
- **Direct sun** (sharp) — shadow-map injected where the sun is directly visible.

Reconcile with the existing per-fragment **N·L** term: keep N·L as the cheap directional brightening of
the ambient/direct sky term; the shadow map adds true occlusion on top. Keep an **ambient floor** so
faces turned from the sun aren't pure black. A single sun shadow map over a floating-island world will
be low-resolution everywhere — **cascaded shadow maps** (or a camera-focused frustum) are the eventual
answer; v1 may use one coarse map.

---

## Cross-grid and dynamic lights (unified, no special tier)

Each grid owns its own local-space light volume. A light from another volume (a lamp on ship B, or the
world-space sun) is injected into the *target* volume's light buffer after a shadow-map depth test,
then flooded locally. This is the **same** inject+flood mechanism used everywhere — there is no separate
cross-grid algorithm. "All grids shade all grids" is the default; bounding it is an optimization, not a
correctness requirement.

---

## Infrastructure this requires (currently absent)

- **Compute pipeline support in the renderer.** `Renderer.cs` is render-only today — no compute
  pipelines, storage buffers/textures, compute bind groups, or dispatch. All of this is a prerequisite.
- **GPU-resident voxel data + sync.** The BFS needs an opacity buffer (and the injection needs block
  identity for emitters) resident on GPU for every loaded chunk and grid, kept in sync with CPU
  `SetBlock` edits. Define who writes it and when (upload on load/edit, or a GPU pass deriving opacity
  from a block-id buffer).
- **Memory budget.** A persistent per-chunk light buffer (sky+block ≈ 2 B/voxel = 64 KB/chunk) plus
  opacity, across hundreds of loaded chunks and every grid, is tens of MB. Acceptable on desktop;
  budget it. Each grid needs its own local-space volume.
- **Double-buffered handoff.** The BFS writes (1–2 Hz) the same buffers the fragment shader samples
  every frame; use a double-buffered or fenced handoff so a frame never samples a half-flooded buffer.
- **Fixed iteration budget.** ≥15 flood passes per cycle for full radius; define it.

---

## Ambient occlusion

AO is no longer baked per-vertex. In the fragment-sampling model it comes from either a **separate
baked AO channel** (sampled like light) or **in-shader** from the opacity neighbourhood (or SSAO).
Decide during the AO phase; it must use the same air-side, opacity-masked sampling as light.

---

## What we trade away (and how we get it back)

This abandons **global motion-invariance**: v1 re-floods the entire loaded world + every grid + the sun
shadow map at the fixed cadence regardless of whether anything moved. That is acceptable *only* as a
naive first cut. The deferred **culling / change-detection** work is therefore not a nice-to-have — it
is what brings steady-state cost back down to where the tiered design started:
- skip chunks/volumes with no light change and no nearby moved light;
- radius-gate shadow maps (a ship alone in open sky casts none) via the physics broadphase;
- bake-and-cache static-world and static-grid lighting; only re-flood disturbed regions.

**Validate the v1 budget survives "re-flood everything at 2 Hz" before culling exists** — if it doesn't,
move some culling forward.

---

## Reused vs replaced from the earlier CPU lighting work

| Item | Fate |
|---|---|
| `BlockDef.LightEmission` / `Opacity` | **Reused** — drive GPU injection and the opacity buffer. |
| `BlockId.Lamp` glowing block | **Reused** as the test emitter. |
| Chunk / `ChunkVolume` / `DynamicGrid` structure | **Reused** as the GPU data substrate (per-chunk buffers). |
| Per-face air-side sampling *convention* | **Reused** — now done in the fragment shader instead of the mesher. |
| CPU `LightData` per-voxel store | **Replaced** by GPU light buffers (CPU copy only if needed for gameplay queries). |
| CPU `LightEngine` BFS + incremental remove/relight | **Replaced** by the GPU flood (full recompute in v1). |
| `LightSystem` (CPU per-frame init/relight) | **Replaced** by a GPU lighting system driving the compute/shadow passes. |
| Per-vertex light attribute in `Vertex` | **Removed** — fragment samples the light buffer instead. |
| Light in the greedy-mesh merge key | **Removed** — meshing is light-independent again. |
| Per-fragment N·L sun term + sun-direction uniform | **Reused** — combined with the new sun shadow map. |
