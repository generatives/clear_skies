using ClearSkies.Engine.ECS;
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
    private const ulong BlockTableBytes = ChunkQuad.BlockTableSize * 16; // blockTable: one vec4<f32> per block
    private const ulong CameraSize  = 304;   // two mat4x4<f32> (view, proj) + eleven vec4<f32> (sun, light params, camera position, fog, zenith, horizon, haze, sea, light params 2, water, water 2)
    private const ulong ModelSize   = 96;    // mat4x4<f32> + vec3<i32> chunk + i32 grid + vec4<f32> params

    private static readonly string Wgsl = @"
const MIN_AMBIENT: f32 = 0.00;        // floor so no geometry is ever fully black
const AO_MIN: f32 = 0.15;             // darkest ambient-occluded corner (1 = no AO). 0.15 provides exaggeration for evaluation;
                                      // 0.45 is the subtler default.
const NO_LIGHTMAP: u32 = 0xFFFFFFFFu; // a quad without a light map (see VSOut.lm)
const OPEN_AIR_GRID: i32 = -2;         // a chunk lit as open air, with no light data (Renderer.OpenAirGrid)

// sunDir.w: sun strength (SunLight.Strength). lightParams.x: unused (ray AO is in the voxel light), .y: the climate
// tint volume's origin x (lightParams2.w its z; see climateTinted), .z: ambient
// (0-1), .w: 1 on an sRGB surface (see lin). camPos.xyz: camera world position. fog.xy: the world's fog start/end
// (by distance), fog.zw: the cloud layer's (see CloudLayer), in blocks from the camera. zenith/horizon.rgb: the sky
// gradient (see SkySettings). horizon.w: the distance haze's strength (0-1), haze.rgb its colour, haze.w its distance.
// sea: the cloud sea's altitude, coverage (0 = off), cell size and thickness, in blocks (see cloudSea).
// lightParams2.x: exposure, a multiplier on lit surfaces before fog. lightParams2.y/.z: render pass debug toggles
// (see shadeBlock). water.rgb: the deep water and underwater fog colour, .w its clarity; water2.x: the thinnest
// water's opacity, .y its tint, .z how much its thickness varies through it, .w the size of those variations in blocks
// (see WaterSettings and fs_chunk_transparent).
struct Camera {
    view: mat4x4<f32>, proj: mat4x4<f32>, sunDir: vec4<f32>, lightParams: vec4<f32>,
    camPos: vec4<f32>, fog: vec4<f32>, zenith: vec4<f32>, horizon: vec4<f32>, haze: vec4<f32>, sea: vec4<f32>,
    lightParams2: vec4<f32>, water: vec4<f32>, water2: vec4<f32>,
};
@group(0) @binding(0) var<uniform> camera: Camera;
// Per block type (indexed by BlockId; see ChunkQuad.BuildBlockTable): its colour (sRGB, shown on untextured blocks) and
// opacity (1 unless translucent). Chunk quads store only their block.
@group(0) @binding(1) var<uniform> blockTable: array<vec4<f32>, 256>;
// The climate tint around the camera (ClimateTintSystem): x, z, then y across the texture's width, height and depth, rgb its
// colour (sRGB) and a its strength, sampled with linear filtering so it blends smoothly between texels. The near volume
// is the first half of the depth, the far one (coarser cells, around the same middle) the second.
@group(0) @binding(2) var tintTex: texture_3d<f32>;
@group(0) @binding(3) var tintSamp: sampler;
const TINT_EXTENT_XZ: f32 = " + (ClimateTintSystem.SizeXZ * ClimateTintSystem.CellXZ) + @".0;
const TINT_EXTENT_Y: f32 = " + (ClimateTintSystem.SizeY * ClimateTintSystem.CellY) + @".0;
const TINT_BOTTOM: f32 = " + ClimateTintSystem.BottomY + @".0;
const TINT_SIZE_XZ: f32 = " + ClimateTintSystem.SizeXZ + @".0;
const TINT_SIZE_Y: f32 = " + ClimateTintSystem.SizeY + @".0;
const TINT_FAR_EXTENT_XZ: f32 = " + (ClimateTintSystem.SizeXZ * ClimateTintSystem.FarCellXZ) + @".0;
const TINT_FAR_OFFSET: f32 = " + ClimateTintSystem.FarOffset + @".0;

// A tinted face's base colour (linear) at world position p: as bright as before, shifted towards the tint's colour by
// its strength there.
fn climateTinted(base: vec3<f32>, p: vec3<f32>) -> vec3<f32> {
    // Each half's layers kept to their texel middles, so the filter never blends one volume into the other.
    let y = clamp((p.y - TINT_BOTTOM) / TINT_EXTENT_Y, 0.5 / TINT_SIZE_Y, 1.0 - 0.5 / TINT_SIZE_Y) * 0.5;
    let origin = vec2<f32>(camera.lightParams.y, camera.lightParams2.w);
    let nearUv = (p.xz - origin) / TINT_EXTENT_XZ;
    let edge = 0.5 / TINT_SIZE_XZ;
    var uvw = vec3<f32>(nearUv, y);
    if (any(nearUv < vec2<f32>(edge)) || any(nearUv > vec2<f32>(1.0 - edge))) {
        uvw = vec3<f32>((p.xz - origin - TINT_FAR_OFFSET) / TINT_FAR_EXTENT_XZ, 0.5 + y);
    }
    let t = textureSampleLevel(tintTex, tintSamp, uvw, 0.0);
    let luma = vec3<f32>(0.2126, 0.7152, 0.0722);
    let colour = lin(t.rgb);
    let shifted = colour * (dot(base, luma) / max(dot(colour, luma), 0.001));
    return mix(base, shifted, t.a);
}

// Authored colours (vertex colours, sky, haze, clouds, the cloud sea) are display (sRGB) values. On an sRGB surface
// the shaders work in linear light and the output is encoded on write, so they're converted to linear first; textures
// are decoded by their sRGB format instead. Unlit colours (sky, clouds) come out as authored; lit surfaces get the
// light applied in linear, which lifts shadows and dim light and gives dark values many more 8-bit steps.
fn lin(c: vec3<f32>) -> vec3<f32> {
    if (camera.lightParams.w < 0.5) { return c; }
    return select(pow((c + 0.055) / 1.055, vec3<f32>(2.4)), c / 12.92, c <= vec3<f32>(0.04045));
}

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
    return lin(c + vec3<f32>(1.0, 0.9, 0.7) * glow * camera.sunDir.w);
}

// Aerial perspective: a blue-grey tint that builds with (3D) distance, reaching 63% of horizon.w by haze.w blocks,
// and itself turns towards the sky colour behind as it thickens, so far islands (above and below too) read as far
// away and melt into the horizon.
fn applyHaze(color: vec3<f32>, d: vec3<f32>) -> vec3<f32> {
    let k = camera.horizon.w * (1.0 - exp(-length(d) / camera.haze.w));
    return mix(color, mix(lin(camera.haze.rgb), skyColor(normalize(d)), k), k);
}

// Hazes a lit surface colour at worldPos, then fades it into what fs_sky draws behind it (the sky, or the cloud sea
// below) by distance: the loaded world ends in a ball around the view (coarse levels of terrain are balls; without
// them, streaming loads whole columns, which reach at least as far), where the fog is total (see
// SkySettings.FogDistance). Fading into the sky colour alone would leave islands showing as blue shapes over the white
// sea as they load in.
fn applyFog(color: vec3<f32>, worldPos: vec3<f32>) -> vec3<f32> {
    let d = worldPos - camera.camPos.xyz;
    let f = smoothstep(camera.fog.x, camera.fog.y, length(d));
    let hazed = applyHaze(color, d);
    if (f <= 0.0) { return applyWater(hazed, d); } // only the fog band pays for tracing the sea
    return applyWater(mix(hazed, background(normalize(d)).rgb, f), d);
}

// Underwater, as Minecraft does it: with the camera in water (camPos.w > 0, how far it sees), everything fades
// linearly into the water's colour (camera.water.rgb) by that distance, the sky included. Like Minecraft's, the fade starts
// WATER_FOG_START blocks behind the camera, so even what is right in front of it is tinted (by about a quarter on first
// going under). The colour is dimmed with the daylight.
const WATER_FOG_START: f32 = -8.0;
fn waterFogColor() -> vec3<f32> { return camera.water.rgb * max(camera.lightParams.z, 0.8 * camera.sunDir.w); }
fn applyWater(color: vec3<f32>, d: vec3<f32>) -> vec3<f32> {
    if (camera.camPos.w <= 0.0) { return color; }
    let f = (length(d) - WATER_FOG_START) / (camera.camPos.w - WATER_FOG_START);
    return mix(color, waterFogColor(), clamp(f, 0.0, 1.0));
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
    if (camera.camPos.w > 0.0) { return vec4<f32>(waterFogColor(), 1.0); } // underwater: the fog hides the sky
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
                return vec4<f32>(lin(seaShade(n)), lo);
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

// Chunk draws (vs_chunk) use group 1 differently: every chunk mesh's quads sit in one shared buffer (quadArena), and
// each draw's record (chunkDraws, picked by its instance index: the draw's firstInstance) says where its quads start
// and where it's drawn, so a run of chunk draws needs no bind group or uniform per draw. Their light maps (see
// QuadLightmap) sit in another (lightArena, 16-bit corners two to a word), from the record's lightBase (in words;
// NO_LIGHTMAP for a mesh without one).
struct ChunkDraw { model: mat4x4<f32>, chunk: vec3<i32>, grid: i32, quadBase: u32, lightBase: u32, dissolve: f32, pad2: u32 };
@group(1) @binding(2) var<storage, read> chunkDraws: array<ChunkDraw>;
@group(1) @binding(3) var<storage, read> quadArena: array<vec2<u32>>;
@group(1) @binding(4) var<storage, read> lightArena: array<u32>;

// The chunk and grid a fragment is lit from (drawChunk, drawGrid): set from the vertex's cg at the top of each
// fragment entry point that lights, since chunk draws carry them per draw rather than in the model uniform.
var<private> drawChunk: vec3<i32>;
var<private> drawGrid: i32;
// How much of a lit chunk face's colour is the underwater fog's instead (0-1), before the world's fog: set by
// fs_chunk_transparent for deep water seen from above (see shadeBlock).
var<private> waterShare: f32;

// The shared voxel storage (see GridStore): occupancy pool, chunk table ((occupancy slot or code, chunk tag), then
// ray-only data), brick table (light slot per 8³ brick), light pool and grid descriptors. Only a slot's display half
// is read here: one u16 per voxel, two per word (see LightCodecWgsl), sun visibility (0-3) in bits 14-15.
" + GridStore.LookupWgsl + QuadLightmap.SmoothLightWgsl + @"
fn ambientSky() -> f32 { return camera.lightParams.z; }
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

// pos is invariant so every pipeline drawing the same quad computes the same depth, which the cut-out faces' Equal
// test against their own depth pre-pass relies on (see fs_cutout_shaded).
struct VSOut {
    @builtin(position) @invariant pos: vec4<f32>,
    @location(0)       color:       vec3<f32>,
    @location(1)       worldNormal: vec3<f32>,
    @location(2)       localPos:    vec3<f32>,
    @location(3)       localNormal: vec3<f32>,
    @location(4)       uv:          vec3<f32>,
    @location(5)       worldPos:    vec3<f32>,
    @location(6)       alpha:       f32,          // chunk meshes: a transparent block's opacity (see fs_chunk_transparent)
    // Chunk meshes: for a cross block's quad (faces 6, 7), its cell (chunk-local) in xyz and w = 1, so it is lit from
    // that cell (see shadeBlock); w = 0 for every other surface.
    @location(7) @interpolate(flat) cross: vec4<i32>,
    // The chunk (xyz, in its grid) and grid (w; -1 for a non-chunk draw) this surface is lit from: the model uniform's,
    // or a chunk draw's record's.
    @location(8) @interpolate(flat) cg: vec4<i32>,
    // Chunk quads: where the fragment is on its quad, in cells along its width and height (see shadeLightmap), and its
    // light map: the first corner's index in lightArena (16-bit units; NO_LIGHTMAP for none), width and height.
    @location(9) quv: vec2<f32>,
    @location(10) @interpolate(flat) lm: vec4<u32>,
    // Chunk draws: the share of its pixels dissolved (see dissolved), while one chunk swaps for another.
    @location(11) @interpolate(flat) dissolve: f32,
    // Chunk draws: the direction from the camera to the fragment in the chunk's own (grid) space, not normalized (see
    // waterPath).
    @location(12) localView: vec3<f32>,
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
    o.cg          = vec4<i32>(model.chunk, model.grid);
    o.lm          = vec4<u32>(NO_LIGHTMAP, 0u, 0u, 0u);
    return o;
}

// A chunk mesh quad, packed (see ChunkQuad): its first corner x, y, z in a bits 0-17 (6 each),
// face in 18-20 (+X, -X, +Y, -Y, +Z, -Z), texture layer in 21-28 (255: untextured), width - 1 in a bits 29-31 and b
// bits 24-25, height - 1 in b bits 26-30; the block in b bits 0-7, whose colour and opacity come from blockTable.
// Corners follow the mesher's order (GreedyMesher.EmitQuad) and the texture coordinates follow from position and face,
// as GreedyMesher.MakeUv makes them: V runs down world Y on the side faces. Faces 6 and 7 are a cross block's two
// diagonal quads, one cell in size from its low corner, with b bit 8 set for the back side (GreedyMesher.EmitCross).
// The mesh's quads, read by vertex index from its range of quadArena. Drawn indexed through one shared index buffer
// (quad q's triangles are 4q + 0, 1, 2 and 0, 2, 3), so vertex_index is 4q + corner and the GPU reuses the corners the
// two triangles share: four shader runs per quad, as with the old four-vertex meshes, where six separate vertices
// (instanced or not) cost about 1 ms a frame more on a laptop GPU.
@vertex
fn vs_chunk(@builtin(vertex_index) vi: u32, @builtin(instance_index) ii: u32) -> VSOut {
    let d = chunkDraws[ii];
    return chunkVertex(d, quadArena[d.quadBase + vi / 4u], vi % 4u);
}

// The wireframe: each quad's outline as four lines.
@vertex
fn vs_chunk_lines(@builtin(vertex_index) vi: u32, @builtin(instance_index) ii: u32) -> VSOut {
    var corners = array<u32, 8>(0u, 1u, 1u, 2u, 2u, 3u, 3u, 0u);
    let d = chunkDraws[ii];
    return chunkVertex(d, quadArena[d.quadBase + vi / 8u], corners[vi % 8u]);
}

fn chunkVertex(d: ChunkDraw, quad: vec2<u32>, corner: u32) -> VSOut {
    let a = quad.x;
    let c = quad.y;
    let face = (a >> 18u) & 7u;
    let w = ((a >> 29u) | (((c >> 24u) & 3u) << 3u)) + 1u;
    let h = ((c >> 26u) & 31u) + 1u;
    let du = f32(w);
    let dv = f32(h);
    // The mesher winds +X, -Y and +Z the other way round (FaceDesc.Flip), and a diagonal's front.
    let back = (c & 256u) != 0u;
    let flip = face == 0u || face == 3u || face == 4u || (face >= 6u && !back);
    let cu = select(select(0.0, du, corner >= 2u), select(0.0, du, corner == 1u || corner == 2u), flip);
    let cv = select(select(0.0, dv, corner == 1u || corner == 2u), select(0.0, dv, corner >= 2u), flip);
    var position = vec3<f32>(f32(a & 63u), f32((a >> 6u) & 63u), f32((a >> 12u) & 63u));
    let s = select(1.0, -1.0, (face & 1u) == 1u);
    var normal = vec3<f32>(0.0);
    var uv2: vec2<f32>;
    var cross = vec4<i32>(0);
    if (face < 2u)      { position.y += cu; position.z += cv; normal.x = s; uv2 = vec2<f32>(position.z, -position.y); }
    else if (face < 4u) { position.x += cu; position.z += cv; normal.y = s; uv2 = position.xz; }
    else if (face < 6u) { position.x += cu; position.y += cv; normal.z = s; uv2 = vec2<f32>(position.x, -position.y); }
    else {
        // A cross block: cu runs along the diagonal, cv up. Lit as an upward-facing surface (see shadeBlock), so its
        // two quads, and both their sides, match each other and the ground they stand on.
        // Varied by hashes of its world cell, so plants don't line up in a grid but each looks the same every time
        // it's meshed: the plant is turned a full circle (not just the quarter that covers every angle of the X, so a
        // sprite shows mirrored as often as not: its back side's texture runs the other way), nudged up to 0.2 from
        // the cell's middle and stretched to 0.75-1.15 tall; then each of its two quads (both sides alike) is twisted
        // up to 25 degrees and shifted up to 0.08 on its own, so they don't meet in a perfect X, unless the block
        // keeps a clean X (BlockDef.RigidCross: blockTable's a is 0, see ChunkQuad.BuildBlockTable).
        cross = vec4<i32>(vec3<i32>(position), 1);
        let h = cellHash(d.chunk * 32 + cross.xyz);
        let q = mixHash(h ^ (face * 0x9e3779b9u));
        let loose = blockTable[c & 255u].a % 2.0; // less the tint flags (see below)
        let turn = f32(h & 1023u) * (6.2831853 / 1024.0) + loose * (f32(q & 1023u) / 1023.0 - 0.5) * 0.87;
        let nudge = (vec2<f32>(f32((h >> 10u) & 255u), f32((h >> 18u) & 255u)) / 255.0 - 0.5) * 0.4
                  + loose * (vec2<f32>(f32((q >> 10u) & 255u), f32((q >> 18u) & 255u)) / 255.0 - 0.5) * 0.16;
        let tall = 0.75 + 0.4 * f32((h >> 26u) & 63u) / 63.0;
        let d = vec2<f32>(select(0.5 - cu, cu - 0.5, face == 6u), cu - 0.5); // from the cell's middle, in x and z
        let cs = cos(turn);
        let sn = sin(turn);
        let r = vec2<f32>(cs * d.x - sn * d.y, sn * d.x + cs * d.y) + 0.5 + nudge;
        // V follows the unstretched height, so the sprite stretches with the quad rather than being cropped.
        uv2 = vec2<f32>(cu, -(position.y + cv));
        position += vec3<f32>(r.x, cv * tall, r.y);
        normal.y = 1.0;
    }
    let layerBits = (a >> 21u) & 255u;
    let layer = select(f32(layerBits), -1.0, layerBits == 255u);
    let block = blockTable[c & 255u];
    // Its alpha is the opacity plus 2 if the block's top takes the climate tint, 4 if all its faces do.
    let tintAll = block.a >= 3.5;
    let tintTop = !tintAll && block.a >= 1.5;
    let opacity = block.a - select(select(0.0, 2.0, tintTop), 4.0, tintAll);

    var o: VSOut;
    let world     = d.model * vec4<f32>(position, 1.0);
    o.pos         = camera.proj * camera.view * world;
    o.worldPos    = world.xyz;
    o.color       = block.rgb;
    o.worldNormal = (d.model * vec4<f32>(normal, 0.0)).xyz;
    o.localPos    = position;
    o.localNormal = normal;
    o.uv          = vec3<f32>(uv2, layer);
    o.alpha       = opacity;
    o.cross       = cross;
    o.cg          = vec4<i32>(d.chunk, d.grid);
    // The quad's light map (ChunkQuad.LightOffset), unless it has none (a cross block's, or past a huge mesh's 16 bits).
    let lightOffset = ((c >> 9u) & 0x7FFFu) | ((c >> 31u) << 15u);
    // lm.w: whether this face takes the climate tint (see climateTinted).
    o.lm          = vec4<u32>(NO_LIGHTMAP, w, h, select(0u, 1u, tintAll || (tintTop && face == 2u)));
    if (face < 6u && lightOffset != 0xFFFFu && d.lightBase != NO_LIGHTMAP) { o.lm.x = 2u * (d.lightBase + lightOffset); }
    o.quv         = vec2<f32>(cu, cv);
    o.dissolve    = d.dissolve;
    // The model is a rotation, uniform scale and translation, so its transposed 3x3 turns a world direction back
    // into a grid one (scaled, which normalizing undoes).
    o.localView   = transpose(mat3x3<f32>(d.model[0].xyz, d.model[1].xyz, d.model[2].xyz)) * (world.xyz - camera.camPos.xyz);
    return o;
}

// Whether this pixel of a dissolving chunk draw is left out: a 4x4 ordered dither on the screen. A chunk going away
// (dissolve d > 0) leaves out the pixels under d; one coming in over it (dissolve -d) draws only those, so through a
// swap each pixel shows one or the other, never both (see ChunkDrawStates).
fn dissolved(in: VSOut) -> bool {
    if (in.dissolve == 0.0) { return false; }
    var bayer = array<f32, 16>(0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
    let p = vec2<u32>(in.pos.xy) & vec2<u32>(3u);
    let b = (bayer[p.x + 4u * p.y] + 0.5) / 16.0;
    if (in.dissolve > 0.0) { return b < in.dissolve; }
    return b >= -in.dissolve;
}

// A well-mixed hash of a world cell, for per-cell variation that's the same wherever the cell is meshed.
fn cellHash(p: vec3<i32>) -> u32 {
    let h = (bitcast<u32>(p.x) * 0x8da6b343u) ^ (bitcast<u32>(p.y) * 0xd8163841u) ^ (bitcast<u32>(p.z) * 0xcb1ab31fu);
    return mixHash(h);
}

// Scrambles h's bits (a 32-bit finaliser), e.g. to draw a second, independent hash from one.
fn mixHash(x: u32) -> u32 {
    var h = x;
    h = h ^ (h >> 16u); h = h * 0x7feb352du; h = h ^ (h >> 15u); h = h * 0x846ca68bu; h = h ^ (h >> 16u);
    return h;
}

// A plant's colour, varied a little by its cell (see chunkVertex): 0.85-1.1 as bright, a touch yellower or bluer.
fn plantTint(cell: vec3<i32>) -> vec3<f32> {
    let h = mixHash(cellHash(cell) ^ 0x5bd1e995u);
    let bright = 0.85 + 0.25 * f32(h & 255u) / 255.0;
    let warm = (f32((h >> 8u) & 255u) / 255.0 - 0.5) * 0.16;
    return bright * vec3<f32>(1.0 + warm, 1.0, 1.0 - warm);
}

// Voxel v in this draw's grid (grid voxel space). Unloaded → open.
fn isSolid(v: vec3<i32>) -> bool {
    let i = entryOf(drawGrid, v >> vec3<u32>(5u));
    if (i < 0) { return false; }
    let code = chunkTable[2 * i].x;
    if (code >= 0) {
        let l = v & vec3<i32>(31);
        return ((occPool[u32(code * WPC + l.y + 32 * l.z)] >> u32(l.x)) & 1u) == 1u;
    }
    return code == OCC_ALL_SOLID;
}

" + GridStore.LightCodecWgsl + @"

// One cell's light: sky (the flat ambient, only where there is no stored light), RGB (everything else: ambient
// darkened by the ray AO, lamps and bounce, see LightCodecWgsl) and sun visibility 0-1.
struct Cell { sky: f32, rgb: vec3<f32>, sun: f32 };

fn cellAt(v: vec3<i32>) -> Cell {
    let d = displayAt(v);
    var c: Cell;
    if (isEmptyLight(d)) { c.sky = camera.lightParams.z; c.rgb = vec3<f32>(0.0); c.sun = 1.0; return c; }
    c.sky = 0.0;
    c.rgb = decodeLight(d);
    c.sun = f32((d >> 14u) & 3u) / 3.0;
    return c;
}

struct Shade { sky: f32, rgb: vec3<f32>, sun: f32, ao: f32 };

// Smooth light and corner AO worked out for this pixel's cell face (see faceCorners), interpolated by where the pixel
// is on it: what a light map stores, worked out per pixel instead. Kept to compare against (the panel's per-pixel
// smoothing mode).
fn shadeFast(localPos: vec3<f32>, localNormal: vec3<f32>, see: bool) -> Shade {
    let air = drawChunk * 32 + vec3<i32>(floor(localPos + 0.5 * localNormal));
    let n = abs(localNormal);
    let ax = faceAxes(select(select(2u, 1u, n.y > 0.5), 0u, n.x > 0.5));
    let f = faceCorners(air, vec3<i32>(round(localNormal)), vec3<i32>(ax[0]), vec3<i32>(ax[1]), see);

    let ft = fract(dot(localPos, ax[0]));
    let fb = fract(dot(localPos, ax[1]));
    var o: Shade;
    let a = mix(mix(f.c00.a, f.c10.a, ft), mix(f.c01.a, f.c11.a, ft), fb);
    o.sky = a.x;
    o.rgb = a.yzw;
    o.sun = mix(mix(f.c00.sun, f.c10.sun, ft), mix(f.c01.sun, f.c11.sun, ft), fb);
    let s = smoothstep(0.0, 1.0, ft);
    let t = smoothstep(0.0, 1.0, fb);
    o.ao = mix(mix(f.ao.x, f.ao.y, s), mix(f.ao.z, f.ao.w, s), t);
    return o;
}

fn lmCorner(i: u32) -> LmCorner { return decodeCorner((lightArena[i >> 1u] >> ((i & 1u) * 16u)) & 0xFFFFu); }

// Smooth light and corner AO from a chunk quad's light map (see QuadLightmap): the 4 stored corners of the cell the
// pixel is in, light blended linearly and AO by smoothstep, as shadeFast blends them. quv: where the pixel is on the
// quad, in cells; lm: its light map (VSOut.lm); back: the quad's back is shown (water seen from below), whose corners
// follow its front's.
fn shadeLightmap(quv: vec2<f32>, lm: vec4<u32>, back: bool) -> Shade {
    let w = lm.y;
    let h = lm.z;
    let cell = min(vec2<u32>(max(floor(quv), vec2<f32>(0.0))), vec2<u32>(w - 1u, h - 1u));
    let f = clamp(quv - vec2<f32>(cell), vec2<f32>(0.0), vec2<f32>(1.0));
    let stride = w + 1u;
    let i = lm.x + select(0u, stride * (h + 1u), back) + cell.x + stride * cell.y;
    let c00 = lmCorner(i);
    let c10 = lmCorner(i + 1u);
    let c01 = lmCorner(i + stride);
    let c11 = lmCorner(i + stride + 1u);
    var o: Shade;
    o.sky = 0.0;
    o.rgb = mix(mix(c00.rgb, c10.rgb, f.x), mix(c01.rgb, c11.rgb, f.x), f.y);
    o.sun = mix(mix(c00.sun, c10.sun, f.x), mix(c01.sun, c11.sun, f.x), f.y);
    let s = smoothstep(vec2<f32>(0.0), vec2<f32>(1.0), f);
    o.ao = mix(mix(c00.ao, c10.ao, s.x), mix(c01.ao, c11.ao, s.x), s.y);
    return o;
}

// shadeFast's corner AO alone (the solid mask, no light lookups), for the flat-light debug mode.
fn cornerAoFast(localPos: vec3<f32>, localNormal: vec3<f32>) -> f32 {
    let air = drawChunk * 32 + vec3<i32>(floor(localPos + 0.5 * localNormal));
    let n = abs(localNormal);
    var T: vec3<i32>; var B: vec3<i32>;
    if (n.x > 0.5)      { T = vec3<i32>(0, 1, 0); B = vec3<i32>(0, 0, 1); }
    else if (n.y > 0.5) { T = vec3<i32>(1, 0, 0); B = vec3<i32>(0, 0, 1); }
    else                { T = vec3<i32>(1, 0, 0); B = vec3<i32>(0, 1, 0); }
    let m = solidMask(air, entryOf(drawGrid, air >> vec3<u32>(5u)));
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

// The block texture at a fragment, or for an untextured block (uv.z < 0, the no-texture sentinel) its vertex colour
// (alpha 1). Sampled unconditionally (avoids implicit-derivative issues from branching on a per-fragment value).
// Debug (Renderer panel, for measuring what the render pass spends): lightParams2.y bit 1 = no texture sample.
fn blockColor(in: VSOut) -> vec4<f32> {
    let dbg = u32(camera.lightParams2.y);
    let layer = max(i32(round(in.uv.z)), 0);
    var tex   = vec4<f32>(0.6, 0.6, 0.6, 1.0);
    // The mip level comes from the unwrapped coordinates' gradients: fract's jump at every block edge would make the
    // GPU pick the smallest mip along each edge (thin lines between blocks).
    let gx = dpdx(in.uv.xy);
    let gy = dpdy(in.uv.xy);
    if ((dbg & 1u) == 0u) { tex = textureSampleGrad(atlasTex, atlasSamp, fract(in.uv.xy), layer, gx, gy); }
    return select(vec4<f32>(lin(in.color), 1.0), tex, in.uv.z >= 0.0);
}

// vs_main's meshes (the selection highlight, debug shapes, the grapple rope): no light data, so full-bright.
@fragment
fn fs_main(in: VSOut) -> @location(0) vec4<f32> {
    return vec4<f32>(blockColor(in).rgb, 1.0);
}

// Opaque chunk faces.
@fragment
fn fs_chunk(in: VSOut) -> @location(0) vec4<f32> {
    var base = blockColor(in).rgb;
    if (in.lm.w != 0u) { base = climateTinted(base, in.worldPos); }
    return vec4<f32>(shadeBlock(in, base, false, false), 1.0);
}

// Opaque chunk faces dissolving (one chunk swapping for another): its own pipeline, since a discard costs the early
// depth test.
@fragment
fn fs_chunk_dissolve(in: VSOut) -> @location(0) vec4<f32> {
    if (dissolved(in)) { discard; }
    var base = blockColor(in).rgb;
    if (in.lm.w != 0u) { base = climateTinted(base, in.worldPos); }
    return vec4<f32>(shadeBlock(in, base, false, false), 1.0);
}

// Translucent blocks' faces (see RenderLayer.Translucent), alpha-blended over the opaque world: lit like fs_main, with
// the texture's alpha times the block's opacity. Fully clear texels are cut out. Drawn only where the face is the
// nearest transparent one (fs_chunk_transparent_depth laid that depth down first), so every pixel blends exactly one
// transparent layer and the draw order doesn't matter. Both sides are drawn; a face seen from behind (from inside the
// water) is lit as facing the viewer, from the cell on the viewer's side, so it is lit like the water around it rather
// than like the air beyond.
//
// Seen from outside, the water darkens and turns opaque with how much of it the view looks through, along the view
// from the face to what's behind it (see waterPath). Seen from inside, the underwater fog does that
// instead (see applyWater).
@fragment
fn fs_chunk_transparent(in: VSOut, @builtin(front_facing) front: bool) -> @location(0) vec4<f32> {
    let c = blockColor(in);
    var alpha = c.a * in.alpha;
    if (alpha < 0.004 || dissolved(in)) { discard; }
    // Seen from outside: the view runs into the face against its normal (out of the water). Decided from the geometry
    // rather than front_facing, so it holds whatever a face's winding.
    if (dot(in.localView, in.localNormal) < 0.0 && in.cg.w >= 0) {
        drawChunk = in.cg.xyz;
        drawGrid  = in.cg.w;
        let clear = exp(-waterPath(in.localPos, vec3<i32>(floor(in.localPos - 0.5 * in.localNormal)), normalize(in.localView)) / max(camera.water.w, 0.1));
        alpha = 1.0 - (1.0 - camera.water2.x) * clear;
        waterShare = 1.0 - (1.0 - camera.water2.y) * clear;
    }
    var v = in;
    if (!front) { v.localNormal = -v.localNormal; v.worldNormal = -v.worldNormal; }
    return vec4<f32>(shadeBlock(v, c.rgb, true, !front), alpha);
}

// Water depth shading (fs_chunk_transparent): each camera.water.w blocks the view goes through cut what shows through
// the water to about a third, and the water's own colour turns as much towards the underwater fog's (waterFogColor),
// so deep water looks the same colour from above as from inside it. The thinnest water is camera.water2.x opaque and
// already camera.water2.y of the way to the fog's colour (the rest its lit texture, for the ripples), so the shallows
// are the depths' colour, only clearer. (See WaterSettings.) The view's walk through the water (waterPath) counts up
// to WATER_MAX_PATH blocks of water, over at most WATER_REACH blocks.
const WATER_MAX_PATH: f32 = 16.0;
const WATER_REACH: f32 = 32.0;

// How thick the water is at p (grid space), as a multiple of its clarity's: 1 give or take camera.water2.z, by smooth
// noise camera.water2.w blocks across, so there are murkier and clearer stretches of water rather than one even body.
fn waterDensity(p: vec3<f32>) -> f32 {
    return 1.0 + camera.water2.z * (2.0 * valueNoise3(p / max(camera.water2.w, 0.5)) - 1.0);
}

// Smooth 3D value noise, 0-1, varying over about one unit of p.
fn valueNoise3(p: vec3<f32>) -> f32 {
    let i = vec3<i32>(floor(p));
    let f = p - floor(p);
    let u = f * f * (3.0 - 2.0 * f);
    var c: array<f32, 8>;
    for (var k = 0; k < 8; k++) {
        let o = vec3<i32>(k & 1, (k >> 1u) & 1, k >> 2u);
        c[k] = f32(cellHash(i + o) & 1023u);
    }
    let x0 = mix(mix(c[0], c[1], u.x), mix(c[2], c[3], u.x), u.y);
    let x1 = mix(mix(c[4], c[5], u.x), mix(c[6], c[7], u.x), u.y);
    return mix(x0, x1, u.z) / 1023.0;
}

// What voxel v in this draw's grid holds: 2 a block that blocks light, 1 water (a translucent block: its chunk's
// translucent bits, whose occupancy slot plus one is its chunk-table entry's second word's w less its covered bit, 0
// for none; see GridStore.EntryWords), 0 anything else. One chunk lookup for both.
fn waterOrSolid(v: vec3<i32>) -> i32 {
    let i = entryOf(drawGrid, v >> vec3<u32>(5u));
    if (i < 0) { return 0; }
    let code = chunkTable[2 * i].x;
    let l = v & vec3<i32>(31);
    let w = u32(l.y + 32 * l.z);
    if (code == OCC_ALL_SOLID) { return 2; }
    if (code >= 0 && ((occPool[u32(code * WPC) + w] >> u32(l.x)) & 1u) == 1u) { return 2; }
    let slot = chunkTable[2 * i + 1].w >> 1u;
    if (slot > 0 && ((occPool[u32((slot - 1) * WPC) + w] >> u32(l.x)) & 1u) == 1u) { return 1; }
    return 0;
}

// How much water the view goes through from p on a water face, into its cell (both chunk-local, so precise however
// far the chunk is from the origin), along rd (normalized, grid space), in blocks, up to WATER_MAX_PATH: a walk
// through the cells it crosses, adding up the water in them (not stopping where it first leaves the water, so a ragged
// waterfall's air gaps don't break its shading into bands), each stretch weighed by how thick the water is there
// (waterDensity), until a block that blocks light or WATER_REACH blocks.
fn waterPath(p: vec3<f32>, start: vec3<i32>, rd: vec3<f32>) -> f32 {
    var cell = start;
    let stepC = vec3<i32>(select(-1, 1, rd.x > 0.0), select(-1, 1, rd.y > 0.0), select(-1, 1, rd.z > 0.0));
    let tDelta = 1.0 / max(abs(rd), vec3<f32>(1e-6));
    var tMax = abs(vec3<f32>(select(cell, cell + 1, rd > vec3<f32>(0.0))) - p) * tDelta;
    var t = 0.0;
    var path = 0.0;
    let origin = vec3<f32>(drawChunk * 32);
    for (var i = 0; i < 64; i++) {
        let what = waterOrSolid(drawChunk * 32 + cell);
        if (what == 2) { break; }
        let tNext = min(tMax.x, min(tMax.y, tMax.z));
        if (what == 1) { path += (tNext - t) * waterDensity(origin + p + rd * (0.5 * (t + tNext))); }
        t = tNext;
        if (path >= WATER_MAX_PATH || t >= WATER_REACH) { break; }
        if (tMax.x <= tMax.y && tMax.x <= tMax.z) { tMax.x += tDelta.x; cell.x += stepC.x; }
        else if (tMax.y <= tMax.z)                { tMax.y += tDelta.y; cell.y += stepC.y; }
        else                                      { tMax.z += tDelta.z; cell.z += stepC.z; }
    }
    return min(path, WATER_MAX_PATH);
}

// Cut-out blocks' faces (RenderLayer.Cutout: leaves, grass, glass), in two passes, after the opaque world.
// fs_cutout_depth lays down the nearest cut-out face's depth at each pixel, cutting out the texels under half alpha;
// fs_cutout_shaded then lights only the face that matches it exactly (depth Equal: a texel cut out of the pre-pass left a
// farther depth there, so it fails too). A discard costs the early depth test, so with one discarding pass a leaf canopy
// lit every face behind each pixel before the hidden ones lost the depth test. The lit pass has no discard and keeps it,
// and neither has fs_main.
@fragment
fn fs_cutout_depth(in: VSOut) -> @location(0) vec4<f32> {
    if (blockColor(in).a < 0.5 || dissolved(in)) { discard; }
    return vec4<f32>(0.0);
}

@fragment
fn fs_cutout_shaded(in: VSOut) -> @location(0) vec4<f32> {
    var base = blockColor(in).rgb;
    if (in.lm.w != 0u) { base = climateTinted(base, in.worldPos); }
    if (in.cross.w != 0) { base *= plantTint(in.cg.xyz * 32 + in.cross.xyz); }
    return vec4<f32>(shadeBlock(in, base, true, false), 1.0);
}

// Transparent faces' depth pre-pass: the nearest transparent face at each pixel, without its fully clear texels.
@fragment
fn fs_chunk_transparent_depth(in: VSOut) -> @location(0) vec4<f32> {
    if (blockColor(in).a * in.alpha < 0.004 || dissolved(in)) { discard; }
    return vec4<f32>(0.0);
}

// A chunk face's lit, fogged colour (baseColor: its texture or colour; see: a see-through block's face; back: its back
// is shown). Debug (Renderer panel): lightParams2.y bit 2 = no fog or haze; lightParams2.z = lighting mode (see below).
fn shadeBlock(in: VSOut, baseColor: vec3<f32>, see: bool, back: bool) -> vec3<f32> {
    drawChunk = in.cg.xyz;
    drawGrid  = in.cg.w;
    let dbg = u32(camera.lightParams2.y);

    // A chunk drawn without a grid has no light data: full-bright; or lit as open air below (OPEN_AIR_GRID).
    if (drawGrid < 0 && drawGrid != OPEN_AIR_GRID) { return baseColor; }

    let worldN = normalize(in.worldNormal);
    let ndotl  = max(dot(worldN, -(camera.sunDir.xyz)), 0.0);
    var s: Cell;
    var ao: f32;
    let mode = i32(camera.lightParams2.z);
    let air = drawChunk * 32 + vec3<i32>(floor(in.localPos + 0.5 * in.localNormal));
    if (mode == 3 || drawGrid == OPEN_AIR_GRID) {
        // No voxel lighting at all (open sky, full sun, no AO): a debug view, and a chunk lit as open air.
        s.sky = camera.lightParams.z; s.rgb = vec3<f32>(0.0); s.sun = 1.0;
        ao = 1.0;
    } else if (in.cross.w != 0) {
        // A cross block (plant): its own cell's light, which it doesn't block, flat over the whole plant, darkening
        // towards its foot in place of corner AO.
        let c = cellAt(drawChunk * 32 + in.cross.xyz);
        s.sky = c.sky; s.rgb = c.rgb; s.sun = c.sun;
        ao = mix(0.6, 1.0, clamp(in.localPos.y - f32(in.cross.y), 0.0, 1.0));
    } else if (mode == 1 || mode == 2 || in.lm.x == NO_LIGHTMAP) {
        // Flat light: the debug modes, with (1) or without (2) corner AO, or a quad without a light map.
        let c = cellAt(air);
        s.sky = c.sky; s.rgb = c.rgb; s.sun = c.sun;
        ao = 1.0;
        if (mode == 1) { ao = cornerAoFast(in.localPos, in.localNormal); }
    } else if (mode == 4) {
        // Debug: the smoothing worked out per pixel, as before light maps.
        let f = shadeFast(in.localPos, in.localNormal, see);
        s.sky = f.sky; s.rgb = f.rgb; s.sun = f.sun;
        ao = f.ao;
    } else {
        let f = shadeLightmap(in.quv, in.lm, back);
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

    let shaded = mix(baseColor * lit * aoFactor * camera.lightParams2.x, waterFogColor(), waterShare);
    if ((dbg & 2u) != 0u) { return shaded; }
    return applyFog(shaded, in.worldPos);
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
// light — sky/AO, lamp light and sun visibility, one flat value for the whole model — combined like fs_main does. A
// model block that blocks light (BlockDef.OpaqueModel) has no light of its own (the light pass gives such cells
// only flat ambient and full sun), so it takes the brightest of its open neighbours' (see modelCell).
// Any other model has no light data and is lit like an open-air surface: the brighter of the flat ambient and
// Lambertian sun. Both are fogged like the terrain. Drawn with culling off (glTF doubleSided is common and cheap
// here), so back faces flip their normal. Texels under the material's alpha cutoff are cut out.
// The light a model block at voxel v is lit by: its own cell's, or if v blocks light, each channel's brightest over
// its open face neighbours (all open neighbours blocked too: ambient in shadow).
fn modelCell(v: vec3<i32>) -> Cell {
    if (!isSolid(v)) { return cellAt(v); }
    var c: Cell;
    c.sky = 0.0; c.rgb = vec3<f32>(0.0); c.sun = 0.0;
    var any = false;
    for (var f = 0; f < 6; f = f + 1) {
        var d = vec3<i32>(0);
        d[f / 2] = select(1, -1, (f & 1) == 1);
        let n = v + d;
        if (isSolid(n)) { continue; }
        let o = cellAt(n);
        c.sky = max(c.sky, o.sky); c.rgb = max(c.rgb, o.rgb); c.sun = max(c.sun, o.sun);
        any = true;
    }
    if (!any) { c.sky = camera.lightParams.z; }
    return c;
}

@fragment
fn fs_model(in: VSOut, @builtin(front_facing) front: bool) -> @location(0) vec4<f32> {
    drawChunk = model.chunk;
    drawGrid  = model.grid;
    let tex = textureSample(atlasTex, atlasSamp, in.uv.xy, 0);
    if (tex.a < model.params.x) { discard; }
    var n = normalize(in.worldNormal);
    if (!front) { n = -n; }
    let ndotl = max(dot(n, -camera.sunDir.xyz), 0.0);
    var lit = vec3<f32>(max(camera.lightParams.z, ndotl * camera.sunDir.w));
    if (model.grid >= 0) {
        let c = modelCell(model.chunk * 32 + vec3<i32>(model.params.yzw));
        lit = max(vec3<f32>(max(c.sky, ndotl * c.sun * camera.sunDir.w)), c.rgb);
    }
    lit = max(lit, vec3<f32>(MIN_AMBIENT));
    return vec4<f32>(applyFog(tex.rgb * lin(in.color) * lit * camera.lightParams2.x, in.worldPos), 1.0);
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
    o.cg = vec4<i32>(0, 0, 0, -1);
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
    return vec4<f32>(applyWater(mix(applyHaze(lin(in.color * shade), d), skyColor(normalize(d)), f), d), 1.0);
}

// Particles (ParticleRenderer): one camera-facing square per instance, 6 vertices. p.xyz: its centre in world space,
// p.w: its size in blocks. color.rgb: sRGB, drawn unlit (flames glow).
@vertex
fn vs_particle(@builtin(vertex_index) vi: u32, @location(0) p: vec4<f32>, @location(1) color: vec4<f32>) -> VSOut {
    var corners = array<vec2<f32>, 6>(vec2<f32>(-0.5, -0.5), vec2<f32>(0.5, -0.5), vec2<f32>(0.5, 0.5),
                                      vec2<f32>(-0.5, -0.5), vec2<f32>(0.5, 0.5), vec2<f32>(-0.5, 0.5));
    let c = corners[vi];
    // The view matrix's rows are the camera's right, up and back axes in world space.
    let right = vec3<f32>(camera.view[0].x, camera.view[1].x, camera.view[2].x);
    let up    = vec3<f32>(camera.view[0].y, camera.view[1].y, camera.view[2].y);
    let back  = vec3<f32>(camera.view[0].z, camera.view[1].z, camera.view[2].z);
    let world = p.xyz + (right * c.x + up * c.y) * p.w;
    var o: VSOut;
    o.pos         = camera.proj * camera.view * vec4<f32>(world, 1.0);
    o.worldPos    = world;
    o.color       = color.rgb;
    o.worldNormal = back;
    o.localNormal = back;
    o.uv          = vec3<f32>(0.0, 0.0, -1.0);
    return o;
}

@fragment
fn fs_particle(in: VSOut) -> @location(0) vec4<f32> {
    return vec4<f32>(applyFog(lin(in.color), in.worldPos), 1.0);
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
    // Chunk meshes: group 1 also holds the mesh's quads (read by vs_chunk), one bind group per mesh.
    private BindGroupLayout* _chunkModelLayout;
    private PipelineLayout* _chunkPipelineLayout;
    private RenderPipeline* _pipeline;
    private RenderPipeline* _wireframePipeline;
    private RenderPipeline* _hudPipeline;
    private RenderPipeline* _skyPipeline;
    private RenderPipeline* _cloudPipeline;
    private RenderPipeline* _particlePipeline;
    private RenderPipeline* _modelPipeline;
    private RenderPipeline* _chunkPipeline;          // vs_chunk: chunk meshes (ChunkQuad instances)
    private RenderPipeline* _chunkDissolvePipeline;  // dissolving ones (fs_chunk_dissolve)
    private RenderPipeline* _chunkWireframePipeline;
    private RenderPipeline* _chunkOverdrawPipeline;  // debug overdraw view
    private RenderPipeline* _chunkCutoutDepthPipeline;  // their depth pre-pass (fs_cutout_depth)
    private RenderPipeline* _chunkCutoutShadedPipeline; // then lit where they're nearest (fs_cutout_shaded)
    private RenderPipeline* _chunkTransparentPipeline; // translucent blocks' faces, alpha-blended (fs_chunk_transparent)
    private RenderPipeline* _chunkTransparentDepthPipeline; // their depth pre-pass (fs_chunk_transparent_depth)

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
    private readonly GpuBuffer _blockTable; // per block type: colour and opacity (ChunkQuad.BuildBlockTable)
    private readonly GpuBuffer _hudCameraBuffer; // permanently holds identity view+proj
    private readonly GpuBuffer _modelBuffer;

    // One index buffer for every chunk mesh: quad q's two triangles as 4q + (0, 1, 2, 0, 2, 3), for as many quads as a
    // chunk can have (every block of a checkerboard showing all six faces). vs_chunk takes quad = index / 4 and
    // corner = index % 4, so the GPU reuses the two corners the triangles share: four vertex shader runs per quad
    // instead of six.
    private const int MaxChunkQuads = ChunkData.Size * ChunkData.Size * ChunkData.Size / 2 * 6;
    private readonly GpuBuffer _quadIndices;

    // Chunk draws: every chunk mesh's quads in one buffer, and each frame's chunk draws' records (transform, chunk, grid,
    // first quad) in another, read by instance index; one bind group over both, rebuilt when the quad buffer grows. So a
    // run of chunk draws sets group 1 and the index buffer once, and each chunk is a single DrawIndexed.
    private const int MaxChunkDraws = 16384;
    private readonly GpuBufferArena _quadArena;
    private readonly QuadLightmap _lightmap;
    private int _lightmapEpoch;          // bumped when what every light map bakes in changes (the flat ambient)
    private float _lightmapAmbient = float.NaN;
    private int _chunkGroupLightVersion = -1;
    private bool _lightmapFullWarned;
    private readonly GpuBuffer _chunkDrawBuffer;
    private readonly ChunkDrawRecord[] _chunkDrawStaging = new ChunkDrawRecord[MaxChunkDraws];
    private int _chunkDrawCount;
    private BindGroup* _chunkGroup;
    private int _chunkGroupVersion = -1;
    private bool _chunkStateBound; // group 1 and the index buffer are the chunk draws' (cleared by any other draw)
    private bool _arenaFullWarned;

    private BindGroup* _cameraBindGroup;
    private BindGroup* _hudCameraBindGroup;

    // The climate tint volume (ClimateTintSystem), bound beside the camera; zero (no tint) until the game submits one.
    private Texture*     _tintTexture;
    private TextureView* _tintView;
    private Sampler*     _tintSampler;
    private float        _tintOriginX, _tintOriginZ;
    private (float X, float Z, byte[] Texels)? _pendingTint; // the next climate tint volume, uploaded with the next camera
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

    /// <summary>Call after binding a pipeline of your own on <see cref="CurrentPass"/> (e.g. the game UI's), so this
    /// renderer's later draws rebind theirs rather than assume it's still bound.</summary>
    internal void ForgetBoundPipeline() => _boundPipeline = null;

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
        _particlePipeline  = CreateParticlePipeline();
        _chunkPipeline          = CreateChunkPipeline("vs_chunk", PrimitiveTopology.TriangleList, CullMode.Back, "fs_chunk");
        _chunkDissolvePipeline  = CreateChunkPipeline("vs_chunk", PrimitiveTopology.TriangleList, CullMode.Back, "fs_chunk_dissolve");
        _chunkWireframePipeline = CreateChunkPipeline("vs_chunk_lines", PrimitiveTopology.LineList, CullMode.None, "fs_chunk");
        _chunkOverdrawPipeline  = CreateOverdrawPipeline();
        _chunkCutoutDepthPipeline = CreatePipeline("vs_chunk", "fs_cutout_depth", null, PrimitiveTopology.TriangleList, CullMode.Back,
                                                   depthWrite: true, CompareFunction.Greater, writeMask: ColorWriteMask.None,
                                                   layout: _chunkPipelineLayout);
        _chunkCutoutShadedPipeline = CreatePipeline("vs_chunk", "fs_cutout_shaded", null, PrimitiveTopology.TriangleList, CullMode.Back,
                                                    depthWrite: false, CompareFunction.Equal, layout: _chunkPipelineLayout);
        _chunkTransparentPipeline = CreateTransparentChunkPipeline();
        _chunkTransparentDepthPipeline = CreateTransparentChunkDepthPipeline();
        // The background pass: a full-screen triangle at the far plane that only fills pixels still at the cleared
        // depth (reversed: the far plane and the clear value are both 0), without writing depth.
        _skyPipeline       = CreatePipeline("vs_sky", "fs_sky", null, PrimitiveTopology.TriangleList, CullMode.None,
                                            depthWrite: false, CompareFunction.GreaterEqual);

        _cameraBuffer    = GpuBuffer.CreateUniform(ctx, CameraSize);
        _blockTable      = GpuBuffer.CreateUniform(ctx, BlockTableBytes);
        _blockTable.Write<Vector4D<float>>(0, ChunkQuad.BuildBlockTable());
        _hudCameraBuffer = GpuBuffer.CreateUniform(ctx, CameraSize);
        _modelBuffer     = GpuBuffer.CreateUniform(ctx, ModelStride * MaxObjects);
        var quadIdx = new uint[MaxChunkQuads * 6];
        for (uint q = 0, k = 0; q < MaxChunkQuads; q++)
        {
            uint b = 4 * q;
            quadIdx[k++] = b; quadIdx[k++] = b + 1; quadIdx[k++] = b + 2;
            quadIdx[k++] = b; quadIdx[k++] = b + 2; quadIdx[k++] = b + 3;
        }
        _quadIndices = GpuBuffer.Create(ctx, (ulong)quadIdx.Length * 4, BufferUsage.Index | BufferUsage.CopyDst);
        _quadIndices.Write<uint>(0, quadIdx);
        _quadArena = new GpuBufferArena(ctx, ChunkQuad.SizeBytes, BufferUsage.Storage, 1 << 20, // 8 MB to start; doubles as needed
                                        ctx.AdapterLimits.MaxStorageBufferBindingSize);
        _lightmap = new QuadLightmap(ctx);
        _chunkDrawBuffer = GpuBuffer.Create(ctx, (ulong)(MaxChunkDraws * sizeof(ChunkDrawRecord)), BufferUsage.Storage | BufferUsage.CopyDst);
        CreateBindGroups();
        CreateFallbackAtlas();

        // Pre-load identity matrices; never overwritten after this.
        Span<CameraUniform> id = stackalloc CameraUniform[1];
        id[0] = new CameraUniform { View = Mat4.Identity, Projection = Mat4.Identity,
                                    LinearizeColors = _ctx.SurfaceIsSrgb ? 1f : 0f };
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
        BindGroupLayoutEntry* camEntries = stackalloc BindGroupLayoutEntry[4];
        camEntries[0] = camEntry;
        camEntries[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Visibility = ShaderStage.Vertex, // chunk quads' block colours and opacities
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, HasDynamicOffset = false, MinBindingSize = BlockTableBytes },
        };
        camEntries[2] = new BindGroupLayoutEntry
        {
            Binding    = 2, // the climate tint volume (ClimateTintSystem)
            Visibility = ShaderStage.Fragment,
            Texture    = new TextureBindingLayout { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension3D, Multisampled = false },
        };
        camEntries[3] = new BindGroupLayoutEntry
        {
            Binding    = 3,
            Visibility = ShaderStage.Fragment,
            Sampler    = new SamplerBindingLayout { Type = SamplerBindingType.Filtering },
        };
        var camDesc = new BindGroupLayoutDescriptor { EntryCount = 4, Entries = camEntries };
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

        BindGroupLayoutEntry* chunkEntries = stackalloc BindGroupLayoutEntry[3];
        chunkEntries[0] = new BindGroupLayoutEntry
        {
            Binding = 2, // chunkDraws
            Visibility = ShaderStage.Vertex,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage, HasDynamicOffset = false, MinBindingSize = (ulong)sizeof(ChunkDrawRecord) },
        };
        chunkEntries[1] = new BindGroupLayoutEntry
        {
            Binding = 3, // quadArena
            Visibility = ShaderStage.Vertex,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage, HasDynamicOffset = false, MinBindingSize = ChunkQuad.SizeBytes },
        };
        chunkEntries[2] = new BindGroupLayoutEntry
        {
            Binding = 4, // lightArena
            Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage, HasDynamicOffset = false, MinBindingSize = 4 },
        };
        var chunkDesc = new BindGroupLayoutDescriptor { EntryCount = 3, Entries = chunkEntries };
        _chunkModelLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &chunkDesc);
        layouts[1] = _chunkModelLayout;
        var chunkPlDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 4, BindGroupLayouts = layouts };
        _chunkPipelineLayout = _api.DeviceCreatePipelineLayout(_ctx.Device, &chunkPlDesc);
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

    /// <summary>A pipeline for chunk meshes (<paramref name="vertexEntry"/>, which reads the mesh's packed
    /// <see cref="ChunkQuad"/>s from a storage buffer in group 1: no vertex buffer) and <paramref name="fragmentEntry"/>
    /// (fs_chunk), depth-tested and depth-writing.</summary>
    private RenderPipeline* CreateChunkPipeline(string vertexEntry, PrimitiveTopology topology, CullMode cullMode,
                                                string fragmentEntry)
        => CreatePipeline(vertexEntry, fragmentEntry, null, topology, cullMode, depthWrite: true, CompareFunction.Greater,
                          layout: _chunkPipelineLayout);

    /// <summary>Translucent blocks' faces (vs_chunk, fs_chunk_transparent): alpha-blended over what's drawn,
    /// only where they're the nearest transparent face (GreaterEqual against
    /// <see cref="CreateTransparentChunkDepthPipeline"/>'s depth), without writing depth. Both sides drawn, so a water
    /// surface is seen from under it too.</summary>
    private RenderPipeline* CreateTransparentChunkPipeline()
    {
        var color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha };
        var alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha };
        var blend = new BlendState { Color = color, Alpha = alpha };
        return CreatePipeline("vs_chunk", "fs_chunk_transparent", null, PrimitiveTopology.TriangleList, CullMode.None,
                              depthWrite: false, CompareFunction.GreaterEqual, &blend, layout: _chunkPipelineLayout);
    }

    /// <summary>The transparent faces' depth pre-pass (vs_chunk, fs_chunk_transparent_depth): depth only, no
    /// colour, so the colour pass draws just the nearest transparent face at each pixel.</summary>
    private RenderPipeline* CreateTransparentChunkDepthPipeline()
        => CreatePipeline("vs_chunk", "fs_chunk_transparent_depth", null, PrimitiveTopology.TriangleList, CullMode.None,
                          depthWrite: true, CompareFunction.Greater, writeMask: ColorWriteMask.None, layout: _chunkPipelineLayout);

    /// <summary>Debug overdraw view (fs_overdraw): terrain drawn additively, depth-tested in the normal draw order, so
    /// it counts the fragments that really get shaded (one per pixel is ideal).</summary>
    private RenderPipeline* CreateOverdrawPipeline()
    {
        var add = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.One };
        var blend = new BlendState { Color = add, Alpha = add };
        return CreatePipeline("vs_chunk", "fs_overdraw", null, PrimitiveTopology.TriangleList, CullMode.Back,
                              depthWrite: true, CompareFunction.Greater, &blend, layout: _chunkPipelineLayout);
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

    /// <summary>The particle squares (vs_particle/fs_particle): no vertex buffer, one
    /// <see cref="ParticleRenderer.Instance"/> per particle, opaque and depth-writing like the world (no sorting).</summary>
    private RenderPipeline* CreateParticlePipeline()
    {
        VertexAttribute* attrs = stackalloc VertexAttribute[2];
        attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 0,  ShaderLocation = 0 };
        attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 16, ShaderLocation = 1 };
        var vbLayout = new VertexBufferLayout
        {
            ArrayStride = (ulong)sizeof(ParticleRenderer.Instance), StepMode = VertexStepMode.Instance, AttributeCount = 2, Attributes = attrs,
        };
        return CreatePipeline("vs_particle", "fs_particle", &vbLayout, PrimitiveTopology.TriangleList, CullMode.None,
                              depthWrite: true, CompareFunction.Greater);
    }

    /// <summary>A pipeline over the shared shader and layouts. <paramref name="vertexBuffer"/> is the layout of its one
    /// vertex buffer, or null for none (the vertex shader makes its own geometry).</summary>
    private RenderPipeline* CreatePipeline(string vertexEntry, string fragmentEntry, VertexBufferLayout* vertexBuffer,
                                           PrimitiveTopology topology, CullMode cullMode, bool depthWrite,
                                           CompareFunction depthCompare, BlendState* blend = null,
                                           ColorWriteMask writeMask = ColorWriteMask.All, PipelineLayout* layout = null)
    {
        var vsEntry = (byte*)SilkMarshal.StringToPtr(vertexEntry, NativeStringEncoding.UTF8);
        var fsEntry = (byte*)SilkMarshal.StringToPtr(fragmentEntry, NativeStringEncoding.UTF8);

        var vertexState   = new VertexState { Module = _shader, EntryPoint = vsEntry, BufferCount = vertexBuffer == null ? 0u : 1u, Buffers = vertexBuffer };
        var colorTarget   = new ColorTargetState { Format = _ctx.SurfaceFormat, Blend = blend, WriteMask = writeMask };
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
            Layout    = layout != null ? layout : _pipelineLayout,
            Vertex    = vertexState,
            Primitive = new PrimitiveState
            {
                Topology         = topology,
                StripIndexFormat = IndexFormat.Undefined,
                FrontFace        = FrontFace.Ccw,
                CullMode         = cullMode,
            },
            DepthStencil = &depth,
            Multisample  = new MultisampleState { Count = _ctx.SampleCount, Mask = ~0u, AlphaToCoverageEnabled = false },
            Fragment     = &fragmentState,
        };
        var pipeline = _api.DeviceCreateRenderPipeline(_ctx.Device, &desc);

        SilkMarshal.Free((nint)vsEntry);
        SilkMarshal.Free((nint)fsEntry);
        return pipeline;
    }

    private void CreateBindGroups()
    {
        CreateTintVolume();
        BindGroupEntry* camEntries = stackalloc BindGroupEntry[4];
        camEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _cameraBuffer.Handle, Offset = 0, Size = CameraSize };
        camEntries[1] = new BindGroupEntry { Binding = 1, Buffer = _blockTable.Handle, Offset = 0, Size = BlockTableBytes };
        camEntries[2] = new BindGroupEntry { Binding = 2, TextureView = _tintView };
        camEntries[3] = new BindGroupEntry { Binding = 3, Sampler = _tintSampler };
        var camDesc = new BindGroupDescriptor { Layout = _cameraLayout, EntryCount = 4, Entries = camEntries };
        _cameraBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &camDesc);

        camEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _hudCameraBuffer.Handle, Offset = 0, Size = CameraSize };
        var hudCamDesc  = new BindGroupDescriptor { Layout = _cameraLayout, EntryCount = 4, Entries = camEntries };
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

    private void CreateTintVolume()
    {
        var texDesc = new TextureDescriptor
        {
            Usage         = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            Dimension     = TextureDimension.Dimension3D,
            Size          = new Extent3D(ClimateTintSystem.SizeXZ, ClimateTintSystem.SizeXZ, ClimateTintSystem.SizeY * 2),
            Format        = TextureFormat.Rgba8Unorm, // sRGB colours, decoded in the shader (climateTinted)
            MipLevelCount = 1,
            SampleCount   = 1,
        };
        _tintTexture = _api.DeviceCreateTexture(_ctx.Device, &texDesc);
        var viewDesc = new TextureViewDescriptor
        {
            Format = TextureFormat.Rgba8Unorm, Dimension = TextureViewDimension.Dimension3D,
            BaseMipLevel = 0, MipLevelCount = 1, BaseArrayLayer = 0, ArrayLayerCount = 1, Aspect = TextureAspect.All,
        };
        _tintView = _api.TextureCreateView(_tintTexture, &viewDesc);
        var samplerDesc = new SamplerDescriptor
        {
            AddressModeU = AddressMode.ClampToEdge, AddressModeV = AddressMode.ClampToEdge, AddressModeW = AddressMode.ClampToEdge,
            MagFilter = FilterMode.Linear, MinFilter = FilterMode.Linear, MipmapFilter = MipmapFilterMode.Nearest,
            LodMinClamp = 0, LodMaxClamp = 1, Compare = CompareFunction.Undefined, MaxAnisotropy = 1,
        };
        _tintSampler = _api.DeviceCreateSampler(_ctx.Device, &samplerDesc);
        UploadTint(0f, 0f, new byte[ClimateTintSystem.Bytes]); // textures start uninitialised in some backends: clear it
    }

    /// <summary>Hands the shader a new climate tint volume (<see cref="ClimateTintSystem.Bytes"/> long, laid out as
    /// that describes) whose low corner is at world (<paramref name="originX"/>, <paramref name="originZ"/>). It is
    /// drawn from the next frame on, so its origin and texels change together.</summary>
    public void SetClimateTint(float originX, float originZ, byte[] texels)
    {
        if (texels.Length != ClimateTintSystem.Bytes) throw new ArgumentException($"A tint volume is {ClimateTintSystem.Bytes} bytes.", nameof(texels));
        _pendingTint = (originX, originZ, texels);
    }

    private void UploadTint(float originX, float originZ, byte[] texels)
    {
        fixed (byte* data = texels)
        {
            var dest = new ImageCopyTexture { Texture = _tintTexture, MipLevel = 0, Origin = new Origin3D(0, 0, 0), Aspect = TextureAspect.All };
            var layout = new TextureDataLayout { Offset = 0, BytesPerRow = ClimateTintSystem.SizeXZ * 4, RowsPerImage = ClimateTintSystem.SizeXZ };
            var size = new Extent3D(ClimateTintSystem.SizeXZ, ClimateTintSystem.SizeXZ, ClimateTintSystem.SizeY * 2);
            _api.QueueWriteTexture(_ctx.Queue, &dest, data, (nuint)texels.Length, &layout, &size);
        }
        _tintOriginX = originX;
        _tintOriginZ = originZ;
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
            // Texels stay crisp up close (nearest within a level); farther away the mip levels (see MipChain) blend, so
            // a pixel covering many texels shows their average instead of an arbitrary one (moiré on distant terrain).
            MagFilter     = FilterMode.Nearest,
            MinFilter     = FilterMode.Nearest,
            MipmapFilter  = MipmapFilterMode.Linear,
            LodMinClamp   = 0,
            LodMaxClamp   = 32,
            Compare       = CompareFunction.Undefined,
            MaxAnisotropy = 1,
        };
        CreateTextureArray(tileWidth, tileHeight, layers, samplerDesc,
                           out _atlasTexture, out _atlasTextureView, out _atlasSampler, out _atlasBindGroup, mipmaps: true);
    }

    /// <summary>Block and model textures are authored in sRGB: on an sRGB surface they're stored as such, so sampling
    /// decodes them to the linear colour the shaders light.</summary>
    private TextureFormat TexelFormat => _ctx.SurfaceIsSrgb ? TextureFormat.Rgba8UnormSrgb : TextureFormat.Rgba8Unorm;

    /// <summary>Uploads <paramref name="layers"/> (RGBA8, <paramref name="width"/>×<paramref name="height"/> each) as a
    /// <c>texture_2d_array</c> and wraps it with a sampler in a bind group on the group-3 layout.</summary>
    private void CreateTextureArray(int width, int height, byte[][] layers, in SamplerDescriptor samplerDesc,
                                    out Texture* texture, out TextureView* view, out Sampler* sampler, out BindGroup* bindGroup,
                                    bool mipmaps = false)
    {
        int levels = 1;
        if (mipmaps) for (int m = System.Math.Max(width, height); m > 1; m >>= 1) levels++;
        var texDesc = new TextureDescriptor
        {
            Usage         = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            Dimension     = TextureDimension.Dimension2D,
            Size          = new Extent3D((uint)width, (uint)height, (uint)layers.Length),
            Format        = TexelFormat, // sampled as linear colour when the surface is sRGB
            MipLevelCount = (uint)levels,
            SampleCount   = 1,
        };
        texture = _api.DeviceCreateTexture(_ctx.Device, &texDesc);

        for (uint layer = 0; layer < layers.Length; layer++)
        {
            byte[] pixels = layers[layer];
            int w = width, h = height;
            for (int level = 0; level < levels; level++)
            {
                if (level > 0) (pixels, w, h) = MipChain.Halve(pixels, w, h, _ctx.SurfaceIsSrgb);
                fixed (byte* data = pixels)
                {
                    var dest = new ImageCopyTexture { Texture = texture, MipLevel = (uint)level, Origin = new Origin3D(0, 0, layer), Aspect = TextureAspect.All };
                    var dataLayout = new TextureDataLayout { Offset = 0, BytesPerRow = (uint)(w * 4), RowsPerImage = (uint)h };
                    var writeSize = new Extent3D((uint)w, (uint)h, 1);
                    _api.QueueWriteTexture(_ctx.Queue, &dest, data, (nuint)(w * h * 4), &dataLayout, &writeSize);
                }
            }
        }

        var viewDesc = new TextureViewDescriptor
        {
            Format          = TexelFormat,
            Dimension       = TextureViewDimension.Dimension2DArray,
            BaseMipLevel    = 0,
            MipLevelCount   = (uint)levels,
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

    /// <summary>
    /// Uploads a chunk mesh: <paramref name="quadCount"/> packed <see cref="ChunkQuad"/>s into a run of the shared quad
    /// buffer (grown if needed), drawn by <see cref="DrawChunkMesh"/> and the like and given back when the mesh is
    /// disposed. Null if even the largest buffer the device allows has no room (the chunk isn't drawn).
    /// Its light map takes <paramref name="lightWords"/> words of the shared light map buffer (see
    /// <see cref="ChunkQuad.AssignLightOffsets"/>), baked before it's first drawn; with no room there, the mesh is lit
    /// flat.
    /// </summary>
    public GpuMesh? UploadChunkQuads(ReadOnlySpan<byte> quads, uint quadCount, uint lightWords, LightmapKind lightKind,
                                     int[]? faceEnds = null)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var span = _quadArena.Allocate(quadCount);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (span is null)
        {
            if (!_arenaFullWarned) Console.WriteLine($"[render] chunk quad buffer full at {_quadArena.CapacityElements:N0} quads: a chunk isn't drawn");
            _arenaFullWarned = true;
            return null;
        }
        span.Write(quads);
        var light = _lightmap.Allocate(lightWords);
        if (light is null && lightWords > 0 && !_lightmapFullWarned)
        {
            Console.WriteLine($"[render] light map buffer full at {_lightmap.Arena.CapacityElements:N0} words: a chunk is lit flat");
            _lightmapFullWarned = true;
        }
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastCreateMs = (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastWriteMs  = (t2 - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return new GpuMesh(span, light, lightKind) { FaceEnds = faceEnds };
    }

    /// <summary>Lists <paramref name="mesh"/>'s light map for <see cref="BakeLightmaps"/> if it hasn't been baked since
    /// the light of its chunk (<paramref name="chunk"/> of <paramref name="grid"/>) last changed. Call for every chunk
    /// mesh about to be drawn, before drawing any.</summary>
    public void PrepareLightmap(GpuMesh? mesh, GridHandle? grid, ChunkPosition chunk, int stamp)
    {
        if (mesh?.Light == null || grid == null || grid.Index < 0) return;
        long baked = (long)_lightmapEpoch << 32 | (uint)stamp;
        if (mesh.BakedStamp == baked) return;
        mesh.BakedStamp = baked;
        _lightmap.Queue(mesh, grid.Index, chunk);
    }

    /// <summary>Whether <see cref="PrepareLightmap"/> would list <paramref name="mesh"/> for baking (its light map hasn't
    /// been baked at <paramref name="stamp"/>), for callers that bake to a budget.</summary>
    public bool LightmapStale(GpuMesh? mesh, GridHandle? grid, int stamp)
        => mesh?.Light != null && grid != null && grid.Index >= 0 && mesh.BakedStamp != ((long)_lightmapEpoch << 32 | (uint)stamp);

    /// <summary>Whether <paramref name="mesh"/>'s light map has ever been baked (true for one without a light map).</summary>
    public bool LightmapEverBaked(GpuMesh? mesh) => mesh?.Light == null || mesh.BakedStamp != -1;

    /// <summary>The light stamp <see cref="PrepareLightmap"/> takes for a chunk (see <see cref="GridStore.LightStampOf"/>).</summary>
    public int LightStampOf(GridHandle? grid, ChunkPosition chunk) => grid == null ? 0 : _gridStore?.LightStampOf(grid, chunk) ?? 0;

    /// <summary>Bakes the light maps <see cref="PrepareLightmap"/> listed, ahead of this frame's draws.</summary>
    public void BakeLightmaps() => _lightmap.Bake(_gridStore!, _quadArena.Buffer, RayLightingSettings.Ambient);

    /// <summary>The meshes and 64-quad groups the last <see cref="BakeLightmaps"/> baked, and the shared light map
    /// buffer's use and size (words), for the debug panel.</summary>
    public (int Meshes, int Groups) LastLightmapBake => (_lightmap.LastMeshes, _lightmap.LastGroups);
    public const int LightmapQuadsPerGroup = QuadLightmap.QuadsPerGroup;
    public (long Used, long Capacity) LightmapArenaUse => (_lightmap.Arena.UsedElements, _lightmap.Arena.CapacityElements);

    /// <summary>The shared chunk quad buffer's size and use (quads), for reports.</summary>
    public (long Used, long Capacity) ChunkQuadArenaUse => (_quadArena.UsedElements, _quadArena.CapacityElements);

    /// <summary>How long the last <see cref="UploadChunkQuads"/> spent creating its buffer and writing it (ms), for
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
    /// Draws <paramref name="mesh"/>'s triangles with the general mesh pipeline (vertex colours, back faces culled),
    /// depth-tested like the world, or as a wireframe while <see cref="WireframeMode"/> is on. For small dynamic shapes
    /// such as a grapple rope.
    /// </summary>
    public void DrawMesh(GpuMesh mesh, in Mat4 model)
    {
        if (WireframeMode) { DrawMeshWireframe(mesh, model); return; }
        if (_drawIndex >= MaxObjects) return;

        uint dynOffset = StageModel(ModelUniform.Default(model));
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset); _chunkStateBound = false;
        _api.RenderPassEncoderSetVertexBuffer(_pass, 0, mesh.VertexBuffer.Handle, mesh.VertexOffset, mesh.VertexBytes);
        SetPipeline(_pipeline);
        _api.RenderPassEncoderSetIndexBuffer(_pass, mesh.IndexBuffer.Handle, mesh.IndexFormat, mesh.IndexOffset, mesh.IndexBytes);
        _api.RenderPassEncoderDrawIndexed(_pass, mesh.IndexCount, 1, 0, 0, 0);
        _drawIndex++;
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
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset); _chunkStateBound = false;
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
            _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset); _chunkStateBound = false;
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
            _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset); _chunkStateBound = false;
            _api.RenderPassEncoderSetVertexBuffer(_pass, 0, tile.Instances.Handle, 0, tile.Instances.SizeBytes);
            _api.RenderPassEncoderDraw(_pass, 36, tile.Count, 0, 0);
            _drawIndex++;
        }
        SetPipeline(WireframeMode ? _wireframePipeline : _pipeline);
    }

    /// <summary>Draws the first <paramref name="count"/> particles in <paramref name="instances"/> (see
    /// <see cref="ParticleRenderer"/>) as camera-facing squares, depth-tested and depth-writing like the world.</summary>
    public void DrawParticles(GpuBuffer instances, uint count)
    {
        if (OverdrawMode || count == 0 || _drawIndex >= MaxObjects) return;
        SetPipeline(_particlePipeline);
        uint dynOffset = StageModel(ModelUniform.Default(Mat4.Identity));
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset); _chunkStateBound = false;
        _api.RenderPassEncoderSetVertexBuffer(_pass, 0, instances.Handle, 0, instances.SizeBytes);
        _api.RenderPassEncoderDraw(_pass, 6, count, 0, 0);
        _drawIndex++;
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
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset); _chunkStateBound = false;
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
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset); _chunkStateBound = false;
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
        _chunkDrawCount = 0;
        ChunkQuadsDrawn = _quadsDrawn;
        ChunkQuadsSkipped = _quadsSkipped;
        _quadsDrawn = _quadsSkipped = 0;
        _chunkStateBound = false;
        EnsureVoxelBindGroup();
        // The flat ambient is baked into the light maps where there's no light storage: rebake them all when it moves.
        if (RayLightingSettings.Ambient != _lightmapAmbient)
        {
            _lightmapAmbient = RayLightingSettings.Ambient;
            _lightmapEpoch++;
        }

        var encDesc = new CommandEncoderDescriptor();
        _encoder = _api.DeviceCreateCommandEncoder(_ctx.Device, &encDesc);
        _splitting = SplitPassTimers;
        BeginPass(_splitting ? "Render: before the world" : "Render pass (world, sky, HUD, ImGui)", first: true);
        return true;
    }

    /// <summary>Debug: <see cref="Section"/> ends the render pass and goes on in a new one, so the GPU timings show each
    /// part of the frame on its own line. Each extra pass costs a little, so the parts add up to a bit more.</summary>
    public bool SplitPassTimers { get; set; }
    private bool _splitting; // SplitPassTimers as of this frame's BeginFrame

    /// <summary>With <see cref="SplitPassTimers"/>, ends this part of the render pass and times what's drawn next as
    /// <paramref name="name"/>; otherwise does nothing.</summary>
    public void Section(string name)
    {
        if (!_splitting || _pass == null) return;
        EndPass(resolve: false);
        BeginPass(name, first: false);
    }

    /// <summary>Opens the frame's render pass (<paramref name="first"/>: clearing the targets), or, splitting it, the
    /// next part of it, drawing over what the last part left.</summary>
    private void BeginPass(string name, bool first)
    {
        // With MSAA the pass draws into the multisampled target and resolves it into the swapchain image at the end; the
        // samples themselves needn't be kept.
        bool msaa = _ctx.MsaaView != null;
        var colorAtt = new RenderPassColorAttachment
        {
            View = msaa ? _ctx.MsaaView : _ctx.CurrentView,
            // Split into parts, the samples are kept for the next part, and resolved by an empty last one (EndPass).
            ResolveTarget = msaa && !_splitting ? _ctx.CurrentView : null,
            DepthSlice = uint.MaxValue, // WGPU_DEPTH_SLICE_UNDEFINED
            LoadOp = first ? LoadOp.Clear : LoadOp.Load,
            StoreOp = msaa && !_splitting ? StoreOp.Discard : StoreOp.Store,
            // Sky blue; DrawSky paints over whatever the world leaves uncovered.
            ClearValue = OverdrawMode ? new Color { R = 0, G = 0, B = 0, A = 1 } : new Color { R = 0.10, G = 0.3078, B = 0.4804, A = 1.0 },
        };
        var depthAtt = new RenderPassDepthStencilAttachment
        {
            View = _ctx.DepthView,
            DepthLoadOp = first ? LoadOp.Clear : LoadOp.Load,
            DepthStoreOp = StoreOp.Store,
            DepthClearValue = 0.0f, // reversed depth: 0 is the far plane
        };
        var passDesc = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAtt,
            DepthStencilAttachment = &depthAtt,
        };
        if (_ctx.Timer.TimeRender(name, out var tsw)) passDesc.TimestampWrites = &tsw;
        _pass = _api.CommandEncoderBeginRenderPass(_encoder, &passDesc);
        _boundPipeline = null;
        _chunkStateBound = false;
        SetPipeline(WireframeMode ? _wireframePipeline : _pipeline);
        _api.RenderPassEncoderSetBindGroup(_pass, 0, _cameraBindGroup, 0, null);
        // Groups 2 (voxel storage) and 3 (block texture array) are the same for every draw and persist across
        // pipeline switches (all pipelines share the layout), so bind them once here.
        _api.RenderPassEncoderSetBindGroup(_pass, 2, _voxelBindGroup, 0, null);
        _api.RenderPassEncoderSetBindGroup(_pass, 3, _atlasBindGroup, 0, null);
    }

    private void EndPass(bool resolve)
    {
        _api.RenderPassEncoderEnd(_pass);
        _api.RenderPassEncoderRelease(_pass);
        _pass = null;
        if (!resolve || _ctx.MsaaView == null) return;
        // Split into parts with MSAA: an empty pass resolves the samples into the swapchain image.
        var colorAtt = new RenderPassColorAttachment
        {
            View = _ctx.MsaaView, ResolveTarget = _ctx.CurrentView, DepthSlice = uint.MaxValue,
            LoadOp = LoadOp.Load, StoreOp = StoreOp.Discard,
        };
        var passDesc = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &colorAtt };
        var pass = _api.CommandEncoderBeginRenderPass(_encoder, &passDesc);
        _api.RenderPassEncoderEnd(pass);
        _api.RenderPassEncoderRelease(pass);
    }

    public void SetCameraUniform(in CameraUniform camera)
    {
        if (_pendingTint is { } tint) { UploadTint(tint.X, tint.Z, tint.Texels); _pendingTint = null; }
        Span<CameraUniform> s = stackalloc CameraUniform[1];
        s[0] = camera;
        _cameraPosition = camera.CameraPosition;
        s[0].TintOriginX = _tintOriginX;
        s[0].TintOriginZ = _tintOriginZ;
        _cameraBuffer.Write<CameraUniform>(0, s);
    }

    /// <summary>The grid a chunk is drawn with to be lit as open air (ambient and sun), when it has no voxel light to
    /// show (see ChunkRenderSystem).</summary>
    public const int OpenAirGrid = -2;

    /// <summary>
    /// Draws a chunk mesh lit from grid <paramref name="grid"/> (its <see cref="GridHandle.Index"/>; -1 draws it
    /// full-bright, <see cref="OpenAirGrid"/> lit as open air). <paramref name="chunk"/> is the chunk's coordinate in that grid.
    /// <paramref name="dissolve"/> (here and in the other chunk draws), while one chunk swaps for another: 0 draws it
    /// whole; d (0-1) leaves out that share of its pixels in an even screen dither (going away); -d draws only that
    /// share, the very pixels d leaves out (coming in).
    /// </summary>
    public void DrawChunkMesh(GpuMesh mesh, in Mat4 model, int grid, ChunkPosition chunk, float dissolve = 0f)
        => DrawChunk(mesh, model, grid, chunk, dissolve != 0f ? _chunkDissolvePipeline : _chunkPipeline, dissolve);

    /// <summary>
    /// Lays down a chunk's cut-out mesh's depth (<see cref="ChunkRenderData.CutoutMesh"/>), minus its texels under half
    /// alpha, so <see cref="DrawCutoutChunkMesh"/> lights only the nearest cut-out face at each pixel. Call for every
    /// visible chunk's cut-out mesh after the opaque chunk meshes. Skipped in the wireframe and overdraw views.
    /// </summary>
    public void DrawCutoutChunkDepth(GpuMesh mesh, in Mat4 model, int grid, ChunkPosition chunk, float dissolve = 0f)
    {
        if (WireframeMode || OverdrawMode) return;
        DrawChunk(mesh, model, grid, chunk, _chunkCutoutDepthPipeline, dissolve);
    }

    /// <summary>
    /// Draws a chunk's cut-out mesh (<see cref="ChunkRenderData.CutoutMesh"/>) like <see cref="DrawChunkMesh"/>, where
    /// <see cref="DrawCutoutChunkDepth"/> laid it down (so minus its texels under half alpha). Call after every visible
    /// chunk's cut-out depth.
    /// </summary>
    public void DrawCutoutChunkMesh(GpuMesh mesh, in Mat4 model, int grid, ChunkPosition chunk, float dissolve = 0f)
        => DrawChunk(mesh, model, grid, chunk, _chunkCutoutShadedPipeline, dissolve);

    /// <summary>
    /// Lays down a chunk's transparent mesh's depth (<see cref="ChunkRenderData.TransparentMesh"/>), so
    /// <see cref="DrawTransparentChunkMesh"/> draws only the nearest transparent face at each pixel. Call for every
    /// visible chunk's transparent mesh first. Skipped in the wireframe and overdraw views.
    /// </summary>
    public void DrawTransparentChunkDepth(GpuMesh mesh, in Mat4 model, int grid, ChunkPosition chunk, float dissolve = 0f)
    {
        if (WireframeMode || OverdrawMode) return;
        DrawChunk(mesh, model, grid, chunk, _chunkTransparentDepthPipeline, dissolve);
    }

    /// <summary>
    /// Draws a chunk's transparent mesh alpha-blended over everything drawn so far, where it's the nearest transparent
    /// face (see <see cref="DrawTransparentChunkDepth"/>). Call after the opaque world and <see cref="DrawSky"/> (the sky
    /// only fills pixels with no depth, so it would paint over them).
    /// </summary>
    public void DrawTransparentChunkMesh(GpuMesh mesh, in Mat4 model, int grid, ChunkPosition chunk, float dissolve = 0f)
        => DrawChunk(mesh, model, grid, chunk, _chunkTransparentPipeline, dissolve);

    private void DrawChunk(GpuMesh mesh, in Mat4 model, int grid, ChunkPosition chunk, RenderPipeline* solidPipeline,
                           float dissolve = 0f)
    {
        if (_chunkDrawCount >= MaxChunkDraws || mesh.QuadCount == 0) return;

        // Chunk meshes are packed quads (ChunkQuad) in the shared quad buffer: their own pipeline, set once for a run of
        // chunk draws. The wireframe draws the same quads as line loops.
        bool wire = WireframeMode;
        var pipeline = OverdrawMode ? _chunkOverdrawPipeline : wire ? _chunkWireframePipeline : solidPipeline;
        if (_boundPipeline != pipeline) SetPipeline(pipeline);
        if (!_chunkStateBound)
        {
            EnsureChunkGroup();
            _api.RenderPassEncoderSetBindGroup(_pass, 1, _chunkGroup, 0, null);
            _api.RenderPassEncoderSetIndexBuffer(_pass, _quadIndices.Handle, IndexFormat.Uint32, 0, _quadIndices.SizeBytes);
            _chunkStateBound = true;
        }

        uint record = (uint)_chunkDrawCount++;
        _chunkDrawStaging[record] = new ChunkDrawRecord
        {
            Model = model, ChunkX = chunk.X, ChunkY = chunk.Y, ChunkZ = chunk.Z, Grid = grid, QuadBase = mesh.QuadBase,
            LightBase = mesh.Light != null ? mesh.LightBase : uint.MaxValue, Dissolve = dissolve,
        };
        if (wire && !OverdrawMode) { _api.RenderPassEncoderDraw(_pass, 8u * mesh.QuadCount, 1, 0, record); return; }

        // Back-face culled pipelines skip the face groups that can't point at the camera (see FacingCamera); each run of
        // groups that can is one draw from its range of the shared index buffer (vertex index 4q + corner).
        uint total = System.Math.Min(mesh.QuadCount, (uint)MaxChunkQuads);
        int[]? ends = mesh.FaceEnds;
        bool cull = SkipFacingAway && !OverdrawMode && ends != null && pipeline != _chunkTransparentPipeline
                    && pipeline != _chunkTransparentDepthPipeline;
        int mask = cull ? FacingCamera(model) | 1 << 6 : 0x7F;
        if (mask == 0x7F || ends == null)
        {
            _api.RenderPassEncoderDrawIndexed(_pass, 6u * total, 1, 0, 0, record);
            _quadsDrawn += total;
            return;
        }
        uint drawn = 0;
        for (int f = 0; f < GpuMesh.FaceGroups;)
        {
            if ((mask & (1 << f)) == 0) { f++; continue; }
            int g = f;
            while (g + 1 < GpuMesh.FaceGroups && (mask & (1 << (g + 1))) != 0) g++;
            uint start = System.Math.Min(f == 0 ? 0u : (uint)ends[f - 1], total), end = System.Math.Min((uint)ends[g], total);
            if (end > start)
            {
                _api.RenderPassEncoderDrawIndexed(_pass, 6u * (end - start), 1, 6u * start, 0, record);
                drawn += end - start;
            }
            f = g + 1;
        }
        _quadsDrawn += drawn;
        _quadsSkipped += total - drawn;
    }

    /// <summary>Debug (panel): skip the chunk quads facing away from the camera (see <see cref="FacingCamera"/>).</summary>
    public bool SkipFacingAway { get; set; } = true;

    /// <summary>Which face groups of a chunk (bit f: +X, -X, +Y, -Y, +Z, -Z) can point at the camera: a +X face sits on a
    /// plane x = 1..32 of the chunk (its local space), so none of them faces the camera unless it's at x > 0; a -X face on
    /// x = 0..31, so only if it's at x < 32; and so on. The camera is brought into the chunk's space through the
    /// transform's columns (rotation and uniform scale, as chunk transforms are).</summary>
    private int FacingCamera(in Mat4 m)
    {
        var d = _cameraPosition - new Vector3D<float>(m.M12, m.M13, m.M14);
        var c0 = new Vector3D<float>(m.M0, m.M1, m.M2);
        var c1 = new Vector3D<float>(m.M4, m.M5, m.M6);
        var c2 = new Vector3D<float>(m.M8, m.M9, m.M10);
        float x = Vector3D.Dot(c0, d) / c0.LengthSquared, y = Vector3D.Dot(c1, d) / c1.LengthSquared,
              z = Vector3D.Dot(c2, d) / c2.LengthSquared;
        const float S = ChunkData.Size;
        return (x > 0 ? 1 : 0) | (x < S ? 2 : 0) | (y > 0 ? 4 : 0) | (y < S ? 8 : 0) | (z > 0 ? 16 : 0) | (z < S ? 32 : 0);
    }

    /// <summary>Chunk quads drawn last frame, and skipped as facing away from the camera (for the panel).</summary>
    public long ChunkQuadsDrawn { get; private set; }
    public long ChunkQuadsSkipped { get; private set; }
    private long _quadsDrawn, _quadsSkipped;
    private Vector3D<float> _cameraPosition;

    /// <summary>The chunk draws' group-1 bind group over this frame's records, the shared quad buffer and the shared
    /// light map buffer, rebuilt when either buffer has been replaced (grown).</summary>
    private void EnsureChunkGroup()
    {
        var light = _lightmap.Arena;
        if (_chunkGroup != null && _chunkGroupVersion == _quadArena.Version && _chunkGroupLightVersion == light.Version) return;
        if (_chunkGroup != null) _api.BindGroupRelease(_chunkGroup);
        BindGroupEntry* entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 2, Buffer = _chunkDrawBuffer.Handle, Offset = 0, Size = _chunkDrawBuffer.SizeBytes };
        entries[1] = new BindGroupEntry { Binding = 3, Buffer = _quadArena.Buffer.Handle, Offset = 0, Size = _quadArena.Buffer.SizeBytes };
        entries[2] = new BindGroupEntry { Binding = 4, Buffer = light.Buffer.Handle, Offset = 0, Size = light.Buffer.SizeBytes };
        var desc = new BindGroupDescriptor { Layout = _chunkModelLayout, EntryCount = 3, Entries = entries };
        _chunkGroup = _api.DeviceCreateBindGroup(_ctx.Device, &desc);
        _chunkGroupVersion = _quadArena.Version;
        _chunkGroupLightVersion = light.Version;
    }

    // A chunk draw's record (vs_chunk's ChunkDraw: 96 bytes).
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 96)]
    private struct ChunkDrawRecord
    {
        public Mat4 Model;
        public int ChunkX, ChunkY, ChunkZ, Grid;
        public uint QuadBase, LightBase;
        public float Dissolve;
    }

    /// <summary>Stages this draw's model uniform for <see cref="EndFrame"/>'s single upload; returns its dynamic offset.</summary>
    private uint StageModel(in ModelUniform u)
    {
        _modelStaging[_drawIndex] = u;
        return (uint)((ulong)_drawIndex * ModelStride);
    }

    public void EndFrame()
    {
        EndPass(resolve: _splitting);

        // One upload for every draw's model uniform (queue writes land before the submit below), instead of a
        // QueueWriteBuffer per draw.
        if (_drawIndex > 0) _modelBuffer.Write<ModelUniform>(0, _modelStaging.AsSpan(0, _drawIndex));
        if (_chunkDrawCount > 0) _chunkDrawBuffer.Write<ChunkDrawRecord>(0, _chunkDrawStaging.AsSpan(0, _chunkDrawCount));

        _ctx.Timer.Resolve(_encoder);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = _api.CommandEncoderFinish(_encoder, &cmdDesc);
        _ctx.FlushPending();
        _api.QueueSubmit(_ctx.Queue, 1, &cmd);
        _api.CommandBufferRelease(cmd);
        _api.CommandEncoderRelease(_encoder);
        _encoder = null;
        _ctx.Timer.AfterSubmit();

        long t0 = Stopwatch.GetTimestamp();
        _ctx.Present();
        PresentMs = Ema(PresentMs, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        DrawCount = _drawIndex + _chunkDrawCount;
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
        _blockTable.Dispose();
        _hudCameraBuffer.Dispose();
        _modelBuffer.Dispose();
        _quadIndices.Dispose();
        if (_chunkGroup != null) _api.BindGroupRelease(_chunkGroup);
        _chunkDrawBuffer.Dispose();
        _quadArena.Dispose();
        _lightmap.Dispose();
        if (_voxelBindGroup   != null) _api.BindGroupRelease(_voxelBindGroup);
        if (_tintSampler      != null) _api.SamplerRelease(_tintSampler);
        if (_tintView         != null) _api.TextureViewRelease(_tintView);
        if (_tintTexture      != null) _api.TextureRelease(_tintTexture);
        if (_atlasBindGroup   != null) _api.BindGroupRelease(_atlasBindGroup);
        if (_atlasSampler     != null) _api.SamplerRelease(_atlasSampler);
        if (_atlasTextureView != null) _api.TextureViewRelease(_atlasTextureView);
        if (_atlasTexture     != null) _api.TextureRelease(_atlasTexture);
        if (_modelPipeline      != null) _api.RenderPipelineRelease(_modelPipeline);
        if (_cloudPipeline      != null) _api.RenderPipelineRelease(_cloudPipeline);
        if (_particlePipeline   != null) _api.RenderPipelineRelease(_particlePipeline);
        if (_skyPipeline        != null) _api.RenderPipelineRelease(_skyPipeline);
        if (_hudPipeline        != null) _api.RenderPipelineRelease(_hudPipeline);
        if (_wireframePipeline  != null) _api.RenderPipelineRelease(_wireframePipeline);
        if (_pipeline           != null) _api.RenderPipelineRelease(_pipeline);
        if (_chunkPipeline      != null) _api.RenderPipelineRelease(_chunkPipeline);
        if (_chunkDissolvePipeline != null) _api.RenderPipelineRelease(_chunkDissolvePipeline);
        if (_chunkWireframePipeline != null) _api.RenderPipelineRelease(_chunkWireframePipeline);
        if (_chunkOverdrawPipeline != null) _api.RenderPipelineRelease(_chunkOverdrawPipeline);
        if (_chunkCutoutDepthPipeline != null) _api.RenderPipelineRelease(_chunkCutoutDepthPipeline);
        if (_chunkCutoutShadedPipeline != null) _api.RenderPipelineRelease(_chunkCutoutShadedPipeline);
        if (_chunkTransparentPipeline != null) _api.RenderPipelineRelease(_chunkTransparentPipeline);
        if (_chunkTransparentDepthPipeline != null) _api.RenderPipelineRelease(_chunkTransparentDepthPipeline);
    }
}
