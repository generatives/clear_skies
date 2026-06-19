using ClearSkies.Engine.Core;
using ClearSkies.Engine.Windowing;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using Silk.NET.WebGPU.Extensions.WGPU;

namespace ClearSkies.Engine.Rendering.WebGpu;

/// <summary>
/// Owns the WebGPU instance/adapter/device/queue, the window surface and its configuration, and the
/// depth texture. Native handles are private/internal — no raw pointers leak into the public API.
/// </summary>
public sealed unsafe class GpuContext : IDisposable
{
    private readonly WebGPU _api;
    private readonly bool _logErrors;

    private Wgpu _wgpu = null!;   // wgpu-native extension (DevicePoll); set in Init

    private Instance* _instance;
    private Adapter* _adapter;
    private Device* _device;
    private Queue* _queue;
    private Surface* _surface;

    private Texture* _depthTexture;
    private TextureView* _depthView;

    private Texture* _currentTexture;
    private TextureView* _currentView;

    private PfnErrorCallback _errorCallback;

    public TextureFormat SurfaceFormat { get; private set; }
    public TextureFormat DepthFormat => TextureFormat.Depth32float;
    public Vector2D<int> Size { get; private set; }

    internal WebGPU Api => _api;
    internal Device* Device => _device;
    internal Queue* Queue => _queue;
    internal TextureView* DepthView => _depthView;
    internal TextureView* CurrentView => _currentView;

    private GpuContext(WebGPU api, bool logErrors)
    {
        _api = api;
        _logErrors = logErrors;
    }

    public static GpuContext Create(GameWindow window, EngineOptions options)
    {
        var api = WebGPU.GetApi();
        var ctx = new GpuContext(api, options.LogGpuErrors);
        ctx.Init(window);
        return ctx;
    }

    private void Init(GameWindow window)
    {
        var instanceDesc = new InstanceDescriptor();
        _instance = _api.CreateInstance(&instanceDesc);
        if (_instance == null)
            throw new InvalidOperationException("Failed to create WebGPU instance.");

        _surface = window.Native.CreateWebGPUSurface(_api, _instance);

        // Request adapter (synchronous under wgpu-native).
        var adapterOpts = new RequestAdapterOptions
        {
            CompatibleSurface = _surface,
            PowerPreference = PowerPreference.HighPerformance,
        };
        _api.InstanceRequestAdapter(_instance, &adapterOpts, PfnRequestAdapterCallback.From(HandleAdapter), null);
        if (_adapter == null)
            throw new InvalidOperationException("No suitable WebGPU adapter found.");

        // Request device + queue.
        var deviceDesc = new DeviceDescriptor();
        _api.AdapterRequestDevice(_adapter, &deviceDesc, PfnRequestDeviceCallback.From(HandleDevice), null);
        if (_device == null)
            throw new InvalidOperationException("Failed to create WebGPU device.");
        _queue = _api.DeviceGetQueue(_device);
        _wgpu = new Wgpu(_api.Context);

        if (_logErrors)
        {
            _errorCallback = PfnErrorCallback.From(HandleError);
            _api.DeviceSetUncapturedErrorCallback(_device, _errorCallback, null);
        }

        SurfaceFormat = PickSurfaceFormat();
        Configure(window.FramebufferSize);
    }

    private void HandleAdapter(RequestAdapterStatus status, Adapter* adapter, byte* message, void* _)
    {
        if (status == RequestAdapterStatus.Success) _adapter = adapter;
        else Console.Error.WriteLine($"[wgpu] adapter request failed: {SilkMarshal.PtrToString((nint)message)}");
    }

    private void HandleDevice(RequestDeviceStatus status, Device* device, byte* message, void* _)
    {
        if (status == RequestDeviceStatus.Success) _device = device;
        else Console.Error.WriteLine($"[wgpu] device request failed: {SilkMarshal.PtrToString((nint)message)}");
    }

    private void HandleError(ErrorType type, byte* message, void* _)
        => Console.Error.WriteLine($"[wgpu:{type}] {SilkMarshal.PtrToString((nint)message)}");

    private TextureFormat PickSurfaceFormat()
    {
        var caps = new SurfaceCapabilities();
        _api.SurfaceGetCapabilities(_surface, _adapter, &caps);
        if (caps.FormatCount > 0 && caps.Formats != null)
            return caps.Formats[0];
        return TextureFormat.Bgra8Unorm;
    }

    /// <summary>(Re)configure the surface for a new size and rebuild the depth texture.</summary>
    public void Configure(Vector2D<int> size)
    {
        if (size.X <= 0 || size.Y <= 0)
            return;
        Size = size;

        var config = new SurfaceConfiguration
        {
            Device = _device,
            Format = SurfaceFormat,
            Usage = TextureUsage.RenderAttachment,
            AlphaMode = CompositeAlphaMode.Opaque,
            Width = (uint)size.X,
            Height = (uint)size.Y,
            PresentMode = PresentMode.Fifo,
        };
        _api.SurfaceConfigure(_surface, &config);

        RebuildDepth((uint)size.X, (uint)size.Y);
    }

    private void RebuildDepth(uint width, uint height)
    {
        if (_depthView != null) { _api.TextureViewRelease(_depthView); _depthView = null; }
        if (_depthTexture != null) { _api.TextureRelease(_depthTexture); _depthTexture = null; }

        var desc = new TextureDescriptor
        {
            Usage = TextureUsage.RenderAttachment,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D(width, height, 1),
            Format = DepthFormat,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        _depthTexture = _api.DeviceCreateTexture(_device, &desc);
        _depthView = _api.TextureCreateView(_depthTexture, null);
    }

    /// <summary>Acquire the current swap surface view, or null if the surface needs reconfiguring.</summary>
    internal bool AcquireCurrentView()
    {
        var st = new SurfaceTexture();
        _api.SurfaceGetCurrentTexture(_surface, &st);
        if (st.Status != SurfaceGetCurrentTextureStatus.Success)
        {
            if (st.Texture != null) _api.TextureRelease(st.Texture);
            return false;
        }
        _currentTexture = st.Texture;
        _currentView = _api.TextureCreateView(_currentTexture, null);
        return true;
    }

    internal void Present()
    {
        _api.SurfacePresent(_surface);
        if (_currentView != null) { _api.TextureViewRelease(_currentView); _currentView = null; }
        if (_currentTexture != null) { _api.TextureRelease(_currentTexture); _currentTexture = null; }
    }

    // ── Compute support ──────────────────────────────────────────────────────────

    /// <summary>Flush submitted GPU work; with <paramref name="wait"/> blocks until the queue drains
    /// (needed so a mapped readback callback actually fires under wgpu-native).</summary>
    internal void Poll(bool wait = true) => _wgpu.DevicePoll(_device, new Silk.NET.Core.Bool32(wait), null);

    /// <summary>Encodes and submits a single GPU→GPU buffer copy.</summary>
    internal void CopyBufferToBuffer(GpuBuffer src, GpuBuffer dst, ulong size)
    {
        var encDesc = new CommandEncoderDescriptor();
        var enc = _api.DeviceCreateCommandEncoder(_device, &encDesc);
        _api.CommandEncoderCopyBufferToBuffer(enc, src.Handle, 0, dst.Handle, 0, size);
        var cmdDesc = new CommandBufferDescriptor();
        var cmd = _api.CommandEncoderFinish(enc, &cmdDesc);
        _api.QueueSubmit(_queue, 1, &cmd);
        _api.CommandBufferRelease(cmd);
        _api.CommandEncoderRelease(enc);
    }

    /// <summary>Maps a readback buffer and copies <paramref name="byteCount"/> bytes to managed memory.
    /// Blocks (polls) until the map completes. Debug/self-test use only.</summary>
    internal byte[] ReadBuffer(GpuBuffer readback, int byteCount)
    {
        bool done = false;
        var cb = PfnBufferMapCallback.From((status, _) => done = true);
        _api.BufferMapAsync(readback.Handle, MapMode.Read, 0, (nuint)byteCount, cb, null);
        while (!done) Poll(true);

        void* ptr = _api.BufferGetConstMappedRange(readback.Handle, 0, (nuint)byteCount);
        var outBytes = new byte[byteCount];
        new ReadOnlySpan<byte>(ptr, byteCount).CopyTo(outBytes);
        _api.BufferUnmap(readback.Handle);
        return outBytes;
    }

    public void Dispose()
    {
        if (_depthView != null) _api.TextureViewRelease(_depthView);
        if (_depthTexture != null) _api.TextureRelease(_depthTexture);
        if (_instance != null) _api.InstanceRelease(_instance);
        _api.Dispose();
    }
}
