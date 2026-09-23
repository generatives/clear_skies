using ClearSkies.Engine.Rendering;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>Per-frame view state handed to each <see cref="IWorldRenderPass"/>.</summary>
public readonly record struct WorldRenderContext(Vector3D<float> CameraPosition, Frustum Frustum);

/// <summary>
/// Draws part of the world into <see cref="RenderSystem"/>'s open render pass (between its camera setup and the
/// clouds/sky). Registered with <see cref="RenderSystem.AddWorldPass"/> rather than scheduled as a system, since
/// its draws have to land inside that one pass.
/// </summary>
public interface IWorldRenderPass
{
    void Draw(in WorldRenderContext frame);
}
