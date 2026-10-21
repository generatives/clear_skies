using ClearSkies.Engine.Voxels;
using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// Chunk meshes' light maps: each quad's smoothed light and corner AO at every corner of its cells, baked on the GPU
/// when its chunk's light changes (<see cref="GridStore.LightStampOf"/>), so a terrain pixel blends four stored corners
/// instead of working out the smoothing itself (shadeLightmap in the renderer's shader).
///
/// Within one quad the corners its cells share come out the same from either cell: every cell of a quad is on the same
/// surface, so the same neighbours count, and one side of a shared corner's AO is always the open cell itself. So one
/// grid of (width + 1) x (height + 1) corners per quad shows what working it out per pixel showed. A two-sided mesh
/// (water) keeps a second grid for its back, lit from the cell behind the face.
///
/// Each corner is 16 bits (see encodeCorner). The maps live in one shared buffer (<see cref="Arena"/>), a run per mesh
/// laid out by <see cref="ChunkQuad.AssignLightOffsets"/>; a mesh's quads are baked in groups of
/// <see cref="QuadsPerGroup"/>, each group one workgroup, and a bake is submitted before the frame's render pass.
/// </summary>
internal sealed unsafe class QuadLightmap : IDisposable
{
    public const int QuadsPerGroup = 64;

    /// <summary>WGSL for working out a cell face's smoothed corners, shared by the renderer (shadeFast, the per-pixel
    /// path kept for comparison) and the bake. The including shader declares the store's bindings (occPool, chunkTable,
    /// brickTable, lightPool, grids), <c>var&lt;private&gt; drawGrid: i32</c> and <c>fn ambientSky() -&gt; f32</c> (the flat
    /// ambient where there is no light storage), and includes <see cref="GridStore.LookupWgsl"/> and
    /// <see cref="GridStore.LightCodecWgsl"/>.</summary>
    public static readonly string SmoothLightWgsl = @"
const WPC: i32 = " + GridStore.WordsPerChunk + @";
const OCC_UNLOADED: i32 = " + GridStore.OccUnloaded + @";
const OCC_ALL_SOLID: i32 = " + GridStore.OccAllSolid + @";
const NO_SURFACE: u32 = " + GridStore.NoSurface + @"u;
const SLOT_WORDS: u32 = " + GridStore.WordsPerSlot + @"u;
const EMPTY_DISPLAY: u32 = 0xFFFFu;   // no light storage (or not lit yet): ambient and full sun

// Light slot of the brick holding voxel v, whose chunk-table entry is i (entryOf), or NO_SURFACE.
fn brickSlot(i: i32, v: vec3<i32>) -> u32 {
    if (i < 0) { return NO_SURFACE; }
    let l = v & vec3<i32>(31);
    let b = (l.x >> 3u) + 4 * ((l.y >> 3u) + 4 * (l.z >> 3u));
    return brickTable[u32(i * 64 + b)];
}

// Display value of voxel v stored in light slot s; no light storage reads as sun and ambient only.
fn slotDisplay(s: u32, v: vec3<i32>) -> u32 {
    if (s == NO_SURFACE) { return EMPTY_DISPLAY; }
    let lb = v & vec3<i32>(7);
    let k = u32(lb.x + 8 * (lb.y + 8 * lb.z));
    return (lightPool[s * SLOT_WORDS + (k >> 1u)] >> ((k & 1u) * 16u)) & 0xFFFFu;
}

fn displayAt(v: vec3<i32>) -> u32 { return slotDisplay(brickSlot(entryOf(drawGrid, v >> vec3<u32>(5u)), v), v); }

// Standard voxel corner AO: a corner flanked by two solids is fully dark; otherwise it dims by how many of the
// two sides + diagonal are solid.
fn vAO(s1: f32, s2: f32, c: f32) -> f32 {
    if (s1 > 0.5 && s2 > 0.5) { return 0.0; }
    return (3.0 - (s1 + s2 + c)) / 3.0;
}

// ── Smooth light and corner AO ───────────────────────────────────────────────────────────────────────────────
// Minecraft-style smooth lighting at the air side of a face: each of the 4 corners of a cell's face averages the air
// cells sharing that corner (the face's own air cell, the two beside it in the face plane, and the diagonal), and a
// pixel interpolates the 4 corner values by its position on the face (Minecraft does this per vertex; greedy-merged
// quads have no per-cell vertices, so the corners are kept per quad in its light map instead, see QuadLightmap). Only
// cells on the SAME surface count (open, with a solid directly behind along the face normal), and the diagonal is used
// only if at least one side cell is (Minecraft's corner rule), so light never reaches around an edge or through a
// corner. Corner AO (vAO) comes from the same neighbourhood. faceCorners works out one cell face's corners from one
// chunk-table lookup, one 27-bit solid mask of the air cell's 3x3x3 neighbourhood (9 occupancy words when it lies
// inside one chunk) and one decode per in-plane cell (the brick slot is shared while cells stay in the air cell's
// brick).

// Bit of offset d (each component -1..1) in a neighbourhood mask.
fn nbit(d: vec3<i32>) -> u32 { return u32((d.x + 1) + 3 * (d.y + 1) + 9 * (d.z + 1)); }
fn maskSolid(m: u32, d: vec3<i32>) -> bool { return ((m >> nbit(d)) & 1u) == 1u; }

// Solid mask of the 27 voxels around air (air's own chunk-table entry is i).
fn solidMask(air: vec3<i32>, i: i32) -> u32 {
    let l = air & vec3<i32>(31);
    if (all(l >= vec3<i32>(1)) && all(l <= vec3<i32>(30))) {
        // Whole neighbourhood in air's chunk: one occupancy row word per (dy, dz), 3 bits from each.
        if (i < 0) { return 0u; }
        let code = chunkTable[2 * i].x;
        if (code < 0) { return select(0u, 0x7FFFFFFu, code == OCC_ALL_SOLID); }
        let base = code * WPC + l.y - 1 + 32 * (l.z - 1);
        var m = 0u;
        for (var dz = 0; dz < 3; dz = dz + 1) {
            for (var dy = 0; dy < 3; dy = dy + 1) {
                let w = occPool[u32(base + dy + 32 * dz)];
                m = m | (((w >> u32(l.x - 1)) & 7u) << u32(3 * dy + 9 * dz));
            }
        }
        return m;
    }
    // Chunk border: the neighbourhood spans up to 8 chunks (the air cell's, and one more along each axis it sits at
    // the edge of). Each is looked up once (a chunk lookup is several reads and integer divides), not once per voxel.
    let c0 = air >> vec3<u32>(5u);
    var sx = 0; if (l.x == 0) { sx = -1; } else if (l.x == 31) { sx = 1; }
    var sy = 0; if (l.y == 0) { sy = -1; } else if (l.y == 31) { sy = 1; }
    var sz = 0; if (l.z == 0) { sz = -1; } else if (l.z == 31) { sz = 1; }
    var codes: array<i32, 8>;
    for (var n = 0; n < 8; n = n + 1) {
        // Combinations stepping along an axis the cell isn't at the edge of are never used.
        if (((n & 1) != 0 && sx == 0) || ((n & 2) != 0 && sy == 0) || ((n & 4) != 0 && sz == 0)) { continue; }
        let o = vec3<i32>(select(0, sx, (n & 1) != 0), select(0, sy, (n & 2) != 0), select(0, sz, (n & 4) != 0));
        var e = i;
        if (n != 0) { e = entryOf(drawGrid, c0 + o); }
        codes[n] = select(chunkTable[2 * max(e, 0)].x, OCC_UNLOADED, e < 0);
    }
    var m = 0u;
    for (var k = 0; k < 27; k = k + 1) {
        let d = vec3<i32>(k % 3, (k / 3) % 3, k / 9) - vec3<i32>(1);
        let lv = l + d;
        let n = select(0, 1, lv.x < 0 || lv.x > 31) | select(0, 2, lv.y < 0 || lv.y > 31) | select(0, 4, lv.z < 0 || lv.z > 31);
        let code = codes[n];
        var solid = code == OCC_ALL_SOLID;
        if (code >= 0) {
            let w = lv & vec3<i32>(31);
            solid = ((occPool[u32(code * WPC + w.y + 32 * w.z)] >> u32(w.x)) & 1u) == 1u;
        }
        if (solid) { m = m | (1u << u32(k)); }
    }
    return m;
}

// See-through mask of the 27 voxels around air (air's own chunk-table entry is i), laid out like solidMask's, from
// the chunks' see-through bits (a see-through block with light worked out around it, BlockDef.CatchesLight: water, glass, leaves; slot + 1 in the
// chunk entry's second word's z, 0 for none). Lets a see-through face's smoothing test its neighbours with one
// chunk lookup.
fn seeMask(air: vec3<i32>, i: i32) -> u32 {
    let l = air & vec3<i32>(31);
    if (all(l >= vec3<i32>(1)) && all(l <= vec3<i32>(30))) {
        if (i < 0) { return 0u; }
        let seeSlot = chunkTable[2 * i + 1].z;
        if (seeSlot <= 0) { return 0u; }
        let base = (seeSlot - 1) * WPC + l.y - 1 + 32 * (l.z - 1);
        var m = 0u;
        for (var dz = 0; dz < 3; dz = dz + 1) {
            for (var dy = 0; dy < 3; dy = dy + 1) {
                let w = occPool[u32(base + dy + 32 * dz)];
                m = m | (((w >> u32(l.x - 1)) & 7u) << u32(3 * dy + 9 * dz));
            }
        }
        return m;
    }
    // Chunk border: as solidMask, each of the up to 8 chunks involved is looked up once.
    let c0 = air >> vec3<u32>(5u);
    var sx = 0; if (l.x == 0) { sx = -1; } else if (l.x == 31) { sx = 1; }
    var sy = 0; if (l.y == 0) { sy = -1; } else if (l.y == 31) { sy = 1; }
    var sz = 0; if (l.z == 0) { sz = -1; } else if (l.z == 31) { sz = 1; }
    var slots: array<i32, 8>;
    for (var n = 0; n < 8; n = n + 1) {
        if (((n & 1) != 0 && sx == 0) || ((n & 2) != 0 && sy == 0) || ((n & 4) != 0 && sz == 0)) { continue; }
        let o = vec3<i32>(select(0, sx, (n & 1) != 0), select(0, sy, (n & 2) != 0), select(0, sz, (n & 4) != 0));
        var e = i;
        if (n != 0) { e = entryOf(drawGrid, c0 + o); }
        slots[n] = select(chunkTable[2 * max(e, 0) + 1].z, 0, e < 0);
    }
    var m = 0u;
    for (var k = 0; k < 27; k = k + 1) {
        let d = vec3<i32>(k % 3, (k / 3) % 3, k / 9) - vec3<i32>(1);
        let lv = l + d;
        let n = select(0, 1, lv.x < 0 || lv.x > 31) | select(0, 2, lv.y < 0 || lv.y > 31) | select(0, 4, lv.z < 0 || lv.z > 31);
        let seeSlot = slots[n];
        if (seeSlot > 0) {
            let w = lv & vec3<i32>(31);
            if (((occPool[u32((seeSlot - 1) * WPC + w.y + 32 * w.z)] >> u32(w.x)) & 1u) == 1u) { m = m | (1u << u32(k)); }
        }
    }
    return m;
}

// For a see-through block's face, whether the in-plane cell air + d lies on the same surface without a solid block
// behind it: a see-through block behind it, so a water surface or a canopy smooths across itself, or, seen from inside
// the block (inside: the face's own cell is see-through, e.g. the water surface from below), the cell is see-through
// too. sm is the see-through mask (0 for a face that isn't see-through).
fn seeAt(sm: u32, d: vec3<i32>, N: vec3<i32>, inside: bool) -> bool {
    return maskSolid(sm, d - N) || (inside && maskSolid(sm, d));
}

// One in-plane cell's (sky, r, g, b), sun and weight (0 when not included in the smoothing).
struct WCell { a: vec4<f32>, sun: f32, w: f32 };

fn weighed(inc: bool, v: vec3<i32>, homeBrick: vec3<i32>, homeSlot: u32, homeEntry: i32) -> WCell {
    var r: WCell;
    if (!inc) { r.a = vec4<f32>(0.0); r.sun = 0.0; r.w = 0.0; return r; }
    var d: u32;
    // In the air cell's brick: its slot; else in its chunk: that chunk's entry (no chunk lookup); else a full lookup.
    if (all((v >> vec3<u32>(3u)) == homeBrick)) { d = slotDisplay(homeSlot, v); }
    else if (all((v >> vec3<u32>(5u)) == (homeBrick >> vec3<u32>(2u)))) { d = slotDisplay(brickSlot(homeEntry, v), v); }
    else { d = displayAt(v); }
    if (isEmptyLight(d)) { r.a = vec4<f32>(ambientSky(), 0.0, 0.0, 0.0); r.sun = 1.0; }
    else { r.a = vec4<f32>(0.0, decodeLight(d)); r.sun = f32((d >> 14u) & 3u) / 3.0; }
    r.w = 1.0;
    return r;
}

struct Corner4 { a: vec4<f32>, sun: f32 };

fn avg4(p: WCell, q: WCell, r: WCell, s: WCell) -> Corner4 {
    let n = p.w + q.w + r.w + s.w; // >= 1: the air cell is always included
    var c: Corner4;
    c.a = (p.a + q.a + r.a + s.a) / n;
    c.sun = (p.sun + q.sun + r.sun + s.sun) / n;
    return c;
}

// The smoothed light and corner AO at the 4 corners of one cell face: the face of the open cell air (grid voxel space)
// whose normal N points into air, with T and B the face plane's axes (see faceAxes). see: a see-through block's face
// (see seeAt). c00 is the corner at -T -B, c10 at +T -B, c01 at -T +B and c11 at +T +B; ao holds their AO in that order.
struct FaceCorners { c00: Corner4, c10: Corner4, c01: Corner4, c11: Corner4, ao: vec4<f32> };

fn faceCorners(air: vec3<i32>, N: vec3<i32>, T: vec3<i32>, B: vec3<i32>, see: bool) -> FaceCorners {
    let ai = entryOf(drawGrid, air >> vec3<u32>(5u));
    let m = solidMask(air, ai);
    let hb = air >> vec3<u32>(3u);
    let hs = brickSlot(ai, air);

    // Air-layer solids around the cell (AO, and the first half of the same-surface test).
    let sTm = maskSolid(m, -T);     let sTp = maskSolid(m, T);
    let sBm = maskSolid(m, -B);     let sBp = maskSolid(m, B);
    let sMM = maskSolid(m, -T - B); let sPM = maskSolid(m, T - B);
    let sMP = maskSolid(m, -T + B); let sPP = maskSolid(m, T + B);

    // Same surface: open, with a solid directly behind along the normal (or for a see-through block's face, a
    // see-through block, or from inside one a see-through cell: see seeAt). Diagonals need a side cell (corner rule).
    // A see-through face reads its neighbours' see-through bits from one mask (seeMask).
    var sm = 0u;
    if (see) { sm = seeMask(air, ai); }
    let inside = maskSolid(sm, vec3<i32>(0));
    let oTm = !sTm && (maskSolid(m, -T - N) || seeAt(sm, -T, N, inside));
    let oTp = !sTp && (maskSolid(m, T - N) || seeAt(sm, T, N, inside));
    let oBm = !sBm && (maskSolid(m, -B - N) || seeAt(sm, -B, N, inside));
    let oBp = !sBp && (maskSolid(m, B - N) || seeAt(sm, B, N, inside));
    let oMM = (oTm || oBm) && !sMM && (maskSolid(m, -T - B - N) || seeAt(sm, -T - B, N, inside));
    let oPM = (oTp || oBm) && !sPM && (maskSolid(m, T - B - N) || seeAt(sm, T - B, N, inside));
    let oMP = (oTm || oBp) && !sMP && (maskSolid(m, -T + B - N) || seeAt(sm, -T + B, N, inside));
    let oPP = (oTp || oBp) && !sPP && (maskSolid(m, T + B - N) || seeAt(sm, T + B, N, inside));

    let cC  = weighed(true, air, hb, hs, ai);
    let cTm = weighed(oTm, air - T, hb, hs, ai);     let cTp = weighed(oTp, air + T, hb, hs, ai);
    let cBm = weighed(oBm, air - B, hb, hs, ai);     let cBp = weighed(oBp, air + B, hb, hs, ai);
    let cMM = weighed(oMM, air - T - B, hb, hs, ai); let cPM = weighed(oPM, air + T - B, hb, hs, ai);
    let cMP = weighed(oMP, air - T + B, hb, hs, ai); let cPP = weighed(oPP, air + T + B, hb, hs, ai);

    var f: FaceCorners;
    f.c00 = avg4(cC, cTm, cBm, cMM);
    f.c10 = avg4(cC, cTp, cBm, cPM);
    f.c01 = avg4(cC, cTm, cBp, cMP);
    f.c11 = avg4(cC, cTp, cBp, cPP);
    let s = vec4<f32>(select(0.0, 1.0, sTm), select(0.0, 1.0, sTp), select(0.0, 1.0, sBm), select(0.0, 1.0, sBp));
    f.ao = vec4<f32>(vAO(s.x, s.z, select(0.0, 1.0, sMM)), vAO(s.y, s.z, select(0.0, 1.0, sPM)),
                     vAO(s.x, s.w, select(0.0, 1.0, sMP)), vAO(s.y, s.w, select(0.0, 1.0, sPP)));
    return f;
}

// A face plane's two axes, T then B, for a face along axis 0 (x), 1 (y) or 2 (z): the axes its quads' width and height
// run along (see ChunkQuad).
fn faceAxes(axis: u32) -> mat2x3<f32> {
    if (axis == 0u) { return mat2x3<f32>(vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(0.0, 0.0, 1.0)); }
    if (axis == 1u) { return mat2x3<f32>(vec3<f32>(1.0, 0.0, 0.0), vec3<f32>(0.0, 0.0, 1.0)); }
    return mat2x3<f32>(vec3<f32>(1.0, 0.0, 0.0), vec3<f32>(0.0, 1.0, 0.0));
}

// A face corner as a light map stores it, 16 bits: the light (the brighter of the flat ambient and the rest, per
// channel: they're combined that way when shaded) as brightness on a square-root curve in bits 0-7 and warmth 0-6
// (3 = white, see warmTint) in bits 8-10; sun visibility 0-7 in bits 11-13; and corner AO 0-3 in bits 14-15 (exact:
// vAO only takes thirds).
fn encodeCorner(c: Corner4, ao: f32) -> u32 {
    let l = max(vec3<f32>(c.a.x), c.a.yzw);
    let m = max(l.r, max(l.g, l.b));
    return u32(round(255.0 * sqrt(clamp(m, 0.0, 1.0)))) | (u32(round(lightWarmth(l) * 6.0)) << 8u)
         | (u32(round(clamp(c.sun, 0.0, 1.0) * 7.0)) << 11u) | (u32(round(clamp(ao, 0.0, 1.0) * 3.0)) << 14u);
}

struct LmCorner { rgb: vec3<f32>, sun: f32, ao: f32 };

fn decodeCorner(e: u32) -> LmCorner {
    var c: LmCorner;
    let f = f32(e & 255u) / 255.0;
    c.rgb = f * f * warmTint(f32((e >> 8u) & 7u) / 6.0);
    c.sun = f32((e >> 11u) & 7u) / 7.0;
    c.ao = f32((e >> 14u) & 3u) / 3.0;
    return c;
}
";

    private static readonly string Wgsl = SmoothLightWgsl + GridStore.LookupWgsl + GridStore.LightCodecWgsl + @"
@group(0) @binding(0) var<storage, read> occPool: array<u32>;
@group(0) @binding(1) var<storage, read> chunkTable: array<vec4<i32>>;
@group(0) @binding(2) var<storage, read> brickTable: array<u32>;
@group(0) @binding(3) var<storage, read> lightPool: array<u32>;
@group(0) @binding(4) var<storage, read> grids: array<GridDesc>;
@group(0) @binding(5) var<storage, read> quadArena: array<vec2<u32>>;
@group(0) @binding(6) var<storage, read_write> lightArena: array<atomic<u32>>;

// One group of up to 64 quads of one mesh: its grid and chunk, its first quad in quadArena, how many, where its mesh's
// light map starts in lightArena (words), and flags: 1 a see-through block's faces, 2 two-sided (water).
struct Job { grid: i32, cx: i32, cy: i32, cz: i32, quadStart: u32, quadCount: u32, lightBase: u32, flags: u32 };
@group(0) @binding(7) var<storage, read> jobs: array<Job>;

// ambient: the flat ambient where there is no light storage; jobCount: the jobs listed.
struct Params { ambient: f32, jobCount: u32, pad0: u32, pad1: u32 };
@group(0) @binding(8) var<uniform> params: Params;

var<private> drawGrid: i32;
fn ambientSky() -> f32 { return params.ambient; }

const NO_LIGHTMAP: u32 = 0xFFFFu;
const GROUP: u32 = " + QuadsPerGroup + @"u;

// The cells (of both sides, for a two-sided mesh) each of the job's quads has, summed up to and including it.
var<workgroup> cellEnd: array<u32, " + QuadsPerGroup + @">;

fn lightOffset(q: vec2<u32>) -> u32 { return ((q.y >> 9u) & 0x7FFFu) | ((q.y >> 31u) << 15u); }
fn quadW(q: vec2<u32>) -> u32 { return ((q.x >> 29u) | (((q.y >> 24u) & 3u) << 3u)) + 1u; }
fn quadH(q: vec2<u32>) -> u32 { return ((q.y >> 26u) & 31u) + 1u; }

// One workgroup per job. Each thread first clears its quad's light map (the corners are ORed in, two to a word),
// then the job's cells are shared out evenly: a thread works out a cell's face corners and writes the ones the cell
// owns, each corner of the quad's grid being written by one cell (the one on its +T, +B side, or the last one along
// an edge).
@compute @workgroup_size(" + QuadsPerGroup + @")
fn bake_main(@builtin(workgroup_id) wg: vec3<u32>, @builtin(num_workgroups) nwg: vec3<u32>,
             @builtin(local_invocation_index) t: u32) {
    let ji = wg.x + wg.y * nwg.x;
    if (ji >= params.jobCount) { return; }
    let job = jobs[ji];
    let sides = select(1u, 2u, (job.flags & 2u) != 0u);

    var cells = 0u;
    if (t < job.quadCount) {
        let q = quadArena[job.quadStart + t];
        let off = lightOffset(q);
        if (off != NO_LIGHTMAP && ((q.x >> 18u) & 7u) < 6u) {
            let w = quadW(q);
            let h = quadH(q);
            cells = w * h * sides;
            let words = ((w + 1u) * (h + 1u) * sides + 1u) / 2u;
            for (var k = 0u; k < words; k++) { atomicStore(&lightArena[job.lightBase + off + k], 0u); }
        }
    }
    cellEnd[t] = cells;
    workgroupBarrier();
    for (var s = 1u; s < GROUP; s = s * 2u) {
        var v = cellEnd[t];
        if (t >= s) { v = v + cellEnd[t - s]; }
        workgroupBarrier();
        cellEnd[t] = v;
        workgroupBarrier();
    }
    storageBarrier(); // every quad cleared before any corner is ORed in

    let total = cellEnd[GROUP - 1u];
    for (var c = t; c < total; c = c + GROUP) {
        var lo = 0u;
        var hi = GROUP - 1u;
        while (lo < hi) {
            let mid = (lo + hi) / 2u;
            if (cellEnd[mid] > c) { hi = mid; } else { lo = mid + 1u; }
        }
        var start = 0u;
        if (lo > 0u) { start = cellEnd[lo - 1u]; }
        bakeCell(job, quadArena[job.quadStart + lo], c - start);
    }
}

// Cell k of quad q (row by row along its width; past its cells, the same again for its back).
fn bakeCell(job: Job, q: vec2<u32>, k: u32) {
    drawGrid = job.grid;
    let w = quadW(q);
    let h = quadH(q);
    let back = k >= w * h;
    let kc = k - select(0u, w * h, back);
    let i = kc % w;
    let j = kc / w;

    let face = (q.x >> 18u) & 7u;
    let ax = faceAxes(face / 2u);
    let T = vec3<i32>(ax[0]);
    let B = vec3<i32>(ax[1]);
    var N = vec3<i32>(0);
    N[face / 2u] = select(1, -1, (face & 1u) == 1u);
    if (back) { N = -N; }
    // The quad's first corner is its lowest; the face lies on the block's side N points out of, so the open cell is
    // the one in front of the plane along N.
    let o = vec3<i32>(i32(q.x & 63u), i32((q.x >> 6u) & 63u), i32((q.x >> 12u) & 63u));
    let lc = o + T * i32(i) + B * i32(j) + min(N, vec3<i32>(0));
    let air = vec3<i32>(job.cx, job.cy, job.cz) * 32 + lc;
    let f = faceCorners(air, N, T, B, (job.flags & 1u) != 0u);

    let stride = w + 1u;
    let base = 2u * (job.lightBase + lightOffset(q)) + select(0u, stride * (h + 1u), back) + i + stride * j;
    let lastI = i == w - 1u;
    let lastJ = j == h - 1u;
    putCorner(base, encodeCorner(f.c00, f.ao.x));
    if (lastI) { putCorner(base + 1u, encodeCorner(f.c10, f.ao.y)); }
    if (lastJ) { putCorner(base + stride, encodeCorner(f.c01, f.ao.z)); }
    if (lastI && lastJ) { putCorner(base + stride + 1u, encodeCorner(f.c11, f.ao.w)); }
}

fn putCorner(i: u32, e: u32) { atomicOr(&lightArena[i >> 1u], e << ((i & 1u) * 16u)); }
";

    private readonly GpuContext _ctx;
    private readonly ComputePipeline _bake;
    private readonly GpuBuffer _params;
    private GpuBuffer _jobs;
    private Job[] _staging = new Job[1024];
    private int _jobCount, _meshCount;

    /// <summary>The shared buffer every mesh's light map is a run of (32-bit words, two corners each).</summary>
    public GpuBufferArena Arena { get; }

    /// <summary>Meshes and quad groups baked by the last <see cref="Bake"/>, for the debug panel.</summary>
    public int LastMeshes { get; private set; }
    public int LastGroups { get; private set; }

    public QuadLightmap(GpuContext ctx)
    {
        _ctx = ctx;
        Arena = new GpuBufferArena(ctx, 4, BufferUsage.Storage, 1 << 22, // 16 MB to start; doubles as needed
                                   ctx.AdapterLimits.MaxStorageBufferBindingSize);
        _bake = new ComputePipeline(ctx, Wgsl, "bake_main");
        _params = GpuBuffer.CreateUniform(ctx, 16);
        _jobs = GpuBuffer.CreateStorage(ctx, (ulong)(_staging.Length * sizeof(Job)));
    }

    /// <summary>A run of <paramref name="words"/> words for a mesh's light map, or null for none (or no room).</summary>
    public BufferSpan? Allocate(uint words) => words == 0 ? null : Arena.Allocate(words);

    /// <summary>Lists <paramref name="mesh"/> (of chunk <paramref name="chunk"/> of grid <paramref name="grid"/>) for the
    /// next <see cref="Bake"/>.</summary>
    public void Queue(GpuMesh mesh, int grid, ChunkPosition chunk)
    {
        if (mesh.Light == null) return;
        uint flags = ((mesh.LightKind & LightmapKind.SeeThrough) != 0 ? 1u : 0u)
                   | ((mesh.LightKind & LightmapKind.TwoSided) != 0 ? 2u : 0u);
        for (uint first = 0; first < mesh.QuadCount; first += QuadsPerGroup)
        {
            if (_jobCount == _staging.Length) Array.Resize(ref _staging, _staging.Length * 2);
            _staging[_jobCount++] = new Job
            {
                Grid = grid, ChunkX = chunk.X, ChunkY = chunk.Y, ChunkZ = chunk.Z,
                QuadStart = mesh.QuadBase + first, QuadCount = System.Math.Min(QuadsPerGroup, mesh.QuadCount - first),
                LightBase = mesh.LightBase, Flags = flags,
            };
        }
        _meshCount++;
    }

    /// <summary>Bakes every mesh queued since the last call, as its own submit (so call it before the frame's render
    /// pass is submitted, after this frame's lighting). <paramref name="quads"/> is the shared quad buffer.</summary>
    public void Bake(GridStore store, GpuBuffer quads, float ambient)
    {
        LastMeshes = _meshCount;
        LastGroups = _jobCount;
        if (_jobCount == 0) return;
        ulong bytes = (ulong)(_jobCount * sizeof(Job));
        if (_jobs.SizeBytes < bytes)
        {
            _jobs.Dispose();
            _jobs = GpuBuffer.CreateStorage(_ctx, (ulong)(_staging.Length * sizeof(Job)));
        }
        _jobs.Write<Job>(0, _staging.AsSpan(0, _jobCount));
        Span<Params> p = stackalloc Params[1];
        p[0] = new Params { Ambient = ambient, JobCount = (uint)_jobCount };
        _params.Write<Params>(0, p);

        var bg = _bake.CreateBindGroup(new (uint, GpuBuffer)[]
        {
            (0, store.OccPool), (1, store.ChunkTable), (2, store.BrickTable), (3, store.LightPool), (4, store.Grids),
            (5, quads), (6, Arena.Buffer), (7, _jobs), (8, _params),
        });
        var encDesc = new CommandEncoderDescriptor();
        var enc = _ctx.Api.DeviceCreateCommandEncoder(_ctx.Device, &encDesc);
        // One workgroup per job, as a 2D grid since a big list can exceed the 65535-per-dimension limit.
        const int MaxPerDim = 65535;
        uint gx = (uint)System.Math.Min(_jobCount, MaxPerDim);
        uint gy = ((uint)_jobCount + gx - 1u) / gx;
        _bake.Record(enc, bg, gx, gy, 1u, "Light maps: bake", _jobCount);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = _ctx.Api.CommandEncoderFinish(enc, &cmdDesc);
        _ctx.FlushPending();
        _ctx.Api.QueueSubmit(_ctx.Queue, 1, &cmd);
        _ctx.Api.CommandBufferRelease(cmd);
        _ctx.Api.CommandEncoderRelease(enc);
        _ctx.Api.BindGroupRelease(bg);
        _jobCount = 0;
        _meshCount = 0;
    }

    public void Dispose()
    {
        _bake.Dispose();
        _params.Dispose();
        _jobs.Dispose();
        Arena.Dispose();
    }

    // bake_main's Job (32 bytes).
    private struct Job
    {
        public int Grid, ChunkX, ChunkY, ChunkZ;
        public uint QuadStart, QuadCount, LightBase, Flags;
    }

    // bake_main's Params (16 bytes).
    private struct Params
    {
        public float Ambient;
        public uint JobCount, Pad0, Pad1;
    }
}
