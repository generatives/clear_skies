# Clear Skies — Lighting System Design

## Goal

Minecraft-style per-voxel lighting (BFS flood-fill, smooth per-vertex shading, ambient occlusion) extended to a world of freely-moving, freely-rotating dynamic voxel grids (airships). Light must propagate correctly *within* a grid and *between* grids: a torch on one airship must illuminate and cast shadows onto other airships and the static terrain, and occlusion must respect real geometry as ships tilt and turn.

The central difficulty is that dynamic grids rotate freely, so their blocks do not align with the static world's voxel grid. A single shared voxel grid therefore cannot represent rotated ships exactly, and pure BFS cannot resolve cross-grid occlusion under rotation.

---

## Core Principle

Lighting is computed in the cheapest representation that is *exact* for each situation. This produces a three-tier model with **no orientation quantization anywhere**:

1. **Static world** → plain BFS flood-fill. The terrain never moves or rotates, so classic Minecraft-style propagation is exact.
2. **Each dynamic grid (interior)** → BFS flood-fill in the grid's *own local, axis-aligned coordinate system*. Because lighting is computed in the grid's local frame, it is **invariant under rigid motion** — a ship flying, tilting, or turning does not invalidate its internal lighting. Only block changes do.
3. **Cross-grid coupling** → shadow-map-based direct light injection. This is the only tier that touches continuous rotation, and shadow maps handle it exactly by rendering real geometry.

This decomposition separates **visibility** (hard, continuous — solved by shadow maps) from **diffusion** (local, discrete — solved by BFS). The key structural win is tier 2: because each grid lights itself in its own frame, **rigid motion is free**, which matches a game built around constantly-moving objects.

---

## Tier 1 & 2: Local BFS Flood-Fill

Every voxel grid — static or dynamic — owns a lighting grid: a per-voxel light value (0–15 per channel; RGB for colored light). Propagation is the standard Minecraft algorithm:

- **Sky light**: flooded top-down; full strength in open sky, attenuating as it filters past solids.
- **Block light**: each emitter (torch, lamp, etc.) seeds its voxel at its emission level and floods outward, attenuating by 1 per block, taking the `max` along competing paths (brightest path wins — never additive).
- **Within a grid, occlusion is exact**: light cannot enter solid voxels, so walls cast shadows naturally. This is intra-grid shadowing, and it is correct for free.

### Incremental updates (the source of "fast")

Speed comes from never re-baking a whole grid. On a block or emitter change, run the standard two-pass local update over only the affected region:

```
RemoveLight(region):  // BFS clearing cells whose light originated at the change,
                      // queueing brighter neighbours as re-light seeds
ReLight(seeds):       // additive max-BFS from surviving sources and seeds
```

Sky light uses a per-column heightmap so top-down sunlight is O(1) per column and needs no BFS.

### Motion invariance

A dynamic grid's lighting grid is attached to the grid's local frame. Rigid-body motion (translation + rotation) changes nothing about the relative positions of its blocks, so its internal light field is **never recomputed due to movement** — only due to block edits. This is the property that makes flying cheap.

BFS can run on CPU (per-region incremental) or GPU (iterative ping-pong flood, ~15 passes per dirty region). Static-world and ship-interior lighting are baked and cached; only dirty regions are touched.

---

## Tier 3: Cross-Grid Light via Shadow-Map Injection

Local BFS cannot carry light correctly between two grids that are rotated relative to each other, because their voxel grids are incommensurate. Shadow maps solve this by working in continuous world space against real geometry.

### Pipeline per cross-grid light

1. **Depth map**: render a shadow map for the light from its position, against the real (continuously-rotated) scene geometry in range. Because it renders true geometry, occlusion is rotation-exact and cross-grid-exact.
2. **Injection**: for each *air* voxel of each affected grid's lighting grid within the light's radius, project the voxel center into light space and shadow-test it. If visible, seed it with `attenuation(distance)`. This is a compute pass over a bounded volume.
3. **Diffusion**: the per-grid local BFS (Tier 2) then floods the injected seed values into shadowed pockets, producing the soft around-corner spill that gives the Minecraft look.

This split means shadow maps provide exact *direct* line-of-sight light across grids (handling rotation), while BFS provides the *diffuse* fill within each grid.

---

## Optimizations

These are not optional for shipping — without them the shadow-map tier does not scale.

### 1. Radius gate (the primary cull)

A shadow map is only needed when a light's effect must cross between grids. **Only generate a shadow map for a light whose radius intersects another grid.** A light that reaches only its own grid is fully and exactly handled by Tier 2 local BFS — no map required.

- Test: sphere(light position, radius) vs other grids' AABBs. Use the **physics broadphase (Bepu already maintains one)** to find overlaps cheaply.
- Consequence: a ship alone in open sky generates **zero** shadow maps. A ship near an island or another ship pays only for the torches actually near the boundary.
- The threshold sits where cross-grid contribution is weakest (a torch *just* reaching another grid contributes ≈0 light there), so switching maps on/off as ships drift causes negligible popping. Add a small hysteresis margin if flicker ever appears.

### 2. Directional frustum instead of cube map

Once the gate identifies *which* grid a light intersects, render a single shadow frustum aimed at that grid's AABB rather than a full 6-face cube map. Common case drops from 6 renders to 1.

### 3. Occluder cull (secondary)

Even when a light reaches another grid, if no solid geometry lies between the light and the target grid there is nothing to occlude — plain distance attenuation suffices and the map can be skipped. A quick segment query through the broadphase detects this. Optional; the radius gate alone is the major win.

### 4. Hard cap with graceful fallback

Cap the number of simultaneous shadow-casting lights. Past the cap, keep maps for the N most significant boundary lights and **demote the rest to local-BFS-only**. A demoted small torch at the edge of a dense cluster leaks invisibly. This bounds the genuine worst case (see below).

### 5. Bounded injection and BFS

- Clip the injection compute pass to each light's radius bounding-box in the target grid's local space — never iterate whole volumes.
- Dirty-region + throttle (~10 Hz) all dynamic re-propagation; bake and cache static lights so they cost nothing until disturbed.

---

## Caveats & Known Limitations

### Cross-grid indirect flood is not represented

Tier 3 injects *direct* (line-of-sight) cross-grid light, and Tier 2 BFS diffuses it *within* each grid. What is missing is light that bends around a wall **on one grid** and then spills onto a shadowed region of **another grid** across the air gap — the indirect, around-corner component does not propagate between grids. This case is rare, subtle, and largely static. Accepted as a trade-off.

### Worst case: dense boundary lighting

The radius gate does not help when many torches legitimately cross a boundary at once — e.g. two heavily-lit ships docked together, or a torch-lined ship hugging a torch-lined island. Every map there is doing real work. Optimization 4 (cap + demote) bounds the cost; the visual loss from demoting the least-significant lights in such a cluster is negligible.

### Shadow-map artifacts

Standard shadow-mapping issues (acne, peter-panning) apply. At voxel granularity, testing voxel centers with a modest depth bias is sufficient in practice.

### Colored light cost

RGB light requires per-channel storage and per-channel BFS, roughly tripling diffusion cost. Acceptable, but factor it into the budget if colored torches are desired.

---

## Summary

| Tier | Scope | Method | Exact for | Cost driver |
|---|---|---|---|---|
| 1 | Static world | Plain BFS | Never-moving terrain | Block edits only |
| 2 | Dynamic grid interior | Local-frame BFS | Self-lighting & intra-grid shadows under any motion | Block edits only (motion is free) |
| 3 | Between grids | Shadow-map injection + BFS diffuse | Cross-grid direct light & shadows under rotation | Lights crossing grid boundaries (radius-gated) |

The design uses the cheapest exact tool per situation, keeps the signature Minecraft flood-fill look and intra-grid shadows for free, makes ship movement essentially free, and confines the expensive continuous-rotation work to the radius-gated set of lights that actually bridge grids.
