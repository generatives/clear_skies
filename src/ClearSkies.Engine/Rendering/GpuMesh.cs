using ClearSkies.Engine.Rendering.WebGpu;

namespace ClearSkies.Engine.Rendering;

/// <summary>Uploaded GPU buffers for one mesh, including a pre-built wireframe index buffer.</summary>
public sealed class GpuMesh : IDisposable
{
    public GpuBuffer VertexBuffer        { get; }
    public GpuBuffer IndexBuffer         { get; }
    public GpuBuffer WireframeBuffer     { get; }
    public uint      IndexCount          { get; }
    public uint      WireframeIndexCount { get; }

    public GpuMesh(GpuBuffer vertexBuffer, GpuBuffer indexBuffer, GpuBuffer wireframeBuffer,
                   uint indexCount, uint wireframeIndexCount)
    {
        VertexBuffer        = vertexBuffer;
        IndexBuffer         = indexBuffer;
        WireframeBuffer     = wireframeBuffer;
        IndexCount          = indexCount;
        WireframeIndexCount = wireframeIndexCount;
    }

    public void Dispose()
    {
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
        WireframeBuffer.Dispose();
    }
}
