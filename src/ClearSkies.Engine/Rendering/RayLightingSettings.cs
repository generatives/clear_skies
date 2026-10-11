namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Ray-traced lighting settings the main render pass needs, shared by <c>GpuLightSystem</c> (which owns the
/// debug sliders) and <c>RenderFrame</c> (the <c>CameraUniform</c> light parameters).
/// </summary>
public static class RayLightingSettings
{
    /// <summary>How strongly ray AO darkens the ambient term, 0-1.</summary>
    public static float AoStrength;

    /// <summary>Flat ambient light, 0-1 (the debug panel's 0-15 level / 15).</summary>
    public static float Ambient = 2f / 15f;

    /// <summary>Multiplier on lit surfaces (terrain, ships, models) before fog; the sky and clouds are unlit and keep
    /// their authored colours. Brightens the scene as a whole now that textures are shown as authored on the sRGB
    /// surface.</summary>
    public static float Exposure = 1.0f;

    /// <summary>Half-angle (degrees) of the cone the sun rays spread over, so shadow edges soften with distance from
    /// what casts them (the real sun is about 0.27). A shadow edge d blocks from its caster is about
    /// 2 d tan(angle) wide.</summary>
    public static float SunConeDegrees = 2f;

    /// <summary>tan of <see cref="SunConeDegrees"/>: how far a cone-edge sun ray strays sideways per block.</summary>
    public static float SunConeSpread => MathF.Tan(System.Math.Clamp(SunConeDegrees, 0f, 20f) * MathF.PI / 180f);
}
