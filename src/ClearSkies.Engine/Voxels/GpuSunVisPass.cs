using System.Runtime.InteropServices;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering.WebGpu;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Precomputes per-voxel directional-sun visibility for a <see cref="VolumeGpuResources"/> from the world-space
/// sun shadow map (<see cref="SunShadowPass"/>), once per voxel instead of per fragment. For each <b>surface-air
/// voxel</b> (an air cell touching a solid — the only cells a face fragment ever samples), the cell centre is
/// transformed to world space (<c>voxelToWorld</c>), projected into light clip space (<c>lightViewProj</c>), and
/// PCF-tested against the shadow map; the 0-255 result is written to <see cref="VolumeGpuResources.SunVis"/>.
/// Interior solids and open air default to 255 (lit), so the per-frame cost stays on the lit surface shell.
///
/// The fragment shader then reads <c>SunVis</c> directly (a handful of buffer reads) and blends neighbouring
/// cells for the gradient — replacing the old ~2000-tap-per-fragment PCF. Because visibility is stored per voxel
/// and the fragment interpolates it, the shadow edge is a continuous blend that no longer "breathes" as casters
/// move, and the cost is proportional to surface voxels, not pixels.
/// </summary>
internal sealed class GpuSunVisPass : IDisposable
{
    private const int WordsPerChunk = VolumeGpuResources.WordsPerChunk; // 1024

    // Shadow-map texels averaged per voxel. Per-voxel (not per-pixel), so a wide kernel is cheap; matches the
    // softness the per-fragment path used at PCF_RADIUS = 4 (~9 texels > ~6.4 texels/voxel → overlapping taps).
    private const int PcfRadius = 4;

    // Depth bias suppressing self-shadow acne (matches the old fragment SHADOW_BIAS).
    private const float ShadowBias = 0.0015f;

    private static readonly string Wgsl = @"
const WPC: i32 = " + WordsPerChunk + @";
const SURFACE_OFFSET: f32 = 0.4;   // how far (voxels) to nudge the sample toward adjacent solid faces (< 0.5)

struct Params {
    lightViewProj: mat4x4<f32>,
    voxelToWorld:  mat4x4<f32>,
    dims:   vec4<i32>,   // VW, VH, VD, _
    region: vec4<i32>,   // ox, oy, oz, pcfRadius
    sizev:  vec4<i32>,   // sx, sy, sz, _
    misc:   vec4<f32>,   // bias, _, _, _
};

@group(0) @binding(0) var<storage, read_write> sunvis:    array<u32>;
@group(0) @binding(1) var<storage, read>       opacity:   array<u32>;
@group(0) @binding(2) var                      shadowMap: texture_depth_2d;
@group(0) @binding(3) var<uniform>             p:         Params;

// Chunk-major opacity (matches the flood shader): slot = cx + DX*(cy + DY*cz); word = ly + 32*lz; bit = lx.
fn isOpaque(x: i32, y: i32, z: i32) -> bool {
    if (x < 0 || x >= p.dims.x || y < 0 || y >= p.dims.y || z < 0 || z >= p.dims.z) { return false; }
    let dx   = p.dims.x >> 5;
    let dy   = p.dims.y >> 5;
    let slot = (x >> 5) + dx * ((y >> 5) + dy * (z >> 5));
    let word = slot * WPC + ((y & 31) + 32 * (z & 31));
    let bit  = u32(x & 31);
    return ((opacity[u32(word)] >> bit) & 1u) == 1u;
}

@compute @workgroup_size(4, 4, 4)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let x = p.region.x + i32(gid.x);
    let y = p.region.y + i32(gid.y);
    let z = p.region.z + i32(gid.z);
    if (x >= p.region.x + p.sizev.x || y >= p.region.y + p.sizev.y || z >= p.region.z + p.sizev.z) { return; }
    if (x < 0 || x >= p.dims.x || y < 0 || y >= p.dims.y || z < 0 || z >= p.dims.z) { return; }
    let i = u32(x + p.dims.x * (y + p.dims.y * z));

    // Only air cells touching a solid (the lit surface shell) need a real shadow test; everything else
    // (interior solids, open air) defaults to fully lit. This keeps the per-frame cost on the surface only.
    if (isOpaque(x, y, z)) { sunvis[i] = 255u; return; }
    let sxn = isOpaque(x - 1, y, z); let sxp = isOpaque(x + 1, y, z);
    let syn = isOpaque(x, y - 1, z); let syp = isOpaque(x, y + 1, z);
    let szn = isOpaque(x, y, z - 1); let szp = isOpaque(x, y, z + 1);
    if (!(sxn || sxp || syn || syp || szn || szp)) { sunvis[i] = 255u; return; }

    // Sample point: voxel centre nudged TOWARD each adjacent solid face. Testing the bare voxel centre (½ a
    // voxel off the surface) lifts a concave-corner sample out of the wall's contact shadow, so the shadow gets
    // 'sucked in' / pulled tight to walls at elevation changes. Nudging toward the solid faces drops the sample
    // to just above a floor (and into the concave corner beside a wall), where the contact shadow actually is,
    // so shadows reach the wall. The nudge stays < ½ so the point never leaves the air voxel (still acne-safe).
    var nudge = vec3<f32>(0.0, 0.0, 0.0);
    if (sxn) { nudge.x = nudge.x - 1.0; }  if (sxp) { nudge.x = nudge.x + 1.0; }
    if (syn) { nudge.y = nudge.y - 1.0; }  if (syp) { nudge.y = nudge.y + 1.0; }
    if (szn) { nudge.z = nudge.z - 1.0; }  if (szp) { nudge.z = nudge.z + 1.0; }
    let vc = vec3<f32>(f32(x) + 0.5, f32(y) + 0.5, f32(z) + 0.5) + nudge * SURFACE_OFFSET;
    let world = (p.voxelToWorld * vec4<f32>(vc, 1.0)).xyz;
    let clip  = p.lightViewProj * vec4<f32>(world, 1.0);
    if (clip.w <= 0.0) { sunvis[i] = 255u; return; }
    let ndc   = clip.xyz / clip.w;
    let uv    = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || ndc.z < 0.0 || ndc.z > 1.0) {
        sunvis[i] = 255u; return;   // outside the shadow frustum → lit
    }

    // PCF: tent-weighted kernel of shadow-map texels around the projected centre (sub-voxel soft edge).
    let dim   = vec2<f32>(textureDimensions(shadowMap));
    let dimI  = vec2<i32>(dim);
    let base  = vec2<i32>(floor(uv * dim));
    let R     = p.region.w;
    let denom = f32(R) + 1.0;
    let bias  = p.misc.x;
    var vis = 0.0;
    var sum = 0.0;
    for (var dy = -R; dy <= R; dy = dy + 1) {
        for (var dx = -R; dx <= R; dx = dx + 1) {
            let texel = clamp(base + vec2<i32>(dx, dy), vec2<i32>(0, 0), dimI - vec2<i32>(1, 1));
            let d     = textureLoad(shadowMap, texel, 0);
            let w     = (1.0 - abs(f32(dx)) / denom) * (1.0 - abs(f32(dy)) / denom);
            vis += w * select(0.0, 1.0, ndc.z <= d + bias);
            sum += w;
        }
    }
    sunvis[i] = u32(round(clamp(vis / sum, 0.0, 1.0) * 255.0));
}";

    private readonly GpuContext      _ctx;
    private readonly ComputePipeline _pipeline;
    private readonly GpuBuffer       _param; // SunVisParams uniform, rewritten per volume

    public GpuSunVisPass(GpuContext ctx)
    {
        _ctx      = ctx;
        _pipeline = new ComputePipeline(ctx, Wgsl, "main");
        _param    = GpuBuffer.CreateUniform(ctx, (ulong)Marshal.SizeOf<SunVisParams>());
    }

    /// <summary>
    /// Recomputes sun visibility over <paramref name="region"/> of <paramref name="vol"/> against the world-space
    /// sun shadow map (<paramref name="shadowView"/>, a <c>texture_depth_2d</c> view). <paramref name="voxelToWorld"/>
    /// maps a volume voxel centre to world space; <paramref name="lightViewProj"/> is the sun's light-space
    /// view-projection (the same matrix the shadow map was rendered with). Result lands in <c>vol.SunVis</c>.
    /// </summary>
    public void Compute(VolumeGpuResources vol, nint shadowView, in Mat4 lightViewProj, in Mat4 voxelToWorld, FloodRegion region)
    {
        if (region.Sx <= 0 || region.Sy <= 0 || region.Sz <= 0) return;

        var pr = new SunVisParams
        {
            LightViewProj = lightViewProj,
            VoxelToWorld  = voxelToWorld,
            VW = vol.VW, VH = vol.VH, VD = vol.VD, DimsPad = 0,
            Ox = region.Ox, Oy = region.Oy, Oz = region.Oz, PcfRadius = PcfRadius,
            Sx = region.Sx, Sy = region.Sy, Sz = region.Sz, SizePad = 0,
            Bias = ShadowBias, M1 = 0f, M2 = 0f, M3 = 0f,
        };
        Span<SunVisParams> sp = stackalloc SunVisParams[1] { pr };
        _param.Write<SunVisParams>(0, sp);

        // Cache the bind group; the shadow-map view is constant, so it only needs recreating on volume realloc.
        if (vol.SunVisBind == 0)
            vol.SunVisBind = _pipeline.CreateBindGroupHandle(
                new (uint, GpuBuffer)[] { (0u, vol.SunVis), (1u, vol.Opacity), (3u, _param) }, 2u, shadowView);

        _pipeline.Dispatch(vol.SunVisBind,
            CeilDiv((uint)region.Sx, 4u), CeilDiv((uint)region.Sy, 4u), CeilDiv((uint)region.Sz, 4u));
    }

    private static uint CeilDiv(uint a, uint b) => (a + b - 1u) / b;

    public void Dispose()
    {
        _pipeline.Dispose();
        _param.Dispose();
    }

    /// <summary>Uniform block for the sun-vis pass (192 bytes). Field order/padding match the WGSL
    /// <c>Params</c> std140 layout: two mat4s, then four vec4s (vec4&lt;i32&gt; / vec4&lt;f32&gt;).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SunVisParams
    {
        public Mat4  LightViewProj;
        public Mat4  VoxelToWorld;
        public int   VW, VH, VD, DimsPad;        // vec4<i32> dims
        public int   Ox, Oy, Oz, PcfRadius;      // vec4<i32> region
        public int   Sx, Sy, Sz, SizePad;        // vec4<i32> sizev
        public float Bias, M1, M2, M3;           // vec4<f32> misc
    }
}
