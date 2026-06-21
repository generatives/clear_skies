using ClearSkies.Engine.Rendering.WebGpu;

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
    // Sweep { axis, fromMax, clear } selects the direction and whether to overwrite (clear) or max-merge.
    // Region scopes which lines run and which cells are written.
    private static readonly string SweepWgsl = @"
const BASE_SKY: u32 = " + VolumeGpuResources.BaseSkyLevel + @"u;
const WPC: i32 = " + WordsPerChunk + @";

struct Dims   { w: u32, h: u32, d: u32, pad: u32 };
struct Sweep  { axis: u32, fromMax: u32, clear: u32, pad: u32 };
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
    var cur: u32 = BASE_SKY;
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

    private readonly GpuContext      _ctx;
    private readonly ComputePipeline _skySweep;
    private readonly ComputePipeline _scatter;
    private readonly ComputePipeline _pipeline;

    // Shared param buffers (rewritten per flood). Shared across volumes is safe: only one volume floods per
    // frame and the contents are set immediately before that volume's dispatches.
    private readonly GpuBuffer _sweepParam;  // (axis, fromMax, clear, pad)
    private readonly GpuBuffer _regionParam; // (ox, oy, oz, count, sx, sy, sz, pad)

    // Reusable CPU scratch for the emitter scatter list (pairs of voxelIndex, level). Grown lazily.
    private uint[] _emitterScratch = Array.Empty<uint>();

    public GpuLightFlood(GpuContext ctx)
    {
        _ctx         = ctx;
        _skySweep    = new ComputePipeline(ctx, SweepWgsl,   "main");
        _scatter     = new ComputePipeline(ctx, ScatterWgsl, "main");
        _pipeline    = new ComputePipeline(ctx, FloodWgsl,   "main");
        _sweepParam  = GpuBuffer.CreateStorage(ctx, 4 * sizeof(uint));
        _regionParam = GpuBuffer.CreateStorage(ctx, 8 * sizeof(uint));
    }

    /// <summary>
    /// Floods <paramref name="region"/> of <paramref name="vol"/>: sky sweep (clear) → emitter scatter →
    /// LightA→LightB copy → relaxation ping-pong. Result ends in LightA.
    /// </summary>
    public void Flood(VolumeGpuResources vol, IEnumerable<KeyValuePair<ChunkPosition, ChunkEntry>> chunks, FloodRegion region)
    {
        // 1) Gather in-region emitters into the scatter list and upload.
        int n = GatherEmitters(vol, chunks, region);
        vol.EnsureEmitterCapacity(System.Math.Max(n, 1));
        if (n > 0)
            vol.Emitters!.Write<uint>(0, _emitterScratch.AsSpan(0, n * 2));

        // 2) Region params (count rides along for the scatter pass).
        Span<uint> r = stackalloc uint[8]
        {
            (uint)region.Ox, (uint)region.Oy, (uint)region.Oz, (uint)n,
            (uint)region.Sx, (uint)region.Sy, (uint)region.Sz, 0u,
        };
        _regionParam.Write<uint>(0, r);

        // 3) Lazily (re)create bind groups; reset to 0 on resize / emitter grow.
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

        // 4) Ambient sky sweep — −Y (top-down), clear mode, over the region's XZ footprint. This resets the
        //    region (sky overwritten, block zeroed). Only the top sweep is enabled (matches prior behaviour);
        //    if more directions are added, only the first should use clear mode.
        DispatchSweep(vol, axis: 1u, fromMax: 1u, clear: 1u,
                      gx: CeilDiv((uint)region.Sx, 8u), gy: CeilDiv((uint)region.Sz, 8u));

        // 5) Scatter emitters into the (now reset) region.
        if (n > 0)
            _scatter.Dispatch(vol.ScatterBind, CeilDiv((uint)n, 64u), 1u, 1u);

        // 6) Make both ping-pong buffers agree everywhere, so the region reads correct, stable values just
        //    outside its border on every relaxation pass (whether the src is LightA or LightB).
        _ctx.CopyBufferToBuffer(vol.LightA, vol.LightB, vol.LightA.SizeBytes);

        // 7) Relaxation flood over the region — ping-pong, even passes → result in LightA.
        _pipeline.DispatchPingPong(vol.FloodBindEven, vol.FloodBindOdd,
            CeilDiv((uint)region.Sx, 4u), CeilDiv((uint)region.Sy, 4u), CeilDiv((uint)region.Sz, 4u), Passes);
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

    private void DispatchSweep(VolumeGpuResources vol, uint axis, uint fromMax, uint clear, uint gx, uint gy)
    {
        Span<uint> p = stackalloc uint[4] { axis, fromMax, clear, 0u };
        _sweepParam.Write<uint>(0, p);
        _skySweep.Dispatch(vol.SkySweepBind, gx, gy, 1u);
    }

    private static uint CeilDiv(uint a, uint b) => (a + b - 1u) / b;

    public void Dispose()
    {
        _skySweep.Dispose();
        _scatter.Dispose();
        _pipeline.Dispose();
        _sweepParam.Dispose();
        _regionParam.Dispose();
    }
}
