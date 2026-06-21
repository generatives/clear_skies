# Clear Skies — Lighting System (as-built)

This document describes how the voxel lighting system **currently** works, derived from reading the
source. It describes the code as it is — not a design spec.

Lighting is **entirely GPU-computed**. There is no CPU light engine: sky and block light are both
derived each flood from the per-volume opacity bitset + block emission, by GPU compute shaders. (The
old CPU `LightSystem`/`LightEngine`/`LightData` BFS has been removed.)

Key source files:

| File | Role |
|------|------|
| `Voxels/ChunkData.cs` | Block storage for one 32³ chunk + index formula |
| `Voxels/ChunkEntry.cs` | Per-chunk bookkeeping: data, mesh, dirty flags |
| `Voxels/ChunkVolume.cs` | A set of chunks (world or grid); coordinate decompose; block get/set |
| `Voxels/StaticWorld.cs` | The streamed terrain volume; `Load`/`Unload` |
| `Voxels/VolumeGpuResources.cs` | Per-volume GPU buffers (opacity, LightA/B, dims); emission seed builder |
| `Voxels/GpuLightFlood.cs` | Region-scoped flood: −Y sky sweep (clear) + emitter scatter + relaxation compute shaders (WGSL) + dispatch |
| `Voxels/GreedyMesher.cs` | Light-independent greedy mesh; faces carry only position/normal/color |
| `ECS/ChunkLoadSystem.cs` | Streams chunks in/out around the camera (Logic) |
| `ECS/GpuResidencySystem.cs` | Allocates/grows GPU buffers, uploads opacity (PreRender) |
| `ECS/GpuLightSystem.cs` | Runs the GPU flood once per frame (PreRender) |
| `ECS/ChunkMeshSystem.cs` | Builds meshes (PreRender) |
| `ECS/RenderSystem.cs` | Issues draw calls; builds the light matrix + drives the shadow pass (Render) |
| `Rendering/WebGpu/SunShadowPass.cs` | Directional-sun shadow map: depth target + depth-only pipeline |
| `Rendering/WebGpu/Renderer.cs` | Render pipeline + the fragment shader that samples light |
| `Rendering/WebGpu/ComputePipeline.cs` | Compute pipeline wrapper + ping-pong/one-shot dispatch |

---

## 1. Data model

### Chunk
- `ChunkData.Size = 32`. A chunk is 32³ voxels.
- Voxel index within a chunk: `Index(x,y,z) = x + 32*(y + 32*z)` (X fastest, then Y, then Z).

### GPU light buffer (`VolumeGpuResources.LightA/LightB`)
- One **u32** per voxel: **bits 0–7 = sky** (0–`BaseSkyLevel`), **bits 8–15 = block** (0–15).
- This is the only per-voxel light store; it lives on the GPU. There is no CPU-side light array.

### `VolumeGpuResources.BaseSkyLevel = 10`
- **Ambient sky level** — soft fill light injected from *every* face of the volume (±X, ±Y, ±Z). It is
  not the sun: it is omnidirectional fill so islands are lit from all sides (top, bottom, sides). It
  carries through open air with no attenuation along each sweep direction; relaxation loses 1 per step
  into occluded pockets.
- Baked into both flood shaders as `BASE_SKY`. Samples **outside** the volume fall back to this value
  in the fragment shader (`AMBIENT_SKY`).
- **Direct sun is separate** — applied in the fragment shader at full brightness (`1.0` == level 15) as a
  Lambertian `N·L` term, gated by a **world-space sun shadow map** (`SunShadowPass`, §9). The renderer also
  clamps every fragment to `MIN_AMBIENT = 0.12` so geometry is never fully black.

### Per-chunk flags (`ChunkEntry`, all default **true** on creation)
- `NeedsRemesh` — geometry must be (re)meshed. Cleared by `ChunkMeshSystem`.
- `NeedsRecollide` — collider rebuild (not light related).
- `NeedsGpuUpload` — this chunk's opacity slice not yet uploaded to the GPU buffer. Cleared by
  `GpuResidencySystem` after `UpdateChunkOpacity`.
- `NeedsFlood` — the GPU light buffer is stale for this chunk. Cleared by `GpuLightSystem` after a
  flood **(only for chunks whose opacity is uploaded — see §7)**.

---

## 2. Coordinate systems & indexing (must stay consistent)

Three places index voxels; **all three use X-fastest, then Y, then Z**:

- **Chunk-local** (`ChunkData.Index`): `x + 32*(y + 32*z)`.
- **Volume-space** (light buffers `LightA/B` + emitter indices): `vi = vx + VW*(vy + VH*vz)` where
  `VW=DX*32`, `VH=DY*32` are volume width/height in voxels. A chunk at chunk-offset `(cx,cy,cz)` from the
  volume `Min` starts at voxel `(cx*32, cy*32, cz*32)`.
- **Flood shader** (`GpuLightFlood` WGSL `idx3`): `x + dims.w*(y + dims.h*z)` with `dims=[VW,VH,VD]`.
- **Fragment shader** (`Renderer` WGSL): `volAir.x + volSize.x*(volAir.y + volSize.y*volAir.z)`
  with `volSize=[VW,VH,VD]`.

These are consistent. **Opacity uses a separate chunk-major layout** (not volume-linear): bit for voxel
`(x,y,z)` lives at word `slot*1024 + (ly + 32*lz)`, bit `lx`, where `slot = cx + DX*(cy + DY*cz)` (see §6).

---

## 3. Per-frame execution order

`EngineHost` runs stages in this order. **`Input`, `Logic`, `PreRender` run in the window's Update
callback; `Render` runs in the separate Render callback** (so they may tick at different rates).
Systems run in **registration order** within a stage.

**Logic stage** (from `Program.cs`):
1. `FreeFlyCameraSystem`
2. **`ChunkLoadSystem`** — loads up to **4 chunks/frame** (`LoadsPerFrame=4`) closest-first around the
   camera; unloads distant ones. Radius `xz=3, y=2` → a load region of 7×5×7 = 245 chunks.
3. … colliders, grids, physics, debug, block interaction … (no lighting work in Logic anymore)

**PreRender stage**:
4. **`GpuResidencySystem`** — allocate/grow GPU buffers, upload opacity (see §6).
5. **`GpuLightSystem`** — GPU flood (see §7).
6. **`ChunkMeshSystem`** — build up to **2 meshes/frame** (`MeshesPerFrame=2`).

**Render stage**:
7. **`RenderSystem`** — one draw call per `MeshRenderer`.

> Note the budgets are very different: load 4/frame, opacity upload 8/frame, flood 1 volume/frame,
> mesh 2/frame. The mesh budget (2/frame) is the slowest stage.

---

## 4. Chunk lifecycle

1. `ChunkLoadSystem` calls `StaticWorld.Load(pos, generator)`:
   - generates `ChunkData`, then `AddChunk(pos, data)`.
2. `ChunkVolume.AddChunk`:
   - creates the ECS entity, builds a `ChunkEntry` (all flags **true**),
   - `UpdateBounds(pos)` (grows `BoundsMin/Max`),
   - `MarkNeighboursDirty(pos)` — sets `NeedsRemesh` on the 6 neighbours (face-cull refresh).
3. From here the three remaining flags (`NeedsGpuUpload`, `NeedsFlood`, `NeedsRemesh`) drain
   independently via their systems (see §8 state machine).

`Unload` disposes the mesh/entity, removes the chunk, and marks neighbours for remesh. It does **not**
shrink the GPU volume (see §6).

---

## 5. CPU light — removed

There is no CPU light engine anymore. `LightSystem`, `LightEngine`, and `LightData` were deleted; all
sky and block light is computed on the GPU each flood (§7) from the opacity bitset + block emission.
Runtime block edits no longer need any CPU relight: `ChunkVolume.SetBlock` just sets
`NeedsGpuUpload + NeedsFlood` (and `NeedsRemesh`/`NeedsRecollide`) on the chunk and flags neighbours,
which causes the next flood to recompute the affected region.

---

## 6. GPU residency — `GpuResidencySystem` + `VolumeGpuResources`

### `VolumeGpuResources` (one per volume)
Buffers (all `array<u32>` storage):
- **`Opacity`**: 1 bit/voxel (opaque = block opacity ≥ 15), **chunk-major**: one contiguous
  `WordsPerChunk = 1024`-word slice per chunk at slot `cx + DX*(cy + DY*cz)`; within a slice the word is
  `(ly + 32*lz)` and the bit is `lx`. This layout lets a single edited chunk upload as one contiguous
  4 KB write (see `UpdateChunkOpacity`) instead of re-uploading the whole bitset.
- **`LightA`, `LightB`**: 1 u32/voxel; bits 0–7 = sky, bits 8–15 = block. Volume-linear. Ping-pong pair.
- **`Emitters`** (sparse): per-volume list of `(volume-voxel-index, level)` u32 pairs, grown lazily
  (`EnsureEmitterCapacity`). Replaces a dense per-voxel emission buffer. The scatter pass seeds block light
  from this (see §7).
- **`Dims`**: `[VW, VH, VD, 0]`.
- `AmbientSky = BaseSkyLevel = 10`. On `Allocate`, `LightA` is pre-filled with `AmbientSky` so chunks
  look reasonable before the first flood. The first flood (everything dirty → whole-volume region)
  overwrites all of `LightA`; later region floods only overwrite their region, so this prefill also covers
  any never-flooded cells outside every region.

The volume is **windowed** around the loaded chunks, not grown without bound. `Covers(min,max)` tests
whether the current allocation still contains the loaded AABB; `Reallocate(min,max)` recreates all buffers
to exactly cover a new window (fresh + empty, all bind groups invalidated). Reallocation does **not** remesh:
`chunkBase`/`volSize` are derived live at draw time (see §9), so the unchanged geometry stays valid across a
realloc. The device is also created with the adapter's full limits (`GpuContext`) so a window-sized light
buffer stays under `maxStorageBufferBindingSize` (the default 128 MiB is otherwise exceeded by a modest
volume — the cause of the old far-travel crash).

There is no CPU light-seed build anymore. Block emission is seeded on the GPU each cycle by the scatter
pass from the sparse `Emitters` list; the sky channel is computed entirely on the GPU by the sweep +
relaxation (see §7). No CPU light (`LightData`) is read.

`UpdateChunkOpacity(pos, entry)` rebuilds the chunk's opacity slice **and** its emitter list
(`entry.Emitters`) from block data in one scan, then uploads the slice as a single contiguous 1024-word
write at the chunk's slot — only the edited chunk touches the GPU (no whole-buffer re-upload, no CPU
opacity shadow). `EnsureEmitterCapacity(n)` grows the per-volume emitter buffer as needed.

### `GpuResidencySystem.Update` (PreRender, runs before the flood)
Per volume (skips empty volumes):
- Compute the target window = loaded AABB (`TryGetLoadedBounds`) + `WindowMargin = 2` chunks.
- Create the volume to that window on first load; otherwise **re-window** (`Reallocate`) when the loaded set
  has moved outside the current allocation (`!Covers`) or the allocation is wastefully large
  (`cur > tgt * ShrinkFactor`, `ShrinkFactor = 3`). On a re-window, **every loaded chunk's opacity is
  re-uploaded immediately** (each is a cheap ~4 KB contiguous write) and all chunks marked `NeedsFlood` —
  not drained at 8/frame, so there's no long half-uploaded window during travel.
- For up to **`UploadsPerFrame = 8`** otherwise-dirty chunks (edits / newly streamed): `UpdateChunkOpacity`
  and clear `NeedsGpuUpload`.
- Create `RenderBindGroup` over `LightA` if not present.

> Re-windowing happens roughly every `WindowMargin` chunks of camera travel; each one re-uploads the
> window's opacity and triggers one full-window re-flood. That bounds memory and per-flood cost but makes
> continuous travel pay a periodic full-window flood — a toroidal/scrolling volume (upload only the entering
> slab, no realloc) is the follow-up that would remove it.

---

## 7. GPU flood — `GpuLightSystem` + `GpuLightFlood`

### `GpuLightSystem.Update` (PreRender, after residency)
- `FloodVolume(staticWorld)`, then dynamic grids; **at most one volume floods per frame** (returns
  after the first that floods).
- `FloodVolume` early-outs if `VolumeGpu==null` or `RenderBindGroup==0`.
- It computes the **dirty region** from chunks with **`NeedsFlood && !NeedsGpuUpload`**: their X/Z chunk
  footprint, expanded by a **one-chunk lateral margin** and clamped to the volume; **full height in Y**.
  If no such chunk exists, skip. (Full-Y because sky occlusion is a vertical-column effect — one new block
  shadows its whole column below — so Y can't be culled; the ≥32-voxel lateral margin exceeds the max
  propagation radius 15, keeping the relaxation's border reads correct.) When *everything* is dirty
  (first load / realloc) the footprint becomes the whole volume, recovering the naive full flood.
- `_flood.Flood(VolumeGpu, vol.All, region)`.
- **Flag clearing:** clear `NeedsFlood` only on chunks with **`!NeedsGpuUpload`** — every dirty-uploaded
  chunk lies inside the region footprint by construction, so all are covered. Chunks still awaiting their
  opacity upload keep `NeedsFlood` set so they trigger a corrective reflood once it lands.

### `GpuLightFlood.Flood` — region-scoped, four GPU stages
All passes are scoped to a `Region { ox,oy,oz, count, sx,sy,sz }` storage buffer (shared, rewritten per
flood; `count` is the emitter count for the scatter pass).
1. **Ambient sky sweep, clear mode** (`_skySweep`, `SweepWgsl`): the **−Y (top-down)** dispatch, over the
   region's XZ footprint (`workgroup_size(8,8,1)`, groups `(sx/8, sz/8)`). Each thread walks its **full**
   Y column (so occlusion above the region is accounted for) but **only writes inside the region**: in
   clear mode it **overwrites** each cell — `sky = cur`, **block channel zeroed**. This reset is what makes
   removal correct (a deleted lamp / new occluder), since max-relaxation can never lower a value. `isOpaque`
   reads the chunk-major opacity buffer. (Only the top sweep is enabled, matching prior behaviour; if more
   directions are added, only the first should use clear mode.)
2. **Emitter scatter** (`_scatter`, `ScatterWgsl`): one thread per in-region emitter (`workgroup_size(64)`,
   `ceil(count/64)` groups) writes its emission into the block channel, **preserving the swept sky**. The
   scatter list is gathered each cycle from the `entry.Emitters` of chunks overlapping the region.
3. **LightA → LightB full copy** (`CopyBufferToBuffer`): makes both ping-pong buffers agree *outside* the
   region, so the region reads correct, stable border values on every relaxation pass regardless of which
   buffer is the source.
4. **Relaxation flood** (`_pipeline`, `FloodWgsl`): **`Passes = 16`** ping-pong passes over the region
   (`(sx/4, sy/4, sz/4)` workgroups of 4³, `global_invocation_id` offset by the region origin). Even count
   → result lands in `LightA`. `FloodBindEven`: src=`LightA`, dst=`LightB`; `FloodBindOdd`: swapped.

Each stage is a separate queue submit, so WebGPU barriers between them (sweep → scatter → copy →
relaxation, in order).

### Relaxation shader (WGSL), per voxel
- Out-of-volume invocation → return.
- **Opaque voxel** → `dst = blk<<8` (sky forced to 0 inside solids; emission retained); return.
- **Air voxel** — uniform max-relaxation, both channels lose 1 per step in all 6 directions:
  - **Block:** `b = max(blk, maxNeighbourBlock - 1)`.
  - **Sky:** `s = max(sky, maxNeighbourSky - 1)` (no special-cased direction — the sweep already did
    the unattenuated straight-line injection; relaxation only bleeds ambient into pockets).
  - Write `dst = s | (b<<8)`.

> Effect: the −Y sweep does the cheap unattenuated straight-line (top-down) injection on the GPU (the
> relaxation flood alone would need ~volume-extent passes to fill; 16 only reach 16 voxels). The
> relaxation then bleeds ambient into occluded pockets and propagates block light, converging across chunk
> boundaries within the region. Both channels are derived purely from the chunk-major opacity bitset +
> the sparse emitter scatter — **no CPU light is consumed** (see §5).
>
> Sky's max relaxation reach is `BASE_SKY = 10` and block reach is 15, so 16 passes fully converge within
> the region. The flood is **not** stateless across cycles: cells *outside* the region keep their previous
> result and serve as the boundary condition. The region (dirty footprint + 1-chunk margin, full Y) is
> sized so every cell the change can influence lies inside it and the border cells are beyond the
> propagation radius, so the region recomputes correctly from those stable borders.

---

## 8. Flag state machine (what advances a chunk to "correctly lit")

A freshly loaded chunk has `Upload=Flood=Remesh = true`. To become correctly lit and visible:

```
load (AddChunk)            → Upload=Flood=Remesh true
GpuResidencySystem (Pre)   → UpdateChunkOpacity → NeedsGpuUpload=false  [<=8/frame; ALL at once on re-window]
GpuLightSystem (Pre)       → Flood → NeedsFlood=false (only if !Upload)  [1 volume/frame]
ChunkMeshSystem (Pre)      → mesh → NeedsRemesh=false                   [<=2/frame]
RenderSystem (Render)      → draws MeshRenderer, samples LightA
```

Important interactions:
- The flood runs over the **dirty region** (dirty chunk footprint + 1-chunk margin, full Y), not the whole
  volume. A not-yet-uploaded chunk in the margin still reads as air (opacity 0) → sky can flood through it
  for that cycle; the flag-clear rule keeps such chunks `NeedsFlood` so a later flood corrects them once
  their opacity lands and the region expands to include them.
- A chunk only becomes visible once `ChunkMeshSystem` builds its mesh (2/frame). Light updates after
  that are picked up automatically because the **fragment shader samples `LightA` live** — no remesh
  needed for relight.

---

## 9. Meshing & rendering — how light reaches the screen

### Mesh (`GreedyMesher`)
- Greedy quads merged on **block id only**; lighting is **not** baked into vertices.
- Each vertex carries: position (chunk-local `[0,32]`), face normal, color.
- `ChunkMeshSystem` hands the mesh to the volume; the `MeshRenderer` stores the chunk's `ChunkPos` and a
  reference to the (stable, reused-across-reallocs) `VolumeGpu` object.
- **`RenderSystem` derives `chunkBase = VolumeGpu.ChunkVoxelBase(ChunkPos)` and `volSize = VW/VH/VD`
  live each draw.** This is correct across volume reallocations without any remesh.
  - Fallback for non-chunk meshes / no `VolumeGpu`: base `(0,0,0)`, size `(32,32,32)`.

### Fragment shader (`Renderer` WGSL)
- `localPos` is the interpolated chunk-local vertex position; `localNormal` the face normal.
- `sampleLight`: `localAir = floor(localPos + 0.5*localNormal)` → steps half a voxel along the normal
  into the **adjacent air cell**; `volAir = chunkBase + localAir`.
- If `volAir` is **outside** `[0,volSize)` on any axis → return **`AMBIENT_SKY = 10/15`** (this is the
  flat fallback, e.g. at the bottom/edge of the allocated volume, or for non-chunk draws).
- Else sample `light[idx]`: `sky = (packed & 0xFF)/15` (ambient), `blk = ((packed>>8)&0xFF)/15`.
- Direct sun: `directSun = max(dot(N, -sunDir), 0) * sunShadow(...)` (full brightness `1.0`, hard shadow
  test — see below). `skyTerm = max(sky, directSun)`. Final: `lit = max(skyTerm, blk, MIN_AMBIENT)`;
  output `color * lit`.

> Consequence: a face on a chunk edge samples the **neighbour chunk's** voxel. If that neighbour is
> loaded-but-uninitialised or freshly-reallocated (opacity not re-uploaded), its buffer value is `0`
> → that strip of the face renders **black**. If the sample is outside the volume entirely it renders
> at flat `AMBIENT_SKY` instead.

### Sun shadow map (`SunShadowPass`)
- **Goal:** voxel-resolution hard shadows for the direct sun, handling rotating dynamic grids for free
  (a world-space shadow map, not light injection into the per-grid volume).
- **Light matrix** (`RenderSystem.BuildLightViewProj`): an orthographic box of half-extent `radius = 160`
  centred on the camera, looking along `sunDir`. `lightViewProj = ortho · lookAt(cameraPos − sunDir·radius,
  cameraPos)`. Stored in the camera uniform (`CameraUniform.LightViewProj`, now 208 B).
- **Depth pass:** each frame, before the main pass, `SunShadowPass` renders **all** `MeshRenderer` casters
  depth-only from the sun's POV into a fixed **2048² Depth32Float** texture (`~6 texels/voxel`). Vertex-only
  pipeline (`Fragment = null`); reuses the renderer's dynamic-offset model buffer (same slots, overwritten
  for the main pass — safe because the shadow pass is submitted first). Slope-scaled depth bias kills acne.
- **Test (fragment shader `sunShadow`):** projects the **air-cell centre** (`floor(localPos+0.5·N)+0.5`,
  in world space) through `lightViewProj`, converts to UV + `[0,1]` depth, and `textureLoad`s the shadow
  map (point sample, no sampler → blocky edges). `ndc.z > stored + bias` → shadowed. Testing the voxel
  centre (not the interpolated fragment) makes the whole face share one result → **voxel-resolution**
  shadows. Out-of-frustum → treated as lit.
- Bound as **group 3** (`texture_depth_2d`) in the main pipeline.

---

## 10. Known remaining quirks

The loading-time banding and the stable black patches are **fixed** (live `chunkBase`/`volSize` at draw
time + GPU-derived sky from opacity). These are the remaining rough edges worth knowing:

1. **Opacity-leak window on streaming.** A newly streamed chunk is uploaded at only `UploadsPerFrame = 8`
   chunks/frame, so a flood can briefly treat a not-yet-uploaded solid as air → sky leaks through (e.g. an
   island underside flashes lit before settling). The `NeedsFlood`-kept-until-uploaded rule (§7) guarantees
   a corrective reflood once each chunk's opacity lands, so it self-heals; it's a transient, not a stable
   artifact. (A volume **re-window** does *not* drain at 8/frame — it re-uploads all loaded opacity at once,
   see §6 — so re-windows don't reopen this window.)

2. **Re-window cost during travel.** The volume is now windowed around the camera (bounded memory + cost),
   but continuous travel re-windows roughly every `WindowMargin` chunks, each paying one full-window opacity
   re-upload + re-flood. A toroidal/scrolling volume (shift the origin, upload only the entering slab, never
   realloc) would remove that periodic cost — the natural follow-up.

3. **`mesh budget = 2/frame` is the slowest stage.** Chunks become visible more slowly than they light
   up, so during heavy loading the visible set is a patchwork of differently-staged chunks. Lighting
   itself is correct as soon as a chunk's opacity is uploaded and a flood runs.

4. **Edge faces sample neighbour chunks.** A face on a chunk boundary samples a voxel in the adjacent
   chunk; out-of-volume samples fall back to flat `AMBIENT_SKY`. With GPU sky now derived from opacity,
   not-yet-uploaded neighbours read as lit air (not black), so this no longer produces black seams — but
   it's the mechanism to remember if boundary shading ever looks off.

---

## 11. Quick glossary of the budgets/constants

| Constant | Value | Where |
|----------|-------|-------|
| `ChunkData.Size` | 32 | chunk edge length |
| `BaseSkyLevel` | 10 | ambient sky fill level, injected from all 6 faces (`VolumeGpuResources`) |
| `MIN_AMBIENT` | 0.12 | minimum lit floor in the fragment shader (`Renderer`) |
| `LoadsPerFrame` | 4 | `ChunkLoadSystem` |
| `xzRadius / yRadius` | 3 / 2 | load region (7×5×7) |
| `UploadsPerFrame` | 8 | `GpuResidencySystem` opacity uploads (edits/streaming) |
| `WindowMargin` | 2 | chunk padding around the loaded set when windowing (`GpuResidencySystem`) |
| `ShrinkFactor` | 3 | re-window-to-shrink threshold (`GpuResidencySystem`) |
| `Passes` | 16 | `GpuLightFlood` relaxation ping-pong passes |
| `WordsPerChunk` | 1024 | chunk-major opacity slice size (`VolumeGpuResources`) |
| ambient sky sweep | 1 dispatch (−Y) | `GpuLightFlood` (`workgroup 8×8×1`, region XZ footprint, clear mode) |
| flood scope | dirty footprint + 1-chunk margin, full Y | `GpuLightSystem.FloodVolume` |
| flood/frame | 1 volume | `GpuLightSystem` |
| `MeshesPerFrame` | 2 | `ChunkMeshSystem` |
