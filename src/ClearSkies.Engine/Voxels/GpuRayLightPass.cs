using System.Runtime.InteropServices;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using ComputePipeline = ClearSkies.Engine.Rendering.WebGpu.ComputePipeline;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// One volume's descriptor for <see cref="GpuRayLightPass"/>: its opacity buffer, its voxel&lt;-&gt;world
/// transforms (rigid — rotation + translation, no scale), and its dimensions. Built fresh each frame by
/// <c>GpuLightSystem</c> from a <see cref="VolumeGpuResources"/> and the owning grid's physics pose (or
/// identity pose for the static world).
/// </summary>
internal readonly struct RayVolumeSlot
{
    public readonly GpuBuffer Opacity;
    public readonly Mat4 VoxelToWorld;
    public readonly Mat4 WorldToVoxel;
    public readonly int VW, VH, VD;

    public RayVolumeSlot(GpuBuffer opacity, Mat4 voxelToWorld, Mat4 worldToVoxel, int vw, int vh, int vd)
    {
        Opacity = opacity;
        VoxelToWorld = voxelToWorld;
        WorldToVoxel = worldToVoxel;
        VW = vw; VH = vh; VD = vd;
    }
}

/// <summary>
/// Prototype ray-traced hard-shadow lighting — see the "Multi-Grid Ray-Traced Hard-Shadow Lighting
/// Prototype" plan doc. Sun visibility only so far: an any-hit DDA ray march against voxel occupancy,
/// looped across up to <see cref="MaxRayVolumes"/> loaded volumes (static world + ships) at once, so a
/// ray started in one volume is correctly occluded by another. Replaces the shadow-map + PCF path in
/// <c>GpuSunVisPass</c> when <c>GpuLightSystem</c>'s ray-traced toggle is on — everything else (AO,
/// bounce, color, lamp shadows) is deliberately out of scope for this pass; see the plan doc.
///
/// <para>WGSL has no bindless storage-buffer arrays, so each of the fixed <see cref="MaxRayVolumes"/>
/// slots gets its own <c>@binding(N)</c> opacity declaration baked into the shader source at construction
/// time; unused slots (fewer than <see cref="MaxRayVolumes"/> volumes loaded) are padded with a shared
/// dummy all-zero (never-solid) buffer, so the pipeline never needs rebuilding as the loaded volume count
/// changes — only which buffers are bound.</para>
/// </summary>
internal sealed unsafe class GpuRayLightPass : IDisposable
{
    /// <summary>Static world + up to this many ships. Keeps this pass's dispatches under WebGPU's
    /// spec-default 8-storage-buffers-per-stage limit (6 for the sun pass: 5 opacity + 1 output) without
    /// relying on non-default adapter limits.</summary>
    public const int MaxRayVolumes = 5;

    /// <summary>Cap on the per-target-volume filtered lamp list <see cref="DispatchLamps"/> uploads.
    /// Generous for prototype test scenes; truncated (with a one-time log) rather than exceeded.</summary>
    public const int MaxLampsPerDispatch = 64;

    private static readonly string Wgsl = @"
struct Params {
    voxelToWorld0: mat4x4<f32>, voxelToWorld1: mat4x4<f32>, voxelToWorld2: mat4x4<f32>, voxelToWorld3: mat4x4<f32>, voxelToWorld4: mat4x4<f32>,
    worldToVoxel0: mat4x4<f32>, worldToVoxel1: mat4x4<f32>, worldToVoxel2: mat4x4<f32>, worldToVoxel3: mat4x4<f32>, worldToVoxel4: mat4x4<f32>,
    dims0: vec4<i32>, dims1: vec4<i32>, dims2: vec4<i32>, dims3: vec4<i32>, dims4: vec4<i32>,
    sunDir: vec4<f32>,
    counts: vec4<i32>, // volumeCount, targetVolIdx, lampCount (unused by sun_main), ambientLevel (0-15, lamp_main only)
};

@group(0) @binding(0) var<storage, read> opacity0: array<u32>;
@group(0) @binding(1) var<storage, read> opacity1: array<u32>;
@group(0) @binding(2) var<storage, read> opacity2: array<u32>;
@group(0) @binding(3) var<storage, read> opacity3: array<u32>;
@group(0) @binding(4) var<storage, read> opacity4: array<u32>;
@group(0) @binding(5) var<storage, read_write> sunvis: array<u32>;
@group(0) @binding(6) var<storage, read_write> light: array<u32>;
@group(0) @binding(7) var<storage, read> lamps: array<vec4<f32>>; // xyz world pos, w level (also doubles as radius)
@group(0) @binding(8) var<uniform> p: Params;

const WPC: i32 = " + VolumeGpuResources.WordsPerChunk + @";
const SUN_MAX_DISTANCE: f32 = 2000.0;
const SURFACE_OFFSET: f32 = 0.4;
const SUN_SAMPLES: i32 = 4;      // sun rays per surface voxel: 1 (from the nudged centre) or up to 4 (offset table size)
const SUN_JITTER: f32 = 0.25;    // how far (voxels) each sample point is offset from the nudged centre, per axis
// Flat ambient for the lamp pass's sky byte (no flood running while this pass is active) now comes from
// p.counts.w (GpuLightSystem's debug-panel-adjustable ambient level, 0-15), decoupled from
// VolumeGpuResources.BaseSkyLevel (the old flood's ambient, still used when the toggle is off).
// ddaMarch: axes within this of the step minimum are treated as tied (corner-cutting fix). Generous, not a
// tight float-epsilon — lamp/surface positions are usually voxel-centred, so exact ties are the COMMON
// case, and the world-to-local transform's own rounding can perturb an exact tie past a tiny epsilon.
// Widening this costs a few extra (correct) opacity reads on genuinely near-tied rays; it can't cause false
// blocking, since the extra checks still test real occupancy, not a blanket geometric rule.
const TIE_EPS: f32 = 1e-2;

// Chunk-major opacity bit test (matches GpuLightFlood.isOpaque / GpuSunVisPass.isOpaque), one copy per
// fixed volume slot since WGSL can't parameterize which storage binding a function reads.
fn isOpaque0(x: i32, y: i32, z: i32) -> bool {
    if (x < 0 || x >= p.dims0.x || y < 0 || y >= p.dims0.y || z < 0 || z >= p.dims0.z) { return false; }
    let dx = p.dims0.x >> 5; let dy = p.dims0.y >> 5;
    let slot = (x >> 5) + dx * ((y >> 5) + dy * (z >> 5));
    let word = slot * WPC + ((y & 31) + 32 * (z & 31));
    return ((opacity0[u32(word)] >> u32(x & 31)) & 1u) == 1u;
}
fn isOpaque1(x: i32, y: i32, z: i32) -> bool {
    if (x < 0 || x >= p.dims1.x || y < 0 || y >= p.dims1.y || z < 0 || z >= p.dims1.z) { return false; }
    let dx = p.dims1.x >> 5; let dy = p.dims1.y >> 5;
    let slot = (x >> 5) + dx * ((y >> 5) + dy * (z >> 5));
    let word = slot * WPC + ((y & 31) + 32 * (z & 31));
    return ((opacity1[u32(word)] >> u32(x & 31)) & 1u) == 1u;
}
fn isOpaque2(x: i32, y: i32, z: i32) -> bool {
    if (x < 0 || x >= p.dims2.x || y < 0 || y >= p.dims2.y || z < 0 || z >= p.dims2.z) { return false; }
    let dx = p.dims2.x >> 5; let dy = p.dims2.y >> 5;
    let slot = (x >> 5) + dx * ((y >> 5) + dy * (z >> 5));
    let word = slot * WPC + ((y & 31) + 32 * (z & 31));
    return ((opacity2[u32(word)] >> u32(x & 31)) & 1u) == 1u;
}
fn isOpaque3(x: i32, y: i32, z: i32) -> bool {
    if (x < 0 || x >= p.dims3.x || y < 0 || y >= p.dims3.y || z < 0 || z >= p.dims3.z) { return false; }
    let dx = p.dims3.x >> 5; let dy = p.dims3.y >> 5;
    let slot = (x >> 5) + dx * ((y >> 5) + dy * (z >> 5));
    let word = slot * WPC + ((y & 31) + 32 * (z & 31));
    return ((opacity3[u32(word)] >> u32(x & 31)) & 1u) == 1u;
}
fn isOpaque4(x: i32, y: i32, z: i32) -> bool {
    if (x < 0 || x >= p.dims4.x || y < 0 || y >= p.dims4.y || z < 0 || z >= p.dims4.z) { return false; }
    let dx = p.dims4.x >> 5; let dy = p.dims4.y >> 5;
    let slot = (x >> 5) + dx * ((y >> 5) + dy * (z >> 5));
    let word = slot * WPC + ((y & 31) + 32 * (z & 31));
    return ((opacity4[u32(word)] >> u32(x & 31)) & 1u) == 1u;
}

fn isOpaqueInVolume(volIdx: i32, x: i32, y: i32, z: i32) -> bool {
    switch (volIdx) {
        case 0: { return isOpaque0(x, y, z); }
        case 1: { return isOpaque1(x, y, z); }
        case 2: { return isOpaque2(x, y, z); }
        case 3: { return isOpaque3(x, y, z); }
        case 4: { return isOpaque4(x, y, z); }
        default: { return false; }
    }
}

fn dimsOf(volIdx: i32) -> vec3<i32> {
    switch (volIdx) {
        case 0: { return p.dims0.xyz; }
        case 1: { return p.dims1.xyz; }
        case 2: { return p.dims2.xyz; }
        case 3: { return p.dims3.xyz; }
        case 4: { return p.dims4.xyz; }
        default: { return vec3<i32>(0, 0, 0); }
    }
}

fn voxelToWorldOf(volIdx: i32) -> mat4x4<f32> {
    switch (volIdx) {
        case 0: { return p.voxelToWorld0; }
        case 1: { return p.voxelToWorld1; }
        case 2: { return p.voxelToWorld2; }
        case 3: { return p.voxelToWorld3; }
        case 4: { return p.voxelToWorld4; }
        default: { return mat4x4<f32>(vec4<f32>(0.0), vec4<f32>(0.0), vec4<f32>(0.0), vec4<f32>(0.0)); }
    }
}

fn worldToVoxelOf(volIdx: i32) -> mat4x4<f32> {
    switch (volIdx) {
        case 0: { return p.worldToVoxel0; }
        case 1: { return p.worldToVoxel1; }
        case 2: { return p.worldToVoxel2; }
        case 3: { return p.worldToVoxel3; }
        case 4: { return p.worldToVoxel4; }
        default: { return mat4x4<f32>(vec4<f32>(0.0), vec4<f32>(0.0), vec4<f32>(0.0), vec4<f32>(0.0)); }
    }
}

struct ClipResult { hit: bool, t0: f32, t1: f32 };

// Ray/AABB slab test against [0,dims), intersected with the caller's [tLo,tHi]. o/d are already in this
// volume's own local (voxel) space. Unrolled per axis (no dynamic vector-component indexing).
fn slabClip(o: vec3<f32>, d: vec3<f32>, dims: vec3<i32>, tLo: f32, tHi: f32) -> ClipResult {
    var t0 = tLo;
    var t1 = tHi;

    if (abs(d.x) < 1e-8) {
        if (o.x < 0.0 || o.x > f32(dims.x)) { return ClipResult(false, 0.0, 0.0); }
    } else {
        var ta = (0.0 - o.x) / d.x;
        var tb = (f32(dims.x) - o.x) / d.x;
        if (ta > tb) { let tmp = ta; ta = tb; tb = tmp; }
        t0 = max(t0, ta); t1 = min(t1, tb);
        if (t0 > t1) { return ClipResult(false, 0.0, 0.0); }
    }
    if (abs(d.y) < 1e-8) {
        if (o.y < 0.0 || o.y > f32(dims.y)) { return ClipResult(false, 0.0, 0.0); }
    } else {
        var ta = (0.0 - o.y) / d.y;
        var tb = (f32(dims.y) - o.y) / d.y;
        if (ta > tb) { let tmp = ta; ta = tb; tb = tmp; }
        t0 = max(t0, ta); t1 = min(t1, tb);
        if (t0 > t1) { return ClipResult(false, 0.0, 0.0); }
    }
    if (abs(d.z) < 1e-8) {
        if (o.z < 0.0 || o.z > f32(dims.z)) { return ClipResult(false, 0.0, 0.0); }
    } else {
        var ta = (0.0 - o.z) / d.z;
        var tb = (f32(dims.z) - o.z) / d.z;
        if (ta > tb) { let tmp = ta; ta = tb; tb = tmp; }
        t0 = max(t0, ta); t1 = min(t1, tb);
        if (t0 > t1) { return ClipResult(false, 0.0, 0.0); }
    }
    return ClipResult(true, t0, t1);
}

// Amanatides-Woo DDA over [t0,t1] of a ray already clipped to this volume's box. Boundaries are
// recomputed from the origin every step (not accumulated with a running delta) — accumulation drifts over
// long rays, and sun rays run the length of the loaded volume. Any-hit: returns on the first occupied
// voxel. Bounded to 512 steps (generous for any realistic window) so a DDA bug hangs a ray, not the GPU.
fn ddaMarch(volIdx: i32, o: vec3<f32>, d: vec3<f32>, t0: f32, t1: f32, dims: vec3<i32>, skipStart: bool) -> bool {
    if (t0 >= t1) { return false; }
    let dimsF = vec3<f32>(dims);
    let p0 = clamp(o + d * t0, vec3<f32>(0.0), dimsF - vec3<f32>(0.0001));
    var voxel = clamp(vec3<i32>(floor(p0)), vec3<i32>(0), dims - vec3<i32>(1));

    let stepX: i32 = select(-1, 1, d.x > 0.0);
    let stepY: i32 = select(-1, 1, d.y > 0.0);
    let stepZ: i32 = select(-1, 1, d.z > 0.0);

    var tMaxX: f32 = select(1e30, ((f32(voxel.x) + select(0.0, 1.0, d.x > 0.0)) - o.x) / d.x, abs(d.x) > 1e-8);
    var tMaxY: f32 = select(1e30, ((f32(voxel.y) + select(0.0, 1.0, d.y > 0.0)) - o.y) / d.y, abs(d.y) > 1e-8);
    var tMaxZ: f32 = select(1e30, ((f32(voxel.z) + select(0.0, 1.0, d.z > 0.0)) - o.z) / d.z, abs(d.z) > 1e-8);

    // skipStart: the ray begins inside a cell that must not count as its own occluder (a lamp's own opaque
    // block). Skipping that one cell, rather than shortening the ray, keeps the exit from it fully tested.
    if (!skipStart && isOpaqueInVolume(volIdx, voxel.x, voxel.y, voxel.z)) { return true; }

    for (var iter = 0; iter < 512; iter = iter + 1) {
        let tMin = min(tMaxX, min(tMaxY, tMaxZ));
        if (tMin >= t1) { return false; }

        // Which axes are (near-)tied for the crossing this step. Ray-direction-relative, not a static
        // property of the voxel — unlike a diagonal-solids-always-block occupancy rule (tried and
        // reverted: it falsely blocks every interior cell of an ordinary enclosed corridor, since a
        // corridor's interior has solid neighbours on both perpendicular axes throughout its whole length,
        // open only along its own direction — that rule can't tell a gap between two blocks from a plain
        // hallway wall). Tie detection only fires for a ray whose OWN trajectory is near the critical diagonal
        // angle, so a ray travelling straight down an open corridor never ties and is untouched; only rays
        // actually grazing a corner get the extra check. TIE_EPS is generous (not a tight float-epsilon)
        // because lamp/surface positions are usually voxel-centred, so exact ties are the COMMON case here
        // (rational direction ratios), and the world-to-local transform's rounding can perturb an exact tie
        // by more than a tiny epsilon.
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
            if (isOpaqueInVolume(volIdx, nvx, voxel.y, voxel.z)) { return true; }
            if (isOpaqueInVolume(volIdx, voxel.x, nvy, voxel.z)) { return true; }
        }
        if (tieX && tieZ) {
            if (isOpaqueInVolume(volIdx, nvx, voxel.y, voxel.z)) { return true; }
            if (isOpaqueInVolume(volIdx, voxel.x, voxel.y, nvz)) { return true; }
        }
        if (tieY && tieZ) {
            if (isOpaqueInVolume(volIdx, voxel.x, nvy, voxel.z)) { return true; }
            if (isOpaqueInVolume(volIdx, voxel.x, voxel.y, nvz)) { return true; }
        }
        if (tieX && tieY && tieZ) {
            if (isOpaqueInVolume(volIdx, nvx, nvy, voxel.z)) { return true; }
            if (isOpaqueInVolume(volIdx, nvx, voxel.y, nvz)) { return true; }
            if (isOpaqueInVolume(volIdx, voxel.x, nvy, nvz)) { return true; }
        }

        voxel = vec3<i32>(nvx, nvy, nvz);
        if (voxel.x < 0 || voxel.x >= dims.x || voxel.y < 0 || voxel.y >= dims.y || voxel.z < 0 || voxel.z >= dims.z) { return false; }
        if (isOpaqueInVolume(volIdx, voxel.x, voxel.y, voxel.z)) { return true; }

        if (tieX) { tMaxX = select(1e30, ((f32(voxel.x) + select(0.0, 1.0, d.x > 0.0)) - o.x) / d.x, abs(d.x) > 1e-8); }
        if (tieY) { tMaxY = select(1e30, ((f32(voxel.y) + select(0.0, 1.0, d.y > 0.0)) - o.y) / d.y, abs(d.y) > 1e-8); }
        if (tieZ) { tMaxZ = select(1e30, ((f32(voxel.z) + select(0.0, 1.0, d.z > 0.0)) - o.z) / d.z, abs(d.z) > 1e-8); }
    }
    return false;
}

// Any-hit occlusion test across every loaded volume. worldDir must be unit length; rigid transforms
// preserve distance, so segLen (a world-space length) bounds the walk identically in every volume's own
// local space once the direction is rotated into it.
// skipOrigin: ignore the cell containing worldOrigin in any volume the ray starts inside (clip.t0 == 0) —
// used for lamp rays, which start at the lamp's own opaque block.
fn anyOccluderAlongSegment(worldOrigin: vec3<f32>, worldDir: vec3<f32>, segLen: f32, skipOrigin: bool) -> bool {
    if (segLen <= 0.0) { return false; }
    for (var vi = 0; vi < p.counts.x; vi = vi + 1) {
        let w2v = worldToVoxelOf(vi);
        let lo = (w2v * vec4<f32>(worldOrigin, 1.0)).xyz;
        let ld = (w2v * vec4<f32>(worldDir, 0.0)).xyz; // w=0 drops translation: rotation-only direction transform
        let dims = dimsOf(vi);
        let clip = slabClip(lo, ld, dims, 0.0, segLen);
        if (!clip.hit) { continue; }
        let skip = skipOrigin && clip.t0 <= 1e-4;
        if (ddaMarch(vi, lo, ld, clip.t0, clip.t1, dims, skip)) { return true; }
    }
    return false;
}

@compute @workgroup_size(4, 4, 4)
fn sun_main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let ti = p.counts.y;
    let dims = dimsOf(ti);
    let x = i32(gid.x);
    let y = i32(gid.y);
    let z = i32(gid.z);
    if (x >= dims.x || y >= dims.y || z >= dims.z) { return; }
    let i = u32(x + dims.x * (y + dims.y * z));

    // Surface-air-voxel-only, exactly like GpuSunVisPass: everything else gets the 255 sentinel the
    // fragment shader already knows to exclude from its blend rather than treat as lit.
    if (isOpaqueInVolume(ti, x, y, z)) { sunvis[i] = 255u; return; }
    let sxn = isOpaqueInVolume(ti, x - 1, y, z); let sxp = isOpaqueInVolume(ti, x + 1, y, z);
    let syn = isOpaqueInVolume(ti, x, y - 1, z); let syp = isOpaqueInVolume(ti, x, y + 1, z);
    let szn = isOpaqueInVolume(ti, x, y, z - 1); let szp = isOpaqueInVolume(ti, x, y, z + 1);
    if (!(sxn || sxp || syn || syp || szn || szp)) { sunvis[i] = 255u; return; }

    // Nudge the sample point toward adjacent solid faces (same as GpuSunVisPass) so contact shadows reach
    // the foot of a wall instead of being tested from the bare voxel centre.
    var nudge = vec3<f32>(0.0, 0.0, 0.0);
    if (sxn) { nudge.x = nudge.x - 1.0; } if (sxp) { nudge.x = nudge.x + 1.0; }
    if (syn) { nudge.y = nudge.y - 1.0; } if (syp) { nudge.y = nudge.y + 1.0; }
    if (szn) { nudge.z = nudge.z - 1.0; } if (szp) { nudge.z = nudge.z + 1.0; }
    let vc = vec3<f32>(f32(x) + 0.5, f32(y) + 0.5, f32(z) + 0.5) + nudge * SURFACE_OFFSET;

    // SUN_SAMPLES rays from points spread through the voxel (a tetrahedral pattern, clamped to stay inside
    // this air cell), stored as the lit fraction. A single binary ray per voxel made a moving caster's shadow
    // edge jump a whole voxel at a time; fractional coverage moves it in quarter steps, which the fragment
    // blend then smooths.
    var offs = array<vec3<f32>, 4>(
        vec3<f32>( 1.0,  1.0,  1.0), vec3<f32>( 1.0, -1.0, -1.0),
        vec3<f32>(-1.0,  1.0, -1.0), vec3<f32>(-1.0, -1.0,  1.0));
    let cellLo = vec3<f32>(f32(x), f32(y), f32(z)) + vec3<f32>(0.02);
    let cellHi = vec3<f32>(f32(x), f32(y), f32(z)) + vec3<f32>(0.98);
    let v2w = voxelToWorldOf(ti);
    var lit = 0u;
    for (var s = 0; s < SUN_SAMPLES; s = s + 1) {
        let jitter = select(0.0, SUN_JITTER, SUN_SAMPLES > 1); // a single ray uses the unjittered nudged centre
        let sp = clamp(vc + offs[s] * jitter, cellLo, cellHi);
        let world = (v2w * vec4<f32>(sp, 1.0)).xyz;
        if (!anyOccluderAlongSegment(world, -p.sunDir.xyz, SUN_MAX_DISTANCE, false)) { lit = lit + 1u; }
    }
    sunvis[i] = u32(round(f32(lit) * 254.0 / f32(SUN_SAMPLES))); // 0-254; 255 stays the skip sentinel
}

// Point-lamp shadows, every lamp against every loaded volume, no same-volume special case: a lamp on this
// very volume goes through the identical world-space any-hit test as a lamp on another volume entirely.
// Rays are traced FROM the lamp TO the surface voxel over the full distance, skipping only the lamp's own
// starting cell. (Shortening the ray by a fixed radius instead let exact 45-degree rays stop inside an open
// diagonal cell before reaching the lamp cell's edge/corner, so the face blocks covering the lamp were
// never on the tested segment and light leaked through the diagonals.)
@compute @workgroup_size(4, 4, 4)
fn lamp_main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let ti = p.counts.y;
    let ambient: u32 = u32(clamp(p.counts.w, 0, 15));
    let dims = dimsOf(ti);
    let x = i32(gid.x);
    let y = i32(gid.y);
    let z = i32(gid.z);
    if (x >= dims.x || y >= dims.y || z >= dims.z) { return; }
    let i = u32(x + dims.x * (y + dims.y * z));

    if (isOpaqueInVolume(ti, x, y, z)) { light[i] = 0u; return; }

    let sxn = isOpaqueInVolume(ti, x - 1, y, z); let sxp = isOpaqueInVolume(ti, x + 1, y, z);
    let syn = isOpaqueInVolume(ti, x, y - 1, z); let syp = isOpaqueInVolume(ti, x, y + 1, z);
    let szn = isOpaqueInVolume(ti, x, y, z - 1); let szp = isOpaqueInVolume(ti, x, y, z + 1);
    if (!(sxn || sxp || syn || syp || szn || szp)) { light[i] = ambient; return; } // deep interior air: flat ambient only

    let world = (voxelToWorldOf(ti) * vec4<f32>(f32(x) + 0.5, f32(y) + 0.5, f32(z) + 0.5, 1.0)).xyz;

    var best: u32 = 0u;
    for (var li = 0; li < p.counts.z; li = li + 1) {
        let lamp = lamps[li];
        let toLamp = lamp.xyz - world;
        let dist = length(toLamp);
        if (dist > lamp.w) { continue; }               // level doubles as reach radius
        let contribF = round(lamp.w - dist);            // 1-per-voxel falloff (matches GpuLightFlood.Inject)
        if (contribF <= 0.0) { continue; }

        if (!anyOccluderAlongSegment(lamp.xyz, -toLamp / dist, dist, true)) {
            best = max(best, u32(contribF));
        }
    }
    light[i] = ambient | (min(best, 15u) << 8u);
}";

    private readonly GpuContext _ctx;
    private readonly ComputePipeline _sunPipeline;
    private readonly ComputePipeline _lampPipeline;
    private readonly GpuBuffer _param;
    private readonly GpuBuffer _dummyOpacity; // padding for unused volume slots (never solid)
    private readonly GpuBuffer _lampList;     // scratch: this dispatch's filtered lamp list (xyz, w=level)
    private bool _loggedTooManyLamps;

    public GpuRayLightPass(GpuContext ctx)
    {
        _ctx = ctx;
        _sunPipeline  = new ComputePipeline(ctx, Wgsl, "sun_main");
        _lampPipeline = new ComputePipeline(ctx, Wgsl, "lamp_main");
        _param = GpuBuffer.CreateUniform(ctx, (ulong)Marshal.SizeOf<RayParams>());
        _dummyOpacity = GpuBuffer.CreateStorage(ctx, sizeof(uint));
        _dummyOpacity.Write<uint>(0, new uint[1]);
        _lampList = GpuBuffer.CreateStorage(ctx, (ulong)MaxLampsPerDispatch * 16); // vec4<f32> per lamp
    }

    /// <summary>
    /// Ray-traced sun visibility for <paramref name="targetIdx"/> among <paramref name="slots"/>[0..
    /// <paramref name="volumeCount"/>-1] (the rest of the fixed <see cref="MaxRayVolumes"/>-length span is
    /// padded with the dummy opacity buffer). Any-hit DDA against every real volume's occupancy in turn,
    /// replacing <c>GpuSunVisPass</c>'s shadow-map sample. Writes <paramref name="targetSunVis"/> with the
    /// existing 0/254/255 convention the fragment shader already consumes (255 = non-surface sentinel,
    /// 0/254 = hard shadowed/lit).
    /// </summary>
    public void DispatchSun(ReadOnlySpan<RayVolumeSlot> slots, int volumeCount, int targetIdx,
                            GpuBuffer targetSunVis, Vector3D<float> sunDir)
    {
        if (volumeCount <= 0 || targetIdx < 0 || targetIdx >= volumeCount) return;
        var target = slots[targetIdx];
        if (target.VW <= 0 || target.VH <= 0 || target.VD <= 0) return;

        WriteParams(slots, volumeCount, targetIdx, sunDir, lampCount: 0, ambientLevel: 0);

        var entries = new (uint, GpuBuffer)[7];
        for (int i = 0; i < MaxRayVolumes; i++)
            entries[i] = ((uint)i, i < volumeCount ? slots[i].Opacity : _dummyOpacity);
        entries[5] = (5u, targetSunVis);
        entries[6] = (8u, _param);

        nint bg = _sunPipeline.CreateBindGroupHandle(entries);
        _sunPipeline.Dispatch(bg, CeilDiv((uint)target.VW, 4u), CeilDiv((uint)target.VH, 4u), CeilDiv((uint)target.VD, 4u));
        _ctx.Api.BindGroupRelease((BindGroup*)bg);
    }

    /// <summary>
    /// Ray-traced point-lamp lighting for <paramref name="targetIdx"/>: every entry of
    /// <paramref name="lamps"/> (xyz world position, w level/reach radius — caller should already have
    /// prefiltered to lamps that can plausibly reach this volume, e.g. via <c>GpuLightSystem.LampAabb</c>)
    /// is shadow-tested with the same any-hit multi-volume DDA the sun pass uses, no same-volume special
    /// case. Writes <paramref name="targetLight"/> in full each call (flat <paramref name="ambientLevel"/>
    /// (0-15) in the sky byte, max-combined lamp contribution in the block byte) — fully self-contained,
    /// no dependency on the flood having run.
    /// </summary>
    public void DispatchLamps(ReadOnlySpan<RayVolumeSlot> slots, int volumeCount, int targetIdx,
                              GpuBuffer targetLight, ReadOnlySpan<Vector4D<float>> lamps, int ambientLevel)
    {
        if (volumeCount <= 0 || targetIdx < 0 || targetIdx >= volumeCount) return;
        var target = slots[targetIdx];
        if (target.VW <= 0 || target.VH <= 0 || target.VD <= 0) return;

        int lampCount = lamps.Length;
        if (lampCount > MaxLampsPerDispatch)
        {
            if (!_loggedTooManyLamps)
            {
                _loggedTooManyLamps = true;
                Console.WriteLine($"[ray-lighting] {lampCount} lamps reach a volume this frame; truncating to {MaxLampsPerDispatch}.");
            }
            lampCount = MaxLampsPerDispatch;
        }
        if (lampCount > 0)
            _lampList.Write<Vector4D<float>>(0, lamps.Slice(0, lampCount));

        WriteParams(slots, volumeCount, targetIdx, Vector3D<float>.Zero, lampCount, ambientLevel);

        var entries = new (uint, GpuBuffer)[8];
        for (int i = 0; i < MaxRayVolumes; i++)
            entries[i] = ((uint)i, i < volumeCount ? slots[i].Opacity : _dummyOpacity);
        entries[5] = (6u, targetLight);
        entries[6] = (7u, _lampList);
        entries[7] = (8u, _param);

        nint bg = _lampPipeline.CreateBindGroupHandle(entries);
        _lampPipeline.Dispatch(bg, CeilDiv((uint)target.VW, 4u), CeilDiv((uint)target.VH, 4u), CeilDiv((uint)target.VD, 4u));
        _ctx.Api.BindGroupRelease((BindGroup*)bg);
    }

    private void WriteParams(ReadOnlySpan<RayVolumeSlot> slots, int volumeCount, int targetIdx, Vector3D<float> sunDir, int lampCount, int ambientLevel)
    {
        var pr = new RayParams();
        SetSlot(ref pr, 0, volumeCount > 0 ? slots[0] : default);
        SetSlot(ref pr, 1, volumeCount > 1 ? slots[1] : default);
        SetSlot(ref pr, 2, volumeCount > 2 ? slots[2] : default);
        SetSlot(ref pr, 3, volumeCount > 3 ? slots[3] : default);
        SetSlot(ref pr, 4, volumeCount > 4 ? slots[4] : default);
        pr.SunX = sunDir.X; pr.SunY = sunDir.Y; pr.SunZ = sunDir.Z; pr.SunPad = 0f;
        pr.VolumeCount = volumeCount; pr.TargetIdx = targetIdx; pr.LampCount = lampCount; pr.AmbientLevel = ambientLevel;

        Span<RayParams> sp = stackalloc RayParams[1] { pr };
        _param.Write<RayParams>(0, sp);
    }

    private static void SetSlot(ref RayParams pr, int idx, RayVolumeSlot slot)
    {
        switch (idx)
        {
            case 0:
                pr.VoxelToWorld0 = slot.VoxelToWorld; pr.WorldToVoxel0 = slot.WorldToVoxel;
                pr.VW0 = slot.VW; pr.VH0 = slot.VH; pr.VD0 = slot.VD;
                break;
            case 1:
                pr.VoxelToWorld1 = slot.VoxelToWorld; pr.WorldToVoxel1 = slot.WorldToVoxel;
                pr.VW1 = slot.VW; pr.VH1 = slot.VH; pr.VD1 = slot.VD;
                break;
            case 2:
                pr.VoxelToWorld2 = slot.VoxelToWorld; pr.WorldToVoxel2 = slot.WorldToVoxel;
                pr.VW2 = slot.VW; pr.VH2 = slot.VH; pr.VD2 = slot.VD;
                break;
            case 3:
                pr.VoxelToWorld3 = slot.VoxelToWorld; pr.WorldToVoxel3 = slot.WorldToVoxel;
                pr.VW3 = slot.VW; pr.VH3 = slot.VH; pr.VD3 = slot.VD;
                break;
            case 4:
                pr.VoxelToWorld4 = slot.VoxelToWorld; pr.WorldToVoxel4 = slot.WorldToVoxel;
                pr.VW4 = slot.VW; pr.VH4 = slot.VH; pr.VD4 = slot.VD;
                break;
        }
    }

    private static uint CeilDiv(uint a, uint b) => (a + b - 1u) / b;

    public void Dispose()
    {
        _sunPipeline.Dispose();
        _lampPipeline.Dispose();
        _param.Dispose();
        _dummyOpacity.Dispose();
        _lampList.Dispose();
    }

    /// <summary>Uniform block for the ray-traced pass (752 bytes): 5 volume-to-world mat4s, 5
    /// world-to-volume mat4s, 5 (dims+pad) vec4&lt;i32&gt;s, sun direction, then counts. Field order/padding
    /// match the WGSL <c>Params</c> std140 layout, flattening the per-slot array into named fields the same
    /// way <c>GpuLightFlood.InjectParams</c> flattens its 6 face matrices (F0..F5).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RayParams
    {
        public Mat4 VoxelToWorld0, VoxelToWorld1, VoxelToWorld2, VoxelToWorld3, VoxelToWorld4;
        public Mat4 WorldToVoxel0, WorldToVoxel1, WorldToVoxel2, WorldToVoxel3, WorldToVoxel4;
        public int VW0, VH0, VD0, Pad0;
        public int VW1, VH1, VD1, Pad1;
        public int VW2, VH2, VD2, Pad2;
        public int VW3, VH3, VD3, Pad3;
        public int VW4, VH4, VD4, Pad4;
        public float SunX, SunY, SunZ, SunPad;
        public int VolumeCount, TargetIdx, LampCount, AmbientLevel;
    }
}
