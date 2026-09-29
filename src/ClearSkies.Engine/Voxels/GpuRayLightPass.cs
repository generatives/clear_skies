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
/// other. Which grids and lamps a voxel tests comes from its chunk's list (<see cref="UploadLists"/>), so the cost
/// per voxel doesn't grow with the number of ships or lamps elsewhere.
///
/// <para>Work is a list of light slots (surface bricks) from any grid: one workgroup per brick, 256 threads each
/// owning two voxels — one display word and two accumulation words (see the light layout in the WGSL). Passes run as
/// separate submissions in the order sun (display sun bits), bounce (accumulation), compose (display colour and
/// AO), so no word is ever written by two threads at once.</para>
/// </summary>
internal sealed unsafe class GpuRayLightPass : IDisposable
{
    private static readonly string Wgsl = @"
" + GridStore.LookupWgsl + @"

struct Params {
    sunDir: vec4<f32>,
    counts: vec4<i32>,  // y: word offset of the lamp records in lists, z: work entries this dispatch
    bounce: vec4<f32>,  // x: albedo (fraction of incoming light a surface re-emits), y: sun strength (0-1),
                        // z: bounce display scale (0 = bounce off)
    bounce2: vec4<f32>, // x: rays per evaluation, y: evaluations per full ray set (cycle); compose: z: ambient
                        // (0-1), w: ray AO strength (0-1)
};

@group(0) @binding(0) var<storage, read> occPool: array<u32>;
@group(0) @binding(1) var<storage, read> chunkTable: array<vec4<i32>>;
@group(0) @binding(2) var<storage, read> brickTable: array<u32>;
@group(0) @binding(3) var<storage, read_write> lightPool: array<u32>;
@group(0) @binding(4) var<storage, read> slotInfo: array<vec4<i32>>;
@group(0) @binding(5) var<storage, read> grids: array<GridDesc>;
@group(0) @binding(6) var<storage, read> work: array<u32>;
@group(0) @binding(7) var<storage, read> lists: array<u32>;
@group(0) @binding(8) var<uniform> p: Params;
@group(0) @binding(9) var<storage, read_write> accPool: array<u32>;
@group(0) @binding(10) var<storage, read> accMap: array<u32>;

const WPC: i32 = " + GridStore.WordsPerChunk + @";
const OCC_UNLOADED: i32 = " + GridStore.OccUnloaded + @";
const OCC_ALL_SOLID: i32 = " + GridStore.OccAllSolid + @";
const OCC_ALL_AIR: i32 = " + GridStore.OccAllAir + @";
const NO_SURFACE: u32 = " + GridStore.NoSurface + @"u;
const SUN_MAX_DISTANCE: f32 = 100000.0; // grid bounds clip it; empty chunks and bricks are crossed in one step
const SURFACE_OFFSET: f32 = 0.4;
const SUN_SAMPLES: i32 = 4;      // sun rays per surface voxel: 1 (from the nudged centre) or up to 4 (offset table size)
const SUN_JITTER: f32 = 0.25;    // how far (voxels) each sample point is offset from the nudged centre, per axis
// ddaMarch: axes within this of the step minimum are treated as tied (corner-cutting fix). Generous, not a
// tight float-epsilon — lamp/surface positions are usually voxel-centred, so exact ties are the COMMON
// case, and the world-to-local transform's own rounding can perturb an exact tie past a tiny epsilon.
// Widening this costs a few extra (correct) opacity reads on genuinely near-tied rays; it can't cause false
// blocking, since the extra checks still test real occupancy, not a blanket geometric rule.
const TIE_EPS: f32 = 1e-2;
const DDA_MAX_STEPS: i32 = 4096;

// ── Storage lookups ───────────────────────────────────────────────────────────────────────────────────────

fn occCode(g: i32, c: vec3<i32>) -> i32 {
    let i = entryOf(g, c);
    if (i < 0) { return OCC_UNLOADED; }
    return chunkTable[2 * i].x;
}

// (occupancy code, solid-brick mask low, high, 0) of chunk c.
fn chunkInfo(g: i32, c: vec3<i32>) -> vec4<i32> {
    let i = entryOf(g, c);
    if (i < 0) { return vec4<i32>(OCC_UNLOADED, 0, 0, 0); }
    let m = chunkTable[2 * i + 1];
    return vec4<i32>(chunkTable[2 * i].x, m.x, m.y, 0);
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

// Loads v's chunk info into *info unless it is already the cached chunk *cc (DDA loops).
fn cacheChunk(g: i32, v: vec3<i32>, cc: ptr<function, vec3<i32>>, info: ptr<function, vec4<i32>>) {
    let c = v >> vec3<u32>(5u);
    if (any(c != *cc)) { *cc = c; *info = chunkInfo(g, c); }
}

fn solidCached(g: i32, v: vec3<i32>, cc: ptr<function, vec3<i32>>, info: ptr<function, vec4<i32>>) -> bool {
    cacheChunk(g, v, cc, info);
    return solidIn((*info).x, v);
}

// Size of the empty cell around v that a ray can cross in one step: 32 for an all-air or unloaded chunk, 8 for a
// brick with no solid in it, 0 if v has to be tested.
fn emptyCell(info: vec4<i32>, v: vec3<i32>) -> i32 {
    if (info.x == OCC_UNLOADED || info.x == OCC_ALL_AIR) { return 32; }
    let l = v & vec3<i32>(31);
    let b = u32((l.x >> 3u) + 4 * ((l.y >> 3u) + 4 * (l.z >> 3u)));
    let word = select(bitcast<u32>(info.y), bitcast<u32>(info.z), b >= 32u);
    if (((word >> (b & 31u)) & 1u) == 0u) { return 8; }
    return 0;
}

// Per-axis ray parameter at which the ray leaves voxel v (Amanatides-Woo tMax), from the origin.
fn tMaxOf(v: vec3<i32>, o: vec3<f32>, d: vec3<f32>) -> vec3<f32> {
    let nb = vec3<f32>(v) + select(vec3<f32>(0.0), vec3<f32>(1.0), d > vec3<f32>(0.0));
    return select(vec3<f32>(1e30), (nb - o) / d, abs(d) > vec3<f32>(1e-8));
}

// Crossing the empty cell of the given size around v in one step. kind: 0 = can't (the exit is near an edge or
// corner, where the per-voxel walk's tie handling must see the neighbouring cells), 1 = jumped, 2 = the ray ends
// inside the cell. v: the voxel just past the exit face; prev: the last voxel inside the cell; n: exit face normal.
struct Jump { kind: i32, v: vec3<i32>, prev: vec3<i32>, t: f32, n: vec3<f32> };

fn jumpCell(v: vec3<i32>, size: i32, o: vec3<f32>, d: vec3<f32>, t1: f32) -> Jump {
    var j: Jump;
    j.kind = 0;
    let sh = select(3u, 5u, size == 32);
    let lo = (v >> vec3<u32>(sh)) << vec3<u32>(sh);
    let hi = lo + vec3<i32>(size);
    let bound = select(vec3<f32>(lo), vec3<f32>(hi), d > vec3<f32>(0.0));
    let te = select(vec3<f32>(1e30), (bound - o) / d, abs(d) > vec3<f32>(1e-8));
    let tExit = min(te.x, min(te.y, te.z));
    if (tExit >= t1) { j.kind = 2; return j; }
    let ties = select(0, 1, abs(te.x - tExit) < TIE_EPS) + select(0, 1, abs(te.y - tExit) < TIE_EPS)
             + select(0, 1, abs(te.z - tExit) < TIE_EPS);
    if (ties > 1) { return j; }

    // Clamp into the cell, then push the exit axis one voxel through the exit face.
    let inside = clamp(vec3<i32>(floor(o + d * tExit)), lo, hi - vec3<i32>(1));
    var nv = inside;
    var pv = inside;
    var n = vec3<f32>(0.0);
    if (te.x == tExit) {
        pv.x = select(lo.x, hi.x - 1, d.x > 0.0); nv.x = select(lo.x - 1, hi.x, d.x > 0.0);
        n.x = select(1.0, -1.0, d.x > 0.0);
    } else if (te.y == tExit) {
        pv.y = select(lo.y, hi.y - 1, d.y > 0.0); nv.y = select(lo.y - 1, hi.y, d.y > 0.0);
        n.y = select(1.0, -1.0, d.y > 0.0);
    } else {
        pv.z = select(lo.z, hi.z - 1, d.z > 0.0); nv.z = select(lo.z - 1, hi.z, d.z > 0.0);
        n.z = select(1.0, -1.0, d.z > 0.0);
    }
    j.kind = 1; j.v = nv; j.prev = pv; j.t = tExit; j.n = n;
    return j;
}

// ── Light layout ──────────────────────────────────────────────────────────────────────────────────────────
// A light slot (one 8³ brick, voxel k = x + 8*(y + 8*z)) is SLOT_WORDS u32s:
//   display: one u16 per voxel, two per word (k even = low half): bits 0-8 brightness, 9-13 warmth, 14-15 sun
//   visibility (0-3). Brightness and warmth hold all the non-sun light (ambient times the ray AO, lamps and bounce,
//   combined per channel by max; see encodeLight). The fragment shader reads only this.
// A brick whose bounce is being evaluated also has an accumulation slot in accPool (accMap[slot] = its index + 1, 0
// for none), ACC_WORDS u32s, one per voxel: bits 0-7 bounce R, 8-15 G, 16-23 B (linear 0-1), 24-31 AO occlusion
// (0-255), blended by the bounce pass. The CPU hands one out when the brick's evaluation starts and takes it back when
// its evaluations are done, so a settled brick keeps only its display.
const SLOT_WORDS: i32 = " + GridStore.WordsPerSlot + @";
const ACC_WORDS: i32 = " + GridStore.AccWordsPerSlot + @";

// First accPool word of light slot s's accumulation, or -1 if it has none.
fn accOf(s: u32) -> i32 {
    let a = accMap[s];
    if (a == 0u) { return -1; }
    return i32(a - 1u) * ACC_WORDS;
}

// Display light is a brightness (the brightest channel, 0-510 on a square curve, so ambient and bounce at the dark
// end get fine steps; 511 is kept for no light storage) and a warmth (0-30) along one line of tints: 0 cool
// blue-white, 15 white, 30 deep red. Other colours are carried to the nearest tint on that line.
" + GridStore.LightCodecWgsl + @"

// disp: display word, shift: 0 or 16 into it; acc: accPool word, or -1 if the brick has no accumulation.
struct VoxelRef { ok: bool, disp: i32, shift: u32, acc: i32 };

// Where voxel v of grid g lives in lightPool (ok = false: it has no light storage).
fn voxelRef(g: i32, v: vec3<i32>) -> VoxelRef {
    var r: VoxelRef;
    r.ok = false;
    let i = entryOf(g, v >> vec3<u32>(5u));
    if (i < 0) { return r; }
    let l = v & vec3<i32>(31);
    let b = (l.x >> 3u) + 4 * ((l.y >> 3u) + 4 * (l.z >> 3u));
    let s = brickTable[u32(i * 64 + b)];
    if (s == NO_SURFACE) { return r; }
    let lb = l & vec3<i32>(7);
    let k = lb.x + 8 * (lb.y + 8 * lb.z);
    r.ok = true;
    r.disp = i32(s) * SLOT_WORDS + (k >> 1u);
    r.shift = u32(k & 1) * 16u;
    let a = accOf(s);
    r.acc = select(-1, a + k, a >= 0);
    return r;
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

// ── Per-chunk lists ───────────────────────────────────────────────────────────────────────────────────────
// Clustered shading with the chunk as the cluster. The CPU rebuilds these every frame for the chunks that have work
// (GpuLightSystem.Lists.cs), so a voxel only traces the grids and lamps that can reach its chunk instead of every
// grid and lamp there is. Layout of lists:
//   [0, 2)                 the empty list (no grids, no lamps), for chunks with no list this frame;
//   [2, 2 + table size)    per chunk-table entry, the offset of that chunk's list;
//   [p.counts.y, ...)      lamp records, 8 words each: world position xyz, level (= reach), colour rgb (f32 bits), and
//                          open faces (bits 0-5: +x, -x, +y, -y, +z, -z in its grid) | its grid index << 6;
//   then the lists: grid count, grid indices, lamp count, lamp indices.
// A chunk's grids are every grid whose solid can block a ray from it: the world, its own grid, and any ship near
// it or between it and the sun.
fn listOf(g: i32, v: vec3<i32>) -> u32 {
    let i = entryOf(g, v >> vec3<u32>(5u));
    if (i < 0) { return 0u; }
    return lists[2u + u32(i)];
}

fn inBounds(v: vec3<i32>, lo: vec3<i32>, hi: vec3<i32>) -> bool {
    return all(v >= lo) && all(v < hi);
}

// Amanatides-Woo DDA over [t0,t1] of a ray already clipped to grid g's box. Boundaries are recomputed from the
// origin every step (not accumulated with a running delta) — accumulation drifts over long rays, and sun rays
// run the length of the loaded area. All-air/unloaded chunks and bricks with no solid are crossed in one step.
// Any-hit: returns on the first occupied voxel. Bounded to DDA_MAX_STEPS so a DDA bug hangs a ray, not the GPU.
fn ddaMarch(g: i32, o: vec3<f32>, d: vec3<f32>, t0: f32, t1: f32, lo: vec3<i32>, hi: vec3<i32>, skipStart: bool) -> bool {
    if (t0 >= t1) { return false; }
    let p0 = clamp(o + d * t0, vec3<f32>(lo), vec3<f32>(hi) - vec3<f32>(0.0001));
    var voxel = clamp(vec3<i32>(floor(p0)), lo, hi - vec3<i32>(1));
    var cc = voxel >> vec3<u32>(5u);
    var info = chunkInfo(g, cc);

    let stepX: i32 = select(-1, 1, d.x > 0.0);
    let stepY: i32 = select(-1, 1, d.y > 0.0);
    let stepZ: i32 = select(-1, 1, d.z > 0.0);
    var tm = tMaxOf(voxel, o, d);

    // skipStart: the ray begins inside a cell that must not count as its own occluder (a lamp's own opaque
    // block). Skipping that one cell, rather than shortening the ray, keeps the exit from it fully tested.
    var testCurrent = !skipStart;

    for (var iter = 0; iter < DDA_MAX_STEPS; iter = iter + 1) {
        cacheChunk(g, voxel, &cc, &info);
        let empty = emptyCell(info, voxel);
        if (empty == 0) {
            if (testCurrent && solidIn(info.x, voxel)) { return true; }
        } else if (testCurrent) {
            let j = jumpCell(voxel, empty, o, d, t1);
            if (j.kind == 2) { return false; }
            if (j.kind == 1) {
                voxel = j.v;
                if (!inBounds(voxel, lo, hi)) { return false; }
                tm = tMaxOf(voxel, o, d);
                continue;
            }
        }
        testCurrent = true;

        let tMin = min(tm.x, min(tm.y, tm.z));
        if (tMin >= t1) { return false; }

        // Which axes are (near-)tied for the crossing this step. Ray-direction-relative, not a static
        // property of the voxel — unlike a diagonal-solids-always-block occupancy rule (tried and
        // reverted: it falsely blocks every interior cell of an ordinary enclosed corridor, since a
        // corridor's interior has solid neighbours on both perpendicular axes throughout its whole length,
        // open only along its own direction — that rule can't tell a gap between two blocks from a plain
        // hallway wall). Tie detection only fires for a ray whose OWN trajectory is near the critical diagonal
        // angle, so a ray travelling straight down an open corridor never ties and is untouched; only rays
        // actually grazing a corner get the extra check.
        let tieX = abs(tm.x - tMin) < TIE_EPS;
        let tieY = abs(tm.y - tMin) < TIE_EPS;
        let tieZ = abs(tm.z - tMin) < TIE_EPS;

        var nvx = voxel.x; var nvy = voxel.y; var nvz = voxel.z;
        if (tieX) { nvx = voxel.x + stepX; }
        if (tieY) { nvy = voxel.y + stepY; }
        if (tieZ) { nvz = voxel.z + stepZ; }

        // For every PAIR of simultaneously-tied axes, test both single-axis-only intermediate cells (the
        // ones a face-only-sealed corner's own wall blocks actually occupy), not just the fully-diagonal
        // cell — a 2-way tie has 2 intermediate cells + 1 diagonal, a 3-way tie has 6 intermediate + 1
        // diagonal. Re-testing the same cell across branches is harmless (just a redundant opacity read).
        if (tieX && tieY) {
            if (solidCached(g, vec3<i32>(nvx, voxel.y, voxel.z), &cc, &info)) { return true; }
            if (solidCached(g, vec3<i32>(voxel.x, nvy, voxel.z), &cc, &info)) { return true; }
        }
        if (tieX && tieZ) {
            if (solidCached(g, vec3<i32>(nvx, voxel.y, voxel.z), &cc, &info)) { return true; }
            if (solidCached(g, vec3<i32>(voxel.x, voxel.y, nvz), &cc, &info)) { return true; }
        }
        if (tieY && tieZ) {
            if (solidCached(g, vec3<i32>(voxel.x, nvy, voxel.z), &cc, &info)) { return true; }
            if (solidCached(g, vec3<i32>(voxel.x, voxel.y, nvz), &cc, &info)) { return true; }
        }
        if (tieX && tieY && tieZ) {
            if (solidCached(g, vec3<i32>(nvx, nvy, voxel.z), &cc, &info)) { return true; }
            if (solidCached(g, vec3<i32>(nvx, voxel.y, nvz), &cc, &info)) { return true; }
            if (solidCached(g, vec3<i32>(voxel.x, nvy, nvz), &cc, &info)) { return true; }
        }

        voxel = vec3<i32>(nvx, nvy, nvz);
        if (!inBounds(voxel, lo, hi)) { return false; }
        tm = tMaxOf(voxel, o, d);
    }
    return false;
}

// Any-hit occlusion test across the grids of a chunk's list. worldDir must be unit length; rigid transforms preserve
// distance, so segLen (a world-space length) bounds the walk identically in every grid's own voxel space once the
// direction is rotated into it.
// skipOrigin: ignore the cell containing worldOrigin in any grid the ray starts inside (clip.t0 == 0) — used for
// lamp rays, which start at the lamp's own opaque block.
fn anyOccluderAlongSegment(list: u32, worldOrigin: vec3<f32>, worldDir: vec3<f32>, segLen: f32, skipOrigin: bool) -> bool {
    if (segLen <= 0.0) { return false; }
    let count = lists[list];
    for (var k = 0u; k < count; k = k + 1u) {
        let gi = i32(lists[list + 1u + k]);
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
// One workgroup per brick, 256 threads: thread t owns voxels k = 2t and 2t + 1 (x even and x + 1), i.e. exactly one
// display word and two accumulation words, so every pass writes whole words no other thread touches. The work list
// can exceed the 65535 per-dimension dispatch limit, so it is dispatched as a 2D grid and flattened here.

struct Item { g: i32, v0: vec3<i32>, disp: i32, slot: u32 }; // v0: the thread's first voxel (the second is +x)

fn itemOf(slot: u32, t: u32) -> Item {
    let info = slotInfo[slot];
    let b = info.x & 63;
    let k = i32(t) * 2;
    var it: Item;
    it.g = info.x >> 6u;
    it.v0 = info.yzw * 32 + vec3<i32>(b & 3, (b >> 2u) & 3, b >> 4u) * 8 + vec3<i32>(k & 7, (k >> 3u) & 7, k >> 6u);
    it.disp = i32(slot) * SLOT_WORDS + i32(t);
    it.slot = slot;
    return it;
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

// Sun visibility of a surface air voxel, 0 (shadowed) to 3 (lit). Anything else reads 3: the fragment shader only
// blends cells on the face's own surface, so the value never shows.
fn sunLevel(g: i32, v: vec3<i32>, list: u32) -> u32 {
    if (isSolid(g, v) || !hasSolidNeighbour(g, v)) { return 3u; }

    // Nudge the sample point toward adjacent solid faces so contact shadows reach the foot of a wall instead of
    // being tested from the bare voxel centre.
    let vc = vec3<f32>(v) + vec3<f32>(0.5) + surfaceNudge(g, v) * SURFACE_OFFSET;

    // SUN_SAMPLES rays from points spread through the voxel (a tetrahedral pattern, clamped to stay inside this
    // air cell), stored as the lit fraction. A single binary ray per voxel made a moving caster's shadow edge
    // jump a whole voxel at a time; fractional coverage moves it in partial steps, which the fragment blend then
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
        if (!anyOccluderAlongSegment(list, world, -p.sunDir.xyz, SUN_MAX_DISTANCE, false)) { lit = lit + 1u; }
    }
    return u32(round(f32(lit) * 3.0 / f32(SUN_SAMPLES)));
}

// ── Surface compaction ────────────────────────────────────────────────────────────────────────────────────
// Only surface air voxels (air with a solid face neighbour) trace rays, usually a small share of a brick, scattered
// through it. Given one voxel pair per thread, most lanes of every SIMD group would sit idle while a few traced, so
// the ray passes first gather the brick's surface voxels into a workgroup list and then trace it with consecutive
// threads: the same work in far fewer (and full) SIMD groups.
var<workgroup> wList: array<u32, 512>;      // brick voxel indices (x + 8y + 64z) of the surface air voxels
var<workgroup> wCount: atomic<u32>;
var<workgroup> wSun: array<u32, 512>;       // sun_main: each voxel's result, for the paired write

fn isSurfaceAir(g: i32, v: vec3<i32>) -> bool { return !isSolid(g, v) && hasSolidNeighbour(g, v); }

@compute @workgroup_size(256)
fn sun_main(@builtin(workgroup_id) wid: vec3<u32>, @builtin(num_workgroups) nwg: vec3<u32>,
            @builtin(local_invocation_index) t: u32) {
    let wi = wid.x + wid.y * nwg.x;
    if (wi >= u32(p.counts.z)) { return; }
    if (t == 0u) { atomicStore(&wCount, 0u); }
    workgroupBarrier();
    let it = itemOf(work[wi], t);
    let base = it.v0 - vec3<i32>(i32(t * 2u) & 7, (i32(t * 2u) >> 3u) & 7, i32(t * 2u) >> 6u);
    for (var e = 0u; e < 2u; e = e + 1u) {
        let k = t * 2u + e;
        wSun[k] = 3u; // not a surface voxel: reads lit (see sunLevel)
        if (isSurfaceAir(it.g, it.v0 + vec3<i32>(i32(e), 0, 0))) { wList[atomicAdd(&wCount, 1u)] = k; }
    }
    workgroupBarrier();
    let n = atomicLoad(&wCount);
    let list = listOf(it.g, it.v0);
    for (var i = t; i < n; i = i + 256u) {
        let k = wList[i];
        wSun[k] = sunLevel(it.g, base + vec3<i32>(i32(k & 7u), i32((k >> 3u) & 7u), i32(k >> 6u)), list);
    }
    workgroupBarrier();
    let s0 = wSun[t * 2u];
    let s1 = wSun[t * 2u + 1u];
    let old = lightPool[it.disp] & ~((3u << 14u) | (3u << 30u));
    lightPool[it.disp] = old | (s0 << 14u) | (s1 << 30u);
}

// ── Lamps and composition ─────────────────────────────────────────────────────────────────────────────────

// Whether light from a lamp (centre c, open faces `open` of grid g) reaches world point dest: from each open face
// dest is in front of, a segment from just past the face (inside the open cell) to it, lit if any is
// clear. Faces the point is behind don't count, so a lamp with its top and bottom open lights what is below it
// exactly as one open only at the bottom. Starting at the face rather than the lamp's centre keeps a lamp set in a
// ceiling or wall from being blocked by the blocks beside it, so it lights along a tunnel; starting in the open cell
// (not stopping short of the lamp) still tests every block the light passes, so it can't leak through diagonals.
fn lampReaches(list: u32, c: vec3<f32>, open: u32, g: i32, dest: vec3<f32>) -> bool {
    let m = grids[g].v2w;
    for (var f = 0u; f < 6u; f = f + 1u) {
        if ((open & (1u << f)) == 0u) { continue; }
        let s = select(-1.0, 1.0, (f & 1u) == 0u);
        var nl = vec3<f32>(0.0, 0.0, s);
        if (f < 2u) { nl = vec3<f32>(s, 0.0, 0.0); } else if (f < 4u) { nl = vec3<f32>(0.0, s, 0.0); }
        let n = (m * vec4<f32>(nl, 0.0)).xyz;
        let o = c + n * 0.501;
        let d = dest - o;
        if (dot(d, n) <= 0.0) { continue; } // behind this face
        let len = length(d);
        if (len < 1e-4 || !anyOccluderAlongSegment(list, o, d / len, len, false)) { return true; }
    }
    return false;
}

// Point-lamp light (linear 0-1 per channel) at air voxel v, from the lamps in its chunk's list. No same-grid special
// case: a lamp on this very grid goes through the identical world-space any-hit test as a lamp on another grid
// entirely (see lampReaches). Each lamp falls off one level per block from its centre, from its level, times its
// colour; per channel the brightest lamp wins.
fn lampLight(g: i32, v: vec3<i32>, list: u32) -> vec3<f32> {
    let world = (grids[g].v2w * vec4<f32>(vec3<f32>(v) + vec3<f32>(0.5), 1.0)).xyz;
    var best = vec3<f32>(0.0);
    let lampList = list + 1u + lists[list];
    let count = lists[lampList];
    for (var k = 0u; k < count; k = k + 1u) {
        let r = u32(p.counts.y) + 8u * lists[lampList + 1u + k];
        let lamp = vec4<f32>(bitcast<f32>(lists[r]), bitcast<f32>(lists[r + 1u]), bitcast<f32>(lists[r + 2u]),
                             bitcast<f32>(lists[r + 3u]));
        let col = vec3<f32>(bitcast<f32>(lists[r + 4u]), bitcast<f32>(lists[r + 5u]), bitcast<f32>(lists[r + 6u]));
        let toLamp = lamp.xyz - world;
        let dist = length(toLamp);
        if (dist > lamp.w) { continue; }               // level doubles as reach radius
        let contribF = round(lamp.w - dist);            // 1-per-voxel falloff
        let c = vec3<f32>(contribF) * col;
        if (contribF <= 0.0 || all(c <= best)) { continue; }

        let faces = lists[r + 7u];
        if (lampReaches(list, lamp.xyz, faces & 63u, i32(faces >> 6u), world)) {
            best = max(best, c);
        }
    }
    return min(best, vec3<f32>(15.0)) / 15.0;
}

fn unpackAcc(a: u32) -> vec4<f32> {
    return vec4<f32>(f32(a & 0xFFu), f32((a >> 8u) & 0xFFu), f32((a >> 16u) & 0xFFu), f32(a >> 24u)) / 255.0;
}

// Face direction f (0-5: +x, -x, +y, -y, +z, -z).
fn faceDir(f: i32) -> vec3<i32> {
    let s = select(-1, 1, (f & 1) == 0);
    if (f < 2) { return vec3<i32>(s, 0, 0); }
    if (f < 4) { return vec3<i32>(0, s, 0); }
    return vec3<i32>(0, 0, s);
}

// Whether n is surface air (air with a solid face neighbour), testing through the cached chunk.
fn surfaceAirCached(g: i32, n: vec3<i32>, cc: ptr<function, vec3<i32>>, info: ptr<function, vec4<i32>>) -> bool {
    if (solidCached(g, n, cc, info)) { return false; }
    for (var f = 0; f < 6; f = f + 1) {
        if (solidCached(g, n + faceDir(f), cc, info)) { return true; }
    }
    return false;
}

// A surface voxel's stored bounce (rgb) and AO (a), averaged with those of the surface air around it: its face
// neighbours (weight 1) and its edge diagonals (weight 1/2, only through an air face cell, so never across a wall's
// edge), the voxel itself counting 2. Each voxel fires its own fixed ray directions, so this pools several voxels'
// ray sets: one ray starting or stopping to hit a moving ship moves the result a few times less. The diagonals matter
// on stepped surfaces such as rounded island undersides, where the surface air cells touch only diagonally. Other air
// is skipped (it fires no rays, so it has no bounce or AO). Solidity is read through one cached chunk, since nearly
// all of these cells share the voxel's.
fn smoothedAcc(g: i32, v: vec3<i32>, own: u32) -> vec4<f32> {
    var cc = vec3<i32>(v >> vec3<u32>(5u));
    var info = chunkInfo(g, cc);
    var sum = unpackAcc(own) * 2.0;
    var w = 2.0;
    var faceSolid: array<bool, 6>;
    for (var f = 0; f < 6; f = f + 1) {
        let n = v + faceDir(f);
        faceSolid[f] = solidCached(g, n, &cc, &info);
        if (faceSolid[f] || !surfaceAirCached(g, n, &cc, &info)) { continue; }
        let r = voxelRef(g, n);
        if (r.ok && r.acc >= 0) { sum = sum + unpackAcc(accPool[r.acc]); w = w + 1.0; }
    }
    // Edge diagonals: faces fa < fb on different axes.
    for (var fa = 0; fa < 4; fa = fa + 1) {
        for (var fb = (fa & ~1) + 2; fb < 6; fb = fb + 1) {
            if (faceSolid[fa] && faceSolid[fb]) { continue; } // no air path around the edge
            let n = v + faceDir(fa) + faceDir(fb);
            if (!surfaceAirCached(g, n, &cc, &info)) { continue; }
            let r = voxelRef(g, n);
            if (r.ok && r.acc >= 0) { sum = sum + 0.5 * unpackAcc(accPool[r.acc]); w = w + 0.5; }
        }
    }
    return sum / w;
}

// The voxel's display half: the flat ambient (p.bounce2.z) darkened by the ray AO (times its strength, p.bounce2.w),
// its lamp light (traced now) and bounce (times the display scale, p.bounce.z), combined per channel by max, and the
// given sun level; bounce and AO are the stored ones smoothed over neighbours (smoothedAcc). Air away from any
// surface gets the plain ambient.
fn composeVoxel(g: i32, v: vec3<i32>, acc: u32, sun: u32, list: u32) -> u32 {
    let ambient = p.bounce2.z;
    if (isSolid(g, v) || !hasSolidNeighbour(g, v)) { return encodeLight(vec3<f32>(ambient)) | (sun << 14u); }
    // Only the frame's last compose of a brick smooths (p.bounce.x): the ones before it are there for the bounce passes
    // to read current light, and each voxel's own value does for that at a fraction of the cost.
    let sm = select(unpackAcc(acc), smoothedAcc(g, v, acc), p.bounce.x > 0.5);
    let bounce = sm.rgb * p.bounce.z;
    let sky = ambient * (1.0 - p.bounce2.w * sm.a);
    let light = max(max(lampLight(g, v, list), bounce), vec3<f32>(sky));
    return encodeLight(light) | (sun << 14u);
}

@compute @workgroup_size(256)
fn compose_main(@builtin(workgroup_id) wid: vec3<u32>, @builtin(num_workgroups) nwg: vec3<u32>,
                @builtin(local_invocation_index) t: u32) {
    let wi = wid.x + wid.y * nwg.x;
    if (wi >= u32(p.counts.z)) { return; }
    let it = itemOf(work[wi], t);
    let old = lightPool[it.disp];
    let list = listOf(it.g, it.v0);
    // No accumulation (bounce off, or not evaluated yet): no bounce and no AO.
    let a = accOf(it.slot);
    let k = i32(t) * 2;
    let acc0 = select(0u, accPool[a + k], a >= 0);
    let acc1 = select(0u, accPool[a + k + 1], a >= 0);
    let d0 = composeVoxel(it.g, it.v0, acc0, (old >> 14u) & 3u, list);
    let d1 = composeVoxel(it.g, it.v0 + vec3<i32>(1, 0, 0), acc1, (old >> 30u) & 3u, list);
    lightPool[it.disp] = d0 | (d1 << 16u);
}

// ── Bounce (one-hop indirect light, multi-hop through re-evaluation) ─────────────────────────────────────
// Each evaluation, a surface air voxel fires p.bounce2.x short rays (up to BOUNCE_MAX), cosine-distributed
// around its surface normal, through its chunk's grids: one slice of a fixed per-voxel direction set that a full
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
// diagonal gap only nudges an averaged value); empty chunks and bricks are crossed in one step. A ray that enters
// this grid straight into a solid cell hits with no front cell, which reads as unlit.
fn ddaNearest(g: i32, o: vec3<f32>, d: vec3<f32>, t0: f32, t1: f32, lo: vec3<i32>, hi: vec3<i32>) -> Hit {
    var h: Hit;
    h.hit = false;
    if (t0 >= t1) { return h; }
    let p0 = clamp(o + d * t0, vec3<f32>(lo), vec3<f32>(hi) - vec3<f32>(0.0001));
    var voxel = clamp(vec3<i32>(floor(p0)), lo, hi - vec3<i32>(1));
    var cc = voxel >> vec3<u32>(5u);
    var info = chunkInfo(g, cc);
    let stepX: i32 = select(-1, 1, d.x > 0.0);
    let stepY: i32 = select(-1, 1, d.y > 0.0);
    let stepZ: i32 = select(-1, 1, d.z > 0.0);
    if (solidIn(info.x, voxel)) {
        h.hit = true; h.t = t0; h.front = false; h.n = -d;
        return h;
    }
    var tm = tMaxOf(voxel, o, d);
    for (var iter = 0; iter < 64; iter = iter + 1) {
        cacheChunk(g, voxel, &cc, &info);
        let empty = emptyCell(info, voxel);
        if (empty > 0) {
            let j = jumpCell(voxel, empty, o, d, t1);
            if (j.kind == 2) { return h; }
            if (j.kind == 1) {
                voxel = j.v;
                if (!inBounds(voxel, lo, hi)) { return h; }
                if (solidCached(g, voxel, &cc, &info)) {
                    h.hit = true; h.t = j.t; h.cell = j.prev; h.front = true; h.n = j.n;
                    return h;
                }
                tm = tMaxOf(voxel, o, d);
                continue;
            }
        }

        let prev = voxel;
        var n = vec3<f32>(0.0);
        var t = 0.0;
        if (tm.x <= tm.y && tm.x <= tm.z) {
            t = tm.x; voxel.x = voxel.x + stepX; n = vec3<f32>(f32(-stepX), 0.0, 0.0);
        } else if (tm.y <= tm.z) {
            t = tm.y; voxel.y = voxel.y + stepY; n = vec3<f32>(0.0, f32(-stepY), 0.0);
        } else {
            t = tm.z; voxel.z = voxel.z + stepZ; n = vec3<f32>(0.0, 0.0, f32(-stepZ));
        }
        if (t >= t1) { return h; }
        if (!inBounds(voxel, lo, hi)) { return h; }
        if (solidCached(g, voxel, &cc, &info)) {
            h.hit = true; h.t = t; h.cell = prev; h.front = true; h.n = n;
            return h;
        }
        tm = tMaxOf(voxel, o, d);
    }
    return h;
}

// Light (linear 0-1 per channel) arriving at the hit face from the air cell in front of it, in grid g: the brighter
// of the cell's direct sun (visibility x strength x the face's N.L, white) and its displayed light (which includes
// its AO-darkened ambient, so ambient bounces too: it lifts deep corners a little), per channel.
// Its accumulated bounce is read too: the display is recomposed once a frame, but near-camera repeats within the
// frame need each other's fresh bounce to add their hops.
fn radianceAt(g: i32, h: Hit) -> vec3<f32> {
    if (!h.front) { return vec3<f32>(0.0); }
    let r = voxelRef(g, h.cell);
    if (!r.ok) { return vec3<f32>(0.0); }
    let d = (lightPool[r.disp] >> r.shift) & 0xFFFFu;
    let acc = select(0u, accPool[max(r.acc, 0)], r.acc >= 0); // a settled brick: its bounce is in its display
    let shown = select(decodeLight(d), vec3<f32>(0.0), isEmptyLight(d)); // not composed yet: nothing to reflect
    let bnc = vec3<f32>(f32(acc & 0xFFu), f32((acc >> 8u) & 0xFFu), f32((acc >> 16u) & 0xFFu)) / 255.0;
    let nW = normalize((grids[g].v2w * vec4<f32>(h.n, 0.0)).xyz);
    let sun = f32((d >> 14u) & 3u) / 3.0 * p.bounce.y * max(dot(nW, -p.sunDir.xyz), 0.0);
    return max(vec3<f32>(sun), max(shown, bnc));
}

fn pcg(v: u32) -> u32 {
    let s = v * 747796405u + 2891336453u;
    let w = ((s >> ((s >> 28u) + 4u)) ^ s) * 277803737u;
    return (w >> 22u) ^ w;
}

fn rand01(h: u32) -> f32 { return f32(pcg(h) & 0xFFFFFFu) / 16777216.0; }

// Blends one evaluation into the voxel's accumulation word (bounce RGB and AO) and returns the new word.
// A priming evaluation (the first after a brick's average restarts) measures only AO and stores no bounce: until then
// the brick's voxels were shown with no AO at all (nothing measured yet, or solid a moment ago), as bright as open
// air, and bounce rays reading those walls would start the average far too high, which then feeds on itself. After
// it the brick is recomposed with its AO and the average starts over, reading walls lit as they really are.
const PRIME: u32 = 0xFFFFFFFFu;

fn bounceVoxel(g: i32, v: vec3<i32>, word: u32, alpha: f32, slice: i32, list: u32, prime: bool) -> u32 {
    if (isSolid(g, v)) { return word; }

    let sxn = isSolid(g, v - vec3<i32>(1, 0, 0)); let sxp = isSolid(g, v + vec3<i32>(1, 0, 0));
    let syn = isSolid(g, v - vec3<i32>(0, 1, 0)); let syp = isSolid(g, v + vec3<i32>(0, 1, 0));
    let szn = isSolid(g, v - vec3<i32>(0, 0, 1)); let szp = isSolid(g, v + vec3<i32>(0, 0, 1));
    if (!(sxn || sxp || syn || syp || szn || szp)) { return 0u; } // non-surface air: no bounce, no occlusion

    // The voxel's surfaces face away from its solid neighbours; rays are cosine-distributed around that combined
    // normal, so a floor does not light itself with its own downward rays. Opposite solids (a floor and a
    // ceiling) cancel out, and then rays go in every direction.
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
    // Fibonacci spiral (evenly spread, far less noisy than random directions), twisted and shifted per voxel so
    // neighbours differ. Evaluation n fires slice n mod cycle; slices interleave (direction j = k*cycle + slice), so
    // each one spans the whole hemisphere coarsely and a full cycle covers the complete set.
    let seed = pcg(u32(v.x) ^ pcg(u32(v.y) ^ pcg(u32(v.z))));
    let twist = rand01(seed) * 6.2831853;
    let shift = rand01(seed + 1u);
    var sumL = vec3<f32>(0.0);
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
        let gridCount = lists[list];
        for (var gk = 0u; gk < gridCount; gk = gk + 1u) {
            let gi = i32(lists[list + 1u + gk]);
            let gd = grids[gi];
            if (gd.table.y <= 0) { continue; }
            let lo = (gd.w2v * vec4<f32>(origin, 1.0)).xyz;
            let ld = (gd.w2v * vec4<f32>(dw, 0.0)).xyz;
            let clip = slabClip(lo, ld, vec3<f32>(gd.bmin.xyz), vec3<f32>(gd.bmax.xyz), 0.0, bestT);
            if (!clip.hit) { continue; }
            let h = ddaNearest(gi, lo, ld, clip.t0, clip.t1, gd.bmin.xyz, gd.bmax.xyz);
            if (h.hit && h.t < bestT) { bestT = h.t; bestG = gi; best = h; }
        }
        if (bestG >= 0) { if (!prime) { sumL = sumL + radianceAt(bestG, best); } }
        else { escaped = escaped + 1.0; }
    }
    let estimate = clamp(p.bounce.x * sumL / f32(rays), vec3<f32>(0.0), vec3<f32>(1.0)) * 255.0;
    let occEstimate = (1.0 - escaped / f32(rays)) * 255.0;
    let r8 = blend8(f32(word & 0xFFu), estimate.r, alpha);
    let g8 = blend8(f32((word >> 8u) & 0xFFu), estimate.g, alpha);
    let b8 = blend8(f32((word >> 16u) & 0xFFu), estimate.b, alpha);
    let o8 = blend8(f32(word >> 24u), occEstimate, alpha);
    return u32(r8) | (u32(g8) << 8u) | (u32(b8) << 16u) | (u32(o8) << 24u);
}

// One blend step of an 8-bit stored value toward target (both 0-255). With 8-bit storage a small alpha can
// stall a step short of the target (a change under half a step rounds away), so it always moves at least one
// step toward it.
fn blend8(s8: f32, goal: f32, alpha: f32) -> f32 {
    var q = round(s8 + alpha * (goal - s8));
    if (q == s8 && abs(goal - s8) >= 1.0) { q = s8 + sign(goal - s8); }
    return clamp(q, 0.0, 255.0);
}

@compute @workgroup_size(256)
fn bounce_main(@builtin(workgroup_id) wid: vec3<u32>, @builtin(num_workgroups) nwg: vec3<u32>,
               @builtin(local_invocation_index) t: u32) {
    // The bounce work list is pairs: (light slot, evaluations since the brick changed).
    let wi = wid.x + wid.y * nwg.x;
    if (wi >= u32(p.counts.z)) { return; }
    if (t == 0u) { atomicStore(&wCount, 0u); }
    workgroupBarrier();
    let it = itemOf(work[wi * 2u], t);
    let nu = work[wi * 2u + 1u];
    // A running average over the first full cycle (exactly the complete ray set's mean), then a weight of one
    // cycle, so each further cycle weighs the whole set about equally. N = PRIME: the priming evaluation (see
    // bounceVoxel), which overwrites.
    let cycle = max(u32(p.bounce2.y), 1u);
    let prime = nu == PRIME;
    let alpha = select(max(1.0 / f32(cycle), 1.0 / (f32(nu) + 1.0)), 1.0, prime);
    let slice = select(i32(nu % cycle), i32(cycle) - 1, prime);
    let list = listOf(it.g, it.v0);
    // The CPU gives every brick it lists an accumulation slot; without one there is nothing to write.
    let accBase = accOf(it.slot);
    let has = accBase >= 0;
    // Gather the surface voxels (see Surface compaction); other air has no bounce and no occlusion, solid keeps its word.
    let base = it.v0 - vec3<i32>(i32(t * 2u) & 7, (i32(t * 2u) >> 3u) & 7, i32(t * 2u) >> 6u);
    for (var e = 0u; e < 2u; e = e + 1u) {
        let v = it.v0 + vec3<i32>(i32(e), 0, 0);
        if (!has || isSolid(it.g, v)) { continue; }
        if (hasSolidNeighbour(it.g, v)) { wList[atomicAdd(&wCount, 1u)] = t * 2u + e; }
        else { accPool[accBase + i32(t * 2u + e)] = 0u; }
    }
    workgroupBarrier();
    let n = atomicLoad(&wCount);
    for (var i = t; i < n; i = i + 256u) {
        let k = wList[i];
        let v = base + vec3<i32>(i32(k & 7u), i32((k >> 3u) & 7u), i32(k >> 6u));
        let a = accBase + i32(k);
        accPool[a] = bounceVoxel(it.g, v, accPool[a], alpha, slice, list, prime);
    }
}

// Drops the stored bounce of the listed slots (keeping AO), where a light was removed or dimmed. Bounce there feeds
// on itself — each evaluation reads neighbours whose bounce still holds the old light — so without a reset the old
// colour decays only by the albedo per evaluation and leaves a visible ghost when the brick stops being evaluated.
@compute @workgroup_size(256)
fn clear_main(@builtin(workgroup_id) wid: vec3<u32>, @builtin(num_workgroups) nwg: vec3<u32>,
              @builtin(local_invocation_index) t: u32) {
    let wi = wid.x + wid.y * nwg.x;
    if (wi >= u32(p.counts.z)) { return; }
    let a = accOf(work[wi]);
    if (a < 0) { return; }
    let base = a + i32(t) * 2;
    accPool[base]     = accPool[base] & 0xFF000000u;
    accPool[base + 1] = accPool[base + 1] & 0xFF000000u;
}

// Zeroes the listed accumulation slots (accPool slot numbers, not light slots): ones just handed out, which still
// hold whatever their last brick left, before anything reads them.
@compute @workgroup_size(256)
fn zero_main(@builtin(workgroup_id) wid: vec3<u32>, @builtin(num_workgroups) nwg: vec3<u32>,
             @builtin(local_invocation_index) t: u32) {
    let wi = wid.x + wid.y * nwg.x;
    if (wi >= u32(p.counts.z)) { return; }
    let base = i32(work[wi]) * ACC_WORDS + i32(t) * 2;
    accPool[base] = 0u;
    accPool[base + 1] = 0u;
}";


    private readonly GpuContext _ctx;
    private readonly ComputePipeline _sunPipeline;
    private readonly ComputePipeline _composePipeline;
    private readonly ComputePipeline _bouncePipeline;
    private readonly ComputePipeline _clearPipeline;
    private readonly ComputePipeline _zeroPipeline;
    // One small uniform buffer per dispatch of a frame: the dispatches are recorded into one command buffer, so every
    // parameter write lands before any of them runs and each needs its own.
    private readonly List<GpuBuffer> _params = new();
    private int _paramNext;
    private CommandEncoder* _enc;
    private readonly List<nint> _bindGroups = new();
    private GpuBuffer _lists;                 // this frame's per-chunk grid and lamp lists (see the WGSL)
    private int _lampBase;

    // Bindings each entry point uses (auto layouts only contain what the entry point references).
    private static readonly uint[] SunBindings    = { 0, 1, 3, 4, 5, 6, 7, 8 };
    private static readonly uint[] ComposeBindings = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }; // 2: neighbour smoothing (voxelRef)
    private static readonly uint[] BounceBindings = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
    private static readonly uint[] ClearBindings  = { 6, 8, 9, 10 };
    private static readonly uint[] ZeroBindings   = { 6, 8, 9 };

    public GpuRayLightPass(GpuContext ctx)
    {
        _ctx = ctx;
        _sunPipeline    = new ComputePipeline(ctx, Wgsl, "sun_main");
        _composePipeline = new ComputePipeline(ctx, Wgsl, "compose_main");
        _bouncePipeline = new ComputePipeline(ctx, Wgsl, "bounce_main");
        _clearPipeline  = new ComputePipeline(ctx, Wgsl, "clear_main");
        _zeroPipeline   = new ComputePipeline(ctx, Wgsl, "zero_main");
        _lists = GpuBuffer.CreateStorage(ctx, 65536);
    }

    /// <summary>Uploads this frame's per-chunk lists (layout in the WGSL), which every later dispatch reads.
    /// <paramref name="lampBase"/> is the word offset of the lamp records.</summary>
    public void UploadLists(ReadOnlySpan<uint> words, int lampBase)
    {
        ulong bytes = (ulong)words.Length * sizeof(uint);
        if (_lists.SizeBytes < bytes)
        {
            _lists.Dispose();
            _lists = GpuBuffer.CreateStorage(_ctx, bytes * 2);
        }
        _lists.Write<uint>(0, words);
        _lampBase = lampBase;
    }

    /// <summary>Clears the stored bounce (not AO) of the listed slots (see clear_main).</summary>
    public void DispatchClearBounce(GridStore store, GpuBuffer work, int count)
    {
        if (count <= 0) return;
        var param = WriteParams(Vector3D<float>.Zero, count, default, default);
        Dispatch(_clearPipeline, ClearBindings, store, work, count, param, "Lighting: clear bounce");
    }

    /// <summary>Zeroes the <paramref name="count"/> accumulation slots (accumulation-pool numbers) listed in
    /// <paramref name="work"/>; see zero_main.</summary>
    public void DispatchZeroAcc(GridStore store, GpuBuffer work, int count)
    {
        if (count <= 0) return;
        var param = WriteParams(Vector3D<float>.Zero, count, default, default);
        Dispatch(_zeroPipeline, ZeroBindings, store, work, count, param, "Lighting: zero accumulation");
    }

    /// <summary>Sun visibility for the <paramref name="count"/> light slots listed in <paramref name="work"/>.</summary>
    public void DispatchSun(GridStore store, Vector3D<float> sunDir, GpuBuffer work, int count)
    {
        if (count <= 0) return;
        var param = WriteParams(sunDir, count, default, default);
        Dispatch(_sunPipeline, SunBindings, store, work, count, param, "Lighting: sun");
    }

    /// <summary>Composes the displayed light of the listed slots: lamp light (traced now, from the lamps in each
    /// chunk's list) combined with the stored bounce times <paramref name="bounceScale"/> and the flat
    /// <paramref name="ambient"/> darkened by the stored AO times <paramref name="aoStrength"/>, keeping the sun level
    /// already in place. Run it after the sun and bounce passes. <paramref name="smooth"/>: average bounce and AO over
    /// neighbours (the frame's last compose of these bricks); otherwise each voxel's own.</summary>
    public void DispatchCompose(GridStore store, float bounceScale, float ambient, float aoStrength, GpuBuffer work, int count,
                                bool smooth)
    {
        if (count <= 0) return;
        var param = WriteParams(Vector3D<float>.Zero, count, new Vector4D<float>(smooth ? 1f : 0f, 0f, bounceScale, 0f),
                                new Vector4D<float>(0f, 0f, ambient, aoStrength));
        Dispatch(_composePipeline, ComposeBindings, store, work, count, param, "Lighting: compose");
    }

    /// <summary>
    /// One EMA step of bounce light for the listed bricks (see bounce_main). Reads direct light of every grid, so
    /// run it after the sun pass and before composing. <paramref name="work"/> holds <paramref name="count"/> pairs of (light slot,
    /// evaluations since that brick changed). Each voxel's fixed direction set is <paramref name="rays"/> x
    /// <paramref name="cycle"/> directions, one slice of <paramref name="rays"/> per evaluation.
    /// </summary>
    public void DispatchBounce(GridStore store, Vector3D<float> sunDir, float sunStrength,
                               float albedo, int rays, int cycle, GpuBuffer work, int count)
    {
        if (count <= 0) return;
        var param = WriteParams(sunDir, count,
                                new Vector4D<float>(albedo, sunStrength, 0f, 0f), new Vector4D<float>(rays, cycle, 0f, 0f));
        Dispatch(_bouncePipeline, BounceBindings, store, work, count, param, "Lighting: bounce");
    }

    private void Dispatch(ComputePipeline pipeline, uint[] bindings, GridStore store, GpuBuffer work, int count, GpuBuffer param,
                          string timingName)
    {
        var arr = new (uint, GpuBuffer)[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
            arr[i] = (bindings[i], bindings[i] switch
            {
                0 => store.OccPool,
                1 => store.ChunkTable,
                2 => store.BrickTable,
                3 => store.LightPool,
                4 => store.SlotInfo,
                5 => store.Grids,
                6 => work,
                7 => _lists,
                8 => param,
                9 => store.AccPool,
                _ => store.AccMap,
            });
        nint bg = pipeline.CreateBindGroupHandle(arr);
        _bindGroups.Add(bg);

        if (_enc == null)
        {
            var encDesc = new CommandEncoderDescriptor();
            _enc = _ctx.Api.DeviceCreateCommandEncoder(_ctx.Device, &encDesc);
        }
        // One workgroup per brick, as a 2D grid since a big list can exceed the 65535-per-dimension limit.
        const int MaxPerDim = 65535;
        uint gx = (uint)System.Math.Min(count, MaxPerDim);
        uint gy = ((uint)count + gx - 1u) / gx;
        pipeline.Record(_enc, (BindGroup*)bg, gx, gy, 1u, timingName, count);
    }

    /// <summary>Submits every dispatch recorded since the last submit, as one command buffer. Each dispatch is its own
    /// compute pass, so they run in order with the storage barriers between them. Buffer writes made before this call
    /// (work lists, the per-chunk lists) all land before the first dispatch runs, so a buffer must not be rewritten
    /// between two dispatches of one submit.</summary>
    public void Submit()
    {
        _paramNext = 0;
        if (_enc == null) return;
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = _ctx.Api.CommandEncoderFinish(_enc, &cmdDesc);
        _ctx.Api.QueueSubmit(_ctx.Queue, 1, &cmd);
        _ctx.Api.CommandBufferRelease(cmd);
        _ctx.Api.CommandEncoderRelease(_enc);
        _enc = null;
        foreach (nint bg in _bindGroups) _ctx.Api.BindGroupRelease((BindGroup*)bg);
        _bindGroups.Clear();
    }

    private GpuBuffer WriteParams(Vector3D<float> sunDir, int workCount, Vector4D<float> bounce, Vector4D<float> bounce2)
    {
        if (_paramNext == _params.Count) _params.Add(GpuBuffer.CreateUniform(_ctx, (ulong)Marshal.SizeOf<RayParams>()));
        var param = _params[_paramNext++];
        Span<RayParams> sp = stackalloc RayParams[1];
        sp[0] = new RayParams
        {
            Sun = new Vector4D<float>(sunDir.X, sunDir.Y, sunDir.Z, 0f),
            LampBase = _lampBase, WorkCount = workCount,
            Bounce = bounce, Bounce2 = bounce2,
        };
        param.Write<RayParams>(0, sp);
        return param;
    }

    public void Dispose()
    {
        _sunPipeline.Dispose();
        _composePipeline.Dispose();
        _bouncePipeline.Dispose();
        _clearPipeline.Dispose();
        _zeroPipeline.Dispose();
        Submit();
        foreach (var p in _params) p.Dispose();
        _lists.Dispose();
    }

    /// <summary>Uniform block (64 bytes); matches WGSL <c>Params</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RayParams
    {
        public Vector4D<float> Sun;
        public int Unused, LampBase, WorkCount, Pad;
        public Vector4D<float> Bounce;    // albedo, sun strength, bounce display scale, unused
        public Vector4D<float> Bounce2;   // rays per evaluation, cycle
    }
}
