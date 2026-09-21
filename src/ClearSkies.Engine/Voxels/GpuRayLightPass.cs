using System.Runtime.InteropServices;
using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using ComputePipeline = ClearSkies.Engine.Rendering.WebGpu.ComputePipeline;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Ray-traced voxel lighting over the shared <see cref="GridStore"/>: sun visibility and point-lamp shadows (any-hit
/// DDA), and bounce light + ray AO (nearest-hit DDA), for every grid at once. A ray is carried into each grid's own
/// voxel space and walked there, so ships shadow terrain, terrain shadows ships, and lamps on any grid light any
/// other.
///
/// <para>Work is a list of light slots (surface bricks) from any grid: one workgroup per brick, 256 threads each
/// covering two voxels. Every pass writes only its own byte of the voxel's light word (sun 0-7, lamp 8-15, AO 16-23,
/// bounce 24-31), and passes run as separate submissions, so no word is written by two threads at once.</para>
/// </summary>
internal sealed unsafe class GpuRayLightPass : IDisposable
{
    /// <summary>Cap on the lamp list <see cref="DispatchLamps"/> uploads; every lamp is tested (distance first)
    /// by every voxel in the work list. Truncated, with a one-time log, rather than exceeded.</summary>
    public const int MaxLamps = 256;

    private static readonly string Wgsl = @"
struct GridDesc {
    v2w: mat4x4<f32>,
    w2v: mat4x4<f32>,
    table: vec4<i32>,  // x: chunk-table base, yzw: section dims in chunks (0 = unused descriptor)
    bmin: vec4<i32>,   // voxel bounds [bmin, bmax) of the grid's chunks, grid space
    bmax: vec4<i32>,
};

struct Params {
    sunDir: vec4<f32>,
    counts: vec4<i32>,  // x: grid descriptor count, y: lamp count, z: work entries this dispatch
    bounce: vec4<f32>,  // x: albedo (fraction of incoming light a surface re-emits), y: sun strength (0-1)
    bounce2: vec4<f32>, // x: rays per evaluation, y: evaluations per full ray set (cycle)
};

@group(0) @binding(0) var<storage, read> occPool: array<u32>;
@group(0) @binding(1) var<storage, read> chunkTable: array<vec4<i32>>;
@group(0) @binding(2) var<storage, read> brickTable: array<u32>;
@group(0) @binding(3) var<storage, read_write> lightPool: array<u32>;
@group(0) @binding(4) var<storage, read> slotInfo: array<vec4<i32>>;
@group(0) @binding(5) var<storage, read> grids: array<GridDesc>;
@group(0) @binding(6) var<storage, read> work: array<u32>;
@group(0) @binding(7) var<storage, read> lamps: array<vec4<f32>>; // xyz world pos, w level (also doubles as radius)
@group(0) @binding(8) var<uniform> p: Params;

const WPC: i32 = " + GridStore.WordsPerChunk + @";
const OCC_UNLOADED: i32 = " + GridStore.OccUnloaded + @";
const OCC_ALL_SOLID: i32 = " + GridStore.OccAllSolid + @";
const NO_SURFACE: u32 = " + GridStore.NoSurface + @"u;
const SUN_MAX_DISTANCE: f32 = 2000.0;
const SURFACE_OFFSET: f32 = 0.4;
const SUN_SAMPLES: i32 = 4;      // sun rays per surface voxel: 1 (from the nudged centre) or up to 4 (offset table size)
const SUN_JITTER: f32 = 0.25;    // how far (voxels) each sample point is offset from the nudged centre, per axis
// ddaMarch: axes within this of the step minimum are treated as tied (corner-cutting fix). Generous, not a
// tight float-epsilon — lamp/surface positions are usually voxel-centred, so exact ties are the COMMON
// case, and the world-to-local transform's own rounding can perturb an exact tie past a tiny epsilon.
// Widening this costs a few extra (correct) opacity reads on genuinely near-tied rays; it can't cause false
// blocking, since the extra checks still test real occupancy, not a blanket geometric rule.
const TIE_EPS: f32 = 1e-2;
const DDA_MAX_STEPS: i32 = 1024;

// ── Storage lookups ───────────────────────────────────────────────────────────────────────────────────────

// a mod n in [0, n). Unsigned arithmetic only: signed % returns wrong results for negative operands on at least
// one backend (measured: -1887 % 19 gave 0), which broke every lookup at negative coordinates.
fn wrapi(a: i32, n: i32) -> i32 {
    let un = u32(n);
    if (a >= 0) { return i32(u32(a) % un); }
    return n - 1 - i32(u32(-(a + 1)) % un);
}

// Chunk-table entry index of chunk c in grid g, or -1 when that chunk isn't stored. The table wraps, so the
// entry's own coordinate tag decides whether it really is this chunk.
fn entryOf(g: i32, c: vec3<i32>) -> i32 {
    let t = grids[g].table;
    if (t.y <= 0) { return -1; }
    let idx = t.x + wrapi(c.x, t.y) + t.y * (wrapi(c.y, t.z) + t.z * wrapi(c.z, t.w));
    let e = chunkTable[idx];
    if (e.y != c.x || e.z != c.y || e.w != c.z) { return -1; }
    return idx;
}

fn occCode(g: i32, c: vec3<i32>) -> i32 {
    let i = entryOf(g, c);
    if (i < 0) { return OCC_UNLOADED; }
    return chunkTable[i].x;
}

// Voxel v (grid space) against its chunk's occupancy code: a pool slot, or uniform / unloaded.
fn solidIn(code: i32, v: vec3<i32>) -> bool {
    if (code >= 0) {
        let l = v & vec3<i32>(31);
        return ((occPool[u32(code * WPC + l.y + 32 * l.z)] >> u32(l.x)) & 1u) == 1u;
    }
    return code == OCC_ALL_SOLID;
}

fn isSolid(g: i32, v: vec3<i32>) -> bool { return solidIn(occCode(g, v >> vec3<u32>(5u)), v); }

// As isSolid, re-looking the chunk up only when v is in a different chunk from the last call (DDA loops).
fn solidCached(g: i32, v: vec3<i32>, cc: ptr<function, vec3<i32>>, code: ptr<function, i32>) -> bool {
    let c = v >> vec3<u32>(5u);
    if (any(c != *cc)) { *cc = c; *code = occCode(g, c); }
    return solidIn(*code, v);
}

// Index into lightPool of voxel v in grid g, or -1 if it has no light storage.
fn lightIndex(g: i32, v: vec3<i32>) -> i32 {
    let i = entryOf(g, v >> vec3<u32>(5u));
    if (i < 0) { return -1; }
    let l = v & vec3<i32>(31);
    let b = (l.x >> 3u) + 4 * ((l.y >> 3u) + 4 * (l.z >> 3u));
    let s = brickTable[u32(i * 64 + b)];
    if (s == NO_SURFACE) { return -1; }
    let lb = l & vec3<i32>(7);
    return i32(s) * 512 + lb.x + 8 * (lb.y + 8 * lb.z);
}

struct ClipResult { hit: bool, t0: f32, t1: f32 };

// Ray/AABB slab test against [lo,hi), intersected with the caller's [tLo,tHi]. o/d are already in the grid's
// own voxel space. Unrolled per axis (no dynamic vector-component indexing).
fn slabClip(o: vec3<f32>, d: vec3<f32>, lo: vec3<f32>, hi: vec3<f32>, tLo: f32, tHi: f32) -> ClipResult {
    var t0 = tLo;
    var t1 = tHi;

    if (abs(d.x) < 1e-8) {
        if (o.x < lo.x || o.x > hi.x) { return ClipResult(false, 0.0, 0.0); }
    } else {
        var ta = (lo.x - o.x) / d.x;
        var tb = (hi.x - o.x) / d.x;
        if (ta > tb) { let tmp = ta; ta = tb; tb = tmp; }
        t0 = max(t0, ta); t1 = min(t1, tb);
        if (t0 > t1) { return ClipResult(false, 0.0, 0.0); }
    }
    if (abs(d.y) < 1e-8) {
        if (o.y < lo.y || o.y > hi.y) { return ClipResult(false, 0.0, 0.0); }
    } else {
        var ta = (lo.y - o.y) / d.y;
        var tb = (hi.y - o.y) / d.y;
        if (ta > tb) { let tmp = ta; ta = tb; tb = tmp; }
        t0 = max(t0, ta); t1 = min(t1, tb);
        if (t0 > t1) { return ClipResult(false, 0.0, 0.0); }
    }
    if (abs(d.z) < 1e-8) {
        if (o.z < lo.z || o.z > hi.z) { return ClipResult(false, 0.0, 0.0); }
    } else {
        var ta = (lo.z - o.z) / d.z;
        var tb = (hi.z - o.z) / d.z;
        if (ta > tb) { let tmp = ta; ta = tb; tb = tmp; }
        t0 = max(t0, ta); t1 = min(t1, tb);
        if (t0 > t1) { return ClipResult(false, 0.0, 0.0); }
    }
    return ClipResult(true, t0, t1);
}

fn inBounds(v: vec3<i32>, lo: vec3<i32>, hi: vec3<i32>) -> bool {
    return all(v >= lo) && all(v < hi);
}

// Amanatides-Woo DDA over [t0,t1] of a ray already clipped to grid g's box. Boundaries are recomputed from the
// origin every step (not accumulated with a running delta) — accumulation drifts over long rays, and sun rays
// run the length of the loaded area. Any-hit: returns on the first occupied voxel. Bounded to DDA_MAX_STEPS so a
// DDA bug hangs a ray, not the GPU.
fn ddaMarch(g: i32, o: vec3<f32>, d: vec3<f32>, t0: f32, t1: f32, lo: vec3<i32>, hi: vec3<i32>, skipStart: bool) -> bool {
    if (t0 >= t1) { return false; }
    let p0 = clamp(o + d * t0, vec3<f32>(lo), vec3<f32>(hi) - vec3<f32>(0.0001));
    var voxel = clamp(vec3<i32>(floor(p0)), lo, hi - vec3<i32>(1));
    var cc = vec3<i32>(voxel >> vec3<u32>(5u));
    var code = occCode(g, cc);

    let stepX: i32 = select(-1, 1, d.x > 0.0);
    let stepY: i32 = select(-1, 1, d.y > 0.0);
    let stepZ: i32 = select(-1, 1, d.z > 0.0);

    var tMaxX: f32 = select(1e30, ((f32(voxel.x) + select(0.0, 1.0, d.x > 0.0)) - o.x) / d.x, abs(d.x) > 1e-8);
    var tMaxY: f32 = select(1e30, ((f32(voxel.y) + select(0.0, 1.0, d.y > 0.0)) - o.y) / d.y, abs(d.y) > 1e-8);
    var tMaxZ: f32 = select(1e30, ((f32(voxel.z) + select(0.0, 1.0, d.z > 0.0)) - o.z) / d.z, abs(d.z) > 1e-8);

    // skipStart: the ray begins inside a cell that must not count as its own occluder (a lamp's own opaque
    // block). Skipping that one cell, rather than shortening the ray, keeps the exit from it fully tested.
    if (!skipStart && solidCached(g, voxel, &cc, &code)) { return true; }

    for (var iter = 0; iter < DDA_MAX_STEPS; iter = iter + 1) {
        let tMin = min(tMaxX, min(tMaxY, tMaxZ));
        if (tMin >= t1) { return false; }

        // Which axes are (near-)tied for the crossing this step. Ray-direction-relative, not a static
        // property of the voxel — unlike a diagonal-solids-always-block occupancy rule (tried and
        // reverted: it falsely blocks every interior cell of an ordinary enclosed corridor, since a
        // corridor's interior has solid neighbours on both perpendicular axes throughout its whole length,
        // open only along its own direction — that rule can't tell a gap between two blocks from a plain
        // hallway wall). Tie detection only fires for a ray whose OWN trajectory is near the critical diagonal
        // angle, so a ray travelling straight down an open corridor never ties and is untouched; only rays
        // actually grazing a corner get the extra check.
        let tieX = abs(tMaxX - tMin) < TIE_EPS;
        let tieY = abs(tMaxY - tMin) < TIE_EPS;
        let tieZ = abs(tMaxZ - tMin) < TIE_EPS;

        var nvx = voxel.x; var nvy = voxel.y; var nvz = voxel.z;
        if (tieX) { nvx = voxel.x + stepX; }
        if (tieY) { nvy = voxel.y + stepY; }
        if (tieZ) { nvz = voxel.z + stepZ; }

        // For every PAIR of simultaneously-tied axes, test both single-axis-only intermediate cells (the
        // ones a face-only-sealed corner's own wall blocks actually occupy), not just the fully-diagonal
        // cell — a 2-way tie has 2 intermediate cells + 1 diagonal, a 3-way tie has 6 intermediate + 1
        // diagonal. Re-testing the same cell across branches is harmless (just a redundant opacity read).
        if (tieX && tieY) {
            if (solidCached(g, vec3<i32>(nvx, voxel.y, voxel.z), &cc, &code)) { return true; }
            if (solidCached(g, vec3<i32>(voxel.x, nvy, voxel.z), &cc, &code)) { return true; }
        }
        if (tieX && tieZ) {
            if (solidCached(g, vec3<i32>(nvx, voxel.y, voxel.z), &cc, &code)) { return true; }
            if (solidCached(g, vec3<i32>(voxel.x, voxel.y, nvz), &cc, &code)) { return true; }
        }
        if (tieY && tieZ) {
            if (solidCached(g, vec3<i32>(voxel.x, nvy, voxel.z), &cc, &code)) { return true; }
            if (solidCached(g, vec3<i32>(voxel.x, voxel.y, nvz), &cc, &code)) { return true; }
        }
        if (tieX && tieY && tieZ) {
            if (solidCached(g, vec3<i32>(nvx, nvy, voxel.z), &cc, &code)) { return true; }
            if (solidCached(g, vec3<i32>(nvx, voxel.y, nvz), &cc, &code)) { return true; }
            if (solidCached(g, vec3<i32>(voxel.x, nvy, nvz), &cc, &code)) { return true; }
        }

        voxel = vec3<i32>(nvx, nvy, nvz);
        if (!inBounds(voxel, lo, hi)) { return false; }
        if (solidCached(g, voxel, &cc, &code)) { return true; }

        if (tieX) { tMaxX = select(1e30, ((f32(voxel.x) + select(0.0, 1.0, d.x > 0.0)) - o.x) / d.x, abs(d.x) > 1e-8); }
        if (tieY) { tMaxY = select(1e30, ((f32(voxel.y) + select(0.0, 1.0, d.y > 0.0)) - o.y) / d.y, abs(d.y) > 1e-8); }
        if (tieZ) { tMaxZ = select(1e30, ((f32(voxel.z) + select(0.0, 1.0, d.z > 0.0)) - o.z) / d.z, abs(d.z) > 1e-8); }
    }
    return false;
}

// Any-hit occlusion test across every grid. worldDir must be unit length; rigid transforms preserve distance, so
// segLen (a world-space length) bounds the walk identically in every grid's own voxel space once the direction is
// rotated into it.
// skipOrigin: ignore the cell containing worldOrigin in any grid the ray starts inside (clip.t0 == 0) — used for
// lamp rays, which start at the lamp's own opaque block.
fn anyOccluderAlongSegment(worldOrigin: vec3<f32>, worldDir: vec3<f32>, segLen: f32, skipOrigin: bool) -> bool {
    if (segLen <= 0.0) { return false; }
    for (var gi = 0; gi < p.counts.x; gi = gi + 1) {
        let gd = grids[gi];
        if (gd.table.y <= 0) { continue; }
        let lo = (gd.w2v * vec4<f32>(worldOrigin, 1.0)).xyz;
        let ld = (gd.w2v * vec4<f32>(worldDir, 0.0)).xyz; // w=0 drops translation: rotation-only direction transform
        let clip = slabClip(lo, ld, vec3<f32>(gd.bmin.xyz), vec3<f32>(gd.bmax.xyz), 0.0, segLen);
        if (!clip.hit) { continue; }
        let skip = skipOrigin && clip.t0 <= 1e-4;
        if (ddaMarch(gi, lo, ld, clip.t0, clip.t1, gd.bmin.xyz, gd.bmax.xyz, skip)) { return true; }
    }
    return false;
}

// ── Work items ────────────────────────────────────────────────────────────────────────────────────────────

struct Brick { g: i32, origin: vec3<i32>, base: i32 }; // base: the brick's first word in lightPool

fn brickOf(slot: u32) -> Brick {
    let info = slotInfo[slot];
    let b = info.x >> 8u;
    var br: Brick;
    br.g = info.x & 255;
    br.origin = info.yzw * 32 + vec3<i32>(b & 3, (b >> 2u) & 3, b >> 4u) * 8;
    br.base = i32(slot) * 512;
    return br;
}

fn surfaceNudge(g: i32, v: vec3<i32>) -> vec3<f32> {
    var n = vec3<f32>(0.0);
    if (isSolid(g, v - vec3<i32>(1, 0, 0))) { n.x = n.x - 1.0; } if (isSolid(g, v + vec3<i32>(1, 0, 0))) { n.x = n.x + 1.0; }
    if (isSolid(g, v - vec3<i32>(0, 1, 0))) { n.y = n.y - 1.0; } if (isSolid(g, v + vec3<i32>(0, 1, 0))) { n.y = n.y + 1.0; }
    if (isSolid(g, v - vec3<i32>(0, 0, 1))) { n.z = n.z - 1.0; } if (isSolid(g, v + vec3<i32>(0, 0, 1))) { n.z = n.z + 1.0; }
    return n;
}

fn hasSolidNeighbour(g: i32, v: vec3<i32>) -> bool {
    return isSolid(g, v - vec3<i32>(1, 0, 0)) || isSolid(g, v + vec3<i32>(1, 0, 0)) ||
           isSolid(g, v - vec3<i32>(0, 1, 0)) || isSolid(g, v + vec3<i32>(0, 1, 0)) ||
           isSolid(g, v - vec3<i32>(0, 0, 1)) || isSolid(g, v + vec3<i32>(0, 0, 1));
}

// ── Sun ───────────────────────────────────────────────────────────────────────────────────────────────────

// Sun visibility (0-254 lit fraction) of surface air voxels into bits 0-7; everything else gets the 255 'no
// sample' sentinel the fragment shader excludes from its blend rather than treating as lit.
fn sunVoxel(g: i32, v: vec3<i32>, li: i32) {
    let old = lightPool[li] & 0xFFFFFF00u;
    if (isSolid(g, v) || !hasSolidNeighbour(g, v)) { lightPool[li] = old | 255u; return; }

    // Nudge the sample point toward adjacent solid faces so contact shadows reach the foot of a wall instead of
    // being tested from the bare voxel centre.
    let vc = vec3<f32>(v) + vec3<f32>(0.5) + surfaceNudge(g, v) * SURFACE_OFFSET;

    // SUN_SAMPLES rays from points spread through the voxel (a tetrahedral pattern, clamped to stay inside this
    // air cell), stored as the lit fraction. A single binary ray per voxel made a moving caster's shadow edge
    // jump a whole voxel at a time; fractional coverage moves it in quarter steps, which the fragment blend then
    // smooths.
    var offs = array<vec3<f32>, 4>(
        vec3<f32>( 1.0,  1.0,  1.0), vec3<f32>( 1.0, -1.0, -1.0),
        vec3<f32>(-1.0,  1.0, -1.0), vec3<f32>(-1.0, -1.0,  1.0));
    let cellLo = vec3<f32>(v) + vec3<f32>(0.02);
    let cellHi = vec3<f32>(v) + vec3<f32>(0.98);
    let v2w = grids[g].v2w;
    var lit = 0u;
    for (var s = 0; s < SUN_SAMPLES; s = s + 1) {
        let jitter = select(0.0, SUN_JITTER, SUN_SAMPLES > 1); // a single ray uses the unjittered nudged centre
        let sp = clamp(vc + offs[s] * jitter, cellLo, cellHi);
        let world = (v2w * vec4<f32>(sp, 1.0)).xyz;
        if (!anyOccluderAlongSegment(world, -p.sunDir.xyz, SUN_MAX_DISTANCE, false)) { lit = lit + 1u; }
    }
    lightPool[li] = old | u32(round(f32(lit) * 254.0 / f32(SUN_SAMPLES)));
}

// ── Lamps ─────────────────────────────────────────────────────────────────────────────────────────────────

// Point-lamp shadows, every lamp against every grid, no same-grid special case: a lamp on this very grid goes
// through the identical world-space any-hit test as a lamp on another grid entirely. Rays are traced FROM the
// lamp TO the surface voxel over the full distance, skipping only the lamp's own starting cell. (Shortening the
// ray by a fixed radius instead let exact 45-degree rays stop inside an open diagonal cell before reaching the
// lamp cell's edge/corner, so the face blocks covering the lamp were never on the tested segment and light leaked
// through the diagonals.) Brightest lamp wins, into bits 8-15.
fn lampVoxel(g: i32, v: vec3<i32>, li: i32) {
    let old = lightPool[li] & 0xFFFF00FFu;
    if (isSolid(g, v) || !hasSolidNeighbour(g, v)) { lightPool[li] = old; return; }

    let world = (grids[g].v2w * vec4<f32>(vec3<f32>(v) + vec3<f32>(0.5), 1.0)).xyz;
    var best: u32 = 0u;
    for (var k = 0; k < p.counts.y; k = k + 1) {
        let lamp = lamps[k];
        let toLamp = lamp.xyz - world;
        let dist = length(toLamp);
        if (dist > lamp.w) { continue; }               // level doubles as reach radius
        let contribF = round(lamp.w - dist);            // 1-per-voxel falloff
        if (contribF <= 0.0 || u32(contribF) <= best) { continue; }

        if (!anyOccluderAlongSegment(lamp.xyz, -toLamp / dist, dist, true)) {
            best = u32(contribF);
        }
    }
    lightPool[li] = old | (min(best, 15u) << 8u);
}

// One workgroup per brick: 256 threads, each covering voxels (x, y, z) and (x, y, z + 4) of it. The work list can
// exceed the 65535 per-dimension dispatch limit, so it is dispatched as a 2D grid and flattened here.
@compute @workgroup_size(8, 8, 4)
fn sun_main(@builtin(workgroup_id) wid: vec3<u32>, @builtin(num_workgroups) nwg: vec3<u32>,
            @builtin(local_invocation_id) lid: vec3<u32>) {
    let wi = wid.x + wid.y * nwg.x;
    if (wi >= u32(p.counts.z)) { return; }
    let br = brickOf(work[wi]);
    let l = vec3<i32>(lid);
    let li = br.base + l.x + 8 * (l.y + 8 * l.z);
    sunVoxel(br.g, br.origin + l, li);
    sunVoxel(br.g, br.origin + l + vec3<i32>(0, 0, 4), li + 256);
}

@compute @workgroup_size(8, 8, 4)
fn lamp_main(@builtin(workgroup_id) wid: vec3<u32>, @builtin(num_workgroups) nwg: vec3<u32>,
             @builtin(local_invocation_id) lid: vec3<u32>) {
    let wi = wid.x + wid.y * nwg.x;
    if (wi >= u32(p.counts.z)) { return; }
    let br = brickOf(work[wi]);
    let l = vec3<i32>(lid);
    let li = br.base + l.x + 8 * (l.y + 8 * l.z);
    lampVoxel(br.g, br.origin + l, li);
    lampVoxel(br.g, br.origin + l + vec3<i32>(0, 0, 4), li + 256);
}

// ── Bounce (one-hop indirect light, multi-hop through re-evaluation) ─────────────────────────────────────
// Each evaluation, a surface air voxel fires p.bounce2.x short rays (up to BOUNCE_MAX), cosine-distributed
// around its surface normal, through every grid: one slice of a fixed per-voxel direction set that a full
// cycle of p.bounce2.y evaluations covers (see bounceVoxel). At each ray's nearest hit it reads the light
// arriving at the hit face from the air cell in front of it: the brighter of that cell's direct sun
// (visibility x strength x the face's N.L), its lamp light and its own stored bounce; a miss reads 0. The
// mean of those times the albedo is this evaluation's estimate, blended into the stored bounce (0-255, bits
// 24-31 of the light word) with weight alpha = max(1/cycle, 1/(n+1)), n being how many times the brick has
// been evaluated since it changed: a running average over the first cycle (after which the value is exactly
// the complete set's mean), then an EMA weighing one cycle.
// The same rays measure ambient occlusion: the fraction that hit nothing within BOUNCE_MAX is the voxel's sky
// visibility (cosine-weighted, since the rays are), and one minus it is blended into bits 16-23 the same way.
// The fragment shader scales the flat ambient by it, so caves and sealed rooms go dark while open ground,
// whose rays all go up, stays fully lit. Rays cross grids, so a ship darkens the ground under it.
// Reading the stored bounce at the hit is what makes it multi-hop:
// each evaluation adds a hop, and the CPU keeps a changed area evaluating for a number of frames. Keep albedo
// well below 1 or the feedback lingers (a removed light ghosts longer). Ambient is not bounced; it is
// already uniform.
const BOUNCE_MAX: f32 = 16.0;
const BOUNCE_MAX_RAYS: i32 = 32;

struct Hit { hit: bool, t: f32, cell: vec3<i32>, front: bool, n: vec3<f32> }; // cell: air cell in front of the hit face (front = false: none); n: face normal (grid space)

// Nearest-hit DDA over [t0,t1] in one grid. Single-axis steps, no tie handling (a ray slipping through a
// diagonal gap only nudges an averaged value). A ray that enters this grid straight into a solid cell hits
// with no front cell, which reads as unlit.
fn ddaNearest(g: i32, o: vec3<f32>, d: vec3<f32>, t0: f32, t1: f32, lo: vec3<i32>, hi: vec3<i32>) -> Hit {
    var h: Hit;
    h.hit = false;
    if (t0 >= t1) { return h; }
    let p0 = clamp(o + d * t0, vec3<f32>(lo), vec3<f32>(hi) - vec3<f32>(0.0001));
    var voxel = clamp(vec3<i32>(floor(p0)), lo, hi - vec3<i32>(1));
    var cc = vec3<i32>(voxel >> vec3<u32>(5u));
    var code = occCode(g, cc);
    let stepX: i32 = select(-1, 1, d.x > 0.0);
    let stepY: i32 = select(-1, 1, d.y > 0.0);
    let stepZ: i32 = select(-1, 1, d.z > 0.0);
    if (solidCached(g, voxel, &cc, &code)) {
        h.hit = true; h.t = t0; h.front = false; h.n = -d;
        return h;
    }
    var tMaxX: f32 = select(1e30, ((f32(voxel.x) + select(0.0, 1.0, d.x > 0.0)) - o.x) / d.x, abs(d.x) > 1e-8);
    var tMaxY: f32 = select(1e30, ((f32(voxel.y) + select(0.0, 1.0, d.y > 0.0)) - o.y) / d.y, abs(d.y) > 1e-8);
    var tMaxZ: f32 = select(1e30, ((f32(voxel.z) + select(0.0, 1.0, d.z > 0.0)) - o.z) / d.z, abs(d.z) > 1e-8);
    for (var iter = 0; iter < 64; iter = iter + 1) {
        let prev = voxel;
        var n = vec3<f32>(0.0);
        var t = 0.0;
        if (tMaxX <= tMaxY && tMaxX <= tMaxZ) {
            t = tMaxX;
            voxel.x = voxel.x + stepX; n = vec3<f32>(f32(-stepX), 0.0, 0.0);
            tMaxX = ((f32(voxel.x) + select(0.0, 1.0, d.x > 0.0)) - o.x) / d.x;
        } else if (tMaxY <= tMaxZ) {
            t = tMaxY;
            voxel.y = voxel.y + stepY; n = vec3<f32>(0.0, f32(-stepY), 0.0);
            tMaxY = ((f32(voxel.y) + select(0.0, 1.0, d.y > 0.0)) - o.y) / d.y;
        } else {
            t = tMaxZ;
            voxel.z = voxel.z + stepZ; n = vec3<f32>(0.0, 0.0, f32(-stepZ));
            tMaxZ = ((f32(voxel.z) + select(0.0, 1.0, d.z > 0.0)) - o.z) / d.z;
        }
        if (t >= t1) { return h; }
        if (!inBounds(voxel, lo, hi)) { return h; }
        if (solidCached(g, voxel, &cc, &code)) {
            h.hit = true; h.t = t; h.cell = prev; h.front = true; h.n = n;
            return h;
        }
    }
    return h;
}

// Light (0-1) arriving at the hit face from the air cell in front of it, in grid g.
fn radianceAt(g: i32, h: Hit) -> f32 {
    if (!h.front) { return 0.0; }
    let li = lightIndex(g, h.cell);
    if (li < 0) { return 0.0; }
    let word = lightPool[li];
    let blk = f32((word >> 8u) & 0xFFu) / 15.0;
    let bnc = f32(word >> 24u) / 255.0;
    var sun = 0.0;
    let sv = word & 0xFFu;
    if (sv < 255u) {
        let nW = normalize((grids[g].v2w * vec4<f32>(h.n, 0.0)).xyz);
        sun = f32(sv) / 254.0 * p.bounce.y * max(dot(nW, -p.sunDir.xyz), 0.0);
    }
    return max(sun, max(blk, bnc));
}

fn pcg(v: u32) -> u32 {
    let s = v * 747796405u + 2891336453u;
    let w = ((s >> ((s >> 28u) + 4u)) ^ s) * 277803737u;
    return (w >> 22u) ^ w;
}

fn rand01(h: u32) -> f32 { return f32(pcg(h) & 0xFFFFFFu) / 16777216.0; }

fn bounceVoxel(g: i32, v: vec3<i32>, li: i32, alpha: f32, slice: i32) {
    if (isSolid(g, v)) { return; }
    let word = lightPool[li];

    let sxn = isSolid(g, v - vec3<i32>(1, 0, 0)); let sxp = isSolid(g, v + vec3<i32>(1, 0, 0));
    let syn = isSolid(g, v - vec3<i32>(0, 1, 0)); let syp = isSolid(g, v + vec3<i32>(0, 1, 0));
    let szn = isSolid(g, v - vec3<i32>(0, 0, 1)); let szp = isSolid(g, v + vec3<i32>(0, 0, 1));
    var bounce8 = 0.0;   // non-surface air: no bounce, no occlusion
    var occ8 = 0.0;
    if (sxn || sxp || syn || syp || szn || szp) {
        // The voxel's surfaces face away from its solid neighbours; rays are cosine-distributed around that
        // combined normal, so a floor does not light itself with its own downward rays. Opposite solids (a
        // floor and a ceiling) cancel out, and then rays go in every direction.
        var nrm = vec3<f32>(0.0);
        if (sxn) { nrm.x = nrm.x + 1.0; } if (sxp) { nrm.x = nrm.x - 1.0; }
        if (syn) { nrm.y = nrm.y + 1.0; } if (syp) { nrm.y = nrm.y - 1.0; }
        if (szn) { nrm.z = nrm.z + 1.0; } if (szp) { nrm.z = nrm.z - 1.0; }
        let hasN = dot(nrm, nrm) > 0.0;
        let nLoc = select(vec3<f32>(0.0, 1.0, 0.0), normalize(nrm), hasN);
        // Tangent frame around nLoc.
        let up = select(vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(1.0, 0.0, 0.0), abs(nLoc.y) > 0.9);
        let tx = normalize(cross(up, nLoc));
        let ty = cross(nLoc, tx);

        let v2w = grids[g].v2w;
        let origin = (v2w * vec4<f32>(vec3<f32>(v) + vec3<f32>(0.5), 1.0)).xyz;
        // Each voxel has one fixed set of rays * cycle directions, set by its position alone: a cosine-weighted
        // Fibonacci spiral (evenly spread, far less noisy than random directions), twisted and shifted per voxel
        // so neighbours differ. Evaluation n fires slice n mod cycle; slices interleave (direction j = k*cycle +
        // slice), so each one spans the whole hemisphere coarsely and a full cycle covers the complete set.
        let seed = pcg(u32(v.x) ^ pcg(u32(v.y) ^ pcg(u32(v.z))));
        let twist = rand01(seed) * 6.2831853;
        let shift = rand01(seed + 1u);
        var sumL = 0.0;
        var escaped = 0.0;
        let rays = clamp(i32(p.bounce2.x), 1, BOUNCE_MAX_RAYS);
        let cycle = max(i32(p.bounce2.y), 1);
        let total = f32(rays * cycle);
        for (var k = 0; k < rays; k = k + 1) {
            let j = f32(k * cycle + slice);
            let u1 = fract((j + shift) / total);
            let phi = j * 2.39996323 + twist;
            var dl: vec3<f32>;
            if (hasN) {
                let r = sqrt(u1);                                   // cosine-weighted hemisphere
                let hh = sqrt(max(0.0, 1.0 - u1));
                dl = tx * (r * cos(phi)) + ty * (r * sin(phi)) + nLoc * hh;
            } else {
                let cz = 1.0 - 2.0 * u1;                            // uniform sphere
                let r = sqrt(max(0.0, 1.0 - cz * cz));
                dl = vec3<f32>(r * cos(phi), cz, r * sin(phi));
            }

            let dw = (v2w * vec4<f32>(dl, 0.0)).xyz;
            var bestT = BOUNCE_MAX;
            var bestG = -1;
            var best: Hit;
            for (var gi = 0; gi < p.counts.x; gi = gi + 1) {
                let gd = grids[gi];
                if (gd.table.y <= 0) { continue; }
                let lo = (gd.w2v * vec4<f32>(origin, 1.0)).xyz;
                let ld = (gd.w2v * vec4<f32>(dw, 0.0)).xyz;
                let clip = slabClip(lo, ld, vec3<f32>(gd.bmin.xyz), vec3<f32>(gd.bmax.xyz), 0.0, bestT);
                if (!clip.hit) { continue; }
                let h = ddaNearest(gi, lo, ld, clip.t0, clip.t1, gd.bmin.xyz, gd.bmax.xyz);
                if (h.hit && h.t < bestT) { bestT = h.t; bestG = gi; best = h; }
            }
            if (bestG >= 0) { sumL = sumL + radianceAt(bestG, best); }
            else { escaped = escaped + 1.0; }
        }
        let estimate = clamp(p.bounce.x * sumL / f32(rays), 0.0, 1.0);
        let occEstimate = 1.0 - escaped / f32(rays);
        bounce8 = blend8(f32(word >> 24u), estimate * 255.0, alpha);
        occ8 = blend8(f32((word >> 16u) & 0xFFu), occEstimate * 255.0, alpha);
    }
    lightPool[li] = (word & 0x0000FFFFu) | (u32(occ8) << 16u) | (u32(bounce8) << 24u);
}

// One blend step of an 8-bit stored value toward target (both 0-255). With 8-bit storage a small alpha can
// stall a step short of the target (a change under half a step rounds away), so it always moves at least one
// step toward it.
fn blend8(s8: f32, goal: f32, alpha: f32) -> f32 {
    var q = round(s8 + alpha * (goal - s8));
    if (q == s8 && abs(goal - s8) >= 1.0) { q = s8 + sign(goal - s8); }
    return clamp(q, 0.0, 255.0);
}

@compute @workgroup_size(8, 8, 4)
fn bounce_main(@builtin(workgroup_id) wid: vec3<u32>, @builtin(num_workgroups) nwg: vec3<u32>,
               @builtin(local_invocation_id) lid: vec3<u32>) {
    // The bounce work list is pairs: (light slot, evaluations since the brick changed).
    let wi = wid.x + wid.y * nwg.x;
    if (wi >= u32(p.counts.z)) { return; }
    let br = brickOf(work[wi * 2u]);
    let nu = work[wi * 2u + 1u];
    // A running average over the first full cycle (exactly the complete ray set's mean), then a weight of one
    // cycle, so each further cycle weighs the whole set about equally.
    let cycle = max(u32(p.bounce2.y), 1u);
    let alpha = max(1.0 / f32(cycle), 1.0 / (f32(nu) + 1.0));
    let slice = i32(nu % cycle);
    let l = vec3<i32>(lid);
    let li = br.base + l.x + 8 * (l.y + 8 * l.z);
    bounceVoxel(br.g, br.origin + l, li, alpha, slice);
    bounceVoxel(br.g, br.origin + l + vec3<i32>(0, 0, 4), li + 256, alpha, slice);
}";

    private readonly GpuContext _ctx;
    private readonly ComputePipeline _sunPipeline;
    private readonly ComputePipeline _lampPipeline;
    private readonly ComputePipeline _bouncePipeline;
    private readonly GpuBuffer _param;
    private readonly GpuBuffer _lampList;     // this frame's lamps (xyz world, w level)
    private bool _loggedTooManyLamps;

    // Bindings each entry point uses (auto layouts only contain what the entry point references).
    private static readonly uint[] SunBindings    = { 0, 1, 3, 4, 5, 6, 8 };
    private static readonly uint[] LampBindings   = { 0, 1, 3, 4, 5, 6, 7, 8 };
    private static readonly uint[] BounceBindings = { 0, 1, 2, 3, 4, 5, 6, 8 };

    public GpuRayLightPass(GpuContext ctx)
    {
        _ctx = ctx;
        _sunPipeline    = new ComputePipeline(ctx, Wgsl, "sun_main");
        _lampPipeline   = new ComputePipeline(ctx, Wgsl, "lamp_main");
        _bouncePipeline = new ComputePipeline(ctx, Wgsl, "bounce_main");
        _param    = GpuBuffer.CreateUniform(ctx, (ulong)Marshal.SizeOf<RayParams>());
        _lampList = GpuBuffer.CreateStorage(ctx, (ulong)MaxLamps * 16);
    }

    /// <summary>Sun visibility for the <paramref name="count"/> light slots listed in <paramref name="work"/>.</summary>
    public void DispatchSun(GridStore store, int gridCount, Vector3D<float> sunDir, GpuBuffer work, int count)
    {
        if (count <= 0) return;
        WriteParams(sunDir, gridCount, 0, count, default, default);
        Dispatch(_sunPipeline, SunBindings, store, work, count);
    }

    /// <summary>Lamp light for the listed slots. Every lamp is tested by every voxel (nearest-first is not
    /// needed: distance rejects most of them before any ray).</summary>
    public void DispatchLamps(GridStore store, int gridCount, ReadOnlySpan<Vector4D<float>> lamps, GpuBuffer work, int count)
    {
        if (count <= 0) return;
        int lampCount = lamps.Length;
        if (lampCount > MaxLamps)
        {
            if (!_loggedTooManyLamps)
            {
                _loggedTooManyLamps = true;
                Console.WriteLine($"[ray-lighting] {lampCount} lamps loaded; only the first {MaxLamps} light anything.");
            }
            lampCount = MaxLamps;
        }
        if (lampCount > 0) _lampList.Write<Vector4D<float>>(0, lamps.Slice(0, lampCount));
        WriteParams(Vector3D<float>.Zero, gridCount, lampCount, count, default, default);
        Dispatch(_lampPipeline, LampBindings, store, work, count);
    }

    /// <summary>
    /// One EMA step of bounce light for the listed bricks (see bounce_main). Reads direct light of every grid, so
    /// run it after the direct passes. <paramref name="work"/> holds <paramref name="count"/> pairs of (light slot,
    /// evaluations since that brick changed). Each voxel's fixed direction set is <paramref name="rays"/> x
    /// <paramref name="cycle"/> directions, one slice of <paramref name="rays"/> per evaluation.
    /// </summary>
    public void DispatchBounce(GridStore store, int gridCount, Vector3D<float> sunDir, float sunStrength,
                               float albedo, int rays, int cycle, GpuBuffer work, int count)
    {
        if (count <= 0) return;
        WriteParams(sunDir, gridCount, 0, count,
                    new Vector4D<float>(albedo, sunStrength, 0f, 0f), new Vector4D<float>(rays, cycle, 0f, 0f));
        Dispatch(_bouncePipeline, BounceBindings, store, work, count);
    }

    private void Dispatch(ComputePipeline pipeline, uint[] bindings, GridStore store, GpuBuffer work, int count)
    {
        var entries = new (uint, GpuBuffer)[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
            entries[i] = (bindings[i], bindings[i] switch
            {
                0 => store.OccPool,
                1 => store.ChunkTable,
                2 => store.BrickTable,
                3 => store.LightPool,
                4 => store.SlotInfo,
                5 => store.Grids,
                6 => work,
                7 => _lampList,
                _ => _param,
            });
        nint bg = pipeline.CreateBindGroupHandle(entries);

        // One workgroup per brick, as a 2D grid since a big list can exceed the 65535-per-dimension limit.
        const int MaxPerDim = 65535;
        uint gx = (uint)System.Math.Min(count, MaxPerDim);
        uint gy = ((uint)count + gx - 1u) / gx;
        pipeline.Dispatch(bg, gx, gy, 1u);
        _ctx.Api.BindGroupRelease((BindGroup*)bg);
    }

    private void WriteParams(Vector3D<float> sunDir, int gridCount, int lampCount, int workCount,
                             Vector4D<float> bounce, Vector4D<float> bounce2)
    {
        Span<RayParams> sp = stackalloc RayParams[1];
        sp[0] = new RayParams
        {
            Sun = new Vector4D<float>(sunDir.X, sunDir.Y, sunDir.Z, 0f),
            GridCount = gridCount, LampCount = lampCount, WorkCount = workCount,
            Bounce = bounce, Bounce2 = bounce2,
        };
        _param.Write<RayParams>(0, sp);
    }

    public void Dispose()
    {
        _sunPipeline.Dispose();
        _lampPipeline.Dispose();
        _bouncePipeline.Dispose();
        _param.Dispose();
        _lampList.Dispose();
    }

    /// <summary>Uniform block (64 bytes); matches WGSL <c>Params</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RayParams
    {
        public Vector4D<float> Sun;
        public int GridCount, LampCount, WorkCount, Pad;
        public Vector4D<float> Bounce;    // albedo, sun strength, unused, unused
        public Vector4D<float> Bounce2;   // rays per evaluation, cycle
    }
}
