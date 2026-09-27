namespace ClearSkies.Engine.Core;

/// <summary>Ordered stages systems run in each frame. Input through PreRender run on the window's update callback:
/// <see cref="Input"/> once per frame, then <see cref="Tick"/> zero or more times (once per fixed 60 Hz tick, see
/// <see cref="TickClock"/>), then <see cref="Logic"/> and <see cref="PreRender"/> once per frame. The render
/// stages (<see cref="RenderWorld"/> onwards) run on the render tick, and only when <see cref="EngineHost"/> managed
/// to open the frame (<c>RenderFrame</c>): they hold <see cref="IRenderSystem"/>s, which all draw into its single GPU
/// render pass and are handed this frame's camera and time. A skipped frame (no active camera, no swapchain image)
/// runs none of them.
/// </summary>
public enum SystemStage
{
    /// <summary>Once per frame, before any ticks: UI frames, mouse-look, and collecting input for the ticks.</summary>
    Input,

    /// <summary>Once per fixed tick, 0 or more times a frame, handed <see cref="Time.TickSeconds"/>: gameplay
    /// simulation and exactly one physics step. Systems here read input only through <c>PlayerInput</c>.</summary>
    Tick,

    /// <summary>Once per frame, after the ticks: presentation and per-frame interaction.</summary>
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

/// <summary>Minimal scheduling contract so engine and game systems share one update order. Runs in the update stages
/// (Input, Tick, Logic, PreRender).</summary>
public interface ISystem
{
    void Update(float dt);
}

/// <summary>A system that draws: runs in a render stage (<see cref="SystemStage.RenderWorld"/> onwards), inside the
/// open frame, and is handed that frame's camera and time. Registered with the same
/// <see cref="EngineHost.AddSystem(IRenderSystem, SystemStage)"/> as any other system.</summary>
public interface IRenderSystem
{
    void Render(in Rendering.RenderContext frame);
}
