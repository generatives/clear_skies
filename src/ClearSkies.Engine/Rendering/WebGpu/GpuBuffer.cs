using Silk.NET.WebGPU;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>A GPU buffer. Uploads go through <c>QueueWriteBuffer</c> (no explicit staging needed).</summary>
public sealed unsafe class GpuBuffer : IDisposable
{
    private readonly GpuContext _ctx;
    internal WgpuBuffer* Handle { get; private set; }
    public ulong SizeBytes { get; }

    private GpuBuffer(GpuContext ctx, WgpuBuffer* handle, ulong size)
    {
        _ctx = ctx;
        Handle = handle;
        SizeBytes = size;
    }

    public static GpuBuffer Create(GpuContext ctx, ulong size, BufferUsage usage)
    {
        var desc = new BufferDescriptor { Usage = usage, Size = size, MappedAtCreation = false };
        var handle = ctx.Api.DeviceCreateBuffer(ctx.Device, &desc);
        return new GpuBuffer(ctx, handle, size);
    }

    public static GpuBuffer CreateVertex<T>(GpuContext ctx, ReadOnlySpan<T> data) where T : unmanaged
    {
        ulong size = (ulong)(data.Length * sizeof(T));
        var buf = Create(ctx, Align4(size), BufferUsage.Vertex | BufferUsage.CopyDst);
        buf.Write(0, data);
        return buf;
    }

    public static GpuBuffer CreateIndex(GpuContext ctx, ReadOnlySpan<uint> data)
    {
        ulong size = (ulong)(data.Length * sizeof(uint));
        var buf = Create(ctx, Align4(size), BufferUsage.Index | BufferUsage.CopyDst);
        buf.Write(0, data);
        return buf;
    }

    public static GpuBuffer CreateUniform(GpuContext ctx, ulong size)
        => Create(ctx, Align4(size), BufferUsage.Uniform | BufferUsage.CopyDst);

    public void Write<T>(ulong offset, ReadOnlySpan<T> data) where T : unmanaged
    {
        fixed (T* p = data)
            _ctx.Api.QueueWriteBuffer(_ctx.Queue, Handle, offset, p, (nuint)(data.Length * sizeof(T)));
    }

    private static ulong Align4(ulong n) => (n + 3UL) & ~3UL;

    public void Dispose()
    {
        if (Handle != null) { _ctx.Api.BufferRelease(Handle); Handle = null; }
    }
}
