using System.Runtime.InteropServices;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// A voxel-space sub-box of a volume that a flood is confined to. Origin + size are in voxels. The flood is
/// scoped to this region (Phase 4.6 culling): only its voxels are re-seeded and re-relaxed; everything
/// outside is left untouched and acts as a stable boundary condition.
/// </summary>
internal readonly record struct FloodRegion(int Ox, int Oy, int Oz, int Sx, int Sy, int Sz);

/// <summary>
/// GPU light flood for a <see cref="VolumeGpuResources"/>, scoped to a <see cref="FloodRegion"/>. Each cycle:
///   1. <b>Ambient sky sweep (clear mode)</b> — the −Y (top-down) sweep walks each full column in the
///      region's XZ footprint, writing <see cref="VolumeGpuResources.BaseSkyLevel"/> through open air
///      (0 behind the first solid). In clear mode it <i>overwrites</i> each cell (sky = swept value, block
///      channel zeroed), which is what resets the region for a re-flood — max-relaxation alone can never
///      lower a value, so removal (a deleted lamp / a new occluder) only works if the region is reset first.
///   2. <b>Emitter scatter</b> — one thread per in-region emitter writes its emission into the block channel
///      (preserving the just-written sky). Emitters are a sparse per-volume list, not a dense buffer.
///   3. <b>Relaxation</b> — ping-pong max-relaxation over the region, bleeding sky into pockets and
///      propagating block light. A full LightA→LightB copy first makes both ping-pong buffers agree outside
///      the region so the region reads correct, stable border values on every pass. Result lands in LightA.
///
/// Sky (bits 0-7) and block (bits 8-15) are derived entirely on the GPU from the chunk-major opacity bitset
/// + sparse emitter list; no CPU light values are consumed. Direct sun is a separate renderer term.
///
/// <para><b>Why the region is full-height in Y:</b> sky occlusion is a vertical-column effect — one new
/// block shadows its whole column below — so the region spans the full volume height and is culled only in
/// X/Z (the dirty chunk footprint + a one-chunk lateral margin ≥ the max propagation radius of 15).</para>
/// </summary>
internal sealed class GpuLightFlood : IDisposable
{
    // 16 passes → max propagation radius 15 (max emission, and >= BaseSkyLevel for sky ambient bleed).
    // Even pass count → result lands in LightA.
    private const int Passes = 16;

    private const int WordsPerChunk = VolumeGpuResources.WordsPerChunk; // 1024

    // ── Ambient sky sweep: one thread per line along the swept axis, loops along it ───────────────
    // Sweep { axis, fromMax, clear, level } selects the direction, whether to overwrite (clear) or max-merge,
    // and the sky level injected from the sky-facing end. To keep ambient correct as a grid rotates, world-up
    // (in the volume's local frame) is decomposed into up to three axis sweeps, each weighted by alignment and
    // max-merged (see GpuLightFlood.SkySweeps); for the static world this is the single +Y top-down sweep.
    // Region scopes which lines run and which cells are written.
    private static readonly string SweepWgsl = @"
const WPC: i32 = " + WordsPerChunk + @";

struct Dims   { w: u32, h: u32, d: u32, pad: u32 };
struct Sweep  { axis: u32, fromMax: u32, clear: u32, level: u32 };
struct Region { ox: u32, oy: u32, oz: u32, count: u32, sx: u32, sy: u32, sz: u32, pad: u32 };

@group(0) @binding(0) var<storage, read_write> light:   array<u32>;
@group(0) @binding(1) var<storage, read>       opacity: array<u32>;
@group(0) @binding(2) var<storage, read>       dims:    Dims;
@group(0) @binding(3) var<storage, read>       sweep:   Sweep;
@group(0) @binding(4) var<storage, read>       region:  Region;

fn idx3(x: i32, y: i32, z: i32) -> i32 {
    return x + i32(dims.w) * (y + i32(dims.h) * z);
}

// Chunk-major opacity: slot = cx + DX*(cy + DY*cz); word within chunk = (ly + 32*lz); bit = lx. (S=32.)
fn isOpaque(x: i32, y: i32, z: i32) -> bool {
    let dx   = i32(dims.w) >> 5;
    let dy   = i32(dims.h) >> 5;
    let slot = (x >> 5) + dx * ((y >> 5) + dy * (z >> 5));
    let word = slot * WPC + ((y & 31) + 32 * (z & 31));
    let bit  = u32(x & 31);
    return ((opacity[u32(word)] >> bit) & 1u) == 1u;
}

fn inRegion(x: i32, y: i32, z: i32) -> bool {
    return x >= i32(region.ox) && x < i32(region.ox + region.sx) &&
           y >= i32(region.oy) && y < i32(region.oy + region.sy) &&
           z >= i32(region.oz) && z < i32(region.oz + region.sz);
}

@compute @workgroup_size(8, 8, 1)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let a = sweep.axis;

    // (len) = extent along the swept axis; (u,v) index the 2D grid of lines, offset into the region.
    var len: i32; var u0: i32; var uN: i32; var v0: i32; var vN: i32;
    if (a == 0u)      { len = i32(dims.w); u0 = i32(region.oy); uN = i32(region.oy + region.sy); v0 = i32(region.oz); vN = i32(region.oz + region.sz); }
    else if (a == 1u) { len = i32(dims.h); u0 = i32(region.ox); uN = i32(region.ox + region.sx); v0 = i32(region.oz); vN = i32(region.oz + region.sz); }
    else              { len = i32(dims.d); u0 = i32(region.ox); uN = i32(region.ox + region.sx); v0 = i32(region.oy); vN = i32(region.oy + region.sy); }

    let u = u0 + i32(gid.x);
    let v = v0 + i32(gid.y);
    if (u >= uN || v >= vN) { return; }

    // Walk the FULL line (so occlusion above the region is accounted for) but only write inside the region.
    var cur: u32 = sweep.level;
    for (var s: i32 = 0; s < len; s = s + 1) {
        let p = select(s, len - 1 - s, sweep.fromMax == 1u);
        var x: i32; var y: i32; var z: i32;
        if (a == 0u)      { x = p; y = u; z = v; }
        else if (a == 1u) { x = u; y = p; z = v; }
        else              { x = u; y = v; z = p; }

        if (isOpaque(x, y, z)) { cur = 0u; }
        if (inRegion(x, y, z)) {
            let i = u32(idx3(x, y, z));
            if (sweep.clear == 1u) {
                light[i] = cur;                                       // sky = cur, block channel zeroed (reset)
            } else {
                let prevSky = light[i] & 0xFFu;
                light[i] = (light[i] & 0xFFFFFF00u) | max(prevSky, cur);
            }
        }
    }
}";

    // ── Emitter scatter: one thread per emitter, sets the block channel (sky preserved) ──────────────
    private static readonly string ScatterWgsl = @"
struct Region { ox: u32, oy: u32, oz: u32, count: u32, sx: u32, sy: u32, sz: u32, pad: u32 };

@group(0) @binding(0) var<storage, read_write> light:    array<u32>;
@group(0) @binding(1) var<storage, read>       emitters: array<u32>;   // pairs: [voxelIndex, level, ...]
@group(0) @binding(2) var<storage, read>       region:   Region;

@compute @workgroup_size(64, 1, 1)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let e = gid.x;
    if (e >= region.count) { return; }
    let idx = emitters[e * 2u];
    let lvl = emitters[e * 2u + 1u];
    light[idx] = (light[idx] & 0xFFu) | (lvl << 8u);
}";

    // ── Relaxation pass: uniform max-relaxation for both sky and block, losing 1 per step, region-scoped ──
    private static readonly string FloodWgsl = @"
const WPC: i32 = " + WordsPerChunk + @";

struct Dims   { w: u32, h: u32, d: u32, pad: u32 };
struct Region { ox: u32, oy: u32, oz: u32, count: u32, sx: u32, sy: u32, sz: u32, pad: u32 };

@group(0) @binding(0) var<storage, read>       src:     array<u32>;
@group(0) @binding(1) var<storage, read_write> dst:     array<u32>;
@group(0) @binding(2) var<storage, read>       opacity: array<u32>;
@group(0) @binding(3) var<storage, read>       dims:    Dims;
@group(0) @binding(4) var<storage, read>       region:  Region;

fn idx3(x: i32, y: i32, z: i32) -> i32 {
    return x + i32(dims.w) * (y + i32(dims.h) * z);
}

fn inVol(x: i32, y: i32, z: i32) -> bool {
    return x >= 0 && x < i32(dims.w) &&
           y >= 0 && y < i32(dims.h) &&
           z >= 0 && z < i32(dims.d);
}

fn isOpaque(x: i32, y: i32, z: i32) -> bool {
    let dx   = i32(dims.w) >> 5;
    let dy   = i32(dims.h) >> 5;
    let slot = (x >> 5) + dx * ((y >> 5) + dy * (z >> 5));
    let word = slot * WPC + ((y & 31) + 32 * (z & 31));
    let bit  = u32(x & 31);
    return ((opacity[u32(word)] >> bit) & 1u) == 1u;
}

fn blockAt(x: i32, y: i32, z: i32) -> u32 {
    if (!inVol(x, y, z)) { return 0u; }
    return (src[u32(idx3(x, y, z))] >> 8u) & 0xFFu;
}

fn skyAt(x: i32, y: i32, z: i32) -> u32 {
    if (!inVol(x, y, z)) { return 0u; }
    return src[u32(idx3(x, y, z))] & 0xFFu;
}

@compute @workgroup_size(4, 4, 4)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let x = i32(region.ox) + i32(gid.x);
    let y = i32(region.oy) + i32(gid.y);
    let z = i32(region.oz) + i32(gid.z);
    if (x >= i32(region.ox + region.sx) ||
        y >= i32(region.oy + region.sy) ||
        z >= i32(region.oz + region.sz)) { return; }
    if (!inVol(x, y, z)) { return; }
    let i = idx3(x, y, z);

    let sv  = src[u32(i)];
    let sky = sv & 0xFFu;
    let blk = (sv >> 8u) & 0xFFu;

    // Solid voxels: sky=0 inside solid, keep seeded emission. No relay.
    if (isOpaque(x, y, z)) {
        dst[u32(i)] = blk << 8u;
        return;
    }

    // ── Block channel: max-relaxation, loses 1 per step in all directions ──
    let nb = max(max(max(blockAt(x+1,y,z), blockAt(x-1,y,z)),
                     max(blockAt(x,y+1,z), blockAt(x,y-1,z))),
                 max(blockAt(x,y,z+1), blockAt(x,y,z-1)));
    var b = blk;
    if (nb > 0u) { b = max(b, nb - 1u); }

    // ── Sky channel: uniform max-relaxation across all 6 neighbours (bleeds ambient into pockets) ──
    let ns = max(max(max(skyAt(x+1,y,z), skyAt(x-1,y,z)),
                     max(skyAt(x,y+1,z), skyAt(x,y-1,z))),
                 max(skyAt(x,y,z+1), skyAt(x,y,z-1)));
    var s = sky;
    if (ns > 0u) { s = max(s, ns - 1u); }

    dst[u32(i)] = s | (b << 8u);
}";

    // ── Cross-volume injection (Phase 4.4): depth-tested point-lamp light from another volume ─────────
    // Per voxel in the lamp's AABB: transform voxel-centre → world, attenuate by distance (1/voxel, matching
    // the flood), then find the cube face whose frustum owns the lamp→voxel direction and hard depth-test
    // against that face's stored nearest-caster depth. Visible → write into the block channel (max), preserving
    // the swept sky channel. Occluded → skip (the source volume's hull, the target's own walls, and every other
    // grid are all in the cube map, so the shadow is cross-volume exact). The relaxation pass then fills pockets.
    private static readonly string InjectWgsl = @"
struct Params {
    voxelToWorld: mat4x4<f32>,
    faceVP:       array<mat4x4<f32>, 6>,
    lampWorld:    vec4<f32>,   // xyz lamp position
    region:       vec4<i32>,   // ox, oy, oz, mapSize
    sizev:        vec4<i32>,   // sx, sy, sz, _
    vol:          vec4<i32>,   // VW, VH, VD, _
    misc:         vec4<f32>,   // radius, level, bias, _
};

@group(0) @binding(0) var<storage, read_write> light:   array<u32>;
@group(0) @binding(1) var                      distMap: texture_2d_array<f32>;
@group(0) @binding(2) var<uniform>             p:       Params;

const SELF_RADIUS: f32 = 1.0; // ignore occluders within this of the lamp (its own block walls)

@compute @workgroup_size(4, 4, 4)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let x = p.region.x + i32(gid.x);
    let y = p.region.y + i32(gid.y);
    let z = p.region.z + i32(gid.z);
    if (x >= p.region.x + p.sizev.x || y >= p.region.y + p.sizev.y || z >= p.region.z + p.sizev.z) { return; }
    if (x < 0 || x >= p.vol.x || y < 0 || y >= p.vol.y || z < 0 || z >= p.vol.z) { return; }

    let vc    = vec3<f32>(f32(x) + 0.5, f32(y) + 0.5, f32(z) + 0.5);
    let world = (p.voxelToWorld * vec4<f32>(vc, 1.0)).xyz;
    let dist  = length(world - p.lampWorld.xyz);
    if (dist > p.misc.x) { return; }                       // outside reach

    let contrib = i32(round(p.misc.y - dist));             // attenuation, 1 per voxel
    if (contrib <= 0) { return; }

    let mapSize = f32(p.region.w);
    var lit = false;
    for (var f = 0; f < 6; f = f + 1) {
        let clip = p.faceVP[f] * vec4<f32>(world, 1.0);
        if (clip.w <= 0.0) { continue; }
        let ndc = clip.xyz / clip.w;
        if (ndc.x < -1.0 || ndc.x > 1.0 || ndc.y < -1.0 || ndc.y > 1.0 || ndc.z < 0.0 || ndc.z > 1.0) { continue; }
        let uv    = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
        let texel = clamp(vec2<i32>(uv * mapSize), vec2<i32>(0), vec2<i32>(i32(mapSize) - 1));
        let nearest = textureLoad(distMap, texel, f, 0).r; // nearest caster distance from the lamp
        // Occluded only if a real surface (past the lamp's own block) is closer than this voxel.
        let occluded = nearest > SELF_RADIUS && nearest < dist - p.misc.z;
        lit = !occluded;
        break;                                             // exactly one face owns this direction
    }
    if (!lit) { return; }

    let i   = u32(x + p.vol.x * (y + p.vol.y * z));
    let cur = (light[i] >> 8u) & 0xFFu;
    let nb  = u32(contrib);
    if (nb > cur) { light[i] = (light[i] & 0xFFu) | (nb << 8u); } // keep sky, set block channel
}";

    private readonly GpuContext      _ctx;
    private readonly ComputePipeline _skySweep;
    private readonly ComputePipeline _scatter;
    private readonly ComputePipeline _pipeline;
    private readonly ComputePipeline _inject;

    // Shared param buffers (rewritten per flood). Shared across volumes is safe: only one volume floods per
    // frame and the contents are set immediately before that volume's dispatches.
    private readonly GpuBuffer _sweepParam;  // (axis, fromMax, clear, pad)
    private readonly GpuBuffer _regionParam; // (ox, oy, oz, count, sx, sy, sz, pad)
    private readonly GpuBuffer _injectParam; // InjectParams (528 bytes), rewritten per injected lamp

    // Reusable CPU scratch for the emitter scatter list (pairs of voxelIndex, level). Grown lazily.
    private uint[] _emitterScratch = Array.Empty<uint>();

    public GpuLightFlood(GpuContext ctx)
    {
        _ctx         = ctx;
        _skySweep    = new ComputePipeline(ctx, SweepWgsl,   "main");
        _scatter     = new ComputePipeline(ctx, ScatterWgsl, "main");
        _pipeline    = new ComputePipeline(ctx, FloodWgsl,   "main");
        _inject      = new ComputePipeline(ctx, InjectWgsl,  "main");
        _sweepParam  = GpuBuffer.CreateStorage(ctx, 4 * sizeof(uint));
        _regionParam = GpuBuffer.CreateStorage(ctx, 8 * sizeof(uint));
        _injectParam = GpuBuffer.CreateUniform(ctx, (ulong)Marshal.SizeOf<InjectParams>());
    }

    /// <summary>
    /// Floods <paramref name="region"/> of <paramref name="vol"/>: sky sweep (clear) → emitter scatter →
    /// LightA→LightB copy → relaxation ping-pong. Result ends in LightA. For cross-volume light, call
    /// <see cref="PrepareRegion"/>, then <see cref="Inject"/> once per reaching lamp, then
    /// <see cref="FinishRegion"/> instead — injection must land after the clear/scatter and before the relax.
    /// </summary>
    public void Flood(VolumeGpuResources vol, IEnumerable<KeyValuePair<ChunkPosition, ChunkEntry>> chunks,
                      FloodRegion region, Vector3D<float> localUp)
    {
        PrepareRegion(vol, chunks, region, localUp);
        FinishRegion(vol, region);
    }

    /// <summary>
    /// First half of a flood: gather + upload in-region emitters, write the region params, ensure bind groups,
    /// run the ambient sky sweep (clear mode — this is what resets the region) and the emitter scatter. After
    /// this, LightA holds swept sky + local block seeds in the region; cross-volume <see cref="Inject"/> calls
    /// may add to the block channel before <see cref="FinishRegion"/> relaxes.
    /// </summary>
    public void PrepareRegion(VolumeGpuResources vol, IEnumerable<KeyValuePair<ChunkPosition, ChunkEntry>> chunks,
                              FloodRegion region, Vector3D<float> localUp)
    {
        int n = GatherEmitters(vol, chunks, region);
        vol.EnsureEmitterCapacity(System.Math.Max(n, 1));
        if (n > 0)
            vol.Emitters!.Write<uint>(0, _emitterScratch.AsSpan(0, n * 2));

        // Region params (count rides along for the scatter pass).
        Span<uint> r = stackalloc uint[8]
        {
            (uint)region.Ox, (uint)region.Oy, (uint)region.Oz, (uint)n,
            (uint)region.Sx, (uint)region.Sy, (uint)region.Sz, 0u,
        };
        _regionParam.Write<uint>(0, r);

        // Lazily (re)create bind groups; reset to 0 on resize / emitter grow.
        if (vol.SkySweepBind == 0)
            vol.SkySweepBind = _skySweep.CreateBindGroupHandle(
                new (uint, GpuBuffer)[] { (0u, vol.LightA), (1u, vol.Opacity), (2u, vol.Dims), (3u, _sweepParam), (4u, _regionParam) });
        if (vol.ScatterBind == 0 && vol.Emitters != null)
            vol.ScatterBind = _scatter.CreateBindGroupHandle(
                new (uint, GpuBuffer)[] { (0u, vol.LightA), (1u, vol.Emitters), (2u, _regionParam) });
        if (vol.FloodBindEven == 0)
            vol.FloodBindEven = _pipeline.CreateBindGroupHandle(
                new (uint, GpuBuffer)[] { (0u, vol.LightA), (1u, vol.LightB), (2u, vol.Opacity), (3u, vol.Dims), (4u, _regionParam) });
        if (vol.FloodBindOdd == 0)
            vol.FloodBindOdd = _pipeline.CreateBindGroupHandle(
                new (uint, GpuBuffer)[] { (0u, vol.LightB), (1u, vol.LightA), (2u, vol.Opacity), (3u, vol.Dims), (4u, _regionParam) });

        // Ambient sky sweep(s) along world-up (in this volume's frame). The first (strongest) sweep runs in
        // clear mode, resetting the region (sky overwritten, block zeroed); any others max-merge. This is what
        // makes grid ambient track world-up under rotation.
        SkySweeps(vol, region, localUp);

        // Scatter local emitters into the (now reset) region.
        if (n > 0)
            _scatter.Dispatch(vol.ScatterBind, CeilDiv((uint)n, 64u), 1u, 1u);
    }

    /// <summary>
    /// Second half of a flood: make both ping-pong buffers agree everywhere (so region border reads are stable),
    /// then run the relaxation ping-pong over the region. Result ends in LightA.
    /// </summary>
    public void FinishRegion(VolumeGpuResources vol, FloodRegion region)
    {
        _ctx.CopyBufferToBuffer(vol.LightA, vol.LightB, vol.LightA.SizeBytes);
        _pipeline.DispatchPingPong(vol.FloodBindEven, vol.FloodBindOdd,
            CeilDiv((uint)region.Sx, 4u), CeilDiv((uint)region.Sy, 4u), CeilDiv((uint)region.Sz, 4u), Passes);
    }

    /// <summary>
    /// Injects one cross-volume point lamp into <paramref name="vol"/>'s block channel using a depth test
    /// against its already-rendered cube map (<paramref name="depthArrayView"/>, a six-layer
    /// <c>texture_depth_2d_array</c>). Must be called between <see cref="PrepareRegion"/> and
    /// <see cref="FinishRegion"/>. <paramref name="voxelToWorld"/> maps a volume voxel centre to world space;
    /// <paramref name="faceVP"/> are the six face view-projections used to render the cube. The dispatch covers
    /// the lamp's reach AABB (origin <paramref name="ax"/>,<paramref name="ay"/>,<paramref name="az"/>, size
    /// <paramref name="sx"/>,<paramref name="sy"/>,<paramref name="sz"/>) in volume voxels.
    /// </summary>
    public void Inject(VolumeGpuResources vol, nint depthArrayView, in Mat4 voxelToWorld, ReadOnlySpan<Mat4> faceVP,
                       Vector3D<float> lampWorld, int level, float radius,
                       int ax, int ay, int az, int sx, int sy, int sz)
    {
        if (sx <= 0 || sy <= 0 || sz <= 0) return;

        var pr = new InjectParams
        {
            VoxelToWorld = voxelToWorld,
            F0 = faceVP[0], F1 = faceVP[1], F2 = faceVP[2], F3 = faceVP[3], F4 = faceVP[4], F5 = faceVP[5],
            LampX = lampWorld.X, LampY = lampWorld.Y, LampZ = lampWorld.Z, LampW = 0f,
            Ox = ax, Oy = ay, Oz = az, MapSize = (int)Rendering.WebGpu.LightShadowPass.MapSize,
            Sx = sx, Sy = sy, Sz = sz, SizePad = 0,
            VW = vol.VW, VH = vol.VH, VD = vol.VD, VolPad = 0,
            // Bias is a linear-distance tolerance (half a voxel): a surface must be at least this much closer
            // than the voxel to count as an occluder, so a lit voxel's own backing solid doesn't self-shadow it.
            Radius = radius, Level = level, Bias = 0.5f, MiscPad = 0f,
        };
        Span<InjectParams> sp = stackalloc InjectParams[1] { pr };
        _injectParam.Write<InjectParams>(0, sp);

        if (vol.InjectBind == 0)
            vol.InjectBind = _inject.CreateBindGroupHandle(
                new (uint, GpuBuffer)[] { (0u, vol.LightA), (2u, _injectParam) }, 1u, depthArrayView);

        _inject.Dispatch(vol.InjectBind, CeilDiv((uint)sx, 4u), CeilDiv((uint)sy, 4u), CeilDiv((uint)sz, 4u));
    }

    /// <summary>
    /// Fills <see cref="_emitterScratch"/> with (volume-voxel-index, level) pairs for every emitter in a
    /// chunk that overlaps <paramref name="region"/> laterally (the region is chunk-aligned in X/Z and
    /// full-height in Y, so a chunk is wholly in or out). Returns the emitter count.
    /// </summary>
    private int GatherEmitters(VolumeGpuResources vol, IEnumerable<KeyValuePair<ChunkPosition, ChunkEntry>> chunks, FloodRegion region)
    {
        int vw = vol.VW, vh = vol.VH;
        int n = 0;
        foreach (var (pos, entry) in chunks)
        {
            if (entry.Emitters.Count == 0) continue;
            if (!vol.Contains(pos)) continue;
            var (bx, by, bz) = vol.ChunkVoxelBase(pos);

            // Chunk overlaps the region in X and Z? (Y is always full-height.)
            if (bx + 32 <= region.Ox || bx >= region.Ox + region.Sx) continue;
            if (bz + 32 <= region.Oz || bz >= region.Oz + region.Sz) continue;

            foreach (var em in entry.Emitters)
            {
                int vi = (bx + em.Lx) + vw * ((by + em.Ly) + vh * (bz + em.Lz));
                EnsureScratch((n + 1) * 2);
                _emitterScratch[n * 2]     = (uint)vi;
                _emitterScratch[n * 2 + 1] = em.Level;
                n++;
            }
        }
        return n;
    }

    private void EnsureScratch(int len)
    {
        if (_emitterScratch.Length >= len) return;
        int cap = _emitterScratch.Length == 0 ? 128 : _emitterScratch.Length;
        while (cap < len) cap *= 2;
        Array.Resize(ref _emitterScratch, cap);
    }

    /// <summary>
    /// Runs the ambient sky sweep(s) for a volume. World-up expressed in the volume's local frame
    /// (<paramref name="localUp"/>) is split into its axis components; each axis with a meaningful component
    /// gets one sweep, entering from the face world-up points toward (<c>fromMax</c>) and injecting a level
    /// scaled by that component's magnitude. Sweeps are processed strongest-first: the first runs in clear mode
    /// (resetting the region — sky overwritten, block zeroed), the rest max-merge. For an axis-aligned up
    /// (the static world, or an unrotated grid) this is exactly one full-strength top-down sweep.
    /// </summary>
    private void SkySweeps(VolumeGpuResources vol, FloodRegion region, Vector3D<float> localUp)
    {
        Span<float> comp  = stackalloc float[3] { localUp.X, localUp.Y, localUp.Z };
        Span<int>   order = stackalloc int[3] { 0, 1, 2 };
        // Sort axes by |component| descending (3 elements → trivial selection sort).
        for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 3; j++)
                if (MathF.Abs(comp[order[j]]) > MathF.Abs(comp[order[i]]))
                    (order[i], order[j]) = (order[j], order[i]);

        bool first = true;
        foreach (int a in order)
        {
            float c = comp[a];
            uint level = (uint)MathF.Round(VolumeGpuResources.BaseSkyLevel * MathF.Abs(c));
            if (level == 0) continue;                    // negligible alignment with world-up → skip

            uint fromMax = c > 0f ? 1u : 0u;             // sky enters from the +axis face when up points +axis
            uint gx, gy;
            if (a == 0)      { gx = CeilDiv((uint)region.Sy, 8u); gy = CeilDiv((uint)region.Sz, 8u); }
            else if (a == 1) { gx = CeilDiv((uint)region.Sx, 8u); gy = CeilDiv((uint)region.Sz, 8u); }
            else             { gx = CeilDiv((uint)region.Sx, 8u); gy = CeilDiv((uint)region.Sy, 8u); }

            DispatchSweep(vol, (uint)a, fromMax, clear: first ? 1u : 0u, level: level, gx: gx, gy: gy);
            first = false;
        }
    }

    private void DispatchSweep(VolumeGpuResources vol, uint axis, uint fromMax, uint clear, uint level, uint gx, uint gy)
    {
        Span<uint> p = stackalloc uint[4] { axis, fromMax, clear, level };
        _sweepParam.Write<uint>(0, p);
        _skySweep.Dispatch(vol.SkySweepBind, gx, gy, 1u);
    }

    private static uint CeilDiv(uint a, uint b) => (a + b - 1u) / b;

    public void Dispose()
    {
        _skySweep.Dispose();
        _scatter.Dispose();
        _pipeline.Dispose();
        _inject.Dispose();
        _sweepParam.Dispose();
        _regionParam.Dispose();
        _injectParam.Dispose();
    }

    /// <summary>Uniform block for the injection pass (528 bytes). Field order/padding match the WGSL
    /// <c>Params</c> std140 layout: a mat4, six face mat4s, then five vec4s (vec4&lt;i32&gt; / vec4&lt;f32&gt;).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InjectParams
    {
        public Mat4 VoxelToWorld;
        public Mat4 F0, F1, F2, F3, F4, F5;
        public float LampX, LampY, LampZ, LampW;   // vec4<f32> lampWorld
        public int   Ox, Oy, Oz, MapSize;          // vec4<i32> region
        public int   Sx, Sy, Sz, SizePad;          // vec4<i32> sizev
        public int   VW, VH, VD, VolPad;           // vec4<i32> vol
        public float Radius, Level, Bias, MiscPad; // vec4<f32> misc
    }
}
