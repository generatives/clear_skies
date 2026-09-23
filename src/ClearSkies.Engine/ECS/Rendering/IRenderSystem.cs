using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// The ordered stages of <see cref="RenderSystem"/>'s frame. Every stage draws into the same WebGPU render pass;
/// the stage decides when its systems run and what state they start from. Any number of
/// <see cref="IRenderSystem"/>s can be added to each, and run in the order added.
/// </summary>
public enum RenderPass
{
    /// <summary>Depth-tested, depth-writing world geometry (chunks, models, clouds). Draw nearest-first where it's
    /// cheap to: the depth test then skips shading hidden fragments.</summary>
    World,

    /// <summary>The sky background, after the world so it only shades uncovered pixels.</summary>
    Sky,

    /// <summary>World-space overlays drawn over the finished scene (e.g. the targeted-face wireframe).</summary>
    Overlay,

    /// <summary>Screen-space elements: RenderSystem binds the HUD pipeline and identity camera first
    /// (<see cref="Rendering.WebGpu.Renderer.BeginHudPass"/>), so vertices are in NDC and depth always passes.</summary>
    Hud,
}

/// <summary>Per-frame view state handed to every <see cref="IRenderSystem"/>.</summary>
public readonly record struct RenderContext(
    Vector3D<float> CameraPosition, Mat4 View, Mat4 Projection, Frustum Frustum, double TimeSeconds);

/// <summary>
/// Draws part of the frame inside <see cref="RenderSystem"/>'s open render pass, at the <see cref="RenderPass"/> it
/// was added to (<see cref="RenderSystem.Add"/>). Not scheduled as an <see cref="Core.ISystem"/>: its draws have to
/// land inside that pass. Implement <see cref="Gui.IDebugUiSystem"/> too to get a debug panel.
/// </summary>
public interface IRenderSystem
{
    void Render(in RenderContext frame);
}
