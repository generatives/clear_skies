using ClearSkies.Engine.Rendering.WebGpu;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// GPU block-light flood for a single chunk (Phase 4.2 cut 2, intra-chunk only).
///
/// Cycle per dirty chunk: clear+inject lamp emission into <see cref="ChunkGpuResources.LightA"/>, then
/// run a fixed number of max-relaxation passes ping-ponging A↔B. Light propagates −1 per air step and
/// is blocked by opacity. An even pass count leaves the result back in LightA (what the renderer
/// samples). Cross-chunk propagation (light crossing chunk borders) is a later cut — neighbours outside
/// the chunk currently contribute nothing, so lamps near a chunk edge don't spill into the next chunk.
///
/// Light buffer packing per voxel (u32): bits 0-7 sky (0-15), bits 8-15 block (0-15).
/// </summary>
internal sealed class GpuLightFlood : IDisposable
{
    // Block light reaches at most its emission level (≤15) → that many propagation steps. Even count
    // so the final write lands in LightA. 16 ≥ 15 covers the max radius.
    private const int Passes = 16;

    private const string Wgsl = @"
const CHUNK: i32 = 32;
const AMBIENT_SKY: u32 = 6u; // keep in sync with ChunkGpuResources.AmbientSky & render WGSL

@group(0) @binding(0) var<storage, read>       src: array<u32>;
@group(0) @binding(1) var<storage, read_write> dst: array<u32>;
@group(0) @binding(2) var<storage, read>       opacity: array<u32>;

fn flatIndex(x: i32, y: i32, z: i32) -> i32 { return x + CHUNK * (y + CHUNK * z); }

fn isOpaque(i: i32) -> bool {
    return ((opacity[u32(i) >> 5u] >> (u32(i) & 31u)) & 1u) == 1u;
}

// Block light of a voxel (0 if outside this chunk — intra-chunk flood only).
fn blockAt(x: i32, y: i32, z: i32) -> u32 {
    if (x < 0 || x >= CHUNK || y < 0 || y >= CHUNK || z < 0 || z >= CHUNK) { return 0u; }
    return (src[u32(flatIndex(x, y, z))] >> 8u) & 0xFFu;
}

@compute @workgroup_size(4, 4, 4)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let x = i32(gid.x); let y = i32(gid.y); let z = i32(gid.z);
    if (x >= CHUNK || y >= CHUNK || z >= CHUNK) { return; }
    let i = flatIndex(x, y, z);

    let self_block = (src[u32(i)] >> 8u) & 0xFFu;

    // Solid voxels keep their seeded block (emitters hold their emission; walls hold 0) and never
    // relay neighbour light, so light cannot pass through walls. Sky is 0 inside solids.
    if (isOpaque(i)) {
        dst[u32(i)] = self_block << 8u;
        return;
    }

    // Air voxel: brightest of (own value) and (each neighbour − 1).
    let nb = max(max(max(blockAt(x + 1, y, z), blockAt(x - 1, y, z)),
                     max(blockAt(x, y + 1, z), blockAt(x, y - 1, z))),
                 max(blockAt(x, y, z + 1), blockAt(x, y, z - 1)));
    var b = self_block;
    if (nb > 0u) { b = max(b, nb - 1u); }

    dst[u32(i)] = AMBIENT_SKY | (b << 8u);
}";

    private readonly GpuContext     _ctx;
    private readonly ComputePipeline _pipeline;
    private readonly uint[]          _seed = new uint[ChunkGpuResources.VoxelCount];

    public GpuLightFlood(GpuContext ctx)
    {
        _ctx      = ctx;
        _pipeline = new ComputePipeline(ctx, Wgsl, "main");
    }

    /// <summary>Clears + injects lamp emission, then floods. Result ends in <c>gpu.LightA</c>.</summary>
    public void Flood(ChunkGpuResources gpu, ChunkData data)
    {
        // Clear + inject: block = emission for emitters, 0 otherwise. Sky is filled by the flood.
        for (int z = 0; z < ChunkData.Size; z++)
        for (int y = 0; y < ChunkData.Size; y++)
        for (int x = 0; x < ChunkData.Size; x++)
        {
            byte emission = BlockRegistry.Get(data.Get(x, y, z)).LightEmission;
            _seed[ChunkData.Index(x, y, z)] = (uint)emission << 8;
        }
        gpu.LightA.Write<uint>(0, _seed);

        // even pass (p=0): read A → write B; odd pass (p=15): read B → write A → result in LightA.
        gpu.FloodBindEven = EnsureBind(gpu.FloodBindEven, gpu.LightA, gpu.LightB, gpu.Opacity);
        gpu.FloodBindOdd  = EnsureBind(gpu.FloodBindOdd,  gpu.LightB, gpu.LightA, gpu.Opacity);

        const uint groups = ChunkData.Size / 4; // workgroup_size 4 → 8 groups per axis
        _pipeline.DispatchPingPong(gpu.FloodBindEven, gpu.FloodBindOdd, groups, groups, groups, Passes);
    }

    private nint EnsureBind(nint existing, GpuBuffer read, GpuBuffer write, GpuBuffer opacity)
        => existing != 0
            ? existing
            : _pipeline.CreateBindGroupHandle(new (uint, GpuBuffer)[] { (0u, read), (1u, write), (2u, opacity) });

    public void Dispose() => _pipeline.Dispose();
}
