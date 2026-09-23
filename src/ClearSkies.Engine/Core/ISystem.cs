namespace ClearSkies.Engine.Core;

/// <summary>Ordered stages systems run in each frame. Input through PreRender run on the update tick. The render
/// stages (<see cref="RenderWorld"/> onwards) run on the render tick, and only when <see cref="EngineHost"/> managed
/// to open the frame (<c>RenderFrame</c>): all of them draw into its single GPU render pass, with this frame's
/// camera in <c>RenderFrame.Context</c>. A skipped frame (no active camera, no swapchain image) runs none of them.
/// </summary>
public enum SystemStage
{
    Input,
    Logic,
    PreRender,

    /// <summary>Depth-tested, depth-writing world geometry (chunks, models, clouds). Draw nearest-first where it's
    /// cheap to: the depth test then skips shading hidden fragments.</summary>
    RenderWorld,

    /// <summary>The sky background, after the world so it only shades the pixels it left uncovered.</summary>
    RenderSky,

    /// <summary>World-space overlays over the finished scene (e.g. the targeted-face wireframe).</summary>
    RenderOverlay,

    /// <summary>Screen-space elements in NDC. Each system here calls <c>Renderer.BeginHudPass</c> first (HUD
    /// pipeline and identity camera, depth always passes; cheap to repeat).</summary>
    RenderHud,
}

/// <summary>Minimal scheduling contract so engine and game systems share one update order.</summary>
public interface ISystem
{
    void Update(float dt);
}
