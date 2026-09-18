using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// Fills a <c>u32</c> storage buffer with a constant value entirely on the GPU. A freshly (re)allocated
/// voxel volume's LightA/SunVis buffers need a plausible default (dim ambient sky, fully sunlit) before their
/// first real flood/sun-vis pass — filling that CPU-side (allocate an array the size of the buffer, fill it,
/// upload it) costs a full CPU-to-GPU transfer of the whole buffer, which for a few-hundred-MB volume is tens
/// of milliseconds, entirely on the frame that triggered the (re)allocation (see GpuResidencySystem). A tiny
/// compute dispatch writing the same constant does the same job using only GPU-internal bandwidth.
/// </summary>
public sealed unsafe class GpuBufferFill : IDisposable
{
    private const int WorkgroupSize = 256;

    // A single dispatch dimension caps out at 65535 workgroups (a WebGPU/Vulkan device limit) — for a
    // several-hundred-MB buffer at 256 elements/workgroup that's easily exceeded (e.g. ~226k workgroups
    // for a 220MB volume), so the workgroup grid is 2D and the shader reconstructs a linear index from
    // (workgroup_id, local_invocation_id) using the known grid width (groupsX) passed in Params.
    private static readonly string FillWgsl = @"
struct Params { value: u32, count: u32, groupsX: u32, pad1: u32 };

@group(0) @binding(0) var<storage, read_write> buf: array<u32>;
@group(0) @binding(1) var<uniform>             p:   Params;

@compute @workgroup_size(" + WorkgroupSize + @")
fn main(@builtin(workgroup_id) wid: vec3<u32>, @builtin(local_invocation_id) lid: vec3<u32>) {
    let workgroupIndex = wid.x + wid.y * p.groupsX;
    let idx = workgroupIndex * " + WorkgroupSize + @"u + lid.x;
    if (idx >= p.count) { return; }
    buf[idx] = p.value;
}";

    private readonly GpuContext      _ctx;
    private readonly ComputePipeline _pipeline;
    private readonly GpuBuffer       _param;

    public GpuBufferFill(GpuContext ctx)
    {
        _ctx      = ctx;
        _pipeline = new ComputePipeline(ctx, FillWgsl, "main");
        _param    = GpuBuffer.CreateUniform(ctx, 4 * sizeof(uint));
    }

    /// <summary>Fills the first <paramref name="count"/> <c>u32</c> elements of <paramref name="target"/> with
    /// <paramref name="value"/>. Creates and releases a bind group per call, since <paramref name="target"/>
    /// is a different buffer object each time (a fresh allocation) — cheap next to the transfer this replaces.</summary>
    public void FillU32(GpuBuffer target, uint value, int count)
    {
        if (count <= 0) return;

        uint totalGroups = (uint)((count + WorkgroupSize - 1) / WorkgroupSize);
        uint groupsX = System.Math.Min(totalGroups, 65535u);
        uint groupsY = (totalGroups + groupsX - 1) / groupsX;

        Span<uint> p = stackalloc uint[4] { value, (uint)count, groupsX, 0u };
        _param.Write<uint>(0, p);

        var bind = _pipeline.CreateBindGroup(new (uint, GpuBuffer)[] { (0u, target), (1u, _param) });
        _pipeline.Dispatch(bind, groupsX, groupsY, 1u);
        _ctx.Api.BindGroupRelease(bind);
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _param.Dispose();
    }
}
