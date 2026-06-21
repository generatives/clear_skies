using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// Directional-sun shadow map. Renders all world geometry depth-only from the sun's point of view into a
/// fixed-size depth texture using the light-space view-projection stored in the camera uniform
/// (<c>camera.lightViewProj</c>). The main fragment shader then projects each fragment's <b>voxel-center</b>
/// world position through the same matrix and does a hard depth test against this texture — sampling the
/// voxel center (not the interpolated fragment position) is what makes the shadow edges land on voxel
/// boundaries instead of smoothly following the surface.
///
/// Owns only the depth target and the depth-only pipeline. The <see cref="Renderer"/> drives it: it binds
/// the (shared) camera + model bind groups and records the draws, reusing its own dynamic-offset model
/// buffer so no geometry data is duplicated.
/// </summary>
internal sealed unsafe class SunShadowPass : IDisposable
{
    // 2048² over a ~320-unit light frustum → ~6 texels per 1-unit voxel: crisp at voxel resolution.
    public const uint MapSize = 2048;

    // Depth-only pass: just transform position by lightViewProj * model. No fragment stage.
    private const string Wgsl = @"
struct Camera { view: mat4x4<f32>, proj: mat4x4<f32>, sunDir: vec4<f32>, lightViewProj: mat4x4<f32> };
@group(0) @binding(0) var<uniform> camera: Camera;

struct Model { model: mat4x4<f32>, chunkBase: vec3<i32>, _p0: i32, volSize: vec3<i32>, _p1: i32 };
@group(1) @binding(0) var<uniform> model: Model;

@vertex
fn vs_main(@location(0) position: vec3<f32>) -> @builtin(position) vec4<f32> {
    return camera.lightViewProj * model.model * vec4<f32>(position, 1.0);
}";

    private readonly GpuContext _ctx;
    private readonly WebGPU _api;

    private ShaderModule*  _shader;
    private PipelineLayout* _pipelineLayout;
    private RenderPipeline* _pipeline;

    private Texture*     _depthTexture;
    private TextureView* _depthView;

    private CommandEncoder*   _encoder;
    private RenderPassEncoder* _pass;

    /// <summary>The shadow depth texture view — bound (group 3) by the renderer for sampling.</summary>
    internal TextureView* DepthView => _depthView;

    /// <summary>The active depth pass between <see cref="Begin"/> and <see cref="End"/>; the renderer
    /// records its caster draws into this.</summary>
    internal RenderPassEncoder* Pass => _pass;

    public SunShadowPass(GpuContext ctx, BindGroupLayout* cameraLayout, BindGroupLayout* modelLayout)
    {
        _ctx = ctx;
        _api = ctx.Api;

        CreateDepthTarget();
        _shader = CreateShader(Wgsl);

        BindGroupLayout** layouts = stackalloc BindGroupLayout*[2];
        layouts[0] = cameraLayout;
        layouts[1] = modelLayout;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 2, BindGroupLayouts = layouts };
        _pipelineLayout = _api.DeviceCreatePipelineLayout(_ctx.Device, &plDesc);

        _pipeline = CreatePipeline();
    }

    private void CreateDepthTarget()
    {
        var desc = new TextureDescriptor
        {
            Usage         = TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            Dimension     = TextureDimension.Dimension2D,
            Size          = new Extent3D(MapSize, MapSize, 1),
            Format        = _ctx.DepthFormat,
            MipLevelCount = 1,
            SampleCount   = 1,
        };
        _depthTexture = _api.DeviceCreateTexture(_ctx.Device, &desc);
        _depthView    = _api.TextureCreateView(_depthTexture, null);
    }

    private ShaderModule* CreateShader(string wgsl)
    {
        var code = (byte*)SilkMarshal.StringToPtr(wgsl, NativeStringEncoding.UTF8);
        var wgslDesc = new ShaderModuleWGSLDescriptor
        {
            Chain = new ChainedStruct { SType = SType.ShaderModuleWgslDescriptor },
            Code  = code,
        };
        var desc = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgslDesc };
        var module = _api.DeviceCreateShaderModule(_ctx.Device, &desc);
        SilkMarshal.Free((nint)code);
        return module;
    }

    private RenderPipeline* CreatePipeline()
    {
        // Only position is needed; the full 36-byte vertex stride is kept so chunk/cube meshes bind as-is.
        var posAttr  = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 };
        var vbLayout = new VertexBufferLayout { ArrayStride = 36, StepMode = VertexStepMode.Vertex, AttributeCount = 1, Attributes = &posAttr };

        var vsEntry     = (byte*)SilkMarshal.StringToPtr("vs_main", NativeStringEncoding.UTF8);
        var vertexState = new VertexState { Module = _shader, EntryPoint = vsEntry, BufferCount = 1, Buffers = &vbLayout };

        var keep  = StencilOperation.Keep;
        var depth = new DepthStencilState
        {
            Format            = _ctx.DepthFormat,
            DepthWriteEnabled = true,
            DepthCompare      = CompareFunction.Less,
            // Constant + slope-scaled bias pushes casters away from the light to kill shadow acne; combined
            // with the half-voxel test offset in the main shader this keeps lit voxel faces self-shadow free.
            DepthBias           = 2,
            DepthBiasSlopeScale = 2.0f,
            DepthBiasClamp      = 0.0f,
            StencilFront = new StencilFaceState { Compare = CompareFunction.Always, FailOp = keep, DepthFailOp = keep, PassOp = keep },
            StencilBack  = new StencilFaceState { Compare = CompareFunction.Always, FailOp = keep, DepthFailOp = keep, PassOp = keep },
        };

        var desc = new RenderPipelineDescriptor
        {
            Layout    = _pipelineLayout,
            Vertex    = vertexState,
            Primitive = new PrimitiveState
            {
                Topology         = PrimitiveTopology.TriangleList,
                StripIndexFormat = IndexFormat.Undefined,
                FrontFace        = FrontFace.Ccw,
                CullMode         = CullMode.Back,
            },
            DepthStencil = &depth,
            Multisample  = new MultisampleState { Count = 1, Mask = ~0u, AlphaToCoverageEnabled = false },
            Fragment     = null, // depth-only
        };
        var pipeline = _api.DeviceCreateRenderPipeline(_ctx.Device, &desc);

        SilkMarshal.Free((nint)vsEntry);
        return pipeline;
    }

    /// <summary>Opens a fresh command encoder + depth pass, clearing the shadow map, and sets the pipeline.</summary>
    public void Begin()
    {
        var encDesc = new CommandEncoderDescriptor();
        _encoder = _api.DeviceCreateCommandEncoder(_ctx.Device, &encDesc);

        var depthAtt = new RenderPassDepthStencilAttachment
        {
            View            = _depthView,
            DepthLoadOp     = LoadOp.Clear,
            DepthStoreOp    = StoreOp.Store,
            DepthClearValue = 1.0f,
        };
        var passDesc = new RenderPassDescriptor
        {
            ColorAttachmentCount   = 0,
            ColorAttachments       = null,
            DepthStencilAttachment = &depthAtt,
        };
        _pass = _api.CommandEncoderBeginRenderPass(_encoder, &passDesc);
        _api.RenderPassEncoderSetPipeline(_pass, _pipeline);
    }

    /// <summary>Ends the depth pass and submits it (so the map is ready before the main pass samples it).</summary>
    public void End()
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
    }

    public void Dispose()
    {
        if (_pipeline       != null) { _api.RenderPipelineRelease(_pipeline);   _pipeline       = null; }
        if (_pipelineLayout != null) { _api.PipelineLayoutRelease(_pipelineLayout); _pipelineLayout = null; }
        if (_shader         != null) { _api.ShaderModuleRelease(_shader);       _shader         = null; }
        if (_depthView      != null) { _api.TextureViewRelease(_depthView);     _depthView      = null; }
        if (_depthTexture   != null) { _api.TextureRelease(_depthTexture);      _depthTexture   = null; }
    }
}
