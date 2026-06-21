using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// A compute pipeline built from WGSL with an auto-derived bind-group layout (group 0). Callers create
/// bind groups of storage/uniform buffers against it and dispatch. This is the shared substrate for the
/// GPU lighting passes (opacity-driven flood, injection) added in later phases.
/// </summary>
public sealed unsafe class ComputePipeline : IDisposable
{
    private readonly GpuContext _ctx;
    private readonly WebGPU _api;

    private ShaderModule* _shader;
    private Silk.NET.WebGPU.ComputePipeline* _pipeline;
    private BindGroupLayout* _layout;   // implicit layout for group 0, derived from the WGSL

    public ComputePipeline(GpuContext ctx, string wgsl, string entryPoint)
    {
        _ctx = ctx;
        _api = ctx.Api;

        _shader = CreateShader(wgsl);

        var entry = (byte*)SilkMarshal.StringToPtr(entryPoint, NativeStringEncoding.UTF8);
        var desc = new ComputePipelineDescriptor
        {
            Layout  = null,   // auto layout: derive bind-group layout from the shader
            Compute = new ProgrammableStageDescriptor { Module = _shader, EntryPoint = entry },
        };
        _pipeline = _api.DeviceCreateComputePipeline(_ctx.Device, &desc);
        SilkMarshal.Free((nint)entry);

        _layout = _api.ComputePipelineGetBindGroupLayout(_pipeline, 0);
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

    /// <summary>Creates a bind group for group 0 binding the given (binding, buffer) pairs in full.</summary>
    public BindGroup* CreateBindGroup(ReadOnlySpan<(uint binding, GpuBuffer buffer)> entries)
    {
        BindGroupEntry* arr = stackalloc BindGroupEntry[entries.Length];
        for (int i = 0; i < entries.Length; i++)
            arr[i] = new BindGroupEntry
            {
                Binding = entries[i].binding,
                Buffer  = entries[i].buffer.Handle,
                Offset  = 0,
                Size    = entries[i].buffer.SizeBytes,
            };

        var desc = new BindGroupDescriptor { Layout = _layout, EntryCount = (uint)entries.Length, Entries = arr };
        return _api.DeviceCreateBindGroup(_ctx.Device, &desc);
    }

    /// <summary>As <see cref="CreateBindGroup"/> but returns an opaque handle (caller releases it).</summary>
    public nint CreateBindGroupHandle(ReadOnlySpan<(uint binding, GpuBuffer buffer)> entries)
        => (nint)CreateBindGroup(entries);

    /// <summary>
    /// Records <paramref name="passes"/> dispatches into one command encoder, alternating between the two
    /// bind groups each pass (ping-pong). Each pass is its own compute pass, so WebGPU inserts the
    /// storage-buffer barrier between them. With an even <paramref name="passes"/> the final write lands
    /// via <paramref name="bindGroupOdd"/> — set up so that resolves to the render-sampled buffer.
    /// </summary>
    public void DispatchPingPong(nint bindGroupEven, nint bindGroupOdd, uint gx, uint gy, uint gz, int passes)
    {
        var encDesc = new CommandEncoderDescriptor();
        var enc = _api.DeviceCreateCommandEncoder(_ctx.Device, &encDesc);

        for (int p = 0; p < passes; p++)
        {
            var bg = (BindGroup*)((p & 1) == 0 ? bindGroupEven : bindGroupOdd);
            var passDesc = new ComputePassDescriptor();
            var pass = _api.CommandEncoderBeginComputePass(enc, &passDesc);
            _api.ComputePassEncoderSetPipeline(pass, _pipeline);
            _api.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);
            _api.ComputePassEncoderDispatchWorkgroups(pass, gx, gy, gz);
            _api.ComputePassEncoderEnd(pass);
            _api.ComputePassEncoderRelease(pass);
        }

        var cmdDesc = new CommandBufferDescriptor();
        var cmd = _api.CommandEncoderFinish(enc, &cmdDesc);
        _api.QueueSubmit(_ctx.Queue, 1, &cmd);
        _api.CommandBufferRelease(cmd);
        _api.CommandEncoderRelease(enc);
    }

    /// <summary>As <see cref="Dispatch(BindGroup*,uint,uint,uint)"/> but takes an opaque bind-group handle.</summary>
    public void Dispatch(nint bindGroup, uint groupsX, uint groupsY, uint groupsZ)
        => Dispatch((BindGroup*)bindGroup, groupsX, groupsY, groupsZ);

    /// <summary>Encodes and submits a one-shot dispatch of (groupsX, groupsY, groupsZ) workgroups.</summary>
    public void Dispatch(BindGroup* bindGroup, uint groupsX, uint groupsY = 1, uint groupsZ = 1)
    {
        var encDesc = new CommandEncoderDescriptor();
        var enc = _api.DeviceCreateCommandEncoder(_ctx.Device, &encDesc);

        var passDesc = new ComputePassDescriptor();
        var pass = _api.CommandEncoderBeginComputePass(enc, &passDesc);
        _api.ComputePassEncoderSetPipeline(pass, _pipeline);
        _api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        _api.ComputePassEncoderDispatchWorkgroups(pass, groupsX, groupsY, groupsZ);
        _api.ComputePassEncoderEnd(pass);
        _api.ComputePassEncoderRelease(pass);

        var cmdDesc = new CommandBufferDescriptor();
        var cmd = _api.CommandEncoderFinish(enc, &cmdDesc);
        _api.QueueSubmit(_ctx.Queue, 1, &cmd);
        _api.CommandBufferRelease(cmd);
        _api.CommandEncoderRelease(enc);
    }

    public void Dispose()
    {
        if (_layout   != null) { _api.BindGroupLayoutRelease(_layout);  _layout   = null; }
        if (_pipeline != null) { _api.ComputePipelineRelease(_pipeline); _pipeline = null; }
        if (_shader   != null) { _api.ShaderModuleRelease(_shader);     _shader   = null; }
    }
}
