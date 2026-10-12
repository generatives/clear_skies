# Ray-traced lighting: quick reference

A short, checked summary of how the ray-traced lighting works and what its numbers are. It was written from the
code on branch `claude/project-thread-rcxbni` (2026-10-11). `lighting_system.md` describes the older flood-fill
system and is out of date. When this file and the code disagree, the code is right; fix this file.

Code: `ECS/GpuLightSystem.cs` (settings, presets, panel), `ECS/GpuLightSystem.RayDirty.cs` (what changed, updates,
scheduling), `Voxels/GpuRayLightPass.cs` (WGSL passes).

## Storage

| Thing | Size | Notes |
|---|---|---|
| Light slot | one 8³ brick that has a surface | 256 words of display, 2 voxels per word |
| Display (per voxel, 16 bits) | 9-bit brightness, 5-bit warmth, 2-bit sun | Brightness 0-510 on a square curve (fine steps in the dark). 511 means no storage. Warmth 0-30. Sun 0-3. |
| Accumulation (per voxel, 32 bits) | 8-bit bounce R, G, B and 8-bit AO | 512 words per slot. Bounce on a square-root curve (fine steps for dim light); AO linear 0-255. |

**A world brick has accumulation only while it has an update to come.** After its last update, its accumulation is
freed (`ReleaseSettledAccs`), and the brick keeps only its composed display, so its bounce and AO are baked in. When
anything touches a settled brick, it gets fresh accumulation seeded from its display (`seed_main`: bounce = shown
light ÷ bounce scale, AO none), so it carries on from the light it was showing. **Ships keep their accumulation**
while they have light storage: freeing and re-seeding lost a little light each time, which dimmed a ship's hold with
every edit (2026-10-11).

## Passes, in frame order

1. **Sun**: 4 jittered rays per surface voxel give the 2-bit sun level.
2. **Clear**: zeroes bounce RGB (keeps AO) in the clear boxes. Does nothing to a brick without accumulation.
3. **Compose** (unsmoothed), so bounce rays read current direct light.
4. **Bounce**: the updates, one dispatch per step. Step 0 is every updating brick's first hop, step 1 each one's
   next, and so on. No compose between steps (a hit reads stored bounce directly).
5. **Compose** (final, smoothed).

Compose: `light = max(lamp, bounce × scale, ambient × (1 − aoStrength × AO))` per channel. Lamps are traced at
compose time. Defaults: ambient level 0.5/15 (a floor: bounce dimmer than it doesn't show), AO strength 0.95, albedo 0.5, bounce scale 1. The final compose smooths
bounce and AO: the voxel counts 2, face neighbours 1, edge diagonals ½. Only neighbours that have accumulation count.
Only the final compose runs the smoothing (an `if`; it used to be a `select`, which ran it on every compose). A
wider 2-cell smoothing was tried (2026-10-11) and made little visible difference, so it was dropped.

## Terms

| Term | Meaning | Was called (before 2026-10-11) |
|---|---|---|
| **Directions** | A voxel's fixed bounce directions, rays per group × direction groups (Medium: 8 × 4 = 32) | ray set |
| **Group** | One interleaved share of the directions, spanning the hemisphere coarsely | slice |
| **Hop** | One dispatch: fire one or more groups, write the result; light travels one more bounce | evaluation |
| **Update** | What a touched brick gets on its turn: the band's hops back to back in one frame | burst |
| (removed) | An AO-only dispatch before a restarting brick's update; gone with restarts (2026-10-11) | AO pass, prime |
| **Rays per voxel** | All, ½ or ¼ of each group's rays per voxel, neighbours firing the rest | checkerboard spread |

## Bounce rays

- Every surface air voxel has fixed **directions**, a cosine-weighted spiral twisted per voxel, split into
  **groups**: group g is directions `j = k × groups + g`, so each group spans the hemisphere coarsely.
- A **hop** fires one or more groups and writes the result: `first` group, `count` groups, a blend weight
  `1/denominator` and the spread, packed in the hop word (`HopWord`; the shader's `bounce_main`).
- **Rays per voxel** (spread S: 1, 2 or 4, set per band, carried in the hop word) makes each voxel fire only the
  rays with `k ≡ phase (mod S)`. Each 2×2 patch holds every phase once, and smoothing pools them. Per group a voxel
  fires `rays / S` rays; over all groups `rays × groups / S` (Medium: all 32 near, **8** of its 32 beyond, its
  neighbours firing the rest). Every spread fires part of the same directions, so a brick crossing bands keeps
  them; changing directions or groups changes the directions themselves, so those stay global.
- A hit reads the brightest of: the hit face's sun, its composed display, and its stored bounce. That is how bounce
  goes multi-hop: each dispatch adds a hop. A miss counts as sky for AO. `blend8` always moves at least one step.
- **No restarts** (2026-10-11): every brick carries its light on through a change, and the update rule alone decides
  replace or blend. Restarting the bricks at an edit (an AO pass, then a replacing update, while the rest blended) made
  a patch that blinked.

## Updates and distance bands (every change alike)

What a change touches differs (see below); what happens to a touched brick depends only on its distance band.

- **Touched**: a relit brick, every world brick within bounce reach of one, and of a moving ship; all of a relit
  ship's bricks. Nothing is marked to restart: what an update does depends only on the brick's recent updates.
- **Bands**: near (within its radius of the camera), middle (within its radius), far. Each has five settings:
  - **hops per update** (H);
  - **groups per update** (up to all of them), always the same ones, split evenly over the H hops (rounding down,
    so H should divide it);
  - **updates per change** (U): a touched brick gets U updates, one per turn, each reading the light the last one
    left, so a change gets U × H hops in all;
  - **update at most every** N frames, on its turn (`(frame + key) mod N = 0`, key = slot, or grid index for a
    ship's bricks, so a ship updates together). 1 = every frame;
  - **rays per voxel** (above).
- **Update** (`UpdateBrick`): hop i weighs `1/(i+1)`, so the update replaces the stored value with its mean (the
  hops read the old light through the neighbours, so it carries on). If the brick was touched again and updated on
  its previous turn too (it keeps changing, as around a moving ship), and its accumulation is neither fresh nor
  cleared this frame, it blends in at ½ instead: hop i weighs
  `1/(2H − i)`, so the old light keeps half and a fast-changing light fades between updates instead of stepping.
  After a blended update the brick is owed one more (`_tail`), which replaces, so no old light is left once it stops
  changing. The further updates per change (`_left`) always replace.
- **Distant terrain sharpens** as the camera nears: a world brick that fired fewer groups than the most any band
  fires is touched again when its band now fires more (`TopUpNearing`).
- **Caps** per frame, nearest first: relit bricks, and brick groups (an update costs its groups).
  Ships go past both. Cleared bricks go past both and update at once. Bricks an occluder change marked (a block
  change, a moving ship: the pad, the sun sweep, lamps in reach) go past the relit cap, so a ship's shadow keeps up
  with it.
- **Clearing**: only where a lamp was removed (its box plus ray reach), since that light would otherwise sustain
  itself through bounce. Everything else carries on.
- History (2026-10-11): before the bands, block changes and ship moves had Clear/Average modes and their own settle
  radii; Average fired every direction once every 4 frames, blended at ½ away from the camera. Groups one at a time
  blended at ¼ leaned toward the latest group and flickered, and once the count stopped at 255 froze every voxel on
  the same 2 rays. Updates keep the cheap per-group hops without that lean.

## What a change does

| Change | Marked for direct light | Bounce |
|---|---|---|
| Block placed or removed, chunk load or unload | A pad around it, its sun-shadow sweep, and lamps whose box touches it, relit this frame | Touched, plus ray reach; the bricks at a block edit (and all of an edited ship) update this frame, past the cap, so air never measured before doesn't show as bright |
| Ship moved | Old and new pose plus pad, both sun sweeps, lamps, relit this frame | Its bricks touched, and everything within ray reach of the hull |
| Lamp added, moved or removed | The lamp's box (level + 1.5), queued | Touched, plus ray reach; a removed lamp clears its box plus ray reach |
| New light storage | The brick (starts from nothing), queued | Touched, plus ray reach |
| Sun moved, display settings changed | Everything, queued | Everything relit is touched |
| Bounce setting changed | — | Everything touched |

## Presets

| | Minimal | Low | Medium (default) | High |
|---|---|---|---|---|
| Mode | Off | AO only | Full | Full |
| Directions per voxel (rays per group × groups) | 16 (4 × 4) | 32 (8 × 4) | 32 (8 × 4) | 32 (8 × 4) |
| Ray reach (voxels) | 8 | 8 | 8 | 16 |
| Near band: radius, hops, groups, updates, every, spread | 96, 4, 4, 1, 4, 1 | 96, 4, 4, 1, 4, 4 | **128, 4, 4, 1, 4, 1** | 256, 4, 4, 1, 4, 1 |
| Middle band | 256, 1, 1, 1, 4, 1 | 256, 1, 1, 1, 4, 4 | **384, 2, 2, 1, 4, 4** | 768, 2, 2, 1, 4, 1 |
| Far band: hops, groups, updates, every, spread | 1, 1, 1, 8, 1 | 1, 1, 1, 8, 4 | **1, 1, 1, 8, 4** | 1, 1, 1, 8, 1 |
| **Medium rays per voxel per group** | | | **8 near, 2 beyond** | |
| Relit bricks / brick groups per frame | 256 / 512 | 256 / 1024 | 512 / 2048 | 1024 / 4096 |

Medium, bounce rays per voxel: a one-off change costs 32 near (4 hops), 4 middle (2 hops), 2 far (1 hop); something that keeps changing costs about 8 a frame near, 1 middle,
0.25 far. Updates per change U multiplies a one-off change's cost and hops by U. Near used spread 4 (8 rays, 2 a frame) until 2026-10-11, when spread 1 there looked steadier in game.
