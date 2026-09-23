namespace ClearSkies.Engine.Core;

/// <summary>Ordered stages systems run in each frame. Input through PreRender run on the update tick; the render
/// stages run on the render tick, all inside the single GPU render pass FrameBeginSystem opens in
/// <see cref="BeginRender"/> and FrameEndSystem closes in <see cref="EndRender"/>. A system in a render stage between
/// those draws only while the frame is open (<c>RenderFrame.IsOpen</c>) — a frame is skipped when there's no active
/// camera or no swapchain image.</summary>
public enum SystemStage
{
    Input,
    Logic,
    PreRender,

    /// <summary>Opens the frame: camera uniform, render pass, the shared <c>RenderFrame</c>.</summary>
    BeginRender,

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

    /// <summary>Closes the frame: ImGui, then submit and present.</summary>
    EndRender,
}

/// <summary>Minimal scheduling contract so engine and game systems share one update order.</summary>
public interface ISystem
{
    void Update(float dt);
}
