using ClearSkies.Engine.Rendering.WebGpu;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// GPU light flood for a <see cref="VolumeGpuResources"/>. Operates on the entire volume buffer in one
/// set of dispatches, so light crosses chunk boundaries naturally. Each cycle: upload seed → ping-pong
/// max-relaxation passes → result in LightA (what the renderer samples).
///
/// Sky channel (bits 0-7): the CPU column pass seeds each chunk's vertical sky (load-order-safe); the
/// flood then max-relaxes it across the whole volume so it converges over chunk boundaries — full sun
/// (<see cref="LightEngine.BaseSkyLevel"/>) passes straight down through air without attenuation, every
/// other direction loses 1 per step. This is what removes the per-chunk-boundary sky seams.
/// Block channel (bits 8-15): seeded from emitter emission; max-relaxation propagates it through air.
/// </summary>
internal sealed class GpuLightFlood : IDisposable
{
    // 16 passes → max propagation radius 15 (max emission, and >= BaseSkyLevel for sky horizontal
    // bleed). Even pass count → result lands in LightA.
    private const int Passes = 16;

    // BaseSkyLevel baked into the shader as the "full sun" sentinel (passes straight down unattenuated).
    private static readonly string Wgsl = @"
const BASE_SKY: u32 = " + LightEngine.BaseSkyLevel + @"u;

// Volume dims passed as a small storage buffer to avoid shader recompilation on resize.
struct Dims { w: u32, h: u32, d: u32, pad: u32 };

@group(0) @binding(0) var<storage, read>       src:     array<u32>;
@group(0) @binding(1) var<storage, read_write> dst:     array<u32>;
@group(0) @binding(2) var<storage, read>       opacity: array<u32>;
@group(0) @binding(3) var<storage, read>       dims:    Dims;

fn idx3(x: i32, y: i32, z: i32) -> i32 {
    return x + i32(dims.w) * (y + i32(dims.h) * z);
}

fn inVol(x: i32, y: i32, z: i32) -> bool {
    return x >= 0 && x < i32(dims.w) &&
           y >= 0 && y < i32(dims.h) &&
           z >= 0 && z < i32(dims.d);
}

fn isOpaque(i: i32) -> bool {
    return ((opacity[u32(i) >> 5u] >> (u32(i) & 31u)) & 1u) == 1u;
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
    let x = i32(gid.x); let y = i32(gid.y); let z = i32(gid.z);
    if (!inVol(x, y, z)) { return; }
    let i = idx3(x, y, z);

    let sv  = src[u32(i)];
    let sky = sv & 0xFFu;
    let blk = (sv >> 8u) & 0xFFu;

    // Solid voxels: sky=0 inside solid, keep seeded emission. No relay.
    if (isOpaque(i)) {
        dst[u32(i)] = blk << 8u;
        return;
    }

    // ── Block channel: max-relaxation, loses 1 per step in all directions ──
    let nb = max(max(max(blockAt(x+1,y,z), blockAt(x-1,y,z)),
                     max(blockAt(x,y+1,z), blockAt(x,y-1,z))),
                 max(blockAt(x,y,z+1), blockAt(x,y,z-1)));
    var b = blk;
    if (nb > 0u) { b = max(b, nb - 1u); }

    // ── Sky channel: max-relaxation across the volume (converges chunk boundaries) ──
    // Full sun from directly above passes straight down with no attenuation; everything else loses 1.
    var s  = sky;                       // CPU column-pass seed
    let up = skyAt(x, y + 1, z);
    if (up >= BASE_SKY) { s = max(s, up); }
    else if (up > 0u)   { s = max(s, up - 1u); }

    let side = max(max(max(skyAt(x-1,y,z), skyAt(x+1,y,z)),
                       max(skyAt(x,y,z-1), skyAt(x,y,z+1))),
                   skyAt(x, y - 1, z));
    if (side > 0u) { s = max(s, side - 1u); }

    dst[u32(i)] = s | (b << 8u);
}";

    private readonly GpuContext      _ctx;
    private readonly ComputePipeline _pipeline;

    // Reusable seed buffer (avoids per-cycle allocation). Grown lazily.
    private uint[] _seedBuf = Array.Empty<uint>();

    public GpuLightFlood(GpuContext ctx)
    {
        _ctx      = ctx;
        _pipeline = new ComputePipeline(ctx, Wgsl, "main");
    }

    /// <summary>
    /// Builds the light seed from <paramref name="vol"/>'s CPU chunk data, uploads it to LightA,
    /// then runs the ping-pong flood. Result ends in LightA.
    /// </summary>
    public void Flood(VolumeGpuResources vol, IEnumerable<KeyValuePair<ChunkPosition, ChunkEntry>> chunks)
    {
        // Grow seed buffer if volume expanded.
        if (_seedBuf.Length < vol.TotalVoxels)
            _seedBuf = new uint[vol.TotalVoxels];

        vol.BuildLightSeed(chunks, _seedBuf);
        vol.LightA.Write<uint>(0, _seedBuf.AsSpan(0, vol.TotalVoxels));

        // Lazily create bind groups. Recreate whenever they were invalidated (resize → 0).
        if (vol.FloodBindEven == 0)
            vol.FloodBindEven = _pipeline.CreateBindGroupHandle(
                new (uint, GpuBuffer)[] { (0u, vol.LightA), (1u, vol.LightB), (2u, vol.Opacity), (3u, vol.Dims) });
        if (vol.FloodBindOdd == 0)
            vol.FloodBindOdd = _pipeline.CreateBindGroupHandle(
                new (uint, GpuBuffer)[] { (0u, vol.LightB), (1u, vol.LightA), (2u, vol.Opacity), (3u, vol.Dims) });

        uint gx = (uint)(vol.VW / 4);
        uint gy = (uint)(vol.VH / 4);
        uint gz = (uint)(vol.VD / 4);
        _pipeline.DispatchPingPong(vol.FloodBindEven, vol.FloodBindOdd, gx, gy, gz, Passes);
    }

    public void Dispose() => _pipeline.Dispose();
}
