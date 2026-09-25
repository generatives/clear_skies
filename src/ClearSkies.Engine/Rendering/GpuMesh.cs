using ClearSkies.Engine.Rendering.WebGpu;

namespace ClearSkies.Engine.Rendering;

/// <summary>Uploaded GPU buffers for one mesh, including a pre-built wireframe index buffer. The vertices, indices and
/// wireframe indices may share one buffer (a chunk mesh: one allocation and one upload instead of three), so draw
/// from their offsets and sizes.</summary>
public sealed class GpuMesh : IDisposable
{
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
    }

    /// <summary>A mesh packed in one buffer: <paramref name="vertexBytes"/> of vertices, then the indices, then the
    /// wireframe indices.</summary>
    public GpuMesh(GpuBuffer packed, ulong vertexBytes, uint indexCount, uint wireframeIndexCount)
    {
        VertexBuffer = IndexBuffer = WireframeBuffer = packed;
        IndexCount          = indexCount;
        WireframeIndexCount = wireframeIndexCount;
        VertexBytes     = vertexBytes;
        IndexOffset     = vertexBytes;
        IndexBytes      = (ulong)indexCount * sizeof(uint);
        WireframeOffset = IndexOffset + IndexBytes;
        WireframeBytes  = (ulong)wireframeIndexCount * sizeof(uint);
    }

    public void Dispose()
    {
        VertexBuffer.Dispose();
        if (IndexBuffer != VertexBuffer) IndexBuffer.Dispose();
        if (WireframeBuffer != VertexBuffer && WireframeBuffer != IndexBuffer) WireframeBuffer.Dispose();
    }
}
