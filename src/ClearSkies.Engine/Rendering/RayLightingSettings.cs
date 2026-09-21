namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Ray-traced lighting prototype settings the main render pass needs, shared by <c>GpuLightSystem</c> (which
/// owns the debug sliders) and <c>RenderSystem</c> (<c>CameraUniform.RayAoStrength</c>/<c>RayBounceScale</c>).
/// Both are 0 while the old lighting path is active, since only the ray-traced path writes these bits of the
/// light word.
/// </summary>
public static class RayLightingSettings
{
    /// <summary>How strongly ray AO darkens the ambient term, 0-1.</summary>
    public static float AoStrength;

    /// <summary>Multiplier on the stored bounce light, 0 = bounce off.</summary>
    public static float BounceScale;
}
