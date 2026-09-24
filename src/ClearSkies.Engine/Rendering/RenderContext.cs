using ClearSkies.Engine.Math;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>This frame's view state, passed to every <see cref="Core.IRenderSystem"/>: the active camera's position,
/// view and projection matrices and frustum, and the render clock.</summary>
public readonly record struct RenderContext(
    Vector3D<float> CameraPosition, Mat4 View, Mat4 Projection, Frustum Frustum, double TimeSeconds);
