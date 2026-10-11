# Ray-traced lighting: quick reference

A short, checked summary of how the ray-traced lighting works and what its numbers are. It was written from the
code on branch `claude/project-thread-rcxbni` (2026-10-11). `lighting_system.md` describes the older flood-fill
system and is out of date. When this file and the code disagree, the code is right; fix this file.

Code: `ECS/GpuLightSystem.cs` (settings, presets, panel), `ECS/GpuLightSystem.RayDirty.cs` (what changed, holds,
scheduling), `Voxels/GpuRayLightPass.cs` (WGSL passes).

## Storage

| Thing | Size | Notes |
|---|---|---|
| Light slot | one 8³ brick that has a surface | 256 words of display, 2 voxels per word |
| Display (per voxel, 16 bits) | 9-bit brightness, 5-bit warmth, 2-bit sun | Brightness 0-510 on a square curve (fine steps in the dark). 511 means no storage. Warmth 0-30. Sun 0-3. |
| Accumulation (per voxel, 32 bits) | 8-bit bounce R, G, B and 8-bit AO | 512 words per slot. Linear 0-255. |

**Accumulation exists only while a brick is being evaluated.** When a brick's hold runs out, its accumulation is
freed (`ReleaseSettledAccs`). After that the brick keeps only its composed display, so its bounce and AO are baked in.
When anything wakes a settled brick, it gets fresh, zeroed accumulation and restarts at N = 0 (`GiveAcc` in
`BuildBounceWork`). **So "Average" only carries on for bricks that are still mid-hold.** A settled brick always
restarts, whatever the setting.

## Passes, in frame order

1. **Sun**: 4 jittered rays per surface voxel give the 2-bit sun level.
2. **Clear**: zeroes bounce RGB (keeps AO) in the clear boxes. Does nothing to a brick without accumulation.
3. **Compose** (unsmoothed), so bounce rays read current direct light.
4. **Bounce**: one evaluation per listed brick. Then near-camera bricks get up to a hold's worth of repeats in the
   same frame, each preceded by a recompose.
5. **Compose** (final, smoothed).

Compose: `light = max(lamp, bounce × scale, ambient × (1 − aoStrength × AO))` per channel. Lamps are traced at
compose time. Defaults: ambient level 1/15, AO strength 0.95, albedo 0.5, bounce scale 1. The final compose smooths
bounce and AO: the voxel counts 2, face neighbours 1, edge diagonals ½. Only neighbours that have accumulation count.
**Wide smoothing** (panel checkbox, on by default) adds, for bounce only (AO keeps the narrow kernel), the cells two away (squared offset ≤ 6) at ½, each only if
the cell one step back toward the voxel isn't solid. On flat ground that's an effective 16 voxels pooled instead of
7. It needs a bigger compose tile: solidity 14³ (was 12³) and accumulation 12³ (was 10³). Earlier compose evaluated
the smoothing on every compose through a `select`; it now runs only on the final one, as intended.

## Bounce rays and the running average

- Every surface air voxel has a fixed set of **rays × cycle** directions (Medium: 8 × 4 = 32). The set is a
  cosine-weighted spiral, twisted per voxel.
- Each evaluation fires one **slice**: slice = N mod cycle, and directions are `j = k × cycle + slice`.
- **Checkerboard spread** S (1, 2 or 4) makes each voxel fire only the rays with `k ≡ phase (mod S)`. Each 2×2
  patch holds every phase once, and smoothing pools them.
  - Per evaluation, a voxel fires `rays / S` rays. Medium: 8 / 4 = **2**.
  - Over a full cycle, a voxel fires `rays × cycle / S`. Medium: **8** of its 32. It never fires the other 24; its
    neighbours do.
- A hit reads the brightest of: the hit face's sun, its composed display, and its stored bounce. That is how bounce
  goes multi-hop: each evaluation adds a hop. A miss counts as sky for AO.
- Blend weight: `alpha = max(1/cycle, 1/(N+1))`. At cycle 4 that is 1, ½, ⅓, ¼, ¼, … So it is an exact running mean
  over the first cycle, then an EMA weighing one cycle. `blend8` always moves at least one 8-bit step.
- **Prime** (N = `PRIME`): the first evaluation after a restart. It measures AO only and stores zero bounce, so a wall
  that just appeared isn't read as bright. It doesn't count toward the hold.
- **FULL_SET** flag (b543cad): one evaluation fires all `cycle` slices (Medium: 8 rays per voxel) with weight 1, and
  then counts as N = cycle. It is used for a Block or Ship restart outside the settle radius, after the prime in the
  same frame.

## Holds: how many evaluations a change gets

- **Hold** = the hold setting rounded up to whole cycles. All presets: **4** (one cycle).
- **Averaging** changes grant `AverageEvals()` instead: `ceil(ln(2/255) / ln(1 − 1/cycle)) + 2`, which is **19** at
  cycle 4. That is how long a 255-step difference takes to fade under weight ¼.
- **Distance tiers** (world bricks): a full hold within the full radius, mid evals within the mid radius, and far
  evals beyond it. A brick is topped up as the camera approaches. Ships always get the full hold.
- **Settle radius**: within it, a held brick runs the rest of its hold in the same frame (repeats). Outside it, the
  brick gets one evaluation per frame.
  - Block and Ship changes each have their own radius; the preset sets both to the near radius.
  - Other changes, and averaging restarts, use the general near radius.
- **Caps** per frame, nearest first: relit bricks and bounced bricks. Cleared bricks go past both caps, and
  averaging-marked bricks go past the relit cap.

## What a change does

| Change | Marked for direct light | Bounce |
|---|---|---|
| Block placed or removed, chunk load or unload (kind Block) | A pad around it, its sun-shadow sweep, and lamps whose box touches it | **Clear**: clears bounce around a placed block and along its sweep, plus ray reach. Everything relit restarts with a hold of 4. **Average**: the change site restarts; the sweep and lamp boxes carry on for 19 evaluations (but settled bricks restart anyway, see Storage). |
| Ship moved (kind Ship) | Old and new pose plus pad, both sun sweeps, lamps | **Clear**: clears both poses and sweeps; restarts, settled within the ship radius. **Average** (blocks unchanged): carries on, and so does everything within ray reach of the hull. The ship's own blocks carry on one evaluation per frame. When the ship stops, it restarts once with a clear. |
| Ship blocks edited | Same as moved | Always restarts. |
| Lamp added, moved or removed (Other) | The lamp's box (level + 1.5) | A removed lamp clears its box plus ray reach. |
| Sun moved, settings changed | Everything | Everything restarts. |

## Presets

| | Minimal | Low | Medium (default) | High |
|---|---|---|---|---|
| Mode | Off | AO only | Full | Full |
| Rays per evaluation (before spread) | 4 | 8 | 8 | 8 |
| Spread | 1 | 4 | 4 | 1 |
| **Rays per voxel per evaluation** | 4 | 2 | **2** | 8 |
| Cycle | 4 | 4 | 4 | 4 |
| **Rays per voxel per full cycle** | 16 | 8 | **8** | 32 |
| Hold (evaluations) | 4 | 4 | 4 | 4 |
| Averaging evaluations | n/a | n/a | 19 | 19 |
| Ray reach (voxels) | 8 | 8 | 8 | 16 |
| Near / settle radius | 0 | 0 | **32** | 64 |
| Full / mid radius | 96 / 256 | 96 / 256 | 128 / 384 | 256 / 768 |
| Mid / far evaluations | 1 / 1 | 1 / 1 | 2 / 1 | 2 / 1 |
| Relit / bounced caps | 256 / 512 | 256 / 1024 | 512 / 2048 | 1024 / 4096 |

Rays per voxel per evaluation = `rays / spread`. Rays per voxel per full cycle = `rays × cycle / spread`.
