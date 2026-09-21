namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Ray-traced lighting settings the main render pass needs, shared by <c>GpuLightSystem</c> (which owns the
/// debug sliders) and <c>RenderSystem</c> (the <c>CameraUniform</c> light parameters).
/// </summary>
public static class RayLightingSettings
{
    /// <summary>How strongly ray AO darkens the ambient term, 0-1.</summary>
    public static float AoStrength;

    /// <summary>Multiplier on the stored bounce light, 0 = bounce off.</summary>
    public static float BounceScale;

    /// <summary>Flat ambient light, 0-1 (the debug panel's 0-15 level / 15).</summary>
    public static float Ambient = 2f / 15f;
}
