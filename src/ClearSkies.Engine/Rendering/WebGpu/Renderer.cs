using ClearSkies.Engine.Math;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// Orchestrates the WebGPU pipeline and exposes a small per-frame API. Owns the render pipeline,
/// the camera uniform + bind group, and a dynamic-offset model uniform buffer + bind group
/// (the WebGPU-core replacement for push constants).
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    private const int MaxObjects = 4096;
    private const ulong ModelStride = 256;   // >= minUniformBufferOffsetAlignment
    private const ulong CameraSize  = 208;   // three mat4x4<f32> (view, proj, lightViewProj) + vec4<f32> sun direction
    private const ulong ModelSize   = 96;    // mat4x4<f32> + vec3<i32> chunkBase + vec3<i32> volSize (each padded to 16B)

    // Ambient fallback (sky=6/15) for fragments whose air-side voxel is outside the volume buffer
    // (e.g. non-chunk draws, or volume not yet initialised).
    private const string Wgsl = @"
const AMBIENT_SKY: f32 = 3.0 / 15.0;  // matches VolumeGpuResources.BaseSkyLevel (out-of-volume fallback)
const SUN_STRENGTH: f32 = 1.0;        // direct sun at full brightness (level 15); paired with the dim sky fill
                                      // + low MIN_AMBIENT so shadows stay dark for the lamp/block system to fill
const MIN_AMBIENT: f32 = 0.05;        // floor so no geometry is ever fully black
const AO_MIN: f32 = 0.45;             // darkest ambient-occluded corner (1 = no AO). 0.15 provides exaggeration for evaluation;
                                      // 0.45 is the subtler default.
const WPC: i32 = 1024;                // u32 opacity words per 32³ chunk (VolumeGpuResources.WordsPerChunk)
const SMOOTH_RADIUS: i32 = 2;     // in-plane light/shadow smoothing radius (cells) → (2R+1)^2 taps. Larger = smoother
                                  // (and softer shadows), but a radius wider than a shadow feature washes it out.
                                  // Sun visibility is sampled from the precomputed per-voxel `sunvis` buffer; the
                                  // shadow-map PCF now lives in GpuSunVisPass (PCF radius + bias are set there).

struct Camera { view: mat4x4<f32>, proj: mat4x4<f32>, sunDir: vec4<f32>, lightViewProj: mat4x4<f32> };
@group(0) @binding(0) var<uniform> camera: Camera;

// model: world transform. chunkBase: this chunk's voxel origin in the volume. volSize: volume dims in voxels.
// vec3<i32> in a WGSL uniform struct is padded to 16 bytes (same as vec4).
struct Model { model: mat4x4<f32>, chunkBase: vec3<i32>, _p0: i32, volSize: vec3<i32>, _p1: i32 };
@group(1) @binding(0) var<uniform> model: Model;

// Per-volume light field (entire ChunkVolume in one buffer).
// 1 u32 per voxel: bits 0-7 = sky (0-15), bits 8-15 = block (0-15).
// Index: vx + volSize.x * (vy + volSize.y * vz) where (vx,vy,vz) = chunkBase + localAir.
@group(2) @binding(0) var<storage, read> light: array<u32>;

// Per-volume opacity bitset (chunk-major, 1 bit/voxel), same buffer the flood reads. Sampled for in-shader
// ambient occlusion of the air-side cell's neighbourhood.
@group(2) @binding(1) var<storage, read> opacity: array<u32>;

// Per-voxel directional-sun visibility (0-255, 255 = fully lit), precomputed each frame by GpuSunVisPass from
// the world-space sun shadow map. Volume-linear like `light`. Sampling this replaces the old per-fragment PCF.
@group(2) @binding(2) var<storage, read> sunvis: array<u32>;

// Directional-sun shadow map, rendered depth-only from the sun's POV (see SunShadowPass). Still bound for the
// depth pass; the main fragment now reads precomputed `sunvis` instead of sampling this directly.
@group(3) @binding(0) var shadowMap: texture_depth_2d;

// Block texture array: each layer is one named sprite from the spritesheet (TextureAtlas), sized
// TileSize² and nearest-filtered. Vertex UV is (u, v, layer): u/v are tile-space (not normalized),
// wrapped per-fragment with fract() so a texture repeats once per block regardless of how large a
// greedy-merged quad is; layer < 0 means untextured — use the vertex color instead (see fs_main).
@group(4) @binding(0) var atlasTex:  texture_2d_array<f32>;
@group(4) @binding(1) var atlasSamp: sampler;

struct VSOut {
    @builtin(position) pos:         vec4<f32>,
    @location(0)       color:       vec3<f32>,
    @location(1)       worldNormal: vec3<f32>,
    @location(2)       localPos:    vec3<f32>,
    @location(3)       localNormal: vec3<f32>,
    @location(4)       uv:          vec3<f32>,
};

@vertex
fn vs_main(
    @location(0) position: vec3<f32>,
    @location(1) normal:   vec3<f32>,
    @location(2) color:    vec3<f32>,
    @location(3) uv:       vec3<f32>
) -> VSOut {
    var o: VSOut;
    o.pos         = camera.proj * camera.view * model.model * vec4<f32>(position, 1.0);
    o.color       = color;
    o.worldNormal = (model.model * vec4<f32>(normal, 0.0)).xyz;
    o.localPos    = position;
    o.localNormal = normal;
    o.uv          = uv;
    return o;
}

// Chunk-major opacity test (matches the flood shader): slot = cx + DX*(cy + DY*cz); word = ly + 32*lz; bit = lx.
fn isSolid(v: vec3<i32>) -> bool {
    if (v.x < 0 || v.x >= model.volSize.x ||
        v.y < 0 || v.y >= model.volSize.y ||
        v.z < 0 || v.z >= model.volSize.z) { return false; } // out of volume → treat as open (no AO at borders)
    let dx   = model.volSize.x >> 5;
    let dy   = model.volSize.y >> 5;
    let slot = (v.x >> 5) + dx * ((v.y >> 5) + dy * (v.z >> 5));
    let word = slot * WPC + ((v.y & 31) + 32 * (v.z & 31));
    let bit  = u32(v.x & 31);
    return ((opacity[u32(word)] >> bit) & 1u) == 1u;
}

fn occ(v: vec3<i32>) -> f32 { return select(0.0, 1.0, isSolid(v)); }

// Light (sky, block) in 0..1 at a single volume voxel. Out-of-volume → ambient fallback.
fn lightAt(vol: vec3<i32>) -> vec2<f32> {
    if (vol.x < 0 || vol.x >= model.volSize.x ||
        vol.y < 0 || vol.y >= model.volSize.y ||
        vol.z < 0 || vol.z >= model.volSize.z) {
        return vec2<f32>(AMBIENT_SKY, 0.0);
    }
    let idx    = u32(vol.x + model.volSize.x * (vol.y + model.volSize.y * vol.z));
    let packed = light[idx];
    return vec2<f32>(f32(packed & 0xFFu) / 15.0, f32((packed >> 8u) & 0xFFu) / 15.0);
}

// Raw per-voxel directional-sun visibility from the `sunvis` buffer that GpuSunVisPass precomputed this frame.
// 0-254 = shadowed…lit; 255 = SKIP SENTINEL ('no surface shadow sample here' — open air / out of volume). The
// sun blend in sampleLit skips 255 so open-air cells past a convex edge don't wash the shadow out before the
// edge. Out-of-volume also returns 255 (skip). The PCF happens once per surface voxel in the compute pass.
fn sunRaw(vol: vec3<i32>) -> u32 {
    if (vol.x < 0 || vol.x >= model.volSize.x ||
        vol.y < 0 || vol.y >= model.volSize.y ||
        vol.z < 0 || vol.z >= model.volSize.z) {
        return 255u;
    }
    return sunvis[u32(vol.x + model.volSize.x * (vol.y + model.volSize.y * vol.z))];
}

// Smoothed surface lighting at the air-side of this fragment: sky + block light (each 0..1) and directional-sun
// visibility (1 lit … 0 shadowed). Both are a gaussian-weighted average of the in-plane neighbourhood of air
// cells, centred on the fragment's CONTINUOUS position in the face plane — so the result is a smooth function of
// position (no per-cell banding) that ramps over several voxels (no sudden jumps as light/shadow boundaries move).
// Opaque cells are dropped so neither bleeds through walls. The sun reads precomputed per-voxel visibility
// (sunVisAt → the `sunvis` buffer); the gaussian blends neighbouring cells so the lit/shadow transition is a
// smooth, continuous function of position. wantSun lets faces turned from the sun skip the sun samples.
// Separable smoothing window: 1 at the centre, falling to 0 — with ZERO SLOPE — at the kernel edge (|d| = R+0.5).
// C1 everywhere, so (a) the reconstructed light is slope-continuous → no Mach banding (bright/dark lines) at cell
// edges, and (b) a cell entering/leaving the finite window contributes ~0, so nothing pops as the sample point or
// a moving shadow crosses a cell boundary. A truncated gaussian fails (b) — its edge weight is non-zero — which is
// what made shadows pulse/'open and close' as objects moved.
fn smoothW(d: f32) -> f32 {
    let a = clamp(abs(d) / (f32(SMOOTH_RADIUS) + 0.5), 0.0, 1.0);
    return 1.0 - a * a * (3.0 - 2.0 * a);
}

struct Lit { sky: f32, blk: f32, sun: f32 };
fn sampleLit(localPos: vec3<f32>, localNormal: vec3<f32>, wantSun: bool) -> Lit {
    let localAir = vec3<i32>(floor(localPos + 0.5 * localNormal));
    let air = model.chunkBase + localAir;
    let n   = abs(localNormal);
    var T: vec3<i32>; var B: vec3<i32>;
    if (n.x > 0.5)      { T = vec3<i32>(0, 1, 0); B = vec3<i32>(0, 0, 1); }
    else if (n.y > 0.5) { T = vec3<i32>(1, 0, 0); B = vec3<i32>(0, 0, 1); }
    else                { T = vec3<i32>(1, 0, 0); B = vec3<i32>(0, 1, 0); }

    // Fragment offset from the centre cell's centre, along each in-plane axis (in cells, range [-0.5, 0.5)).
    let du = fract(dot(localPos, vec3<f32>(T))) - 0.5;
    let dv = fract(dot(localPos, vec3<f32>(B))) - 0.5;

    var accL = vec2<f32>(0.0, 0.0); var sumL = 0.0;
    var accS = 0.0; var sumS = 0.0;              // sun gets its own sum: open-air cells (sentinel) are skipped
    for (var i = -SMOOTH_RADIUS; i <= SMOOTH_RADIUS; i = i + 1) {
        for (var j = -SMOOTH_RADIUS; j <= SMOOTH_RADIUS; j = j + 1) {
            let cell = air + i * T + j * B;
            if (isSolid(cell)) { continue; }            // opaque → no light/shadow contribution
            let w = smoothW(du - f32(i)) * smoothW(dv - f32(j));
            if (w <= 0.0) { continue; }

            accL += w * lightAt(cell);
            sumL += w;
            if (wantSun) {
                let sv = sunRaw(cell);
                if (sv < 255u) {                        // 255 = no surface sample here → don't bias the blend
                    accS += w * f32(sv) / 254.0;        // (so shadows reach convex edges instead of stopping short)
                    sumS += w;
                }
            }
        }
    }

    let lv = accL / max(sumL, 1e-4);
    var o: Lit;
    o.sky = lv.x;
    o.blk = lv.y;
    o.sun = select(1.0, accS / max(sumS, 1e-4), wantSun && sumS > 0.0);
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
    let air = model.chunkBase + vec3<i32>(floor(localPos + 0.5 * localNormal));
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
    // at the far edge of the AO. Same zero-slope trick the light smoothing (smoothW) uses to avoid Mach banding.
    let s = smoothstep(0.0, 1.0, fract(dot(localPos, vec3<f32>(T))));
    let t = smoothstep(0.0, 1.0, fract(dot(localPos, vec3<f32>(B))));
    return mix(mix(ao00, ao10, s), mix(ao01, ao11, s), t);
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4<f32> {
    let worldN = normalize(in.worldNormal);
    let ndotl  = max(dot(worldN, -(camera.sunDir.xyz)), 0.0);
    let s      = sampleLit(in.localPos, in.localNormal, ndotl > 0.0);

    // Soft ambient fill (sky flood from all six faces, max BASE_SKY/15).
    let ambient = s.sky;

    // Direct sun capped at SUN_STRENGTH (level 3): Lambertian on the surface normal, gated by the smoothed
    // voxel sun visibility. Kept dim (with the sky fill) so the lamp/block-light system dominates the scene.
    let directSun = ndotl * s.sun * SUN_STRENGTH;

    // Sky contribution = the brighter of soft ambient and direct sun.
    let skyTerm = max(ambient, directSun);

    // Final = brightest of sky, block light, and the minimum ambient floor (geometry never fully dark).
    let lit = max(max(skyTerm, s.blk), MIN_AMBIENT);

    // Ambient occlusion darkens inner corners / block junctions; lerp from AO_MIN so corners aren't pure black.
    let aoFactor = mix(AO_MIN, 1.0, computeAO(in.localPos, in.localNormal));

    // Sample unconditionally (avoids implicit-derivative issues from branching on a per-fragment value) and
    // select against the vertex color for untextured blocks (uv.z < 0, the no-texture sentinel).
    let layer     = max(i32(round(in.uv.z)), 0);
    let texColor  = textureSample(atlasTex, atlasSamp, fract(in.uv.xy), layer).rgb;
    let baseColor = select(in.color, texColor, in.uv.z >= 0.0);

    return vec4<f32>(baseColor * lit * aoFactor, 1.0);
}
";

    private readonly GpuContext _ctx;
    private readonly WebGPU _api;

    private ShaderModule* _shader;
    private BindGroupLayout* _cameraLayout;
    private BindGroupLayout* _modelLayout;
    private BindGroupLayout* _lightLayout;
    private BindGroupLayout* _shadowLayout;
    private BindGroupLayout* _atlasLayout;
    private PipelineLayout* _pipelineLayout;
    private RenderPipeline* _pipeline;
    private RenderPipeline* _wireframePipeline;
    private RenderPipeline* _hudPipeline;

    private SunShadowPass _shadow = null!;
    private BindGroup* _shadowBindGroup;

    // Block texture array (TextureAtlas → GPU). Constructed with a 1x1 white fallback so BeginFrame
    // always has a valid group-4 bind group; LoadTextureAtlas replaces it with the real spritesheet.
    private Texture*     _atlasTexture;
    private TextureView* _atlasTextureView;
    private Sampler*     _atlasSampler;
    private BindGroup*   _atlasBindGroup;

    public TextureAtlas? Atlas { get; private set; }

    public bool WireframeMode { get; set; }

    private readonly GpuBuffer _cameraBuffer;
    private readonly GpuBuffer _hudCameraBuffer; // permanently holds identity view+proj
    private readonly GpuBuffer _modelBuffer;
    private GpuBuffer _fullBrightLight = null!;  // Phase 4.1: shared sky=15 buffer for all draws
    private GpuBuffer _fullAirOpacity = null!;   // all-air opacity for the fallback (non-chunk) light bind group
    private GpuBuffer _fullLitSunVis = null!;    // all-lit (255) sun visibility for the fallback light bind group
    private BindGroup* _cameraBindGroup;
    private BindGroup* _hudCameraBindGroup;
    private BindGroup* _modelBindGroup;
    private BindGroup* _lightBindGroup;

    private CommandEncoder* _encoder;
    private RenderPassEncoder* _pass;
    private int _drawIndex;

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
        _pipeline          = CreatePipeline(PrimitiveTopology.TriangleList, CullMode.Back);
        _wireframePipeline = CreatePipeline(PrimitiveTopology.LineList,     CullMode.None);
        _hudPipeline       = CreatePipeline(PrimitiveTopology.TriangleList, CullMode.None, depthTest: false);

        // Sun shadow pass shares the camera + model bind-group layouts (its depth shader reads both).
        _shadow = new SunShadowPass(ctx, _cameraLayout, _modelLayout);

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
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment, // fragment reads chunkBase/volSize
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, HasDynamicOffset = true, MinBindingSize = ModelSize },
        };
        var modelDesc = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &modelEntry };
        _modelLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &modelDesc);

        // Group 2: per-volume light (binding 0) + opacity (binding 1) + sun visibility (binding 2), all
        // read-only storage in the fragment.
        BindGroupLayoutEntry* lightEntries = stackalloc BindGroupLayoutEntry[3];
        lightEntries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage, HasDynamicOffset = false, MinBindingSize = 0 },
        };
        lightEntries[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage, HasDynamicOffset = false, MinBindingSize = 0 },
        };
        lightEntries[2] = new BindGroupLayoutEntry
        {
            Binding = 2,
            Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage, HasDynamicOffset = false, MinBindingSize = 0 },
        };
        var lightDesc = new BindGroupLayoutDescriptor { EntryCount = 3, Entries = lightEntries };
        _lightLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &lightDesc);

        // Group 3: the sun shadow map, sampled via textureLoad (no sampler needed → hard, voxel-res shadows).
        var shadowEntry = new BindGroupLayoutEntry
        {
            Binding    = 0,
            Visibility = ShaderStage.Fragment,
            Texture    = new TextureBindingLayout { SampleType = TextureSampleType.Depth, ViewDimension = TextureViewDimension.Dimension2D, Multisampled = false },
        };
        var shadowDesc = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &shadowEntry };
        _shadowLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &shadowDesc);

        // Group 4: block texture array (binding 0) + a filtering sampler (binding 1).
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

        BindGroupLayout** layouts = stackalloc BindGroupLayout*[5];
        layouts[0] = _cameraLayout;
        layouts[1] = _modelLayout;
        layouts[2] = _lightLayout;
        layouts[3] = _shadowLayout;
        layouts[4] = _atlasLayout;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 5, BindGroupLayouts = layouts };
        _pipelineLayout = _api.DeviceCreatePipelineLayout(_ctx.Device, &plDesc);
    }

    private RenderPipeline* CreatePipeline(PrimitiveTopology topology, CullMode cullMode, bool depthTest = true)
    {
        VertexAttribute* attrs = stackalloc VertexAttribute[4];
        attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0,  ShaderLocation = 0 };
        attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 12, ShaderLocation = 1 };
        attrs[2] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 24, ShaderLocation = 2 };
        attrs[3] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 36, ShaderLocation = 3 };
        var vbLayout = new VertexBufferLayout { ArrayStride = Vertex.SizeBytes, StepMode = VertexStepMode.Vertex, AttributeCount = 4, Attributes = attrs };

        var vsEntry = (byte*)SilkMarshal.StringToPtr("vs_main", NativeStringEncoding.UTF8);
        var fsEntry = (byte*)SilkMarshal.StringToPtr("fs_main", NativeStringEncoding.UTF8);

        var vertexState   = new VertexState   { Module = _shader, EntryPoint = vsEntry, BufferCount = 1, Buffers = &vbLayout };
        var colorTarget   = new ColorTargetState { Format = _ctx.SurfaceFormat, Blend = null, WriteMask = ColorWriteMask.All };
        var fragmentState = new FragmentState { Module = _shader, EntryPoint = fsEntry, TargetCount = 1, Targets = &colorTarget };

        var keep  = StencilOperation.Keep;
        var depth = new DepthStencilState
        {
            Format            = _ctx.DepthFormat,
            DepthWriteEnabled = depthTest,
            DepthCompare      = depthTest ? CompareFunction.Less : CompareFunction.Always,
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

        // Phase 4.1: one shared full-bright (sky=15) light buffer bound for every draw, proving the
        // fragment-sampling path. Phase 4.2 replaces this with per-chunk flooded buffers.
        const int voxels = 32 * 32 * 32;
        _fullBrightLight = GpuBuffer.CreateStorage(_ctx, (ulong)(voxels * sizeof(uint)));
        var full = new uint[voxels];
        Array.Fill(full, 15u); // sky=15, block=0
        _fullBrightLight.Write<uint>(0, full);

        // All-air opacity (one 32³ chunk's worth of words) so the fallback's AO sampling sees no occluders.
        const int opWords = voxels / 32;
        _fullAirOpacity = GpuBuffer.CreateStorage(_ctx, (ulong)(opWords * sizeof(uint)));
        _fullAirOpacity.Write<uint>(0, new uint[opWords]);

        // All-lit sun visibility (255) for the fallback (non-chunk) draws — no sun shadowing.
        _fullLitSunVis = GpuBuffer.CreateStorage(_ctx, (ulong)(voxels * sizeof(uint)));
        var litVis = new uint[voxels];
        Array.Fill(litVis, 255u);
        _fullLitSunVis.Write<uint>(0, litVis);

        BindGroupEntry* lightEntries = stackalloc BindGroupEntry[3];
        lightEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _fullBrightLight.Handle, Offset = 0, Size = _fullBrightLight.SizeBytes };
        lightEntries[1] = new BindGroupEntry { Binding = 1, Buffer = _fullAirOpacity.Handle, Offset = 0, Size = _fullAirOpacity.SizeBytes };
        lightEntries[2] = new BindGroupEntry { Binding = 2, Buffer = _fullLitSunVis.Handle, Offset = 0, Size = _fullLitSunVis.SizeBytes };
        var lightDesc  = new BindGroupDescriptor { Layout = _lightLayout, EntryCount = 3, Entries = lightEntries };
        _lightBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &lightDesc);

        // Group 3: the shadow map depth view (same texture the shadow pass renders into).
        var shadowEntry = new BindGroupEntry { Binding = 0, TextureView = _shadow.DepthView };
        var shadowDesc  = new BindGroupDescriptor { Layout = _shadowLayout, EntryCount = 1, Entries = &shadowEntry };
        _shadowBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &shadowDesc);
    }

    /// <summary>1x1 white single-layer array texture, bound as group 4 until <see cref="LoadTextureAtlas"/>
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

        var texDesc = new TextureDescriptor
        {
            Usage         = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            Dimension     = TextureDimension.Dimension2D,
            Size          = new Extent3D((uint)tileWidth, (uint)tileHeight, (uint)layers.Length),
            Format        = TextureFormat.Rgba8Unorm,
            MipLevelCount = 1,
            SampleCount   = 1,
        };
        _atlasTexture = _api.DeviceCreateTexture(_ctx.Device, &texDesc);

        for (uint layer = 0; layer < layers.Length; layer++)
        {
            fixed (byte* data = layers[layer])
            {
                var dest = new ImageCopyTexture { Texture = _atlasTexture, MipLevel = 0, Origin = new Origin3D(0, 0, layer), Aspect = TextureAspect.All };
                var dataLayout = new TextureDataLayout { Offset = 0, BytesPerRow = (uint)(tileWidth * 4), RowsPerImage = (uint)tileHeight };
                var writeSize = new Extent3D((uint)tileWidth, (uint)tileHeight, 1);
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
        _atlasTextureView = _api.TextureCreateView(_atlasTexture, &viewDesc);

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
        _atlasSampler = _api.DeviceCreateSampler(_ctx.Device, &samplerDesc);

        BindGroupEntry* entries = stackalloc BindGroupEntry[2];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = _atlasTextureView };
        entries[1] = new BindGroupEntry { Binding = 1, Sampler = _atlasSampler };
        var desc = new BindGroupDescriptor { Layout = _atlasLayout, EntryCount = 2, Entries = entries };
        _atlasBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &desc);
    }

    public GpuMesh UploadMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
    {
        var vb  = GpuBuffer.CreateVertex(_ctx, vertices);
        var ib  = GpuBuffer.CreateIndex(_ctx, indices);
        var wfi = BuildWireframeIndices(indices);
        var wb  = GpuBuffer.CreateIndex(_ctx, wfi);
        return new GpuMesh(vb, ib, wb, (uint)indices.Length, (uint)wfi.Length);
    }

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
    /// Intended for overlay elements (selection highlight, debug gizmos). Rebinds the full-bright fallback
    /// light group (group 2) first so the highlight always reads as bright/legible instead of picking up
    /// the real (possibly shadowed) lighting of the block it outlines; a following <see cref="DrawMesh"/>
    /// call rebinds its own chunk's light buffer, so this doesn't leak into subsequent world draws.
    /// </summary>
    public void DrawMeshWireframe(GpuMesh mesh, in Mat4 model)
    {
        if (_drawIndex >= MaxObjects) return;

        ulong offset = (ulong)_drawIndex * ModelStride;
        Span<ModelUniform> s = stackalloc ModelUniform[1];
        s[0] = ModelUniform.Default(model);
        _modelBuffer.Write<ModelUniform>(offset, s);

        uint dynOffset = (uint)offset;
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset);
        _api.RenderPassEncoderSetBindGroup(_pass, 2, _lightBindGroup, 0, null);
        _api.RenderPassEncoderSetVertexBuffer(_pass, 0, mesh.VertexBuffer.Handle, 0, mesh.VertexBuffer.SizeBytes);
        _api.RenderPassEncoderSetPipeline(_pass, _wireframePipeline);
        _api.RenderPassEncoderSetIndexBuffer(_pass, mesh.WireframeBuffer.Handle, IndexFormat.Uint32, 0, mesh.WireframeBuffer.SizeBytes);
        _api.RenderPassEncoderDrawIndexed(_pass, mesh.WireframeIndexCount, 1, 0, 0, 0);
        _api.RenderPassEncoderSetPipeline(_pass, WireframeMode ? _wireframePipeline : _pipeline);
        _drawIndex++;
    }

    /// <summary>
    /// Switches to the HUD pipeline (depth always passes, no depth writes) and binds the identity camera.
    /// Uses a dedicated buffer that never changes, so the world camera uniform is not touched. Also rebinds
    /// the full-bright fallback light group (group 2), which world draws may have swapped for a per-chunk
    /// buffer — otherwise the shared fs_main shader would tint HUD geometry with whatever chunk lighting was
    /// last bound instead of drawing it at full brightness.
    /// Call this after all world-space draws; follow with <see cref="DrawHudMesh"/> calls.
    /// </summary>
    public void BeginHudPass()
    {
        _api.RenderPassEncoderSetPipeline(_pass, _hudPipeline);
        _api.RenderPassEncoderSetBindGroup(_pass, 0, _hudCameraBindGroup, 0, null);
        _api.RenderPassEncoderSetBindGroup(_pass, 2, _lightBindGroup, 0, null);
    }

    /// <summary>
    /// Draws a mesh using the HUD pipeline and its solid (triangle) indices. Call after
    /// <see cref="BeginHudPass"/>. Solid triangles rather than a GPU line list so HUD shapes (e.g. the
    /// crosshair) render at an actual on-screen thickness — WebGPU line width is fixed at 1px.
    /// </summary>
    public void DrawHudMesh(GpuMesh mesh, in Mat4 model)
    {
        if (_drawIndex >= MaxObjects) return;

        ulong offset = (ulong)_drawIndex * ModelStride;
        Span<ModelUniform> s = stackalloc ModelUniform[1];
        s[0] = ModelUniform.Default(model);
        _modelBuffer.Write<ModelUniform>(offset, s);

        uint dynOffset = (uint)offset;
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset);
        _api.RenderPassEncoderSetVertexBuffer(_pass, 0, mesh.VertexBuffer.Handle, 0, mesh.VertexBuffer.SizeBytes);
        _api.RenderPassEncoderSetIndexBuffer(_pass, mesh.IndexBuffer.Handle, IndexFormat.Uint32, 0, mesh.IndexBuffer.SizeBytes);
        _api.RenderPassEncoderDrawIndexed(_pass, mesh.IndexCount, 1, 0, 0, 0);
        _drawIndex++;
    }

    // Each triangle (i0,i1,i2) → three line segments → 6 indices.
    private static uint[] BuildWireframeIndices(ReadOnlySpan<uint> tris)
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

    /// <summary>
    /// Opens the sun shadow depth pass. Call after <see cref="SetCameraUniform"/> (the shadow shader reads
    /// <c>lightViewProj</c> from the camera uniform) and before <see cref="BeginFrame"/>. Follow with
    /// <see cref="DrawShadowMesh"/> for every opaque caster, then <see cref="EndShadowPass"/>.
    /// </summary>
    public void BeginShadowPass()
    {
        _shadow.Begin();
        _api.RenderPassEncoderSetBindGroup(_shadow.Pass, 0, _cameraBindGroup, 0, null);
        _drawIndex = 0;
    }

    /// <summary>Renders one caster into the shadow map. Reuses the dynamic-offset model buffer (the same
    /// slot is overwritten with full data for the main pass, which is submitted later).</summary>
    public void DrawShadowMesh(GpuMesh mesh, in Mat4 model)
    {
        if (_drawIndex >= MaxObjects) return;

        ulong offset = (ulong)_drawIndex * ModelStride;
        Span<ModelUniform> s = stackalloc ModelUniform[1];
        s[0] = ModelUniform.Default(model);
        _modelBuffer.Write<ModelUniform>(offset, s);

        uint dynOffset = (uint)offset;
        _api.RenderPassEncoderSetBindGroup(_shadow.Pass, 1, _modelBindGroup, 1, &dynOffset);
        _api.RenderPassEncoderSetVertexBuffer(_shadow.Pass, 0, mesh.VertexBuffer.Handle, 0, mesh.VertexBuffer.SizeBytes);
        _api.RenderPassEncoderSetIndexBuffer(_shadow.Pass, mesh.IndexBuffer.Handle, IndexFormat.Uint32, 0, mesh.IndexBuffer.SizeBytes);
        _api.RenderPassEncoderDrawIndexed(_shadow.Pass, mesh.IndexCount, 1, 0, 0, 0);
        _drawIndex++;
    }

    /// <summary>Ends and submits the shadow pass so the map is ready for the main pass to sample.</summary>
    public void EndShadowPass() => _shadow.End();

    public bool BeginFrame()
    {
        if (!_ctx.AcquireCurrentView())
        {
            _ctx.Configure(_ctx.Size);
            return false;
        }
        _drawIndex = 0;

        var encDesc = new CommandEncoderDescriptor();
        _encoder = _api.DeviceCreateCommandEncoder(_ctx.Device, &encDesc);

        var colorAtt = new RenderPassColorAttachment
        {
            View = _ctx.CurrentView,
            DepthSlice = uint.MaxValue, // WGPU_DEPTH_SLICE_UNDEFINED
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            // Clear to a sky-blue colour
            ClearValue = new Color { R = 0.10, G = 0.3078, B = 0.4804, A = 1.0 },
        };
        var depthAtt = new RenderPassDepthStencilAttachment
        {
            View = _ctx.DepthView,
            DepthLoadOp = LoadOp.Clear,
            DepthStoreOp = StoreOp.Store,
            DepthClearValue = 1.0f,
        };
        var passDesc = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAtt,
            DepthStencilAttachment = &depthAtt,
        };
        _pass = _api.CommandEncoderBeginRenderPass(_encoder, &passDesc);
        _api.RenderPassEncoderSetPipeline(_pass, WireframeMode ? _wireframePipeline : _pipeline);
        _api.RenderPassEncoderSetBindGroup(_pass, 0, _cameraBindGroup, 0, null);
        // Group 2 (light) is the same for every draw in Phase 4.1 and persists across pipeline
        // switches (all pipelines share the layout), so bind it once here.
        _api.RenderPassEncoderSetBindGroup(_pass, 2, _lightBindGroup, 0, null);
        // Group 3 (sun shadow map) is constant for the frame; bind once.
        _api.RenderPassEncoderSetBindGroup(_pass, 3, _shadowBindGroup, 0, null);
        // Group 4 (block texture array) is constant for the frame; bind once.
        _api.RenderPassEncoderSetBindGroup(_pass, 4, _atlasBindGroup, 0, null);
        return true;
    }

    public void SetCameraUniform(in CameraUniform camera)
    {
        Span<CameraUniform> s = stackalloc CameraUniform[1];
        s[0] = camera;
        _cameraBuffer.Write<CameraUniform>(0, s);
        LastLightViewProj = camera.LightViewProj;
    }

    /// <summary>The sun light-space view-projection from the most recent <see cref="SetCameraUniform"/> — the
    /// matrix the current shadow map was rendered with. The sun-vis compute pass projects voxels through this.</summary>
    public Mat4 LastLightViewProj { get; private set; }

    /// <summary>Opaque handle to the sun shadow-map depth view (a <c>texture_depth_2d</c>), bound by the
    /// sun-vis compute pass. Constant for the renderer's lifetime.</summary>
    internal nint ShadowDepthView => (nint)_shadow.DepthView;

    /// <summary>Creates a group-2 bind group over a volume's <paramref name="lightBuffer"/> (binding 0),
    /// <paramref name="opacityBuffer"/> (binding 1, for in-shader AO) and <paramref name="sunVisBuffer"/>
    /// (binding 2, per-voxel sun visibility); returns an opaque handle. The caller owns its lifetime
    /// (release via the buffer's owner).</summary>
    public nint CreateLightBindGroup(GpuBuffer lightBuffer, GpuBuffer opacityBuffer, GpuBuffer sunVisBuffer)
    {
        BindGroupEntry* entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 0, Buffer = lightBuffer.Handle,   Offset = 0, Size = lightBuffer.SizeBytes };
        entries[1] = new BindGroupEntry { Binding = 1, Buffer = opacityBuffer.Handle, Offset = 0, Size = opacityBuffer.SizeBytes };
        entries[2] = new BindGroupEntry { Binding = 2, Buffer = sunVisBuffer.Handle,  Offset = 0, Size = sunVisBuffer.SizeBytes };
        var desc = new BindGroupDescriptor { Layout = _lightLayout, EntryCount = 3, Entries = entries };
        return (nint)_api.DeviceCreateBindGroup(_ctx.Device, &desc);
    }

    /// <summary>
    /// Draws a mesh with per-volume lighting. <paramref name="lightBindGroup"/> (group 2) should be the
    /// volume's LightA bind group; 0 falls back to the shared full-bright buffer.
    /// <paramref name="chunkBase"/> is the chunk's voxel origin within the volume;
    /// <paramref name="volSize"/> is the volume dimensions in voxels.
    /// </summary>
    public void DrawMesh(GpuMesh mesh, in Mat4 model, nint lightBindGroup,
                         int cbx, int cby, int cbz,
                         int vsx, int vsy, int vsz)
    {
        if (_drawIndex >= MaxObjects) return;

        ulong offset = (ulong)_drawIndex * ModelStride;
        Span<ModelUniform> s = stackalloc ModelUniform[1];
        s[0] = new ModelUniform
        {
            Model      = model,
            ChunkBaseX = cbx, ChunkBaseY = cby, ChunkBaseZ = cbz, _Pad0 = 0,
            VolSizeX   = vsx, VolSizeY   = vsy, VolSizeZ   = vsz, _Pad1 = 0,
        };
        _modelBuffer.Write<ModelUniform>(offset, s);

        uint dynOffset = (uint)offset;
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset);
        var lbg = lightBindGroup != 0 ? (BindGroup*)lightBindGroup : _lightBindGroup;
        _api.RenderPassEncoderSetBindGroup(_pass, 2, lbg, 0, null);
        _api.RenderPassEncoderSetVertexBuffer(_pass, 0, mesh.VertexBuffer.Handle, 0, mesh.VertexBuffer.SizeBytes);

        var idxBuf   = WireframeMode ? mesh.WireframeBuffer : mesh.IndexBuffer;
        var idxCount = WireframeMode ? mesh.WireframeIndexCount : mesh.IndexCount;
        _api.RenderPassEncoderSetIndexBuffer(_pass, idxBuf.Handle, IndexFormat.Uint32, 0, idxBuf.SizeBytes);
        _api.RenderPassEncoderDrawIndexed(_pass, idxCount, 1, 0, 0, 0);
        _drawIndex++;
    }

    public void EndFrame()
    {
        _api.RenderPassEncoderEnd(_pass);
        _api.RenderPassEncoderRelease(_pass);
        _pass = null;

        var cmdDesc = new CommandBufferDescriptor();
        var cmd = _api.CommandEncoderFinish(_encoder, &cmdDesc);
        _api.QueueSubmit(_ctx.Queue, 1, &cmd);
        _api.CommandBufferRelease(cmd);
        _api.CommandEncoderRelease(_encoder);
        _encoder = null;

        _ctx.Present();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ModelUniform
    {
        public Mat4 Model;
        // WGSL pads vec3<i32> to 16 bytes, so each vec3 needs an explicit int pad.
        public int ChunkBaseX, ChunkBaseY, ChunkBaseZ, _Pad0;
        public int VolSizeX,   VolSizeY,   VolSizeZ,   _Pad1;
        // sizeof = 64 + 16 + 16 = 96 == ModelSize

        /// <summary>Safe default for non-chunk draws: chunkBase=(0,0,0), volSize=(32,32,32).
        /// Keeps the fragment shader from sampling out-of-bounds on the full-bright buffer.</summary>
        public static ModelUniform Default(in Mat4 m) => new()
        {
            Model      = m,
            ChunkBaseX = 0, ChunkBaseY = 0, ChunkBaseZ = 0, _Pad0 = 0,
            VolSizeX   = 32, VolSizeY  = 32, VolSizeZ  = 32, _Pad1 = 0,
        };
    }

    public void OnResize(Vector2D<int> size) => _ctx.Configure(size);

    public void Dispose()
    {
        _cameraBuffer.Dispose();
        _hudCameraBuffer.Dispose();
        _modelBuffer.Dispose();
        _fullBrightLight.Dispose();
        _fullAirOpacity.Dispose();
        _fullLitSunVis.Dispose();
        _shadow.Dispose();
        if (_atlasBindGroup   != null) _api.BindGroupRelease(_atlasBindGroup);
        if (_atlasSampler     != null) _api.SamplerRelease(_atlasSampler);
        if (_atlasTextureView != null) _api.TextureViewRelease(_atlasTextureView);
        if (_atlasTexture     != null) _api.TextureRelease(_atlasTexture);
        if (_hudPipeline        != null) _api.RenderPipelineRelease(_hudPipeline);
        if (_wireframePipeline  != null) _api.RenderPipelineRelease(_wireframePipeline);
        if (_pipeline           != null) _api.RenderPipelineRelease(_pipeline);
    }
}
