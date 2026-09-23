using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering.WebGpu;

namespace ClearSkies.Engine.ECS;

/// <summary>Closes the frame (<see cref="SystemStage.EndRender"/>): draws ImGui on top of everything, then submits and
/// presents. ImGui's frame is closed even when the frame was skipped, since the Input stage opened it.</summary>
public sealed class FrameEndSystem : ISystem
{
    private readonly RenderFrame _frame;
    private readonly Renderer _renderer;
    private readonly ImGuiController _gui;

    public FrameEndSystem(RenderFrame frame, Renderer renderer, ImGuiController gui)
    {
        _frame    = frame;
        _renderer = renderer;
        _gui      = gui;
    }

    public void Update(float dt)
    {
        _gui.EndFrame();
        if (!_frame.IsOpen) return;
        _renderer.EndFrame();
        _frame.IsOpen = false;
    }
}
