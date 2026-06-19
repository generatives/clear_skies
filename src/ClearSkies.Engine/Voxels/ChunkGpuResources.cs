using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// GPU-resident lighting data for one chunk: an opacity bitset that the BFS flood reads, plus a
/// double-buffered light field for ping-pong propagation. Indexing matches <see cref="ChunkData.Index"/>
/// (x + 32*(y + 32*z)).
///
/// Layouts (all <c>array&lt;u32&gt;</c> storage buffers):
///   - Opacity: 1 bit per voxel (1 = light-blocking), packed 32 voxels per u32 → 1024 u32.
///   - LightA / LightB: 1 u32 per voxel; bits 0-7 = sky (0-15), bits 8-15 = block (0-15). The flood
///     ping-pongs between A and B and always leaves its result in <see cref="LightA"/>, which is what
///     the renderer samples (via <see cref="RenderBindGroup"/>).
///
/// Phase 4.2 cut 1: <see cref="LightA"/> is filled full-bright at creation (sky=15) so the per-chunk
/// render binding can be validated before the flood exists. Cut 2 replaces the fill with the flood.
/// </summary>
internal sealed unsafe class ChunkGpuResources : IDisposable
{
    public const int VoxelCount   = ChunkData.Size * ChunkData.Size * ChunkData.Size; // 32768
    public const int OpacityWords = VoxelCount / 32;                                   // 1024

    private readonly GpuContext _ctx;

    public GpuBuffer Opacity { get; }
    public GpuBuffer LightA  { get; }
    public GpuBuffer LightB  { get; }

    /// <summary>Group-2 bind group over <see cref="LightA"/> for sampling at draw time. Created lazily
    /// by the renderer; stored as an opaque handle and released here. 0 = not created yet.</summary>
    public nint RenderBindGroup { get; set; }

    /// <summary>Flood ping-pong bind groups (group 0): "even" reads A→writes B, "odd" reads B→writes A.
    /// Created lazily by <c>GpuLightFlood</c>; released here. 0 = not created yet.</summary>
    public nint FloodBindEven { get; set; }
    public nint FloodBindOdd  { get; set; }

    /// <summary>Ambient sky level written to air voxels until real sky flood (Phase 4.3). Keep in sync
    /// with the AMBIENT constants in the flood and render WGSL.</summary>
    public const uint AmbientSky = 6;

    private ChunkGpuResources(GpuContext ctx, GpuBuffer opacity, GpuBuffer lightA, GpuBuffer lightB)
    {
        _ctx    = ctx;
        Opacity = opacity;
        LightA  = lightA;
        LightB  = lightB;
    }

    public static ChunkGpuResources Create(GpuContext ctx)
    {
        var opacity = GpuBuffer.CreateStorage(ctx, OpacityWords * sizeof(uint));
        var lightA  = GpuBuffer.CreateStorage(ctx, VoxelCount  * sizeof(uint));
        var lightB  = GpuBuffer.CreateStorage(ctx, VoxelCount  * sizeof(uint));

        // Fill with dim ambient sky so a chunk renders sensibly before/until the flood runs (and so a
        // budget-delayed flood doesn't flash bright). The flood overwrites this with ambient+block.
        var fill = new uint[VoxelCount];
        Array.Fill(fill, AmbientSky); // sky=ambient, block=0
        lightA.Write<uint>(0, fill);

        return new ChunkGpuResources(ctx, opacity, lightA, lightB);
    }

    /// <summary>Repacks this chunk's opacity bitset from block data and uploads it.</summary>
    public void UploadOpacity(ChunkData data)
    {
        Span<uint> bits = stackalloc uint[OpacityWords];
        bits.Clear();

        for (int z = 0; z < ChunkData.Size; z++)
        for (int y = 0; y < ChunkData.Size; y++)
        for (int x = 0; x < ChunkData.Size; x++)
        {
            if (BlockRegistry.Get(data.Get(x, y, z)).Opacity >= 15)
            {
                int i = ChunkData.Index(x, y, z);
                bits[i >> 5] |= 1u << (i & 31);
            }
        }

        Opacity.Write<uint>(0, bits);
    }

    public void Dispose()
    {
        if (RenderBindGroup != 0) { _ctx.Api.BindGroupRelease((BindGroup*)RenderBindGroup); RenderBindGroup = 0; }
        if (FloodBindEven  != 0) { _ctx.Api.BindGroupRelease((BindGroup*)FloodBindEven);  FloodBindEven  = 0; }
        if (FloodBindOdd   != 0) { _ctx.Api.BindGroupRelease((BindGroup*)FloodBindOdd);   FloodBindOdd   = 0; }
        Opacity.Dispose();
        LightA.Dispose();
        LightB.Dispose();
    }
}
