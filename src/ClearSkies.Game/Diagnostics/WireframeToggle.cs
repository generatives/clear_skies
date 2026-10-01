using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Rendering.WebGpu;
using Silk.NET.Input;

namespace ClearSkies.Game.Diagnostics;

/// <summary>Tab draws the world as wireframe, or back.</summary>
public sealed class WireframeToggle : ISystem
{
    private readonly InputManager _input;
    private readonly Renderer _renderer;

    public WireframeToggle(InputManager input, Renderer renderer)
    {
        _input = input;
        _renderer = renderer;
    }

    public void Update(float dt)
    {
        if (!_input.WasKeyPressed(Key.Tab)) return;
        _renderer.WireframeMode = !_renderer.WireframeMode;
        Console.WriteLine($"[debug] wireframe: {_renderer.WireframeMode}");
    }
}
