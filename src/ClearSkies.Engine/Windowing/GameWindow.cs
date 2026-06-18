using ClearSkies.Engine.Core;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace ClearSkies.Engine.Windowing;

/// <summary>
/// Thin wrapper over a Silk.NET <see cref="IWindow"/>. Created with <c>GraphicsAPI.None</c> so
/// Silk does not create an OpenGL context — WebGPU manages the surface itself.
/// </summary>
public sealed class GameWindow : IDisposable
{
    public IWindow Native { get; }

    public event Action? Load;
    public event Action<double>? Update;
    public event Action<double>? Render;
    public event Action<Vector2D<int>>? Resize;
    public event Action? Closing;

    public Vector2D<int> FramebufferSize => Native.FramebufferSize;

    public GameWindow(EngineOptions options)
    {
        var opts = WindowOptions.Default with
        {
            Size = new Vector2D<int>(options.Width, options.Height),
            Title = options.Title,
            API = GraphicsAPI.None,
        };
        Native = Window.Create(opts);

        Native.Load += () => Load?.Invoke();
        Native.Update += d => Update?.Invoke(d);
        Native.Render += d => Render?.Invoke(d);
        Native.FramebufferResize += s => Resize?.Invoke(s);
        Native.Closing += () => Closing?.Invoke();
    }

    public void Run() => Native.Run();

    public void Dispose() => Native.Dispose();
}
