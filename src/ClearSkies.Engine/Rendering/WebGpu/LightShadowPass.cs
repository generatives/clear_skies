using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// Per-lamp omnidirectional shadow map (Phase 4.4 cross-volume light). A point lamp radiates in every
/// direction, so its occlusion is captured as a <b>cube</b> of six 90°-ish perspective faces rendered from the
/// lamp's world position against the real posed scene geometry (every volume at once — that is what makes
/// cross-grid occlusion exact). Each face stores, per texel, the <b>linear distance</b> from the lamp to the
/// nearest caster surface. The cross-volume injection compute pass then, for each candidate voxel, finds the
/// face whose frustum owns the lamp→voxel direction and compares the voxel's distance to the stored nearest
/// distance.
///
/// <para>Linear distance (not projective depth) is used so the injection can ignore occluders closer than ~1
/// unit — those are the lamp's <i>own</i> solid block walls (the lamp sits at a solid block's centre, whose
/// faces are 0.5 away in every direction). Without that, the lamp would shadow itself completely. Everything
/// past ~1 unit (the rest of the source ship's hull, the target, other grids) still occludes correctly.</para>
///
/// One six-layer set is reused for every lamp: render a lamp's cube, inject it, render the next over the top.
/// Queue-submission order guarantees an injection reads the cube rendered immediately before it.
///
/// Self-contained: owns its own face and model uniform buffers (both dynamic-offset).
/// </summary>
internal sealed unsafe class LightShadowPass : IDisposable
{
    public const uint MapSize = 1024;
    public const int  Faces   = 6;

    private const int   MaxCasters = 4096;
    private const ulong Stride     = 256;   // >= minUniformBufferOffsetAlignment

    private const float NoOccluder = 60000f; // cleared distance: "nothing in this direction" (within fp16 range)

    // Stores linear lamp→surface distance into an R32Float colour target, depth-tested so the nearest surface
    // wins. Position is carried to the fragment in world space for the distance computation.
    private const string Wgsl = @"
struct Face  { vp: mat4x4<f32>, lampPos: vec4<f32> };
@group(0) @binding(0) var<uniform> face: Face;

struct Model { m: mat4x4<f32> };
@group(1) @binding(0) var<uniform> model: Model;

struct VSOut { @builtin(position) pos: vec4<f32>, @location(0) world: vec3<f32> };

@vertex
fn vs_main(@location(0) position: vec3<f32>) -> VSOut {
    var o: VSOut;
    let w   = model.m * vec4<f32>(position, 1.0);
    o.world = w.xyz;
    o.pos   = face.vp * w;
    return o;
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4<f32> {
    return vec4<f32>(length(in.world - face.lampPos.xyz), 0.0, 0.0, 0.0);
}";

    private readonly GpuContext _ctx;
    private readonly WebGPU _api;

    private ShaderModule*    _shader;
    private BindGroupLayout* _faceLayout;
    private BindGroupLayout* _modelLayout;
    private PipelineLayout*  _pipelineLayout;
    private RenderPipeline*  _pipeline;

    private Texture*       _distTexture;                 // R32Float, 6 layers
    private TextureView*[] _distFaceViews = new TextureView*[Faces]; // per-layer colour attachments
    private TextureView*   _distArrayView;               // 2D-array view for compute sampling
    private Texture*       _depthTexture;                // single-layer depth, reused per face for z-test
    private TextureView*   _depthView;

    private readonly GpuBuffer _faceBuffer;   // per-face { vp, lampPos } (one Stride slot each)
    private readonly GpuBuffer _modelBuffer;  // one model matrix per caster
    private BindGroup* _faceBindGroup;
    private BindGroup* _modelBindGroup;

    /// <summary>Raw <c>TextureView*</c> of the six-layer distance array, for the injection compute bind group.</summary>
    internal nint DistanceArrayViewHandle => (nint)_distArrayView;

    public LightShadowPass(GpuContext ctx)
    {
        _ctx = ctx;
        _api = ctx.Api;

        CreateTargets();
        _shader = CreateShader(Wgsl);
        CreateLayouts();
        _pipeline = CreatePipeline();

        _faceBuffer  = GpuBuffer.CreateUniform(ctx, Stride * Faces);
        _modelBuffer = GpuBuffer.CreateUniform(ctx, Stride * MaxCasters);
        CreateBindGroups();
    }

    private void CreateTargets()
    {
        var distDesc = new TextureDescriptor
        {
            Usage         = TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            Dimension     = TextureDimension.Dimension2D,
            Size          = new Extent3D(MapSize, MapSize, Faces),
            Format        = TextureFormat.R16float,
            MipLevelCount = 1,
            SampleCount   = 1,
        };
        _distTexture = _api.DeviceCreateTexture(_ctx.Device, &distDesc);

        for (uint f = 0; f < Faces; f++)
        {
            var fv = new TextureViewDescriptor
            {
                Format          = TextureFormat.R16float,
                Dimension       = TextureViewDimension.Dimension2D,
                BaseMipLevel    = 0, MipLevelCount   = 1,
                BaseArrayLayer  = f, ArrayLayerCount = 1,
                Aspect          = TextureAspect.All,
            };
            _distFaceViews[f] = _api.TextureCreateView(_distTexture, &fv);
        }

        var av = new TextureViewDescriptor
        {
            Format          = TextureFormat.R16float,
            Dimension       = TextureViewDimension.Dimension2DArray,
            BaseMipLevel    = 0, MipLevelCount   = 1,
            BaseArrayLayer  = 0, ArrayLayerCount = Faces,
            Aspect          = TextureAspect.All,
        };
        _distArrayView = _api.TextureCreateView(_distTexture, &av);

        var depthDesc = new TextureDescriptor
        {
            Usage         = TextureUsage.RenderAttachment,
            Dimension     = TextureDimension.Dimension2D,
            Size          = new Extent3D(MapSize, MapSize, 1),
            Format        = _ctx.DepthFormat,
            MipLevelCount = 1,
            SampleCount   = 1,
        };
        _depthTexture = _api.DeviceCreateTexture(_ctx.Device, &depthDesc);
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

    private void CreateLayouts()
    {
        var faceEntry = new BindGroupLayoutEntry
        {
            Binding    = 0,
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment, // fragment reads lampPos
            Buffer     = new BufferBindingLayout { Type = BufferBindingType.Uniform, HasDynamicOffset = true, MinBindingSize = 80 },
        };
        var faceDesc = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &faceEntry };
        _faceLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &faceDesc);

        var modelEntry = new BindGroupLayoutEntry
        {
            Binding    = 0,
            Visibility = ShaderStage.Vertex,
            Buffer     = new BufferBindingLayout { Type = BufferBindingType.Uniform, HasDynamicOffset = true, MinBindingSize = 64 },
        };
        var modelDesc = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &modelEntry };
        _modelLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &modelDesc);

        BindGroupLayout** layouts = stackalloc BindGroupLayout*[2];
        layouts[0] = _faceLayout;
        layouts[1] = _modelLayout;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 2, BindGroupLayouts = layouts };
        _pipelineLayout = _api.DeviceCreatePipelineLayout(_ctx.Device, &plDesc);
    }

    private RenderPipeline* CreatePipeline()
    {
        var posAttr  = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 };
        var vbLayout = new VertexBufferLayout { ArrayStride = 36, StepMode = VertexStepMode.Vertex, AttributeCount = 1, Attributes = &posAttr };

        var vsEntry     = (byte*)SilkMarshal.StringToPtr("vs_main", NativeStringEncoding.UTF8);
        var fsEntry     = (byte*)SilkMarshal.StringToPtr("fs_main", NativeStringEncoding.UTF8);
        var vertexState = new VertexState { Module = _shader, EntryPoint = vsEntry, BufferCount = 1, Buffers = &vbLayout };

        var colorTarget = new ColorTargetState { Format = TextureFormat.R16float, Blend = null, WriteMask = ColorWriteMask.All };
        var fragState   = new FragmentState { Module = _shader, EntryPoint = fsEntry, TargetCount = 1, Targets = &colorTarget };

        var keep  = StencilOperation.Keep;
        var depth = new DepthStencilState
        {
            Format              = _ctx.DepthFormat,
            DepthWriteEnabled   = true,
            DepthCompare        = CompareFunction.Less,
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
            Fragment     = &fragState,
        };
        var pipeline = _api.DeviceCreateRenderPipeline(_ctx.Device, &desc);

        SilkMarshal.Free((nint)vsEntry);
        SilkMarshal.Free((nint)fsEntry);
        return pipeline;
    }

    private void CreateBindGroups()
    {
        var faceEntry = new BindGroupEntry { Binding = 0, Buffer = _faceBuffer.Handle, Offset = 0, Size = 80 };
        var faceDesc  = new BindGroupDescriptor { Layout = _faceLayout, EntryCount = 1, Entries = &faceEntry };
        _faceBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &faceDesc);

        var modelEntry = new BindGroupEntry { Binding = 0, Buffer = _modelBuffer.Handle, Offset = 0, Size = 64 };
        var modelDesc  = new BindGroupDescriptor { Layout = _modelLayout, EntryCount = 1, Entries = &modelEntry };
        _modelBindGroup = _api.DeviceCreateBindGroup(_ctx.Device, &modelDesc);
    }

    // Cube-face look directions and up vectors. Up is chosen only to be non-parallel to the direction; the
    // injection pass reuses the exact matrices, so no specific cube-map convention is required.
    private static readonly Vector3D<float>[] Dirs =
    {
        new( 1, 0, 0), new(-1, 0, 0),
        new( 0, 1, 0), new( 0,-1, 0),
        new( 0, 0, 1), new( 0, 0,-1),
    };
    private static readonly Vector3D<float>[] Ups =
    {
        new(0, 1, 0), new(0, 1, 0),
        new(0, 0, 1), new(0, 0, 1),
        new(0, 1, 0), new(0, 1, 0),
    };

    /// <summary>
    /// Builds the six face view-projections for a lamp at <paramref name="lampWorld"/> reaching
    /// <paramref name="radius"/> world units, into <paramref name="outFaceVP"/> (length 6). 91°-ish FOV slightly
    /// overscans each 90° face so directions near cube edges are covered by at least one face.
    /// </summary>
    public static void BuildFaceMatrices(Vector3D<float> lampWorld, float radius, Span<Mat4> outFaceVP)
    {
        float fov  = (MathF.PI / 2f) * 1.02f;
        float far  = radius + 2f;
        var   proj = Mat4.PerspectiveRhZo(fov, 1f, 0.05f, far);
        for (int f = 0; f < Faces; f++)
        {
            var view = Mat4.LookAtRh(lampWorld, lampWorld + Dirs[f], Ups[f]);
            outFaceVP[f] = Mat4.Multiply(proj, view);
        }
    }

    /// <summary>
    /// Renders the six cube faces for <paramref name="lampWorld"/> into the (reused) distance array, drawing
    /// every caster each face. <paramref name="faceVP"/> must be the six matrices from
    /// <see cref="BuildFaceMatrices"/> (the caller keeps them to feed the injection pass). Submits one command
    /// buffer.
    /// </summary>
    public void RenderCube(Vector3D<float> lampWorld, ReadOnlySpan<Mat4> faceVP, IReadOnlyList<(GpuMesh mesh, Mat4 model)> casters)
    {
        int n = System.Math.Min(casters.Count, MaxCasters);

        // Upload all six face uniforms ({vp, lampPos}) and every caster model up front, so the single encoder
        // below selects the right slot per face / per caster via dynamic offset.
        Span<FaceUniform> fu = stackalloc FaceUniform[1];
        for (int f = 0; f < Faces; f++)
        {
            fu[0] = new FaceUniform { Vp = faceVP[f], LampX = lampWorld.X, LampY = lampWorld.Y, LampZ = lampWorld.Z, LampW = 0f };
            _faceBuffer.Write<FaceUniform>((ulong)f * Stride, fu);
        }
        Span<Mat4> one = stackalloc Mat4[1];
        for (int c = 0; c < n; c++) { one[0] = casters[c].model; _modelBuffer.Write<Mat4>((ulong)c * Stride, one); }

        var encDesc = new CommandEncoderDescriptor();
        var encoder = _api.DeviceCreateCommandEncoder(_ctx.Device, &encDesc);

        for (uint f = 0; f < Faces; f++)
        {
            var colorAtt = new RenderPassColorAttachment
            {
                View       = _distFaceViews[f],
                DepthSlice = uint.MaxValue, // WGPU_DEPTH_SLICE_UNDEFINED
                LoadOp     = LoadOp.Clear,
                StoreOp    = StoreOp.Store,
                ClearValue = new Color { R = NoOccluder, G = 0, B = 0, A = 0 },
            };
            var depthAtt = new RenderPassDepthStencilAttachment
            {
                View            = _depthView,
                DepthLoadOp     = LoadOp.Clear,
                DepthStoreOp    = StoreOp.Store,
                DepthClearValue = 1.0f,
            };
            var passDesc = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &colorAtt, DepthStencilAttachment = &depthAtt };
            var pass = _api.CommandEncoderBeginRenderPass(encoder, &passDesc);
            _api.RenderPassEncoderSetPipeline(pass, _pipeline);

            uint faceOffset = (uint)((ulong)f * Stride);
            _api.RenderPassEncoderSetBindGroup(pass, 0, _faceBindGroup, 1, &faceOffset);

            for (int c = 0; c < n; c++)
            {
                var (mesh, _) = casters[c];
                uint modelOffset = (uint)((ulong)c * Stride);
                _api.RenderPassEncoderSetBindGroup(pass, 1, _modelBindGroup, 1, &modelOffset);
                _api.RenderPassEncoderSetVertexBuffer(pass, 0, mesh.VertexBuffer.Handle, 0, mesh.VertexBuffer.SizeBytes);
                _api.RenderPassEncoderSetIndexBuffer(pass, mesh.IndexBuffer.Handle, IndexFormat.Uint32, 0, mesh.IndexBuffer.SizeBytes);
                _api.RenderPassEncoderDrawIndexed(pass, mesh.IndexCount, 1, 0, 0, 0);
            }

            _api.RenderPassEncoderEnd(pass);
            _api.RenderPassEncoderRelease(pass);
        }

        var cmdDesc = new CommandBufferDescriptor();
        var cmd = _api.CommandEncoderFinish(encoder, &cmdDesc);
        _api.QueueSubmit(_ctx.Queue, 1, &cmd);
        _api.CommandBufferRelease(cmd);
        _api.CommandEncoderRelease(encoder);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FaceUniform
    {
        public Mat4  Vp;
        public float LampX, LampY, LampZ, LampW; // vec4<f32> lampPos
    }

    public void Dispose()
    {
        if (_pipeline       != null) { _api.RenderPipelineRelease(_pipeline);        _pipeline       = null; }
        if (_pipelineLayout != null) { _api.PipelineLayoutRelease(_pipelineLayout);  _pipelineLayout = null; }
        if (_faceLayout     != null) { _api.BindGroupLayoutRelease(_faceLayout);     _faceLayout     = null; }
        if (_modelLayout    != null) { _api.BindGroupLayoutRelease(_modelLayout);    _modelLayout    = null; }
        if (_shader         != null) { _api.ShaderModuleRelease(_shader);            _shader         = null; }
        if (_distArrayView  != null) { _api.TextureViewRelease(_distArrayView);      _distArrayView  = null; }
        for (int f = 0; f < Faces; f++)
            if (_distFaceViews[f] != null) { _api.TextureViewRelease(_distFaceViews[f]); _distFaceViews[f] = null; }
        if (_distTexture    != null) { _api.TextureRelease(_distTexture);            _distTexture    = null; }
        if (_depthView      != null) { _api.TextureViewRelease(_depthView);          _depthView      = null; }
        if (_depthTexture   != null) { _api.TextureRelease(_depthTexture);           _depthTexture   = null; }
        _faceBuffer.Dispose();
        _modelBuffer.Dispose();
    }
}
