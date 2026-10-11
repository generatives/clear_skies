# Ray-traced lighting: quick reference

A short, checked summary of how the ray-traced lighting works and what its numbers are. It was written from the
code on branch `claude/project-thread-rcxbni` (2026-10-11). `lighting_system.md` describes the older flood-fill
system and is out of date. When this file and the code disagree, the code is right; fix this file.

Code: `ECS/GpuLightSystem.cs` (settings, presets, panel), `ECS/GpuLightSystem.RayDirty.cs` (what changed, bursts,
scheduling), `Voxels/GpuRayLightPass.cs` (WGSL passes).

## Storage

| Thing | Size | Notes |
|---|---|---|
| Light slot | one 8³ brick that has a surface | 256 words of display, 2 voxels per word |
| Display (per voxel, 16 bits) | 9-bit brightness, 5-bit warmth, 2-bit sun | Brightness 0-510 on a square curve (fine steps in the dark). 511 means no storage. Warmth 0-30. Sun 0-3. |
| Accumulation (per voxel, 32 bits) | 8-bit bounce R, G, B and 8-bit AO | 512 words per slot. Linear 0-255. |

**Accumulation exists only while a brick has a burst to come.** After its last burst, its accumulation is freed
(`ReleaseSettledAccs`), and the brick keeps only its composed display, so its bounce and AO are baked in. When
anything touches a settled brick, it gets fresh, zeroed accumulation and restarts (`GiveAcc` in `Burst`). So only
a brick that keeps changing (touched again before its next turn) carries on its light.

## Passes, in frame order

1. **Sun**: 4 jittered rays per surface voxel give the 2-bit sun level.
2. **Clear**: zeroes bounce RGB (keeps AO) in the clear boxes. Does nothing to a brick without accumulation.
3. **Compose** (unsmoothed), so bounce rays read current direct light.
4. **Bounce**: the bursts, one dispatch per step. Step 0 is every bursting brick's first evaluation (or its prime),
   step 1 each one's next, and so on. No compose between steps (a hit reads stored bounce directly), except that
   primed bricks are recomposed before step 1 so the burst sees their AO.
5. **Compose** (final, smoothed).

Compose: `light = max(lamp, bounce × scale, ambient × (1 − aoStrength × AO))` per channel. Lamps are traced at
compose time. Defaults: ambient level 1/15, AO strength 0.95, albedo 0.5, bounce scale 1. The final compose smooths
bounce and AO: the voxel counts 2, face neighbours 1, edge diagonals ½. Only neighbours that have accumulation count.
Only the final compose runs the smoothing (an `if`; it used to be a `select`, which ran it on every compose). A
wider 2-cell smoothing was tried (2026-10-11) and made little visible difference, so it was dropped.

## Bounce rays

- Every surface air voxel has a fixed **ray set** of **rays × cycle** directions (Medium: 8 × 4 = 32), in `cycle`
  **slices**. The set is a cosine-weighted spiral, twisted per voxel. Slice s is directions `j = k × cycle + s`, so
  each slice spans the hemisphere coarsely.
- An **evaluation** casts one or more slices and writes the result: `first` slice, `count` slices, and a blend
  weight `1/denominator`, packed in the work word (`EvalWord`; the shader's `bounce_main`).
- **Checkerboard spread** S (1, 2 or 4, set per band and carried in the work word) makes each voxel fire only the
  rays with `k ≡ phase (mod S)`. Each 2×2 patch holds every phase once, and smoothing pools them. Per slice a voxel
  fires `rays / S` rays; over the whole set `rays × cycle / S` (Medium: all 32 near, **8** of its 32 beyond, its
  neighbours firing the rest). Every spread fires part of the same set, so a brick crossing bands keeps its
  directions; changing rays or cycle would change the set itself, so those stay global.
- A hit reads the brightest of: the hit face's sun, its composed display, and its stored bounce. That is how bounce
  goes multi-hop: each evaluation adds a hop. A miss counts as sky for AO. `blend8` always moves at least one step.
- **Prime** (`PRIME`): the first evaluation after a restart whose own voxels changed. It measures AO only and stores
  zero bounce, so a wall that just appeared isn't read as bright. A brick restarting only because it had settled
  isn't primed by default (panel: "Prime settled bricks"); priming those made dark interiors noisy in game.

## Bursts and distance bands (every change alike)

What a change touches differs (see below); what happens to a touched brick depends only on its distance band.

- **Touched**: a relit brick, every world brick within bounce reach of one, and of a moving ship; all of a relit
  ship's bricks. A brick whose own voxels changed (the surface right at a changed block, new storage, the bricks
  at a ship's edited blocks, a bounce setting changed, a lamp's clear) is marked to **restart**. Settled bricks restart anyway.
- **Bands**: near (within its radius of the camera), middle (within its radius), far. Each has four settings:
  - **E**, evaluations per burst = hops;
  - **slices per burst** (up to the whole set), always the same ones, split evenly over the E evaluations;
  - **every**: a brick bursts at most once every that many frames, on its turn (`(frame + key) mod every = 0`,
    key = slot, or grid index for a ship's bricks, so a ship bursts together). 1 = settled the frame it changes;
  - **checkerboard** spread (above).
- **Burst** (`Burst`): a restarting brick primes first. Evaluation i weighs `1/(i+1)`, so the burst replaces the
  stored value with its mean. If the brick burst on its previous turn too and isn't restarting (it keeps changing,
  as around a moving ship), it blends in at ½ instead: evaluation i weighs `1/(2E − i)`, so the old light keeps half
  and a fast-changing light fades between bursts instead of stepping. After a blended burst the brick is owed one
  more (`_tail`), which replaces, so no old light is left once it stops changing.
- **Distant terrain sharpens** as the camera nears: a world brick that fired fewer slices than the most any band
  fires is touched again when its band now fires more (`TopUpNearing`).
- **Caps** per frame, nearest first: relit bricks, and bounce slices (a burst costs its slices, a prime 1). Ships
  go past both. Cleared bricks go past both and burst at once. Bricks an occluder change marked (a block change, a
  moving ship: the pad, the sun sweep, lamps in reach) go past the relit cap, so a ship's shadow keeps up with it.
- **Clearing**: only where a lamp was removed (its box plus ray reach), since that light would otherwise sustain
  itself through bounce. Everything else carries on.
- History (2026-10-11): before the bands, block changes and ship moves had Clear/Average modes and their own settle
  radii; Average fired the whole set once every 4 frames, blended at ½ away from the camera. Slices one at a time
  blended at ¼ leaned toward the latest slice and flickered, and once the count stopped at 255 froze every voxel on
  the same 2 rays. Bursts keep the cheap slice hops without that lean.

## What a change does

| Change | Marked for direct light | Bounce |
|---|---|---|
| Block placed or removed, chunk load or unload | A pad around it (restarts), its sun-shadow sweep, and lamps whose box touches it, relit this frame | Touched, plus ray reach |
| Ship moved | Old and new pose plus pad, both sun sweeps, lamps, relit this frame | Its bricks touched (those at edited blocks restart), and everything within ray reach of the hull |
| Lamp added, moved or removed | The lamp's box (level + 1.5), queued | Touched, plus ray reach; a removed lamp clears its box plus ray reach |
| New light storage | The brick (restarts), queued | Touched, plus ray reach |
| Sun moved, display settings changed | Everything, queued | Everything relit is touched |
| Bounce setting changed | — | Everything restarts |

## Presets

| | Minimal | Low | Medium (default) | High |
|---|---|---|---|---|
| Mode | Off | AO only | Full | Full |
| Rays per slice (before spread) | 4 | 8 | 8 | 8 |
| Slices per ray set (cycle) | 4 | 4 | 4 | 4 |
| Ray reach (voxels) | 8 | 8 | 8 | 16 |
| Near band: radius, E, slices, every, spread | 96, 4, 4, 4, 1 | 96, 4, 4, 4, 4 | **128, 4, 4, 4, 1** | 256, 4, 4, 4, 1 |
| Middle band | 256, 1, 1, 4, 1 | 256, 1, 1, 4, 4 | **384, 2, 2, 4, 4** | 768, 2, 2, 4, 1 |
| Far band: E, slices, every, spread | 1, 1, 8, 1 | 1, 1, 8, 4 | **1, 1, 8, 4** | 1, 1, 8, 1 |
| **Medium rays per voxel per slice** | | | **8 near, 2 beyond** | |
| Relit bricks / bounce slices per frame | 256 / 512 | 256 / 1024 | 512 / 2048 | 1024 / 4096 |

Medium, bounce rays per voxel: a one-off change costs 32 near (4 hops), 4 middle (2 hops), 2 far (1 hop), plus a
prime when the brick restarts (8 near, 2 beyond); something that keeps changing costs about 8 a frame near, 1 middle,
0.25 far. Near used spread 4 (8 rays, 2 a frame) until 2026-10-11, when spread 1 there looked steadier in game.
