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
    private const ulong CameraSize = 128;    // two mat4x4<f32>
    private const ulong ModelSize = 64;      // one mat4x4<f32>

    private const string Wgsl = @"
struct Camera { view: mat4x4<f32>, proj: mat4x4<f32> };
@group(0) @binding(0) var<uniform> camera: Camera;
struct Model { model: mat4x4<f32> };
@group(1) @binding(0) var<uniform> model: Model;

struct VSOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) color: vec3<f32>,
};

@vertex
fn vs_main(@location(0) position: vec3<f32>, @location(1) normal: vec3<f32>, @location(2) color: vec3<f32>) -> VSOut {
    var o: VSOut;
    o.pos = camera.proj * camera.view * model.model * vec4<f32>(position, 1.0);
    o.color = color;
    return o;
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4<f32> {
    return vec4<f32>(in.color, 1.0);
}
";

    private readonly GpuContext _ctx;
    private readonly WebGPU _api;

    private ShaderModule* _shader;
    private BindGroupLayout* _cameraLayout;
    private BindGroupLayout* _modelLayout;
    private PipelineLayout* _pipelineLayout;
    private RenderPipeline* _pipeline;
    private RenderPipeline* _wireframePipeline;
    private RenderPipeline* _hudPipeline;

    public bool WireframeMode { get; set; }

    private readonly GpuBuffer _cameraBuffer;
    private readonly GpuBuffer _hudCameraBuffer; // permanently holds identity view+proj
    private readonly GpuBuffer _modelBuffer;
    private BindGroup* _cameraBindGroup;
    private BindGroup* _hudCameraBindGroup;
    private BindGroup* _modelBindGroup;

    private CommandEncoder* _encoder;
    private RenderPassEncoder* _pass;
    private int _drawIndex;

    public float AspectRatio => _ctx.Size.Y <= 0 ? 1f : (float)_ctx.Size.X / _ctx.Size.Y;

    public Renderer(GpuContext ctx)
    {
        _ctx = ctx;
        _api = ctx.Api;

        _shader = CreateShader(Wgsl);
        CreateLayouts();
        _pipeline          = CreatePipeline(PrimitiveTopology.TriangleList, CullMode.Back);
        _wireframePipeline = CreatePipeline(PrimitiveTopology.LineList,     CullMode.None);
        _hudPipeline       = CreatePipeline(PrimitiveTopology.LineList,     CullMode.None, depthTest: false);

        _cameraBuffer    = GpuBuffer.CreateUniform(ctx, CameraSize);
        _hudCameraBuffer = GpuBuffer.CreateUniform(ctx, CameraSize);
        _modelBuffer     = GpuBuffer.CreateUniform(ctx, ModelStride * MaxObjects);
        CreateBindGroups();

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
            Visibility = ShaderStage.Vertex,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, HasDynamicOffset = false, MinBindingSize = CameraSize },
        };
        var camDesc = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &camEntry };
        _cameraLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &camDesc);

        var modelEntry = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Vertex,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, HasDynamicOffset = true, MinBindingSize = ModelSize },
        };
        var modelDesc = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &modelEntry };
        _modelLayout = _api.DeviceCreateBindGroupLayout(_ctx.Device, &modelDesc);

        BindGroupLayout** layouts = stackalloc BindGroupLayout*[2];
        layouts[0] = _cameraLayout;
        layouts[1] = _modelLayout;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 2, BindGroupLayouts = layouts };
        _pipelineLayout = _api.DeviceCreatePipelineLayout(_ctx.Device, &plDesc);
    }

    private RenderPipeline* CreatePipeline(PrimitiveTopology topology, CullMode cullMode, bool depthTest = true)
    {
        VertexAttribute* attrs = stackalloc VertexAttribute[3];
        attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0,  ShaderLocation = 0 };
        attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 12, ShaderLocation = 1 };
        attrs[2] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 24, ShaderLocation = 2 };
        var vbLayout = new VertexBufferLayout { ArrayStride = 36, StepMode = VertexStepMode.Vertex, AttributeCount = 3, Attributes = attrs };

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
    /// Intended for overlay elements (selection highlight, debug gizmos).
    /// </summary>
    public void DrawMeshWireframe(GpuMesh mesh, in Mat4 model)
    {
        if (_drawIndex >= MaxObjects) return;

        ulong offset = (ulong)_drawIndex * ModelStride;
        Span<Mat4> s = stackalloc Mat4[1];
        s[0] = model;
        _modelBuffer.Write<Mat4>(offset, s);

        uint dynOffset = (uint)offset;
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset);
        _api.RenderPassEncoderSetVertexBuffer(_pass, 0, mesh.VertexBuffer.Handle, 0, mesh.VertexBuffer.SizeBytes);
        _api.RenderPassEncoderSetPipeline(_pass, _wireframePipeline);
        _api.RenderPassEncoderSetIndexBuffer(_pass, mesh.WireframeBuffer.Handle, IndexFormat.Uint32, 0, mesh.WireframeBuffer.SizeBytes);
        _api.RenderPassEncoderDrawIndexed(_pass, mesh.WireframeIndexCount, 1, 0, 0, 0);
        _api.RenderPassEncoderSetPipeline(_pass, WireframeMode ? _wireframePipeline : _pipeline);
        _drawIndex++;
    }

    /// <summary>
    /// Switches to the HUD pipeline (depth always passes, no depth writes) and binds the identity camera.
    /// Uses a dedicated buffer that never changes, so the world camera uniform is not touched.
    /// Call this after all world-space draws; follow with <see cref="DrawHudMesh"/> calls.
    /// </summary>
    public void BeginHudPass()
    {
        _api.RenderPassEncoderSetPipeline(_pass, _hudPipeline);
        _api.RenderPassEncoderSetBindGroup(_pass, 0, _hudCameraBindGroup, 0, null);
    }

    /// <summary>Draws a mesh using the HUD pipeline and its wireframe indices. Call after <see cref="BeginHudPass"/>.</summary>
    public void DrawHudMesh(GpuMesh mesh, in Mat4 model)
    {
        if (_drawIndex >= MaxObjects) return;

        ulong offset = (ulong)_drawIndex * ModelStride;
        Span<Mat4> s = stackalloc Mat4[1];
        s[0] = model;
        _modelBuffer.Write<Mat4>(offset, s);

        uint dynOffset = (uint)offset;
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset);
        _api.RenderPassEncoderSetVertexBuffer(_pass, 0, mesh.VertexBuffer.Handle, 0, mesh.VertexBuffer.SizeBytes);
        _api.RenderPassEncoderSetIndexBuffer(_pass, mesh.WireframeBuffer.Handle, IndexFormat.Uint32, 0, mesh.WireframeBuffer.SizeBytes);
        _api.RenderPassEncoderDrawIndexed(_pass, mesh.WireframeIndexCount, 1, 0, 0, 0);
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
            ClearValue = new Color { R = 0.10, G = 0.12, B = 0.16, A = 1.0 },
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
        return true;
    }

    public void SetCameraUniform(in CameraUniform camera)
    {
        Span<CameraUniform> s = stackalloc CameraUniform[1];
        s[0] = camera;
        _cameraBuffer.Write<CameraUniform>(0, s);
    }

    public void DrawMesh(GpuMesh mesh, in Mat4 model)
    {
        if (_drawIndex >= MaxObjects)
            return;

        ulong offset = (ulong)_drawIndex * ModelStride;
        Span<Mat4> s = stackalloc Mat4[1];
        s[0] = model;
        _modelBuffer.Write<Mat4>(offset, s);

        uint dynOffset = (uint)offset;
        _api.RenderPassEncoderSetBindGroup(_pass, 1, _modelBindGroup, 1, &dynOffset);
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

    public void OnResize(Vector2D<int> size) => _ctx.Configure(size);

    public void Dispose()
    {
        _cameraBuffer.Dispose();
        _hudCameraBuffer.Dispose();
        _modelBuffer.Dispose();
        if (_hudPipeline        != null) _api.RenderPipelineRelease(_hudPipeline);
        if (_wireframePipeline  != null) _api.RenderPipelineRelease(_wireframePipeline);
        if (_pipeline           != null) _api.RenderPipelineRelease(_pipeline);
    }
}
