namespace ClearSkies.Engine.Rendering;

/// <summary>
/// How strongly the ray-traced lighting prototype's ray AO darkens the ambient term (0-1), shared by
/// <c>GpuLightSystem</c> (which owns the debug slider) and the main render pass (<c>CameraUniform.RayAoStrength</c>).
/// 0 while the old lighting path is active, since only the ray-traced path writes the AO bits.
/// </summary>
public static class RayAmbientOcclusion
{
    public static float Strength;
}
