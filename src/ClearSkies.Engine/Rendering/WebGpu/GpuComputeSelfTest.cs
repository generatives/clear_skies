namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// One-shot validation that the compute path works end to end: upload → dispatch → readback.
/// Run once at startup (Phase 4.0). Proves storage buffers, compute pipeline creation, bind groups,
/// dispatch, GPU→GPU copy, and CPU map-readback before any real lighting is built on top.
/// </summary>
public static unsafe class GpuComputeSelfTest
{
    private const string Wgsl = @"
@group(0) @binding(0) var<storage, read_write> data: array<u32>;
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    if (i < arrayLength(&data)) { data[i] = data[i] * 2u; }
}";

    public static bool Run(GpuContext ctx)
    {
        const int n = 16;
        var input = new uint[n];
        for (uint i = 0; i < n; i++) input[i] = i + 1;   // 1..16

        using var pipeline = new ComputePipeline(ctx, Wgsl, "main");
        using var storage  = GpuBuffer.CreateStorage(ctx, (ulong)(n * sizeof(uint)));
        using var readback = GpuBuffer.CreateReadback(ctx, (ulong)(n * sizeof(uint)));

        storage.Write<uint>(0, input);

        var bg = pipeline.CreateBindGroup(new (uint, GpuBuffer)[] { (0u, storage) });
        pipeline.Dispatch(bg, groupsX: 1);          // ceil(16 / 64) = 1 workgroup
        ctx.Api.BindGroupRelease(bg);

        ctx.CopyBufferToBuffer(storage, readback, (ulong)(n * sizeof(uint)));
        ctx.Poll(true);

        var bytes  = ctx.ReadBuffer(readback, n * sizeof(uint));
        var result = new uint[n];
        System.Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);

        bool ok = true;
        for (int i = 0; i < n; i++)
            if (result[i] != input[i] * 2u) { ok = false; break; }

        if (ok)
            Console.WriteLine($"[gpu] compute self-test PASSED (e.g. {input[0]}→{result[0]}, {input[n - 1]}→{result[n - 1]})");
        else
            Console.Error.WriteLine($"[gpu] compute self-test FAILED: got [{string.Join(",", result)}]");

        return ok;
    }
}
