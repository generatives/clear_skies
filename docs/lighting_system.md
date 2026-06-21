# Clear Skies — Lighting System (as-built)

This document describes how the voxel lighting system **currently** works, derived from reading the
source. It is a reference for debugging the loading-time banding and the large stable black patches.
It describes the code as it is, including suspected problem areas — it is **not** a design spec.

Key source files:

| File | Role |
|------|------|
| `Voxels/ChunkData.cs` | Block storage for one 32³ chunk + index formula |
| `Voxels/LightData.cs` | Per-voxel CPU light storage (1 byte: sky hi-nibble, block lo-nibble) |
| `Voxels/ChunkEntry.cs` | Per-chunk bookkeeping: data, light, mesh, dirty flags |
| `Voxels/ChunkVolume.cs` | A set of chunks (world or grid); coordinate decompose; light get/set |
| `Voxels/StaticWorld.cs` | The streamed terrain volume; `Load`/`Unload` |
| `Voxels/LightEngine.cs` | CPU BFS light engine (sky column-pass + BFS, block BFS) |
| `Voxels/VolumeGpuResources.cs` | Per-volume GPU buffers (opacity, LightA/B, dims); seed builder |
| `Voxels/GpuLightFlood.cs` | The flood compute shader (WGSL) + dispatch |
| `Voxels/GreedyMesher.cs` | Light-independent greedy mesh; faces carry only position/normal/color |
| `ECS/ChunkLoadSystem.cs` | Streams chunks in/out around the camera (Logic) |
| `ECS/LightSystem.cs` | Drives CPU light init + incremental relight (Logic) |
| `ECS/GpuResidencySystem.cs` | Allocates/grows GPU buffers, uploads opacity (PreRender) |
| `ECS/GpuLightSystem.cs` | Runs the GPU flood once per frame (PreRender) |
| `ECS/ChunkMeshSystem.cs` | Builds meshes (PreRender) |
| `ECS/RenderSystem.cs` | Issues draw calls (Render) |
| `Rendering/WebGpu/Renderer.cs` | Render pipeline + the fragment shader that samples light |
| `Rendering/WebGpu/ComputePipeline.cs` | Compute pipeline wrapper + ping-pong dispatch |

---

## 1. Data model

### Chunk
- `ChunkData.Size = 32`. A chunk is 32³ voxels.
- Voxel index within a chunk: `Index(x,y,z) = x + 32*(y + 32*z)` (X fastest, then Y, then Z).

### CPU light (`LightData`)
- One **byte** per voxel: **sky = high nibble**, **block = low nibble** (each 0–15).
- `GetSky/SetSky`, `GetBlock/SetBlock` mask the nibbles.
- Note the storage allows 0–15, but the sky engine never exceeds `BaseSkyLevel`.

### `LightEngine.BaseSkyLevel = 10`
- This is "full sun". Maximum sky value the engine produces. Lower than 15 to dim the world globally
  without changing attenuation behaviour.
- `ChunkVolume.GetSkyLight` returns **`BaseSkyLevel` (10)** for an **unloaded** chunk (open-sky
  assumption). `GetBlockLight` returns **0** for an unloaded chunk.

### Per-chunk flags (`ChunkEntry`, all default **true** on creation)
- `NeedsRemesh` — geometry must be (re)meshed. Cleared by `ChunkMeshSystem`.
- `NeedsRecollide` — collider rebuild (not light related).
- `NeedsRelight` — CPU light not yet initialised. Cleared by `LightSystem` after `InitializeChunk`.
- `NeedsGpuUpload` — this chunk's opacity slice not yet uploaded to the GPU buffer. Cleared by
  `GpuResidencySystem` after `UpdateChunkOpacity`.
- `NeedsFlood` — the GPU light buffer is stale for this chunk. Cleared by `GpuLightSystem` after a
  flood **(only for chunks that were fully ready — see §7)**.

---

## 2. Coordinate systems & indexing (must stay consistent)

Three places index voxels; **all three use X-fastest, then Y, then Z**:

- **Chunk-local** (`ChunkData.Index`): `x + 32*(y + 32*z)`.
- **Volume-space** (`VolumeGpuResources`, `BuildLightSeed`, `UpdateChunkOpacity`):
  `vi = vx + VW*(vy + VH*vz)` where `VW=DX*32`, `VH=DY*32` are volume width/height in voxels.
  A chunk at chunk-offset `(cx,cy,cz)` from the volume `Min` starts at voxel `(cx*32, cy*32, cz*32)`.
- **Flood shader** (`GpuLightFlood` WGSL `idx3`): `x + dims.w*(y + dims.h*z)` with `dims=[VW,VH,VD]`.
- **Fragment shader** (`Renderer` WGSL): `volAir.x + volSize.x*(volAir.y + volSize.y*volAir.z)`
  with `volSize=[VW,VH,VD]`.

These are consistent. Opacity is a separate bitset: `vi`-th bit lives in `opacity[vi>>5]` bit `vi&31`.

---

## 3. Per-frame execution order

`EngineHost` runs stages in this order. **`Input`, `Logic`, `PreRender` run in the window's Update
callback; `Render` runs in the separate Render callback** (so they may tick at different rates).
Systems run in **registration order** within a stage.

**Logic stage** (from `Program.cs`):
1. `FreeFlyCameraSystem`
2. **`ChunkLoadSystem`** — loads up to **4 chunks/frame** (`LoadsPerFrame=4`) closest-first around the
   camera; unloads distant ones. Radius `xz=3, y=2` → a load region of 7×5×7 = 245 chunks.
3. … colliders, grids, physics, debug, block interaction …
4. **`LightSystem`** — CPU light (see §5). Runs *after* loading in the same frame.

**PreRender stage**:
5. **`GpuResidencySystem`** — allocate/grow GPU buffers, upload opacity (see §6).
6. **`GpuLightSystem`** — GPU flood (see §7).
7. **`ChunkMeshSystem`** — build up to **2 meshes/frame** (`MeshesPerFrame=2`).

**Render stage**:
8. **`RenderSystem`** — one draw call per `MeshRenderer`.

> Note the budgets are very different: load 4/frame, CPU light init 16/frame, opacity upload 8/frame,
> flood 1 volume/frame, mesh 2/frame. The mesh budget (2/frame) is the slowest stage.

---

## 4. Chunk lifecycle

1. `ChunkLoadSystem` calls `StaticWorld.Load(pos, generator)`:
   - generates `ChunkData`, then `AddChunk(pos, data)`.
2. `ChunkVolume.AddChunk`:
   - creates the ECS entity, builds a `ChunkEntry` (all flags **true**),
   - `UpdateBounds(pos)` (grows `BoundsMin/Max`),
   - `MarkNeighboursDirty(pos)` — sets `NeedsRemesh` on the 6 neighbours (face-cull refresh).
3. From here the four flags drain independently via their systems (see §8 state machine).

`Unload` disposes the mesh/entity, removes the chunk, and marks neighbours for remesh. It does **not**
shrink the GPU volume (see §6).

---

## 5. CPU light — `LightSystem` + `LightEngine`

### `LightSystem.Update` (Logic)
For each registered volume:
- Iterate `AllByDescendingY` (highest chunk Y first).
- For each chunk with `NeedsRelight`:
  - **Wait-for-above guard:** if the chunk directly above is *loaded but still `NeedsRelight`*, **skip**
    this chunk this frame (so a chunk is always initialised after the one above it). If above is absent
    (null/unloaded) the column is treated as open sky.
  - `LightEngine.InitializeChunk(vol, pos)`; clear `NeedsRelight`.
  - Budget: up to **`InitPerFrame = 16`** inits/frame.
- Then `LightEngine.ProcessEdits(vol, RelightPerFrame=16)` drains the volume's `RelightQueue`
  (incremental relight around block edits).

> The previous "invalidate-below cascade" was removed; cross-boundary convergence is now delegated to
> the GPU flood (§7).

### `LightEngine.InitializeChunk` (per chunk)
Sky:
- **Top-down column pass:** for each `(lx,lz)` column, start `skyLevel` from the chunk **above**
  (`above.Light.GetSky(lx,0,lz)`), or `BaseSkyLevel` if above is null/`NeedsRelight`. Walk down:
  - opaque voxel → `skyLevel = 0`;
  - else `newSky = (skyLevel==BaseSkyLevel) ? BaseSkyLevel : max(0, skyLevel-1)` — i.e. **full sun
    does not attenuate going straight down**, anything less loses 1 per step. If `newSky>0`, write it
    and enqueue for BFS.
- **Horizontal boundary seeds:** for each of the 4 side neighbours **that is loaded**, enqueue the
  neighbour's boundary plane (`TryEnqueueSky` reads the real value). Unloaded sides are skipped (to
  avoid injecting the `GetSkyLight`-returns-10 open-sky assumption into shadowed zones). **Bottom seeds
  are intentionally omitted** (sun is top-down).
- `PropagateSky`: BFS; loses 1 per step except full-sun straight down; only writes into loaded chunks
  (`SetSkyLight` returns false otherwise). Writing sets the target chunk's `NeedsFlood`.

Block:
- Seed every emitter voxel in the chunk; add boundary seeds from all 6 neighbours
  (`AddBlockBoundarySeeds`); `PropagateBlock` (loses 1 per step; only into loaded chunks — this guard
  is what prevents the earlier exponential fan-out / freeze).

At the end `InitializeChunk` sets `entry.NeedsRemesh = true`.
> ⚠️ Meshing is light-independent (see §9), so this remesh is not required for correctness. It is
> currently harmless only because nothing re-invokes `InitializeChunk` after the first pass.

### `ProcessEdits` / `RelightRegion`
For each queued edit position, clears both light channels in a `(2R+1)³` region (`R=15`), re-bakes sky
columns from above the region, re-seeds from the 4 walls, re-seeds emitters and the 6 faces, and
re-propagates. Used for runtime block placement/removal, not initial load.

---

## 6. GPU residency — `GpuResidencySystem` + `VolumeGpuResources`

### `VolumeGpuResources` (one per volume)
Buffers (all `array<u32>` storage):
- **`Opacity`**: 1 bit/voxel (opaque = block opacity ≥ 15), packed 32 voxels/u32.
- **`LightA`, `LightB`**: 1 u32/voxel; bits 0–7 = sky, bits 8–15 = block. Ping-pong pair.
- **`Dims`**: `[VW, VH, VD, 0]`.
- `AmbientSky = BaseSkyLevel = 10`. On `Allocate`, `LightA` is pre-filled with `AmbientSky` so chunks
  look reasonable before the first flood — **but `BuildLightSeed` overwrites all of `LightA` each
  flood** (see below), so this prefill only matters for the gap before the first flood.

`EnsureContains(newMin,newMax)` **grows only** (never shrinks). On growth it reallocates and the caller
(`GpuResidencySystem`) marks **all** chunks `NeedsGpuUpload + NeedsFlood` (the opacity/light buffers are
fresh and empty). It does **not** remesh: `chunkBase`/`volSize` are derived live at draw time (see §9), so
the unchanged geometry stays valid across a reallocation.

`BuildLightSeed(chunks, buf)`:
- `Array.Clear(buf)` → **everything starts at 0**.
- For each chunk that is `Contains`-ed **and not `NeedsRelight`**: write `sky | (emission<<8)` per voxel
  from its `LightData`/`ChunkData`.
- ⚠️ Chunks that are loaded but still `NeedsRelight`, and any allocated-but-unloaded region, are left
  at **0** in the seed (i.e. fully dark sky + no emission).

`UpdateChunkOpacity(pos, data)` writes the chunk's opaque bits into the CPU `_opacityShadow`; sets
`OpacityDirty`. `UploadOpacityIfDirty` uploads the whole shadow once and clears `OpacityDirty`.

### `GpuResidencySystem.Update` (PreRender, runs before the flood)
Per volume (skips empty volumes):
- `EnsureVolumeExists` (create on first load).
- `EnsureContains(BoundsMin, BoundsMax)`; on growth, mark all chunks dirty + remesh.
- Upload opacity for up to **`UploadsPerFrame = 8`** dirty chunks; clear their `NeedsGpuUpload`; then
  `UploadOpacityIfDirty`.
- Create `RenderBindGroup` over `LightA` if not present.

> Because opacity upload is budgeted at 8/frame but loads are 4/frame, opacity usually keeps up — but
> on the frame a volume **grows/reallocates**, *all* chunks get `NeedsGpuUpload=true` at once and only 8
> are re-uploaded per frame. Until a chunk is re-uploaded its slice in the opacity buffer is **0 (air)**.

---

## 7. GPU flood — `GpuLightSystem` + `GpuLightFlood`

### `GpuLightSystem.Update` (PreRender, after residency)
- `FloodVolume(staticWorld)`, then dynamic grids; **at most one volume floods per frame** (returns
  after the first that floods).
- `FloodVolume` early-outs if: `VolumeGpu==null`, `OpacityDirty` (upload still pending this frame), or
  `RenderBindGroup==0`.
- `anyDirty` = any chunk with **`NeedsFlood && !NeedsGpuUpload`**. If none, skip.
- Otherwise `_flood.Flood(VolumeGpu, vol.All)`.
- **Flag clearing (current):** clear `NeedsFlood` only on chunks with **`!NeedsGpuUpload && !NeedsRelight`**
  (i.e. fully represented in this flood). Chunks still awaiting opacity or CPU light keep `NeedsFlood`
  set so they trigger a corrective reflood once their data lands.

### `GpuLightFlood.Flood`
- Grows the reusable seed buffer if needed.
- `BuildLightSeed(chunks, _seedBuf)` then **`LightA.Write(seed)`** — this overwrites `LightA` with the
  CPU seed (sky from the column pass/BFS; block = emission only) every cycle.
- Lazily create the two ping-pong bind groups:
  - `FloodBindEven`: src=`LightA`, dst=`LightB`
  - `FloodBindOdd`:  src=`LightB`, dst=`LightA`
- Dispatch **`Passes = 16`** ping-pong passes over `(VW/4, VH/4, VD/4)` workgroups of size 4³.
  16 is even, so the final write lands in **`LightA`** (the render-sampled buffer).

### Flood shader (WGSL), per voxel
- Out-of-volume invocation → return.
- Read `sky` (bits 0–7) and `blk` (bits 8–15) from `src`.
- **Opaque voxel** → `dst = blk<<8` (sky forced to 0 inside solids; emission retained); return.
- **Air voxel** — max-relaxation:
  - **Block:** `b = max(blk, maxNeighbourBlock - 1)`.
  - **Sky:** start `s = sky` (the CPU seed). From the voxel **above**: if its sky `>= BASE_SKY (10)`
    take it as-is (full sun straight down, no loss), else `-1`. From the other 5 neighbours (incl.
    below): `-1`. `s = max` over all.
  - Write `dst = s | (b<<8)`.

> Effect: the seed provides per-column vertical sky (cheap, load-order-safe via the wait-for-above
> guard); the flood max-relaxes horizontally (and re-derives vertical) so light **converges across
> chunk boundaries** regardless of which neighbour loaded first. Max-relaxation can only *raise* a
> voxel toward the converged value; it cannot lower an over-bright seed (the wait-for-above guard is
> what's relied on to prevent over-bright seeds in the first place).
>
> Sky's max horizontal reach is `BASE_SKY = 10`, so 16 passes fully converge a single flood. Each flood
> re-seeds from scratch and re-converges; it is stateless across cycles.

---

## 8. Flag state machine (what advances a chunk to "correctly lit")

A freshly loaded chunk has `Relight=Upload=Flood=Remesh = true`. To become correctly lit and visible:

```
load (AddChunk)            → all flags true
LightSystem (Logic)        → InitializeChunk → NeedsRelight=false       [<=16/frame, gated by above]
GpuResidencySystem (Pre)   → UpdateChunkOpacity → NeedsGpuUpload=false  [<=8/frame; ALL at once on grow]
GpuLightSystem (Pre)       → Flood → NeedsFlood=false (only if !Upload && !Relight)  [1 volume/frame]
ChunkMeshSystem (Pre)      → mesh → NeedsRemesh=false                   [<=2/frame]
RenderSystem (Render)      → draws MeshRenderer, samples LightA
```

Important interactions:
- The flood runs over the **entire volume buffer** every cycle, including chunks whose opacity hasn't
  uploaded yet (their opacity bits are still 0 = air) and chunks still `NeedsRelight` (their sky seed is
  0). Those contribute wrong data to that flood; the current flag-clear rule keeps them `NeedsFlood` so
  a later flood corrects them.
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
- Else sample `light[idx]`: `sky = (packed & 0xFF)/15`, `blk = ((packed>>8)&0xFF)/15`.
- Final: `skyLit = sky * (0.35 + 0.65*sun)` where `sun = max(dot(N, -sunDir), 0)`; `lit = max(skyLit, blk)`;
  output `color * lit`.

> Consequence: a face on a chunk edge samples the **neighbour chunk's** voxel. If that neighbour is
> loaded-but-uninitialised or freshly-reallocated (opacity not re-uploaded), its buffer value is `0`
> → that strip of the face renders **black**. If the sample is outside the volume entirely it renders
> at flat `AMBIENT_SKY` instead.

---

## 10. Observations relevant to the current bugs

These are *suspected* contributors to "lots of banding while loading" and "large stable black patches",
written down for the debugging pass — not yet verified or fixed.

1. **Edge faces sample neighbour chunks.** Any face on a chunk boundary samples a voxel owned by the
   adjacent chunk. During loading that neighbour may be (a) loaded but `NeedsRelight` → seed 0 → black,
   (b) allocated-but-unloaded → seed 0, partially filled by flood bleed, or (c) outside the volume →
   flat `AMBIENT_SKY`. This is the structural source of boundary-aligned 1-voxel artifacts.

2. **`BuildLightSeed` zeroes uninitialised chunks.** Loaded-but-`NeedsRelight` chunks and
   allocated-but-unloaded regions go in as sky 0. They flood toward neighbours' values but the floor is
   black, not ambient. The `Allocate` ambient prefill of `LightA` is overwritten by the seed each flood,
   so it does not help after the first flood.

3. **Volume growth re-dirties everything at once.** `EnsureContains` grows the volume (e.g. as the load
   region expands) and marks **all** chunks `NeedsGpuUpload + NeedsFlood`. Opacity then re-uploads at
   only 8/frame, so for many frames large portions of the *freshly-allocated* opacity buffer read as 0
   (air). Floods during that window leak sky through not-yet-uploaded solids (e.g. flat-lit undersides).
   With 245 chunks in the region and 8 uploads/frame this window is ~30 frames per growth, and because
   `BoundsMin/Max` only ever grow (never recomputed on unload) the volume reallocates repeatedly as the
   camera roams — each roam triggers a fresh leak window. *(The stale-`chunkBase`/`volSize` remesh storm
   that previously accompanied each growth is fixed — those are now derived live at draw time.)*

4. **`mesh budget = 2/frame` is the bottleneck.** Chunks become visible far slower than they light up;
   combined with (3) the visible set during loading is a patchwork of differently-staged chunks.

5. **Large *stable* black patches** (persisting after loading settles) imply some chunks end with sky 0
   in `LightA` permanently. Candidate mechanisms to check: a chunk stuck `NeedsRelight` (so it stays 0
   in every seed) whose neighbours are also dark; a chunk whose `NeedsFlood` was cleared while a
   neighbour was not yet contributing and never re-flooded; or a region that only ever samples
   `AMBIENT_SKY`/0 due to volume-bounds edges. The wait-for-above guard can defer a chunk's init, but
   should resolve top-down within a few frames unless something keeps the above chunk `NeedsRelight`.

6. **Flood/visibility are per-volume but `anyDirty` gates on `NeedsFlood && !NeedsGpuUpload`.** A volume
   whose dirty chunks are *all* still awaiting opacity upload will **not** flood that frame, even though
   other (already-correct) chunks are fine — fine in isolation, but means newly-grown regions wait for
   the upload backlog to drain before any flood reflects them.

---

## 11. Quick glossary of the budgets/constants

| Constant | Value | Where |
|----------|-------|-------|
| `ChunkData.Size` | 32 | chunk edge length |
| `BaseSkyLevel` | 10 | full-sun sky level |
| `LoadsPerFrame` | 4 | `ChunkLoadSystem` |
| `xzRadius / yRadius` | 3 / 2 | load region (7×5×7) |
| `InitPerFrame` | 16 | `LightSystem` CPU light init |
| `RelightPerFrame` | 16 | `LightSystem` edit relights |
| `UploadsPerFrame` | 8 | `GpuResidencySystem` opacity uploads |
| `Passes` | 16 | `GpuLightFlood` ping-pong passes |
| flood/frame | 1 volume | `GpuLightSystem` |
| `MeshesPerFrame` | 2 | `ChunkMeshSystem` |
