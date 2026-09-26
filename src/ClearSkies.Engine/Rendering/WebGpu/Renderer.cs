using ClearSkies.Engine.Math;
using ClearSkies.Engine.Voxels;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using System.Diagnostics;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// Orchestrates the WebGPU pipeline and exposes a small per-frame API. Owns the render pipeline,
/// the camera uniform + bind group, and a dynamic-offset model uniform buffer + bind group
/// (the WebGPU-core replacement for push constants).
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    // Draws per frame (each takes one ModelStride slot of the model uniform buffer). Chunks draw nearest first, so
    // hitting this drops the farthest ones — and the sky — which with a streaming budget of ~4400 chunks (see
    // ChunkLoadSystem) plus model blocks, ships and clouds used to cut distant islands off at 4096.
    private const int MaxObjects = 16384;
    private const ulong ModelStride = 256;   // >= minUniformBufferOffsetAlignment
    private const ulong CameraSize  = 272;   // two mat4x4<f32> (view, proj) + nine vec4<f32> (sun, light params, camera position, fog, zenith, horizon, haze, sea, light params 2)
    private const ulong ModelSize   = 96;    // mat4x4<f32> + vec3<i32> chunk + i32 grid + vec4<f32> params

    private static readonly string Wgsl = @"
const MIN_AMBIENT: f32 = 0.00;        // floor so no geometry is ever fully black
const AO_MIN: f32 = 0.15;             // darkest ambient-occluded corner (1 = no AO). 0.15 provides exaggeration for evaluation;
                                      // 0.45 is the subtler default.
const WPC: i32 = " + GridStore.WordsPerChunk + @";
const OCC_UNLOADED: i32 = " + GridStore.OccUnloaded + @";
const OCC_ALL_SOLID: i32 = " + GridStore.OccAllSolid + @";
const NO_SURFACE: u32 = " + GridStore.NoSurface + @"u;
const SLOT_WORDS: u32 = " + GridStore.WordsPerSlot + @"u;
const EMPTY_DISPLAY: u32 = 0x3000u;   // no light storage: full sun, no light, no AO

// sunDir.w: sun strength (SunLight.Strength). lightParams.x: ray AO strength, .y: 1 = reference light path (see
// shadeFast), .z: ambient (0-1), .w: unused. camPos.xyz: camera world position. fog.xy: the world's fog start/end
// (horizontal), fog.zw: the cloud layer's (see CloudLayer), in blocks from the camera. zenith/horizon.rgb: the sky
// gradient (see SkySettings). horizon.w: the distance haze's strength (0-1), haze.rgb its colour, haze.w its distance.
// sea: the cloud sea's altitude, coverage (0 = off), cell size and thickness, in blocks (see cloudSea).
// lightParams2.y/.z: render pass debug toggles (see fs_main).
struct Camera {
    view: mat4x4<f32>, proj: mat4x4<f32>, sunDir: vec4<f32>, lightParams: vec4<f32>,
    camPos: vec4<f32>, fog: vec4<f32>, zenith: vec4<f32>, horizon: vec4<f32>, haze: vec4<f32>, sea: vec4<f32>,
    lightParams2: vec4<f32>,
};
@group(0) @binding(0) var<uniform> camera: Camera;

// Sky colour seen along world direction dir (unit): horizon haze blending up to the zenith colour, deepening a
// little below the horizon (the open void under the islands) so looking down doesn't read as flat grey, plus a
// soft glow around the sun. The sun disc itself is added only by fs_sky, so fogged terrain in front of the sun
// picks up the glow but not a disc.
fn skyColor(dir: vec3<f32>) -> vec3<f32> {
    let up   = clamp(dir.y, -1.0, 1.0);
    let hz   = camera.horizon.rgb;
    let zn   = camera.zenith.rgb;
    var c    = mix(hz, zn, sqrt(max(up, 0.0)));
    c        = mix(c, mix(hz, zn, 0.35), pow(max(-up, 0.0), 0.6));
    let toSun = max(dot(dir, -camera.sunDir.xyz), 0.0);
    let glow  = 0.35 * pow(toSun, 8.0) + 0.25 * pow(toSun, 64.0);
    return c + vec3<f32>(1.0, 0.9, 0.7) * glow * camera.sunDir.w;
}

// Aerial perspective: a blue-grey tint that builds with (3D) distance, reaching 63% of horizon.w by haze.w blocks,
// and itself turns towards the sky colour behind as it thickens, so far islands (above and below too) read as far
// away and melt into the horizon.
fn applyHaze(color: vec3<f32>, d: vec3<f32>) -> vec3<f32> {
    let k = camera.horizon.w * (1.0 - exp(-length(d) / camera.haze.w));
    return mix(color, mix(camera.haze.rgb, skyColor(normalize(d)), k), k);
}

// Hazes a lit surface colour at worldPos, then fades it into what fs_sky draws behind it (the sky, or the cloud sea
// below) by horizontal distance: streaming loads whole chunk columns, so the loaded world ends only sideways, where
// the fog is total (see SkySettings.FogDistance). Fading into the sky colour alone would leave islands showing as
// blue shapes over the white sea as they load in.
fn applyFog(color: vec3<f32>, worldPos: vec3<f32>) -> vec3<f32> {
    let d = worldPos - camera.camPos.xyz;
    let f = smoothstep(camera.fog.x, camera.fog.y, length(d.xz));
    let hazed = applyHaze(color, d);
    if (f <= 0.0) { return hazed; } // only the fog band pays for tracing the sea
    return mix(hazed, background(normalize(d)).rgb, f);
}

// What fs_sky draws along world direction dir (unit), without the sun disc: the sky (w = 0), or the cloud sea if the
// view ray meets it (w = 1).
fn background(dir: vec3<f32>) -> vec4<f32> {
    let sky = skyColor(dir);
    if (camera.sea.y > 0.0) {
        let hit = cloudSea(camera.camPos.xyz, dir);
        if (hit.w >= 0.0) { return vec4<f32>(mix(applyHaze(hit.rgb, dir * hit.w), sky, smoothstep(20000.0, 40000.0, hit.w)), 1.0); }
    }
    return vec4<f32>(sky, 0.0);
}

// Background: one full-screen triangle at the far plane, drawn after the world with depth test LessEqual so it only
// shades pixels nothing else covered. Each vertex carries its world-space view ray (camera rotation only).
struct SkyOut { @builtin(position) pos: vec4<f32>, @location(0) dir: vec3<f32> };

@vertex
fn vs_sky(@builtin(vertex_index) i: u32) -> SkyOut {
    let ndc = vec2<f32>(f32((i << 1u) & 2u), f32(i & 2u)) * 2.0 - 1.0;
    let viewRay = vec3<f32>(ndc.x / camera.proj[0][0], ndc.y / camera.proj[1][1], -1.0);
    let rot = mat3x3<f32>(camera.view[0].xyz, camera.view[1].xyz, camera.view[2].xyz);
    var o: SkyOut;
    o.pos = vec4<f32>(ndc, 0.0, 1.0); // the far plane (depth is reversed: far is 0)
    o.dir = transpose(rot) * viewRay;
    return o;
}

@fragment
fn fs_sky(in: SkyOut) -> @location(0) vec4<f32> {
    let dir   = normalize(in.dir);
    let toSun = dot(dir, -camera.sunDir.xyz);
    let disc  = smoothstep(0.9992, 0.9996, toSun) * camera.sunDir.w;
    let b = background(dir);
    return vec4<f32>(b.rgb + vec3<f32>(1.0, 0.95, 0.85) * disc * (1.0 - b.w), 1.0);
}

// ── Cloud sea ───────────────────────────────────────────────────────────────────────────────────────────────────
// A floor of blocky cloud far below the islands, drawn in the background pass (it is behind everything) by stepping
// the view ray through a grid of sea.z-block cells (a 2D DDA) inside the layer sea.x .. sea.x + sea.w: each cell is
// empty or one flat-topped block up to sea.w thick, shaded per face like the block clouds (fs_cloud). Past
// SeaDetail blocks, where cells would shimmer under a pixel, it is drawn as its average instead.

const SeaDetail = 14000.0;
const SeaSteps  = 96;
const SeaWhite  = vec3<f32>(0.96, 0.97, 1.0);

fn seaHash(c: vec2<i32>) -> f32 {
    var h = u32(c.x) * 0x8DA6B343u ^ u32(c.y) * 0xD8163841u;
    h ^= h >> 16u; h *= 0x7FEB352Du;
    h ^= h >> 15u; h *= 0x846CA68Bu;
    h ^= h >> 16u;
    return f32(h >> 8u) * (1.0 / 16777216.0);
}

// Smooth value noise over cells at a lattice spacing of s cells.
fn seaNoise(c: vec2<i32>, s: f32) -> f32 {
    let f = (vec2<f32>(c) + 0.5) / s;
    let i = vec2<i32>(floor(f));
    let t = f - floor(f);
    let u = t * t * (3.0 - 2.0 * t);
    let a = mix(seaHash(i), seaHash(i + vec2<i32>(1, 0)), u.x);
    let b = mix(seaHash(i + vec2<i32>(0, 1)), seaHash(i + vec2<i32>(1, 1)), u.x);
    return mix(a, b, u.y);
}

// A cell's block height as a fraction of the layer (in quarter steps), or 0 for an empty cell.
fn seaCell(c: vec2<i32>) -> f32 {
    let v = 0.6 * seaNoise(c, 11.0) + 0.3 * seaNoise(c + vec2<i32>(913, 211), 4.0) + 0.1 * seaHash(c);
    let threshold = mix(0.75, 0.25, camera.sea.y);
    if (v < threshold) { return 0.0; }
    return clamp(ceil((v - threshold) / 0.1 * 4.0) * 0.25, 0.25, 1.0);
}

fn seaShade(n: vec3<f32>) -> vec3<f32> {
    var shade = 0.8;
    if (n.y > 0.5)           { shade = 1.0; }
    else if (n.y < -0.5)     { shade = 0.7; }
    else if (abs(n.x) > 0.5) { shade = 0.88; }
    shade *= 0.9 + 0.1 * max(dot(n, -camera.sunDir.xyz), 0.0) * camera.sunDir.w;
    return SeaWhite * shade;
}

// The cloud sea along the ray from ro in direction rd: its shaded colour and the distance to it, or w = -1 for none.
fn cloudSea(ro: vec3<f32>, rd: vec3<f32>) -> vec4<f32> {
    let y0 = camera.sea.x;
    let h  = camera.sea.w;
    let size = camera.sea.z;
    if (abs(rd.y) < 1e-5) { return vec4<f32>(0.0, 0.0, 0.0, -1.0); }
    let ta = (y0 - ro.y) / rd.y;
    let tb = (y0 + h - ro.y) / rd.y;
    let tEnter = max(min(ta, tb), 0.0);
    let tExit  = max(ta, tb);
    if (tExit <= 0.0) { return vec4<f32>(0.0, 0.0, 0.0, -1.0); }
    if (tEnter > SeaDetail) {
        // Far: the average of cloud tops and the gaps between them.
        let avg = mix(skyColor(rd), SeaWhite * 0.95, camera.sea.y);
        return vec4<f32>(avg, tEnter);
    }

    let p = ro + rd * tEnter;
    var cell = vec2<i32>(floor(p.xz / size));
    let dirXZ = rd.xz;
    let stepC = vec2<i32>(select(-1, 1, dirXZ.x > 0.0), select(-1, 1, dirXZ.y > 0.0));
    let inv = 1.0 / max(abs(dirXZ), vec2<f32>(1e-6));
    let next = (vec2<f32>(cell) + select(vec2<f32>(0.0), vec2<f32>(1.0), dirXZ > vec2<f32>(0.0))) * size;
    var tMax = select((next - ro.xz) / dirXZ, vec2<f32>(1e30), abs(dirXZ) < vec2<f32>(1e-6));
    let tDelta = size * inv;
    var t = tEnter;
    var axis = -1; // the axis last stepped across (0 x, 1 z), -1 before any
    for (var i = 0; i < SeaSteps; i++) {
        let tNext = min(tMax.x, tMax.y);
        let tOut = min(tNext, tExit);
        let frac = seaCell(cell);
        if (frac > 0.0) {
            let top = y0 + frac * h;
            let ya = (y0 - ro.y) / rd.y;
            let yb = (top - ro.y) / rd.y;
            let lo = max(min(ya, yb), t);
            let hi = min(max(ya, yb), tOut);
            if (lo <= hi) {
                var n = vec3<f32>(0.0, -sign(rd.y), 0.0);
                if (lo <= t + 1e-3 && axis == 0) { n = vec3<f32>(-f32(stepC.x), 0.0, 0.0); }
                if (lo <= t + 1e-3 && axis == 1) { n = vec3<f32>(0.0, 0.0, -f32(stepC.y)); }
                return vec4<f32>(seaShade(n), lo);
            }
        }
        if (tNext >= tExit) { break; }
        t = tNext;
        if (tMax.x < tMax.y) { tMax.x += tDelta.x; cell.x += stepC.x; axis = 0; }
        else                 { tMax.y += tDelta.y; cell.y += stepC.y; axis = 1; }
    }
    return vec4<f32>(0.0, 0.0, 0.0, -1.0);
}

// model: world transform. chunk: this chunk's coordinate in its grid. grid: the grid's index in the GridStore, or
// -1 for a non-chunk draw (drawn full-bright). params.x: alpha-test cutoff for fs_model (0 = opaque); params.yzw: for a
// model block (fs_model with grid >= 0), its cell's voxel coordinates within the chunk.
struct Model { model: mat4x4<f32>, chunk: vec3<i32>, grid: i32, params: vec4<f32> };
@group(1) @binding(0) var<uniform> model: Model;

// The shared voxel storage (see GridStore): occupancy pool, chunk table ((occupancy slot or code, chunk tag), then
// ray-only data), brick table (light slot per 8³ brick), light pool and grid descriptors. Only a slot's display half
// is read here: one u16 per voxel, two per word — bits 0-3 R, 4-7 G, 8-11 B (lamp + bounce, square-curve encoded),
// 12-13 sun visibility (0-3), 14-15 ray AO occlusion (0-3).
" + GridStore.LookupWgsl + @"
@group(2) @binding(0) var<storage, read> occPool: array<u32>;
@group(2) @binding(1) var<storage, read> chunkTable: array<vec4<i32>>;
@group(2) @binding(2) var<storage, read> brickTable: array<u32>;
@group(2) @binding(3) var<storage, read> lightPool: array<u32>;
@group(2) @binding(4) var<storage, read> grids: array<GridDesc>;

// Block texture array: each layer is one named sprite from the spritesheet (TextureAtlas), sized
// TileSize² and nearest-filtered. Vertex UV is (u, v, layer): u/v are tile-space (not normalized),
// wrapped per-fragment with fract() so a texture repeats once per block regardless of how large a
// greedy-merged quad is; layer < 0 means untextured — use the vertex color instead (see fs_main).
@group(3) @binding(0) var atlasTex:  texture_2d_array<f32>;
@group(3) @binding(1) var atlasSamp: sampler;

struct VSOut {
    @builtin(position) pos:         vec4<f32>,
    @location(0)       color:       vec3<f32>,
    @location(1)       worldNormal: vec3<f32>,
    @location(2)       localPos:    vec3<f32>,
    @location(3)       localNormal: vec3<f32>,
    @location(4)       uv:          vec3<f32>,
    @location(5)       worldPos:    vec3<f32>,
};

@vertex
fn vs_main(
    @location(0) position: vec3<f32>,
    @location(1) normal:   vec3<f32>,
    @location(2) color:    vec3<f32>,
    @location(3) uv:       vec3<f32>
) -> VSOut {
    var o: VSOut;
    let world     = model.model * vec4<f32>(position, 1.0);
    o.pos         = camera.proj * camera.view * world;
    o.worldPos    = world.xyz;
    o.color       = color;
    o.worldNormal = (model.model * vec4<f32>(normal, 0.0)).xyz;
    o.localPos    = position;
    o.localNormal = normal;
    o.uv          = uv;
    return o;
}

// A chunk mesh vertex, packed (see ChunkVertex): x, y, z in bits 0-17 (6 each), face in 18-20 (+X, -X, +Y, -Y, +Z,
// -Z), texture layer in 21-28 (255: untextured); colour as RGB8. The texture coordinates follow from position and
// face, as GreedyMesher.MakeUv makes them: V runs down world Y on the side faces.
@vertex
fn vs_chunk(@location(0) packed: vec2<u32>) -> VSOut {
    let a = packed.x;
    let position = vec3<f32>(f32(a & 63u), f32((a >> 6u) & 63u), f32((a >> 12u) & 63u));
    let face = (a >> 18u) & 7u;
    let s = select(1.0, -1.0, (face & 1u) == 1u);
    var normal = vec3<f32>(0.0);
    var uv2: vec2<f32>;
    if (face < 2u)      { normal.x = s; uv2 = vec2<f32>(position.z, -position.y); }
    else if (face < 4u) { normal.y = s; uv2 = position.xz; }
    else                { normal.z = s; uv2 = vec2<f32>(position.x, -position.y); }
    let layerBits = (a >> 21u) & 255u;
    let layer = select(f32(layerBits), -1.0, layerBits == 255u);
    let c = packed.y;
    let color = vec3<f32>(f32(c & 255u), f32((c >> 8u) & 255u), f32((c >> 16u) & 255u)) / 255.0;

    var o: VSOut;
    let world     = model.model * vec4<f32>(position, 1.0);
    o.pos         = camera.proj * camera.view * world;
    o.worldPos    = world.xyz;
    o.color       = color;
    o.worldNormal = (model.model * vec4<f32>(normal, 0.0)).xyz;
    o.localPos    = position;
    o.localNormal = normal;
    o.uv          = vec3<f32>(uv2, layer);
    return o;
}

// Voxel v in this draw's grid (grid voxel space). Unloaded → open.
fn isSolid(v: vec3<i32>) -> bool {
    let i = entryOf(model.grid, v >> vec3<u32>(5u));
    if (i < 0) { return false; }
    let code = chunkTable[2 * i].x;
    if (code >= 0) {
        let l = v & vec3<i32>(31);
        return ((occPool[u32(code * WPC + l.y + 32 * l.z)] >> u32(l.x)) & 1u) == 1u;
    }
    return code == OCC_ALL_SOLID;
}

fn occ(v: vec3<i32>) -> f32 { return select(0.0, 1.0, isSolid(v)); }

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

fn displayAt(v: vec3<i32>) -> u32 { return slotDisplay(brickSlot(entryOf(model.grid, v >> vec3<u32>(5u)), v), v); }

fn decodeLevel(c: u32) -> f32 { let f = f32(c) / 15.0; return f * f; }

// One cell's light: sky (the flat ambient scaled by the ray AO occlusion, weighted by camera.lightParams.x), RGB
// (lamp and bounce light) and sun visibility 0-1.
struct Cell { sky: f32, rgb: vec3<f32>, sun: f32 };

fn cellAt(v: vec3<i32>) -> Cell {
    let d = displayAt(v);
    var c: Cell;
    c.sky = camera.lightParams.z * (1.0 - camera.lightParams.x * f32(d >> 14u) / 3.0);
    c.rgb = vec3<f32>(decodeLevel(d & 15u), decodeLevel((d >> 4u) & 15u), decodeLevel((d >> 8u) & 15u));
    c.sun = f32((d >> 12u) & 3u) / 3.0;
    return c;
}

// Minecraft-style smooth lighting at the air side of this fragment: sky + block light (each 0..1) and sun
// visibility (1 lit … 0 shadowed). Each of the 4 corners of the fragment's cell face averages the air cells
// sharing that corner — the fragment's own air cell, the two beside it in the face plane, and the diagonal —
// and the fragment interpolates the 4 corner values by its position on the face (Minecraft does this per
// vertex; greedy-merged quads have no per-cell vertices, so it is done per fragment instead). Only cells on
// the SAME surface count (air with a solid directly behind along the face normal), and the diagonal is used
// only if at least one side cell is (Minecraft's corner rule), so light never reaches around an edge or
// through a corner. SMOOTH_LIGHT = false falls back to the flat per-cell value.
const SMOOTH_LIGHT: bool = true;

struct Lit { sky: f32, rgb: vec3<f32>, sun: f32 };

fn onSurface(c: vec3<i32>, N: vec3<i32>) -> bool { return !isSolid(c) && isSolid(c - N); }

// (sky, r, g, b, sun) averaged over the usable cells at one corner.
struct Corner { a: vec4<f32>, sun: f32 };

fn cornerLit(air: vec3<i32>, s1: vec3<i32>, s2: vec3<i32>, dg: vec3<i32>, N: vec3<i32>) -> Corner {
    let inc1 = onSurface(s1, N);
    let inc2 = onSurface(s2, N);
    let incD = (inc1 || inc2) && onSurface(dg, N);
    var cells = array<vec3<i32>, 4>(air, s1, s2, dg);
    var inc = array<bool, 4>(true, inc1, inc2, incD);
    var acc = vec4<f32>(0.0);
    var accS = 0.0;
    var n = 0.0;
    for (var k = 0; k < 4; k = k + 1) {
        if (!inc[k]) { continue; }
        let c = cellAt(cells[k]);
        acc += vec4<f32>(c.sky, c.rgb);
        accS += c.sun;
        n += 1.0;
    }
    var r: Corner;
    r.a = acc / n;
    r.sun = accS / n;
    return r;
}

fn sampleLit(localPos: vec3<f32>, localNormal: vec3<f32>) -> Lit {
    let air = model.chunk * 32 + vec3<i32>(floor(localPos + 0.5 * localNormal));
    var o: Lit;

    if (!SMOOTH_LIGHT) {
        let c = cellAt(air);
        o.sky = c.sky;
        o.rgb = c.rgb;
        o.sun = c.sun;
        return o;
    }

    let N = vec3<i32>(round(localNormal));
    let n = abs(localNormal);
    var T: vec3<i32>; var B: vec3<i32>;
    if (n.x > 0.5)      { T = vec3<i32>(0, 1, 0); B = vec3<i32>(0, 0, 1); }
    else if (n.y > 0.5) { T = vec3<i32>(1, 0, 0); B = vec3<i32>(0, 0, 1); }
    else                { T = vec3<i32>(1, 0, 0); B = vec3<i32>(0, 1, 0); }

    let c00 = cornerLit(air, air - T, air - B, air - T - B, N);
    let c10 = cornerLit(air, air + T, air - B, air + T - B, N);
    let c01 = cornerLit(air, air - T, air + B, air - T + B, N);
    let c11 = cornerLit(air, air + T, air + B, air + T + B, N);

    // Plain bilinear across the cell face, like Minecraft's per-vertex interpolation.
    let s = fract(dot(localPos, vec3<f32>(T)));
    let t = fract(dot(localPos, vec3<f32>(B)));
    let a = mix(mix(c00.a, c10.a, s), mix(c01.a, c11.a, s), t);
    o.sky = a.x;
    o.rgb = a.yzw;
    o.sun = mix(mix(c00.sun, c10.sun, s), mix(c01.sun, c11.sun, s), t);
    return o;
}

// Standard voxel corner AO: a corner flanked by two solids is fully dark; otherwise it dims by how many of the
// two sides + diagonal are solid.
fn vAO(s1: f32, s2: f32, c: f32) -> f32 {
    if (s1 > 0.5 && s2 > 0.5) { return 0.0; }
    return (3.0 - (s1 + s2 + c)) / 3.0;
}

// Smooth ambient occlusion sampled from the opacity neighbourhood of the air-side cell. The face's two in-plane
// axes give four corner AO values (from the side + diagonal neighbours); the fragment's fractional position
// within the cell bilerps between them, so AO stays smooth across a greedy-merged quad and is light-independent.
fn computeAO(localPos: vec3<f32>, localNormal: vec3<f32>) -> f32 {
    let air = model.chunk * 32 + vec3<i32>(floor(localPos + 0.5 * localNormal));
    let n   = abs(localNormal);
    var T: vec3<i32>; var B: vec3<i32>;
    if (n.x > 0.5)      { T = vec3<i32>(0, 1, 0); B = vec3<i32>(0, 0, 1); }
    else if (n.y > 0.5) { T = vec3<i32>(1, 0, 0); B = vec3<i32>(0, 0, 1); }
    else                { T = vec3<i32>(1, 0, 0); B = vec3<i32>(0, 1, 0); }

    let tm = occ(air - T); let tp = occ(air + T);
    let bm = occ(air - B); let bp = occ(air + B);
    let ao00 = vAO(tm, bm, occ(air - T - B));
    let ao10 = vAO(tp, bm, occ(air + T - B));
    let ao01 = vAO(tm, bp, occ(air - T + B));
    let ao11 = vAO(tp, bp, occ(air + T + B));

    // Smoothstep the bilinear weights so the reconstructed AO is slope-continuous (C1) across cell boundaries:
    // each cell's patch meets its neighbour with zero slope. Plain fract bilinear is only C0, so the slope jumps
    // where the AO ramp meets the un-occluded plateau — the eye reads that slope discontinuity as a 'bright line'
    // at the far edge of the AO.
    let s = smoothstep(0.0, 1.0, fract(dot(localPos, vec3<f32>(T))));
    let t = smoothstep(0.0, 1.0, fract(dot(localPos, vec3<f32>(B))));
    return mix(mix(ao00, ao10, s), mix(ao01, ao11, s), t);
}

// ── Fast path ────────────────────────────────────────────────────────────────────────────────────────────────
// sampleLit + computeAO above, written for clarity, look the same few cells up over and over: ~32 isSolid and ~16
// displayAt per fragment, each walking grid → chunk table → pool again. shadeFast gives the same result from
// one chunk-table lookup, one 27-bit solid mask of the air cell's 3x3x3 neighbourhood (9 occupancy words when it
// lies inside one chunk) and one decode per in-plane cell (the brick slot is shared while cells stay in the air
// cell's brick). camera.lightParams.y > 0.5 selects the reference path instead, for A/B comparison.

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
        if (n != 0) { e = entryOf(model.grid, c0 + o); }
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
    r.a = vec4<f32>(camera.lightParams.z * (1.0 - camera.lightParams.x * f32(d >> 14u) / 3.0),
                    decodeLevel(d & 15u), decodeLevel((d >> 4u) & 15u), decodeLevel((d >> 8u) & 15u));
    r.sun = f32((d >> 12u) & 3u) / 3.0;
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

struct Shade { sky: f32, rgb: vec3<f32>, sun: f32, ao: f32 };

fn shadeFast(localPos: vec3<f32>, localNormal: vec3<f32>) -> Shade {
    let air = model.chunk * 32 + vec3<i32>(floor(localPos + 0.5 * localNormal));
    let N = vec3<i32>(round(localNormal));
    let n = abs(localNormal);
    var T: vec3<i32>; var B: vec3<i32>;
    if (n.x > 0.5)      { T = vec3<i32>(0, 1, 0); B = vec3<i32>(0, 0, 1); }
    else if (n.y > 0.5) { T = vec3<i32>(1, 0, 0); B = vec3<i32>(0, 0, 1); }
    else                { T = vec3<i32>(1, 0, 0); B = vec3<i32>(0, 1, 0); }

    let ai = entryOf(model.grid, air >> vec3<u32>(5u));
    let m = solidMask(air, ai);
    let hb = air >> vec3<u32>(3u);
    let hs = brickSlot(ai, air);

    // Air-layer solids around the cell (AO, and the first half of onSurface).
    let sTm = maskSolid(m, -T);     let sTp = maskSolid(m, T);
    let sBm = maskSolid(m, -B);     let sBp = maskSolid(m, B);
    let sMM = maskSolid(m, -T - B); let sPM = maskSolid(m, T - B);
    let sMP = maskSolid(m, -T + B); let sPP = maskSolid(m, T + B);

    // onSurface: open, with a solid directly behind along the normal. Diagonals need a side cell (corner rule).
    let oTm = !sTm && maskSolid(m, -T - N);     let oTp = !sTp && maskSolid(m, T - N);
    let oBm = !sBm && maskSolid(m, -B - N);     let oBp = !sBp && maskSolid(m, B - N);
    let oMM = (oTm || oBm) && !sMM && maskSolid(m, -T - B - N);
    let oPM = (oTp || oBm) && !sPM && maskSolid(m, T - B - N);
    let oMP = (oTm || oBp) && !sMP && maskSolid(m, -T + B - N);
    let oPP = (oTp || oBp) && !sPP && maskSolid(m, T + B - N);

    let cC  = weighed(true, air, hb, hs, ai);
    let cTm = weighed(oTm, air - T, hb, hs, ai);     let cTp = weighed(oTp, air + T, hb, hs, ai);
    let cBm = weighed(oBm, air - B, hb, hs, ai);     let cBp = weighed(oBp, air + B, hb, hs, ai);
    let cMM = weighed(oMM, air - T - B, hb, hs, ai); let cPM = weighed(oPM, air + T - B, hb, hs, ai);
    let cMP = weighed(oMP, air - T + B, hb, hs, ai); let cPP = weighed(oPP, air + T + B, hb, hs, ai);

    let c00 = avg4(cC, cTm, cBm, cMM);
    let c10 = avg4(cC, cTp, cBm, cPM);
    let c01 = avg4(cC, cTm, cBp, cMP);
    let c11 = avg4(cC, cTp, cBp, cPP);

    let ft = fract(dot(localPos, vec3<f32>(T)));
    let fb = fract(dot(localPos, vec3<f32>(B)));
    var o: Shade;
    let a = mix(mix(c00.a, c10.a, ft), mix(c01.a, c11.a, ft), fb);
    o.sky = a.x;
    o.rgb = a.yzw;
    o.sun = mix(mix(c00.sun, c10.sun, ft), mix(c01.sun, c11.sun, ft), fb);

    let ao00 = vAO(select(0.0, 1.0, sTm), select(0.0, 1.0, sBm), select(0.0, 1.0, sMM));
    let ao10 = vAO(select(0.0, 1.0, sTp), select(0.0, 1.0, sBm), select(0.0, 1.0, sPM));
    let ao01 = vAO(select(0.0, 1.0, sTm), select(0.0, 1.0, sBp), select(0.0, 1.0, sMP));
    let ao11 = vAO(select(0.0, 1.0, sTp), select(0.0, 1.0, sBp), select(0.0, 1.0, sPP));
    let s = smoothstep(0.0, 1.0, ft);
    let t = smoothstep(0.0, 1.0, fb);
    o.ao = mix(mix(ao00, ao10, s), mix(ao01, ao11, s), t);
    return o;
}

// shadeFast's corner AO alone (the solid mask, no light lookups), for the flat-light debug mode.
fn cornerAoFast(localPos: vec3<f32>, localNormal: vec3<f32>) -> f32 {
    let air = model.chunk * 32 + vec3<i32>(floor(localPos + 0.5 * localNormal));
    let n = abs(localNormal);
    var T: vec3<i32>; var B: vec3<i32>;
    if (n.x > 0.5)      { T = vec3<i32>(0, 1, 0); B = vec3<i32>(0, 0, 1); }
    else if (n.y > 0.5) { T = vec3<i32>(1, 0, 0); B = vec3<i32>(0, 0, 1); }
    else                { T = vec3<i32>(1, 0, 0); B = vec3<i32>(0, 1, 0); }
    let m = solidMask(air, entryOf(model.grid, air >> vec3<u32>(5u)));
    let sTm = select(0.0, 1.0, maskSolid(m, -T));     let sTp = select(0.0, 1.0, maskSolid(m, T));
    let sBm = select(0.0, 1.0, maskSolid(m, -B));     let sBp = select(0.0, 1.0, maskSolid(m, B));
    let ao00 = vAO(sTm, sBm, select(0.0, 1.0, maskSolid(m, -T - B)));
    let ao10 = vAO(sTp, sBm, select(0.0, 1.0, maskSolid(m, T - B)));
    let ao01 = vAO(sTm, sBp, select(0.0, 1.0, maskSolid(m, -T + B)));
    let ao11 = vAO(sTp, sBp, select(0.0, 1.0, maskSolid(m, T + B)));
    let s = smoothstep(0.0, 1.0, fract(dot(localPos, vec3<f32>(T))));
    let t = smoothstep(0.0, 1.0, fract(dot(localPos, vec3<f32>(B))));
    return mix(mix(ao00, ao10, s), mix(ao01, ao11, s), t);
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4<f32> {
    // Sample unconditionally (avoids implicit-derivative issues from branching on a per-fragment value) and
    // select against the vertex color for untextured blocks (uv.z < 0, the no-texture sentinel).
    // Debug (Renderer panel, for measuring what the render pass spends): lightParams2.y bit 1 = no texture sample,
    // bit 2 = no fog or haze; lightParams2.z = lighting mode (see below).
    let dbg = u32(camera.lightParams2.y);
    let layer     = max(i32(round(in.uv.z)), 0);
    var texColor  = vec3<f32>(0.6);
    if ((dbg & 1u) == 0u) { texColor = textureSample(atlasTex, atlasSamp, fract(in.uv.xy), layer).rgb; }
    let baseColor = select(in.color, texColor, in.uv.z >= 0.0);

    // Non-chunk draws (selection highlight, HUD, debug meshes) have no light data: full-bright.
    if (model.grid < 0) { return vec4<f32>(baseColor, 1.0); }

    let worldN = normalize(in.worldNormal);
    let ndotl  = max(dot(worldN, -(camera.sunDir.xyz)), 0.0);
    var s: Lit;
    var ao: f32;
    let mode = i32(camera.lightParams2.z);
    let air = model.chunk * 32 + vec3<i32>(floor(in.localPos + 0.5 * in.localNormal));
    if (mode == 3) {
        // Debug: no voxel lighting at all (open sky, full sun, no AO).
        s.sky = camera.lightParams.z; s.rgb = vec3<f32>(0.0); s.sun = 1.0;
        ao = 1.0;
    } else if (mode == 1 || mode == 2) {
        // Debug: flat light everywhere, with (1) or without (2) corner AO.
        let c = cellAt(air);
        s.sky = c.sky; s.rgb = c.rgb; s.sun = c.sun;
        ao = 1.0;
        if (mode == 1) { ao = cornerAoFast(in.localPos, in.localNormal); }
    } else if (camera.lightParams.y > 0.5) {
        s  = sampleLit(in.localPos, in.localNormal);
        ao = computeAO(in.localPos, in.localNormal);
    } else {
        let f = shadeFast(in.localPos, in.localNormal);
        s.sky = f.sky; s.rgb = f.rgb; s.sun = f.sun;
        ao = f.ao;
    }

    // Direct sun capped at camera.sunDir.w (SunLight.Strength): Lambertian on the surface normal, gated by
    // the smoothed voxel sun visibility.
    let directSun = ndotl * s.sun * camera.sunDir.w;

    // Sky contribution = the brighter of ambient and direct sun.
    let skyTerm = max(s.sky, directSun);

    // Final, per channel = brightest of sky, lamp/bounce light, and the minimum ambient floor.
    let lit = max(max(vec3<f32>(skyTerm), s.rgb), vec3<f32>(MIN_AMBIENT));

    // Ambient occlusion darkens inner corners / block junctions; lerp from AO_MIN so corners aren't pure black.
    let aoFactor = mix(AO_MIN, 1.0, ao);

    if ((dbg & 2u) != 0u) { return vec4<f32>(baseColor * lit * aoFactor, 1.0); }
    return vec4<f32>(applyFog(baseColor * lit * aoFactor, in.worldPos), 1.0);
}

// Debug overdraw view: every terrain fragment that passes the depth test (in the normal nearest-first draw order)
// adds a fifth of full brightness (and a little red), so brightness counts how many times each pixel is shaded: dim
// grey 1 (ideal), brighter each time, white at 5 or more.
@fragment
fn fs_overdraw(in: VSOut) -> @location(0) vec4<f32> {
    return vec4<f32>(0.2, 0.17, 0.17, 1.0);
}

// 3D models (GpuModel, e.g. glTF props and model blocks): group 3 holds the model's own single-layer texture instead
// of the block array, sampled at the normalized uv.xy. A model block (grid >= 0) is lit from its own cell's voxel
// light — sky/AO, lamp light and sun visibility, one flat value for the whole model — combined like fs_main does.
// Any other model has no light data and is lit like an open-air surface: the brighter of the flat ambient and
// Lambertian sun. Both are fogged like the terrain. Drawn with culling off (glTF doubleSided is common and cheap
// here), so back faces flip their normal. Texels under the material's alpha cutoff are cut out.
@fragment
fn fs_model(in: VSOut, @builtin(front_facing) front: bool) -> @location(0) vec4<f32> {
    let tex = textureSample(atlasTex, atlasSamp, in.uv.xy, 0);
    if (tex.a < model.params.x) { discard; }
    var n = normalize(in.worldNormal);
    if (!front) { n = -n; }
    let ndotl = max(dot(n, -camera.sunDir.xyz), 0.0);
    var lit = vec3<f32>(max(camera.lightParams.z, ndotl * camera.sunDir.w));
    if (model.grid >= 0) {
        let c = cellAt(model.chunk * 32 + vec3<i32>(model.params.yzw));
        lit = max(vec3<f32>(max(c.sky, ndotl * c.sun * camera.sunDir.w)), c.rgb);
    }
    lit = max(lit, vec3<f32>(MIN_AMBIENT));
    return vec4<f32>(applyFog(tex.rgb * in.color * lit, in.worldPos), 1.0);
}

// Cloud boxes (CloudLayer): one instance per cloud cell, 36 vertices each (6 faces x 2 triangles, counter-clockwise
// from outside). cell.x: bits 0-7 x and 8-15 z within the tile; cell.y: bottom and top (signed 16-bit, blocks).
// model: cell units to world. Sides against a neighbouring box are drawn too; depth hides them.
@vertex
fn vs_cloud(@builtin(vertex_index) vi: u32, @location(0) cell: vec2<u32>) -> VSOut {
    var origins = array<vec3<f32>, 6>(vec3<f32>(1.0, 0.0, 0.0), vec3<f32>(0.0), vec3<f32>(0.0, 0.0, 1.0),
                                      vec3<f32>(0.0), vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(0.0));
    var e1s = array<vec3<f32>, 6>(vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(0.0, 0.0, 1.0), vec3<f32>(1.0, 0.0, 0.0),
                                  vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(0.0, 0.0, 1.0), vec3<f32>(1.0, 0.0, 0.0));
    var e2s = array<vec3<f32>, 6>(vec3<f32>(0.0, 0.0, 1.0), vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(0.0, 1.0, 0.0),
                                  vec3<f32>(1.0, 0.0, 0.0), vec3<f32>(1.0, 0.0, 0.0), vec3<f32>(0.0, 0.0, 1.0));
    var normals = array<vec3<f32>, 6>(vec3<f32>(1.0, 0.0, 0.0), vec3<f32>(-1.0, 0.0, 0.0), vec3<f32>(0.0, 0.0, 1.0),
                                      vec3<f32>(0.0, 0.0, -1.0), vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(0.0, -1.0, 0.0));
    var corners = array<u32, 6>(0u, 1u, 2u, 0u, 2u, 3u);

    var o: VSOut;
    let face = vi / 6u;
    let k = corners[vi % 6u];
    var p = origins[face];
    if (k == 1u || k == 2u) { p += e1s[face]; }
    if (k == 2u || k == 3u) { p += e2s[face]; }
    let y0 = f32(bitcast<i32>(cell.y << 16u) >> 16u);
    let y1 = f32(bitcast<i32>(cell.y) >> 16u);
    let local = vec3<f32>(f32(cell.x & 0xFFu) + p.x, mix(y0, y1, p.y), f32((cell.x >> 8u) & 0xFFu) + p.z);
    let world = model.model * vec4<f32>(local, 1.0);
    o.pos         = camera.proj * camera.view * world;
    o.worldPos    = world.xyz;
    o.color       = vec3<f32>(0.97, 0.98, 1.0);
    o.worldNormal = normals[face];
    o.localPos    = local;
    o.localNormal = normals[face];
    o.uv          = vec3<f32>(0.0, 0.0, -1.0);
    return o;
}

// Cloud boxes (CloudLayer): flat per-face shading like Minecraft's clouds (bright tops, darker sides and
// undersides), a little extra on the faces the sun hits, then faded into the sky by the cloud layer's own fog
// distances (fog.zw) — clouds aren't limited by the loaded world, so they don't use its fog.
@fragment
fn fs_cloud(in: VSOut) -> @location(0) vec4<f32> {
    let n = in.worldNormal;
    var shade = 0.8;
    if (n.y > 0.5)             { shade = 1.0; }
    else if (n.y < -0.5)       { shade = 0.7; }
    else if (abs(n.x) > 0.5)   { shade = 0.88; }
    shade *= 0.9 + 0.1 * max(dot(n, -camera.sunDir.xyz), 0.0) * camera.sunDir.w;
    let d = in.worldPos - camera.camPos.xyz;
    let f = smoothstep(camera.fog.z, camera.fog.w, length(d));
    return vec4<f32>(mix(applyHaze(in.color * shade, d), skyColor(normalize(d)), f), 1.0);
}
";

    private readonly GpuContext _ctx;
    private readonly WebGPU _api;

    private ShaderModule* _shader;
    private BindGroupLayout* _cameraLayout;
    private BindGroupLayout* _modelLayout;
    private BindGroupLayout* _voxelLayout;
    private BindGroupLayout* _atlasLayout;
    private PipelineLayout* _pipelineLayout;
    private RenderPipeline* _pipeline;
    private RenderPipeline* _wireframePipeline;
    private RenderPipeline* _hudPipeline;
    private RenderPipeline* _skyPipeline;
    private RenderPipeline* _cloudPipeline;
    private RenderPipeline* _modelPipeline;
    private RenderPipeline* _chunkPipeline;          // vs_chunk: chunk meshes (ChunkVertex)
    private RenderPipeline* _chunkWireframePipeline;
    private RenderPipeline* _chunkOverdrawPipeline;  // debug overdraw view

    /// <summary>Debug: draw terrain additively without depth test, so brightness shows how many surfaces cover each
    /// pixel (sky and clouds are skipped).</summary>
    public bool OverdrawMode { get; set; }
    private RenderPipeline* _boundPipeline;          // the pipeline last set in this pass

    private void SetPipeline(RenderPipeline* pipeline)
    {
        _api.RenderPassEncoderSetPipeline(_pass, pipeline);
        _boundPipeline = pipeline;
    }

    // Block texture array (TextureAtlas → GPU). Constructed with a 1x1 white fallback so BeginFrame
    // always has a valid group-3 bind group; LoadTextureAtlas replaces it with the real spritesheet.
    private Texture*     _atlasTexture;
    private TextureView* _atlasTextureView;
    private Sampler*     _atlasSampler;
    private BindGroup*   _atlasBindGroup;

    public TextureAtlas? Atlas { get; private set; }

    public bool WireframeMode { get; set; }

    private readonly GpuBuffer _cameraBuffer;
    private readonly GpuBuffer _hudCameraBuffer; // permanently holds identity view+proj
    private readonly GpuBuffer _modelBuffer;
    private BindGroup* _cameraBindGroup;
    private BindGroup* _hudCameraBindGroup;
    private BindGroup* _modelBindGroup;

    // Group 2: the shared voxel storage. Rebuilt when the store's buffers are replaced (pool growth).
    private GridStore? _gridStore;
    private BindGroup* _voxelBindGroup;
    private int _voxelBindingVersion = -1;

    private CommandEncoder* _encoder;
    private RenderPassEncoder* _pass;
    private int _drawIndex;
    private readonly ModelUniform[] _modelStaging = new ModelUniform[MaxObjects];

    /// <summary>Smoothed CPU time blocked acquiring the swapchain image / presenting it. Under vsync (Fifo) this is
    /// where waiting on the GPU shows up, so a large value here means GPU-bound rather than CPU-bound.</summary>
    public double AcquireMs { get; private set; }
    public double PresentMs { get; private set; }

    /// <summary>Draw calls issued last frame.</summary>
    public int DrawCount { get; private set; }

    public float AspectRatio => _ctx.Size.Y <= 0 ? 1f : (float)_ctx.Size.X / _ctx.Size.Y;

    /// <summary>The GPU context backing this renderer (device/queue/surface format), for callers that
    /// need to build their own pipeline against the same swapchain — e.g. <see cref="Gui.ImGuiController"/>.</summary>
    internal GpuContext Context => _ctx;

    /// <summary>The render pass currently open between <see cref="BeginFrame"/> and <see cref="EndFrame"/>.
    /// Null outside a frame. Lets <see cref="Gui.ImGuiController"/> submit its own draw calls into the same
    /// pass, after the HUD pass and before <see cref="EndFrame"/> closes it.</summary>
    internal RenderPassEncoder* CurrentPass => _pass;

    public Renderer(GpuContext ctx)
    {
        _ctx = ctx;
        _api = ctx.Api;

        _shader = CreateShader(Wgsl);
        CreateLayouts();
        _pipeline          = CreateMeshPipeline(PrimitiveTopology.TriangleList, CullMode.Back);
        _wireframePipeline = CreateMeshPipeline(PrimitiveTopology.LineList,     CullMode.None);
        _hudPipeline       = CreateMeshPipeline(PrimitiveTopology.TriangleList, CullMode.None, depthTest: false);
        _modelPipeline     = CreateMeshPipeline(PrimitiveTopology.TriangleList, CullMode.None, fragmentEntry: "fs_model");
        _cloudPipeline     = CreateCloudPipeline();
        _chunkPipeline          = CreateChunkPipeline(PrimitiveTopology.TriangleList, CullMode.Back);
        _chunkWireframePipeline = CreateChunkPipeline(PrimitiveTopology.LineList,     CullMode.None);
        _chunkOverdrawPipeline  = CreateOverdrawPipeline();
        // The background pass: a full-screen triangle at the far plane that only fills pixels still at the cleared
        // depth (reversed: the far plane and the clear value are both 0), without writing depth.
        _skyPipeline       = CreatePipeline("vs_sky", "fs_sky", null, PrimitiveTopology.TriangleList, CullMode.None,
                                            depthWrite: false, CompareFunction.GreaterEqual);

        _cameraBuffer    = GpuBuffer.CreateUniform(ctx, CameraSize);
        _hudCameraBuffer = GpuBuffer.CreateUniform(ctx, CameraSize);
        _modelBuffer     = GpuBuffer.CreateUniform(ctx, ModelStride * MaxObjects);
        CreateBindGroups();
        CreateFallbackAtlas();

        // Pre-load identity matrices; never overwritten after this.
        Span<CameraUniform> id = stackalloc CameraUniform[1];
        id[0] = new CameraUniform { View = Mat4.Identity, Projection = Mat4.Identity };
        _hudCameraBuffer.Write<CameraUniform>(0, id);
    }

    /// <summary>Sets the voxel storage chunk draws read their light from. Must be called before the first frame.</summary>
    public void AttachGridStore(GridStore store) => _gridStore = store;

    private ShaderModule* CreateShader(string wgsl)
    {
        var code = (byte*)SilkMarshal.StringToPtr(wgsl, NativeStringEncoding.UTF8);
        var wgslDesc = new ShaderModuleWGSLDescriptor
        {
            Chain = new ChainedStruct { SType = SType.ShaderModuleWgslDescriptor },
            Code = code,
        };
        var desc = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgslDesc };
        var module = _api.DeviceCreateShaderModule(_ctx.Device, &desc);
        SilkMarshal.Free((nint)code);
        return module;
    }

    private void CreateLayouts()
    {
        var camEntry = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment, // fragment reads sunDir
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, HasDynamicOffset = false, MinBindingSize = CameraSize },
        };
        var camDesc = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &camEntry };
        _cameraLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &camDesc);

        var modelEntry = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment, // fragment reads chunk/grid
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, HasDynamicOffset = true, MinBindingSize = ModelSize },
        };
        var modelDesc = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &modelEntry };
        _modelLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &modelDesc);

        // Group 2: the shared voxel storage, all read-only storage in the fragment.
        const int VoxelBindings = 5;
        BindGroupLayoutEntry* voxelEntries = stackalloc BindGroupLayoutEntry[VoxelBindings];
        for (int i = 0; i < VoxelBindings; i++)
            voxelEntries[i] = new BindGroupLayoutEntry
            {
                Binding = (uint)i,
                Visibility = ShaderStage.Fragment,
                Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage, HasDynamicOffset = false, MinBindingSize = 0 },
            };
        var voxelDesc = new BindGroupLayoutDescriptor { EntryCount = VoxelBindings, Entries = voxelEntries };
        _voxelLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &voxelDesc);

        // Group 3: block texture array (binding 0) + a filtering sampler (binding 1).
        BindGroupLayoutEntry* atlasEntries = stackalloc BindGroupLayoutEntry[2];
        atlasEntries[0] = new BindGroupLayoutEntry
        {
            Binding    = 0,
            Visibility = ShaderStage.Fragment,
            Texture    = new TextureBindingLayout { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2DArray, Multisampled = false },
        };
        atlasEntries[1] = new BindGroupLayoutEntry
        {
            Binding    = 1,
            Visibility = ShaderStage.Fragment,
            Sampler    = new SamplerBindingLayout { Type = SamplerBindingType.Filtering },
        };
        var atlasDesc = new BindGroupLayoutDescriptor { EntryCount = 2, Entries = atlasEntries };
        _atlasLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &atlasDesc);

        BindGroupLayout** layouts = stackalloc BindGroupLayout*[4];
        layouts[0] = _cameraLayout;
        layouts[1] = _modelLayout;
        layouts[2] = _voxelLayout;
        layouts[3] = _atlasLayout;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 4, BindGroupLayouts = layouts };
        _pipelineLayout = _api.DeviceCreatePipelineLayout(_ctx.Device, &plDesc);
    }

    /// <summary>A pipeline for vs_main's vertex layout (<see cref="Vertex"/>): the world, wireframe, HUD and models.</summary>
    private RenderPipeline* CreateMeshPipeline(PrimitiveTopology topology, CullMode cullMode, bool depthTest = true,
                                               string fragmentEntry = "fs_main")
    {
        VertexAttribute* attrs = stackalloc VertexAttribute[4];
        attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0,  ShaderLocation = 0 };
        attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 12, ShaderLocation = 1 };
        attrs[2] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 24, ShaderLocation = 2 };
        attrs[3] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 36, ShaderLocation = 3 };
        var vbLayout = new VertexBufferLayout { ArrayStride = Vertex.SizeBytes, StepMode = VertexStepMode.Vertex, AttributeCount = 4, Attributes = attrs };
        return CreatePipeline("vs_main", fragmentEntry, &vbLayout, topology, cullMode, depthWrite: depthTest,
                              depthTest ? CompareFunction.Greater : CompareFunction.Always); // reversed depth: nearer is greater
    }

    /// <summary>A pipeline for chunk meshes (vs_chunk, one packed <see cref="ChunkVertex"/> per vertex) and fs_main.</summary>
    private RenderPipeline* CreateChunkPipeline(PrimitiveTopology topology, CullMode cullMode)
    {
        var attr     = new VertexAttribute { Format = VertexFormat.Uint32x2, Offset = 0, ShaderLocation = 0 };
        var vbLayout = new VertexBufferLayout { ArrayStride = ChunkVertex.SizeBytes, StepMode = VertexStepMode.Vertex, AttributeCount = 1, Attributes = &attr };
        return CreatePipeline("vs_chunk", "fs_main", &vbLayout, topology, cullMode, depthWrite: true, CompareFunction.Greater);
    }

    /// <summary>Debug overdraw view (fs_overdraw): terrain drawn additively, depth-tested in the normal draw order, so
    /// it counts the fragments that really get shaded (one per pixel is ideal).</summary>
    private RenderPipeline* CreateOverdrawPipeline()
    {
        var attr     = new VertexAttribute { Format = VertexFormat.Uint32x2, Offset = 0, ShaderLocation = 0 };
        var vbLayout = new VertexBufferLayout { ArrayStride = ChunkVertex.SizeBytes, StepMode = VertexStepMode.Vertex, AttributeCount = 1, Attributes = &attr };
        var add = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.One };
        var blend = new BlendState { Color = add, Alpha = add };
        return CreatePipeline("vs_chunk", "fs_overdraw", &vbLayout, PrimitiveTopology.TriangleList, CullMode.Back,
                              depthWrite: true, CompareFunction.Greater, &blend);
    }

    /// <summary>The cloud boxes (vs_cloud/fs_cloud): no vertex buffer, one <see cref="CloudLayer.CloudCell"/> instance
    /// per box, back faces culled, depth-tested and depth-writing like the world.</summary>
    private RenderPipeline* CreateCloudPipeline()
    {
        var attr     = new VertexAttribute { Format = VertexFormat.Uint32x2, Offset = 0, ShaderLocation = 0 };
        var vbLayout = new VertexBufferLayout
        {
            ArrayStride = (ulong)sizeof(CloudLayer.CloudCell), StepMode = VertexStepMode.Instance, AttributeCount = 1, Attributes = &attr,
        };
        return CreatePipeline("vs_cloud", "fs_cloud", &vbLayout, PrimitiveTopology.TriangleList, CullMode.Back,
                              depthWrite: true, CompareFunction.Greater);
    }

    /// <summary>A pipeline over the shared shader and layouts. <paramref name="vertexBuffer"/> is the layout of its one
    /// vertex buffer, or null for none (the vertex shader makes its own geometry).</summary>
    private RenderPipeline* CreatePipeline(string vertexEntry, string fragmentEntry, VertexBufferLayout* vertexBuffer,
                                           PrimitiveTopology topology, CullMode cullMode, bool depthWrite,
                                           CompareFunction depthCompare, BlendState* blend = null)
    {
        var vsEntry = (byte*)SilkMarshal.StringToPtr(vertexEntry, NativeStringEncoding.UTF8);
        var fsEntry = (byte*)SilkMarshal.StringToPtr(fragmentEntry, NativeStringEncoding.UTF8);

        var vertexState   = new VertexState { Module = _shader, EntryPoint = vsEntry, BufferCount = vertexBuffer == null ? 0u : 1u, Buffers = vertexBuffer };
        var colorTarget   = new ColorTargetState { Format = _ctx.SurfaceFormat, Blend = blend, WriteMask = ColorWriteMask.All };
        var fragmentState = new FragmentState { Module = _shader, EntryPoint = fsEntry, TargetCount = 1, Targets = &colorTarget };

        var keep  = StencilOperation.Keep;
        var depth = new DepthStencilState
        {
            Format            = _ctx.DepthFormat,
            DepthWriteEnabled = depthWrite,
            DepthCompare      = depthCompare,
            StencilFront = new StencilFaceState { Compare = CompareFunction.Always, FailOp = keep, DepthFailOp = keep, PassOp = keep },
            StencilBack  = new StencilFaceState { Compare = CompareFunction.Always, FailOp = keep, DepthFailOp = keep, PassOp = keep },
        };

        var desc = new RenderPipelineDescriptor
        {
            Layout    = _pipelineLayout,
            Vertex    = vertexState,
            Primitive = new PrimitiveState
            {
                Topology         = topology,
                StripIndexFormat = IndexFormat.Undefined,
                FrontFace        = FrontFace.Ccw,
                CullMode         = cullMode,
            },
            DepthStencil = &depth,
            Multisample  = new MultisampleState { Count = 1, Mask = ~0u, AlphaToCoverageEnabled = false },
            Fragment     = &fragmentState,
        };
        var pipeline = _api.DeviceCreateRenderPipeline(_ctx.Device, &desc);

        SilkMarshal.Free((nint)vsEntry);
        SilkMarshal.Free((nint)fsEntry);
        return pipeline;
    }

    private void CreateBindGroups()
    {
        var camEntry = new BindGroupEntry { Binding = 0, Buffer = _cameraBuffer.Handle, Offset = 0, Size = CameraSize };
        var camDesc = new BindGroupDescriptor { Layout = _cameraLayout, EntryCount = 1, Entries = &camEntry };
        _cameraBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &camDesc);

        var hudCamEntry = new BindGroupEntry { Binding = 0, Buffer = _hudCameraBuffer.Handle, Offset = 0, Size = CameraSize };
        var hudCamDesc  = new BindGroupDescriptor { Layout = _cameraLayout, EntryCount = 1, Entries = &hudCamEntry };
        _hudCameraBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &hudCamDesc);

        var modelEntry = new BindGroupEntry { Binding = 0, Buffer = _modelBuffer.Handle, Offset = 0, Size = ModelSize };
        var modelDesc = new BindGroupDescriptor { Layout = _modelLayout, EntryCount = 1, Entries = &modelEntry };
        _modelBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &modelDesc);
    }

    /// <summary>(Re)creates the group-2 bind group over the store's buffers when they were replaced.</summary>
    private void EnsureVoxelBindGroup()
    {
        var s = _gridStore ?? throw new InvalidOperationException("Renderer.AttachGridStore was not called.");
        if (_voxelBindGroup != null && _voxelBindingVersion == s.BindingVersion) return;
        if (_voxelBindGroup != null) _api.BindGroupRelease(_voxelBindGroup);

        BindGroupEntry* entries = stackalloc BindGroupEntry[5];
        entries[0] = new BindGroupEntry { Binding = 0, Buffer = s.OccPool.Handle,    Offset = 0, Size = s.OccPool.SizeBytes };
        entries[1] = new BindGroupEntry { Binding = 1, Buffer = s.ChunkTable.Handle, Offset = 0, Size = s.ChunkTable.SizeBytes };
        entries[2] = new BindGroupEntry { Binding = 2, Buffer = s.BrickTable.Handle, Offset = 0, Size = s.BrickTable.SizeBytes };
        entries[3] = new BindGroupEntry { Binding = 3, Buffer = s.LightPool.Handle,  Offset = 0, Size = s.LightPool.SizeBytes };
        entries[4] = new BindGroupEntry { Binding = 4, Buffer = s.Grids.Handle,      Offset = 0, Size = s.Grids.SizeBytes };
        var desc = new BindGroupDescriptor { Layout = _voxelLayout, EntryCount = 5, Entries = entries };
        _voxelBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &desc);
        _voxelBindingVersion = s.BindingVersion;
    }

    /// <summary>1x1 white single-layer array texture, bound as group 3 until <see cref="LoadTextureAtlas"/>
    /// replaces it — keeps every draw's texture sample well-defined (white, so untextured blocks are
    /// unaffected since they select the vertex color anyway) before the real spritesheet is loaded.</summary>
    private void CreateFallbackAtlas()
    {
        var white = new byte[] { 255, 255, 255, 255 };
        BuildAtlasTexture(1, 1, new[] { white });
    }

    /// <summary>Loads <paramref name="pngPath"/>/<paramref name="xmlPath"/> as the block texture array
    /// (see <see cref="TextureAtlas"/>), replacing the fallback (or a previously loaded atlas). Must be
    /// called before any chunk referencing a real block texture is meshed, since <see cref="GreedyMesher"/>
    /// resolves texture names to layer indices via <see cref="Atlas"/> at mesh time.</summary>
    public void LoadTextureAtlas(string pngPath, string xmlPath)
    {
        Atlas = new TextureAtlas(pngPath, xmlPath);
        var layers = new byte[Atlas.LayerCount][];
        for (int i = 0; i < layers.Length; i++) layers[i] = Atlas.GetLayerPixels(i);
        BuildAtlasTexture(Atlas.TileSize, Atlas.TileSize, layers);
    }

    private void BuildAtlasTexture(int tileWidth, int tileHeight, byte[][] layers)
    {
        if (_atlasBindGroup   != null) _api.BindGroupRelease(_atlasBindGroup);
        if (_atlasSampler     != null) _api.SamplerRelease(_atlasSampler);
        if (_atlasTextureView != null) _api.TextureViewRelease(_atlasTextureView);
        if (_atlasTexture     != null) _api.TextureRelease(_atlasTexture);

        var samplerDesc = new SamplerDescriptor
        {
            AddressModeU  = AddressMode.Repeat,
            AddressModeV  = AddressMode.Repeat,
            AddressModeW  = AddressMode.Repeat,
            MagFilter     = FilterMode.Nearest,
            MinFilter     = FilterMode.Nearest,
            MipmapFilter  = MipmapFilterMode.Nearest,
            LodMinClamp   = 0,
            LodMaxClamp   = 1,
            Compare       = CompareFunction.Undefined,
            MaxAnisotropy = 1,
        };
        CreateTextureArray(tileWidth, tileHeight, layers, samplerDesc,
                           out _atlasTexture, out _atlasTextureView, out _atlasSampler, out _atlasBindGroup);
    }

    /// <summary>Uploads <paramref name="layers"/> (RGBA8, <paramref name="width"/>×<paramref name="height"/> each) as a
    /// <c>texture_2d_array</c> and wraps it with a sampler in a bind group on the group-3 layout.</summary>
    private void CreateTextureArray(int width, int height, byte[][] layers, in SamplerDescriptor samplerDesc,
                                    out Texture* texture, out TextureView* view, out Sampler* sampler, out BindGroup* bindGroup)
    {
        var texDesc = new TextureDescriptor
        {
            Usage         = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            Dimension     = TextureDimension.Dimension2D,
            Size          = new Extent3D((uint)width, (uint)height, (uint)layers.Length),
            Format        = TextureFormat.Rgba8Unorm,
            MipLevelCount = 1,
            SampleCount   = 1,
        };
        texture = _api.DeviceCreateTexture(_ctx.Device, &texDesc);

        for (uint layer = 0; layer < layers.Length; layer++)
        {
            fixed (byte* data = layers[layer])
            {
                var dest = new ImageCopyTexture { Texture = texture, MipLevel = 0, Origin = new Origin3D(0, 0, layer), Aspect = TextureAspect.All };
                var dataLayout = new TextureDataLayout { Offset = 0, BytesPerRow = (uint)(width * 4), RowsPerImage = (uint)height };
                var writeSize = new Extent3D((uint)width, (uint)height, 1);
                _api.QueueWriteTexture(_ctx.Queue, &dest, data, (nuint)layers[layer].Length, &dataLayout, &writeSize);
            }
        }

        var viewDesc = new TextureViewDescriptor
        {
            Format          = TextureFormat.Rgba8Unorm,
            Dimension       = TextureViewDimension.Dimension2DArray,
            BaseMipLevel    = 0,
            MipLevelCount   = 1,
            BaseArrayLayer  = 0,
            ArrayLayerCount = (uint)layers.Length,
            Aspect          = TextureAspect.All,
        };
        view = _api.TextureCreateView(texture, &viewDesc);

        fixed (SamplerDescriptor* sd = &samplerDesc)
            sampler = _api.DeviceCreateSampler(_ctx.Device, sd);

        BindGroupEntry* entries = stackalloc BindGroupEntry[2];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = view };
        entries[1] = new BindGroupEntry { Binding = 1, Sampler = sampler };
        var desc = new BindGroupDescriptor { Layout = _atlasLayout, EntryCount = 2, Entries = entries };
        bindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &desc);
    }

    /// <summary>Uploads a loaded model (see <see cref="Gltf.GltfLoader"/>): one mesh and texture per material part.
    /// Untextured materials get a 1×1 white texture, so their base colour (carried in the vertex colour) shows as is.</summary>
    public GpuModel UploadModel(Gltf.ModelData data)
    {
        var parts = new List<GpuModelPart>(data.Parts.Count);
        foreach (var part in data.Parts)
        {
            var tex = part.Material.Texture;
            var samplerDesc = new SamplerDescriptor
            {
                AddressModeU  = tex is { ClampU: true } ? AddressMode.ClampToEdge : AddressMode.Repeat,
                AddressModeV  = tex is { ClampV: true } ? AddressMode.ClampToEdge : AddressMode.Repeat,
                AddressModeW  = AddressMode.ClampToEdge,
                MagFilter     = tex is null or { Nearest: true } ? FilterMode.Nearest : FilterMode.Linear,
                MinFilter     = tex is null or { Nearest: true } ? FilterMode.Nearest : FilterMode.Linear,
                MipmapFilter  = MipmapFilterMode.Nearest,
                LodMinClamp   = 0,
                LodMaxClamp   = 1,
                Compare       = CompareFunction.Undefined,
                MaxAnisotropy = 1,
            };
            int w = tex?.Width ?? 1, h = tex?.Height ?? 1;
            var pixels = tex?.Rgba ?? new byte[] { 255, 255, 255, 255 };
            CreateTextureArray(w, h, new[] { pixels }, samplerDesc,
                               out var texture, out var view, out var sampler, out var bindGroup);

            var mesh = UploadMesh(part.Vertices, part.Indices);
            parts.Add(new GpuModelPart(part.Node, mesh, new ModelTexture(_api, texture, view, sampler, bindGroup), part.Material.AlphaCutoff));
        }
        return new GpuModel(data.Nodes, parts, data.BoundsMin, data.BoundsMax);
    }

    public GpuMesh UploadMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
    {
        var vb  = GpuBuffer.CreateVertex(_ctx, vertices);
        var ib  = GpuBuffer.CreateIndex(_ctx, indices);
        var wfi = BuildWireframeIndices(indices);
        var wb  = GpuBuffer.CreateIndex(_ctx, wfi);
        return new GpuMesh(vb, ib, wb, (uint)indices.Length, (uint)wfi.Length);
    }

    /// <summary>Uploads a mesh packed into one block (see <see cref="GpuMesh(GpuBuffer, ulong, uint, uint)"/>): one
    /// buffer and one write, where separate buffers cost three of each.</summary>
    public GpuMesh UploadPackedMesh(ReadOnlySpan<byte> packed, ulong vertexBytes, uint indexCount, uint wireframeIndexCount,
                                    IndexFormat indexFormat)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var buf = GpuBuffer.Create(_ctx, (ulong)packed.Length, BufferUsage.Vertex | BufferUsage.Index | BufferUsage.CopyDst);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        buf.Write(0, packed);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastCreateMs = (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastWriteMs  = (t2 - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return new GpuMesh(buf, vertexBytes, indexCount, wireframeIndexCount, indexFormat);
    }

    /// <summary>How long the last <see cref="UploadPackedMesh"/> spent creating its buffer and writing it (ms), for
    /// the meshing panel.</summary>
    public double LastCreateMs { get; private set; }
    public double LastWriteMs { get; private set; }

    /// <summary>Upload with an explicit wireframe index buffer (e.g. 12 cube edges instead of diagonal-filled faces).</summary>
    public GpuMesh UploadMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<uint> wireframeIndices)
    {
        var vb = GpuBuffer.CreateVertex(_ctx, vertices);
        var ib = GpuBuffer.CreateIndex(_ctx, indices);
        var wb = GpuBuffer.CreateIndex(_ctx, wireframeIndices);
        return new GpuMesh(vb, ib, wb, (uint)indices.Length, (uint)wireframeIndices.Length);
    }

    /// <summary>
    /// Draws <paramref name="mesh"/> using the wireframe pipeline and wireframe index buffer, regardless
    /// of the current <see cref="WireframeMode"/>, then restores the previous pipeline.
    /// Intended for overlay elements (selection highlight, debug gizmos). Drawn full-bright (no grid), so the
    /// highlight always reads as legible instead of picking up the lighting of the block it outlines.
    /// </summary>
    public void DrawMeshWireframe(GpuMesh mesh, in Mat4 model)
    {
        if (_drawIndex >= MaxObjects) return;

        uint dynOffset = StageModel(ModelUniform.Default(model));
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset);
        _api.RenderPassEncoderSetVertexBuffer(_pass, 0, mesh.VertexBuffer.Handle, mesh.VertexOffset, mesh.VertexBytes);
        SetPipeline(_wireframePipeline);
        _api.RenderPassEncoderSetIndexBuffer(_pass, mesh.WireframeBuffer.Handle, mesh.IndexFormat, mesh.WireframeOffset, mesh.WireframeBytes);
        _api.RenderPassEncoderDrawIndexed(_pass, mesh.WireframeIndexCount, 1, 0, 0, 0);
        SetPipeline(WireframeMode ? _wireframePipeline : _pipeline);
        _drawIndex++;
    }

    /// <summary>
    /// Draws every part of <paramref name="gpuModel"/> placed by <paramref name="model"/> with the model shader
    /// (fs_model: its own texture, lighting, fog), or as a full-bright wireframe while <see cref="WireframeMode"/>
    /// is on. Depth-tested like the world, so call it before <see cref="DrawSky"/>. A model block passes its grid
    /// (<see cref="GridHandle.Index"/>), chunk and chunk-local cell to be lit from that cell's voxel light; the
    /// default grid -1 lights it with just sun and ambient. <paramref name="pose"/> gives each model node's
    /// model-space matrix (see <see cref="GpuModel.ComputePose"/>); empty draws the rest pose.
    /// </summary>
    public void DrawModel(GpuModel gpuModel, in Mat4 model, int grid = -1, ChunkPosition chunk = default,
                          Vector3D<int> voxel = default, ReadOnlySpan<Mat4> pose = default)
    {
        // Each part is drawn at its node's model-space matrix from the pose (the rest pose unless one is given).
        if (pose.IsEmpty) pose = gpuModel.RestPose;
        SetPipeline(WireframeMode ? _wireframePipeline : _modelPipeline);
        foreach (var part in gpuModel.Parts)
        {
            if (_drawIndex >= MaxObjects) break;
            var u = new ModelUniform
            {
                Model = part.Node < 0 ? model : Mat4.Multiply(model, pose[part.Node]), Grid = grid, ChunkX = chunk.X, ChunkY = chunk.Y, ChunkZ = chunk.Z,
                AlphaCutoff = part.AlphaCutoff, VoxelX = voxel.X, VoxelY = voxel.Y, VoxelZ = voxel.Z,
            };
            uint dynOffset = StageModel(u);
            _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset);
            _api.RenderPassEncoderSetBindGroup(_pass, 3, part.Texture.BindGroup, 0, null);
            _api.RenderPassEncoderSetVertexBuffer(_pass, 0, part.Mesh.VertexBuffer.Handle, part.Mesh.VertexOffset, part.Mesh.VertexBytes);

            var idxBuf   = WireframeMode ? part.Mesh.WireframeBuffer : part.Mesh.IndexBuffer;
            var idxCount = WireframeMode ? part.Mesh.WireframeIndexCount : part.Mesh.IndexCount;
            var idxOff   = WireframeMode ? part.Mesh.WireframeOffset : part.Mesh.IndexOffset;
            var idxBytes = WireframeMode ? part.Mesh.WireframeBytes : part.Mesh.IndexBytes;
            _api.RenderPassEncoderSetIndexBuffer(_pass, idxBuf.Handle, part.Mesh.IndexFormat, idxOff, idxBytes);
            _api.RenderPassEncoderDrawIndexed(_pass, idxCount, 1, 0, 0, 0);
            _drawIndex++;
        }
        _api.RenderPassEncoderSetBindGroup(_pass, 3, _atlasBindGroup, 0, null);
        SetPipeline(WireframeMode ? _wireframePipeline : _pipeline);
    }

    /// <summary>Uploads per-instance data (e.g. <see cref="CloudLayer.CloudCell"/>s) to a new vertex buffer.</summary>
    public GpuBuffer UploadInstances<T>(ReadOnlySpan<T> data) where T : unmanaged => GpuBuffer.CreateVertex(_ctx, data);

    /// <summary>
    /// Draws <see cref="CloudLayer"/> tiles with the cloud shader, one instanced draw per tile. Depth-tested and
    /// depth-writing like the world, so call it before <see cref="DrawSky"/>.
    /// </summary>
    public void DrawClouds(IReadOnlyList<CloudTileDraw> tiles)
    {
        if (OverdrawMode) return;
        if (tiles.Count == 0) return;
        SetPipeline(_cloudPipeline);
        foreach (var tile in tiles)
        {
            if (_drawIndex >= MaxObjects) break;
            uint dynOffset = StageModel(ModelUniform.Default(tile.Model));
            _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset);
            _api.RenderPassEncoderSetVertexBuffer(_pass, 0, tile.Instances.Handle, 0, tile.Instances.SizeBytes);
            _api.RenderPassEncoderDraw(_pass, 36, tile.Count, 0, 0);
            _drawIndex++;
        }
        SetPipeline(WireframeMode ? _wireframePipeline : _pipeline);
    }

    /// <summary>
    /// Fills every pixel the world didn't cover with the sky gradient and sun (see the shader's fs_sky). Call after
    /// the world draws, so the depth test skips covered pixels, and before overlays and the HUD.
    /// </summary>
    public void DrawSky()
    {
        if (OverdrawMode) return;
        if (_drawIndex >= MaxObjects) return;

        // The sky shader only reads the camera, but the shared pipeline layout needs group 1 bound for any draw
        // (it isn't yet if no chunk was drawn this frame).
        uint dynOffset = StageModel(ModelUniform.Default(Mat4.Identity));
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset);
        SetPipeline(_skyPipeline);
        _api.RenderPassEncoderDraw(_pass, 3, 1, 0, 0);
        SetPipeline(WireframeMode ? _wireframePipeline : _pipeline);
        _drawIndex++;
    }

    /// <summary>
    /// Switches to the HUD pipeline (depth always passes, no depth writes) and binds the identity camera.
    /// Uses a dedicated buffer that never changes, so the world camera uniform is not touched. HUD draws have no
    /// grid, so they render full-bright.
    /// Call this after all world-space draws; follow with <see cref="DrawHudMesh"/> calls.
    /// </summary>
    public void BeginHudPass()
    {
        SetPipeline(_hudPipeline);
        _api.RenderPassEncoderSetBindGroup(_pass, 0, _hudCameraBindGroup, 0, null);
    }

    /// <summary>
    /// Draws a mesh using the HUD pipeline and its solid (triangle) indices. Call after
    /// <see cref="BeginHudPass"/>. Solid triangles rather than a GPU line list so HUD shapes (e.g. the
    /// crosshair) render at an actual on-screen thickness — WebGPU line width is fixed at 1px.
    /// </summary>
    public void DrawHudMesh(GpuMesh mesh, in Mat4 model)
    {
        if (_drawIndex >= MaxObjects) return;

        uint dynOffset = StageModel(ModelUniform.Default(model));
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset);
        _api.RenderPassEncoderSetVertexBuffer(_pass, 0, mesh.VertexBuffer.Handle, mesh.VertexOffset, mesh.VertexBytes);
        _api.RenderPassEncoderSetIndexBuffer(_pass, mesh.IndexBuffer.Handle, mesh.IndexFormat, mesh.IndexOffset, mesh.IndexBytes);
        _api.RenderPassEncoderDrawIndexed(_pass, mesh.IndexCount, 1, 0, 0, 0);
        _drawIndex++;
    }

    // Each triangle (i0,i1,i2) → three line segments → 6 indices.
    public static uint[] BuildWireframeIndices(ReadOnlySpan<uint> tris)
    {
        var lines = new uint[tris.Length * 2];
        int li = 0;
        for (int i = 0; i < tris.Length; i += 3)
        {
            uint i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
            lines[li++] = i0; lines[li++] = i1;
            lines[li++] = i1; lines[li++] = i2;
            lines[li++] = i2; lines[li++] = i0;
        }
        return lines;
    }

    public bool BeginFrame()
    {
        long t0 = Stopwatch.GetTimestamp();
        bool acquired = _ctx.AcquireCurrentView();
        AcquireMs = Ema(AcquireMs, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        if (!acquired)
        {
            _ctx.Configure(_ctx.Size);
            return false;
        }
        _drawIndex = 0;
        EnsureVoxelBindGroup();

        var encDesc = new CommandEncoderDescriptor();
        _encoder = _api.DeviceCreateCommandEncoder(_ctx.Device, &encDesc);

        var colorAtt = new RenderPassColorAttachment
        {
            View = _ctx.CurrentView,
            DepthSlice = uint.MaxValue, // WGPU_DEPTH_SLICE_UNDEFINED
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            // Sky blue; DrawSky paints over whatever the world leaves uncovered.
            ClearValue = OverdrawMode ? new Color { R = 0, G = 0, B = 0, A = 1 } : new Color { R = 0.10, G = 0.3078, B = 0.4804, A = 1.0 },
        };
        var depthAtt = new RenderPassDepthStencilAttachment
        {
            View = _ctx.DepthView,
            DepthLoadOp = LoadOp.Clear,
            DepthStoreOp = StoreOp.Store,
            DepthClearValue = 0.0f, // reversed depth: 0 is the far plane
        };
        var passDesc = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAtt,
            DepthStencilAttachment = &depthAtt,
        };
        if (_ctx.Timer.TimeRender("Render pass (world, sky, HUD, ImGui)", out var tsw)) passDesc.TimestampWrites = &tsw;
        _pass = _api.CommandEncoderBeginRenderPass(_encoder, &passDesc);
        _boundPipeline = null;
        SetPipeline(WireframeMode ? _wireframePipeline : _pipeline);
        _api.RenderPassEncoderSetBindGroup(_pass, 0, _cameraBindGroup, 0, null);
        // Groups 2 (voxel storage) and 3 (block texture array) are the same for every draw and persist across
        // pipeline switches (all pipelines share the layout), so bind them once here.
        _api.RenderPassEncoderSetBindGroup(_pass, 2, _voxelBindGroup, 0, null);
        _api.RenderPassEncoderSetBindGroup(_pass, 3, _atlasBindGroup, 0, null);
        return true;
    }

    public void SetCameraUniform(in CameraUniform camera)
    {
        Span<CameraUniform> s = stackalloc CameraUniform[1];
        s[0] = camera;
        _cameraBuffer.Write<CameraUniform>(0, s);
    }

    /// <summary>
    /// Draws a chunk mesh lit from grid <paramref name="grid"/> (its <see cref="GridHandle.Index"/>; -1 draws it
    /// full-bright). <paramref name="chunk"/> is the chunk's coordinate in that grid.
    /// </summary>
    public void DrawChunkMesh(GpuMesh mesh, in Mat4 model, int grid, ChunkPosition chunk)
    {
        if (_drawIndex >= MaxObjects) return;

        // Chunk meshes are packed (ChunkVertex): their own pipeline, set once for a run of chunk draws.
        // A mesh built while wireframe mode was off has no wireframe (see ChunkMeshSystem): solid until it's remeshed.
        bool wire = WireframeMode && mesh.WireframeIndexCount > 0;
        var pipeline = OverdrawMode ? _chunkOverdrawPipeline : wire ? _chunkWireframePipeline : _chunkPipeline;
        if (_boundPipeline != pipeline) SetPipeline(pipeline);

        uint dynOffset = StageModel(new ModelUniform { Model = model, ChunkX = chunk.X, ChunkY = chunk.Y, ChunkZ = chunk.Z, Grid = grid });
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset);
        _api.RenderPassEncoderSetVertexBuffer(_pass, 0, mesh.VertexBuffer.Handle, mesh.VertexOffset, mesh.VertexBytes);

        var idxBuf   = wire ? mesh.WireframeBuffer : mesh.IndexBuffer;
        var idxCount = wire ? mesh.WireframeIndexCount : mesh.IndexCount;
        var idxOff   = wire ? mesh.WireframeOffset : mesh.IndexOffset;
        var idxBytes = wire ? mesh.WireframeBytes : mesh.IndexBytes;
        _api.RenderPassEncoderSetIndexBuffer(_pass, idxBuf.Handle, mesh.IndexFormat, idxOff, idxBytes);
        _api.RenderPassEncoderDrawIndexed(_pass, idxCount, 1, 0, 0, 0);
        _drawIndex++;
    }

    /// <summary>Stages this draw's model uniform for <see cref="EndFrame"/>'s single upload; returns its dynamic offset.</summary>
    private uint StageModel(in ModelUniform u)
    {
        _modelStaging[_drawIndex] = u;
        return (uint)((ulong)_drawIndex * ModelStride);
    }

    public void EndFrame()
    {
        _api.RenderPassEncoderEnd(_pass);
        _api.RenderPassEncoderRelease(_pass);
        _pass = null;

        // One upload for every draw's model uniform (queue writes land before the submit below), instead of a
        // QueueWriteBuffer per draw.
        if (_drawIndex > 0) _modelBuffer.Write<ModelUniform>(0, _modelStaging.AsSpan(0, _drawIndex));

        _ctx.Timer.Resolve(_encoder);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = _api.CommandEncoderFinish(_encoder, &cmdDesc);
        _api.QueueSubmit(_ctx.Queue, 1, &cmd);
        _api.CommandBufferRelease(cmd);
        _api.CommandEncoderRelease(_encoder);
        _encoder = null;
        _ctx.Timer.AfterSubmit();

        long t0 = Stopwatch.GetTimestamp();
        _ctx.Present();
        PresentMs = Ema(PresentMs, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        DrawCount = _drawIndex;
    }

    private static double Ema(double prev, double sample) => prev + 0.05 * (sample - prev);

    // Padded to ModelStride so the staging array's layout is the uniform buffer's, dynamic-offset slots included.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = (int)ModelStride)]
    private struct ModelUniform
    {
        public Mat4 Model;
        public int ChunkX, ChunkY, ChunkZ, Grid;
        public float AlphaCutoff;             // params.x (fs_model)
        public float VoxelX, VoxelY, VoxelZ;  // params.yzw (fs_model, model blocks)
        // 64 + 16 + 16 = 96 == ModelSize bytes the shader reads, padded to ModelStride

        /// <summary>Non-chunk draws: no grid, drawn full-bright.</summary>
        public static ModelUniform Default(in Mat4 m) => new() { Model = m, Grid = -1 };
    }

    public void OnResize(Vector2D<int> size) => _ctx.Configure(size);

    public void Dispose()
    {
        _cameraBuffer.Dispose();
        _hudCameraBuffer.Dispose();
        _modelBuffer.Dispose();
        if (_voxelBindGroup   != null) _api.BindGroupRelease(_voxelBindGroup);
        if (_atlasBindGroup   != null) _api.BindGroupRelease(_atlasBindGroup);
        if (_atlasSampler     != null) _api.SamplerRelease(_atlasSampler);
        if (_atlasTextureView != null) _api.TextureViewRelease(_atlasTextureView);
        if (_atlasTexture     != null) _api.TextureRelease(_atlasTexture);
        if (_modelPipeline      != null) _api.RenderPipelineRelease(_modelPipeline);
        if (_cloudPipeline      != null) _api.RenderPipelineRelease(_cloudPipeline);
        if (_skyPipeline        != null) _api.RenderPipelineRelease(_skyPipeline);
        if (_hudPipeline        != null) _api.RenderPipelineRelease(_hudPipeline);
        if (_wireframePipeline  != null) _api.RenderPipelineRelease(_wireframePipeline);
        if (_pipeline           != null) _api.RenderPipelineRelease(_pipeline);
        if (_chunkPipeline      != null) _api.RenderPipelineRelease(_chunkPipeline);
        if (_chunkWireframePipeline != null) _api.RenderPipelineRelease(_chunkWireframePipeline);
        if (_chunkOverdrawPipeline != null) _api.RenderPipelineRelease(_chunkOverdrawPipeline);
    }
}
