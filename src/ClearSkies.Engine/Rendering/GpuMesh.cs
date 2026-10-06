using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.WebGPU;

namespace ClearSkies.Engine.Rendering;

/// <summary>Uploaded GPU buffers for one mesh, including a pre-built wireframe index buffer. The vertices, indices and
/// wireframe indices may share one buffer (a chunk mesh: one allocation and one upload instead of three), so draw
/// from their offsets and sizes.</summary>
public sealed class GpuMesh : IDisposable
{
    /// <summary>Bytes held by every mesh not yet disposed, and the wireframe indices' share of them (for reports).</summary>
    public static long LiveBytes => Interlocked.Read(ref _liveBytes);
    public static long LiveWireframeBytes => Interlocked.Read(ref _liveWireBytes);
    private static long _liveBytes, _liveWireBytes;
    private bool _disposed;

    private void Count(int sign)
    {
        Interlocked.Add(ref _liveBytes, sign * (long)(VertexBytes + IndexBytes + WireframeBytes));
        Interlocked.Add(ref _liveWireBytes, sign * (long)WireframeBytes);
    }

    public GpuBuffer VertexBuffer        { get; }
    public GpuBuffer IndexBuffer         { get; }
    public GpuBuffer WireframeBuffer     { get; }
    public uint      IndexCount          { get; }
    public uint      WireframeIndexCount { get; }

    public ulong VertexOffset { get; }
    public ulong VertexBytes { get; }
    public ulong IndexOffset { get; }
    public ulong IndexBytes { get; }
    public ulong WireframeOffset { get; }
    public ulong WireframeBytes { get; }

    /// <summary>The indices' (and wireframe indices') format.</summary>
    public IndexFormat IndexFormat { get; } = IndexFormat.Uint32;

    /// <summary>For a chunk mesh: how many <see cref="ChunkQuad"/>s its vertex buffer holds (it has no indices).</summary>
    public uint QuadCount { get; }

    /// <summary>For a chunk mesh: its first quad in the renderer's shared quad buffer (ChunkQuadArena).</summary>
    public uint QuadBase { get; }
    private readonly Action? _free; // a chunk mesh: gives its run of the shared buffer back

    /// <summary>A chunk mesh: <paramref name="quadCount"/> packed <see cref="ChunkQuad"/>s from
    /// <paramref name="quadBase"/> in the renderer's shared quad buffer <paramref name="arena"/>, which vs_chunk reads
    /// through the draw's record; <paramref name="free"/> gives the run back when the mesh is disposed (the buffer itself
    /// is the renderer's).</summary>
    public GpuMesh(GpuBuffer arena, uint quadBase, uint quadCount, Action free)
    {
        VertexBuffer = IndexBuffer = WireframeBuffer = arena;
        QuadBase    = quadBase;
        QuadCount   = quadCount;
        VertexBytes = quadCount * ChunkQuad.SizeBytes;
        _free = free;
        Count(1);
    }

    public GpuMesh(GpuBuffer vertexBuffer, GpuBuffer indexBuffer, GpuBuffer wireframeBuffer,
                   uint indexCount, uint wireframeIndexCount)
    {
        VertexBuffer        = vertexBuffer;
        IndexBuffer         = indexBuffer;
        WireframeBuffer     = wireframeBuffer;
        IndexCount          = indexCount;
        WireframeIndexCount = wireframeIndexCount;
        VertexBytes    = vertexBuffer.SizeBytes;
        IndexBytes     = indexBuffer.SizeBytes;
        WireframeBytes = wireframeBuffer.SizeBytes;
        Count(1);
    }

    /// <summary>A mesh packed in one buffer: <paramref name="vertexBytes"/> of vertices, then the indices, then the
    /// wireframe indices (none if <paramref name="wireframeIndexCount"/> is 0), all in <paramref name="format"/>.</summary>
    public GpuMesh(GpuBuffer packed, ulong vertexBytes, uint indexCount, uint wireframeIndexCount, IndexFormat format)
    {
        VertexBuffer = IndexBuffer = WireframeBuffer = packed;
        IndexFormat         = format;
        IndexCount          = indexCount;
        WireframeIndexCount = wireframeIndexCount;
        ulong size = format == IndexFormat.Uint16 ? 2UL : 4UL;
        VertexBytes     = vertexBytes;
        IndexOffset     = vertexBytes;
        IndexBytes      = indexCount * size;
        WireframeOffset = IndexOffset + IndexBytes;
        WireframeBytes  = wireframeIndexCount * size;
        Count(1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Count(-1);
        if (_free != null) { _free(); return; }
        VertexBuffer.Dispose();
        if (IndexBuffer != VertexBuffer) IndexBuffer.Dispose();
        if (WireframeBuffer != VertexBuffer && WireframeBuffer != IndexBuffer) WireframeBuffer.Dispose();
    }
}
