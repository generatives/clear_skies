using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>Per-frame view state for render-stage systems.</summary>
public readonly record struct RenderContext(
    Vector3D<float> CameraPosition, Mat4 View, Mat4 Projection, Frustum Frustum, double TimeSeconds);

/// <summary>
/// The frame the render stages draw into, shared by every render-stage system: <see cref="FrameBeginSystem"/> opens
/// it (<see cref="Core.SystemStage.BeginRender"/>) and <see cref="FrameEndSystem"/> closes it
/// (<see cref="Core.SystemStage.EndRender"/>). Systems in between draw only while <see cref="IsOpen"/>.
/// </summary>
public sealed class RenderFrame
{
    /// <summary>True between BeginRender and EndRender of a frame that is actually being drawn; false when it was
    /// skipped (no active camera, or no swapchain image).</summary>
    public bool IsOpen { get; internal set; }

    /// <summary>This frame's camera and time. Only meaningful while <see cref="IsOpen"/>.</summary>
    public RenderContext Context { get; internal set; }
}
