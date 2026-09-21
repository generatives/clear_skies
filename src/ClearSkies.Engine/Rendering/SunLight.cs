using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// The engine's single directional sun — direction and brightness — shared by the main render pass
/// (<c>CameraUniform.SunDirection</c>/<c>SunStrength</c>, the shadow map) and the ray-traced lighting
/// prototype (<c>GpuLightSystem</c>), so both read one source instead of copies that could drift.
/// Mutable (it started as a fixed constant) so the "GPU Lighting" debug panel can drive it live for A/B
/// comparison between the two lighting paths at a matching sun angle/brightness.
/// </summary>
public static class SunLight
{
    /// <summary>Degrees, wraps at 360; rotation of the sun around the world Y axis.</summary>
    public static float AzimuthDegrees;

    /// <summary>Degrees, -90 (straight down) to 90 (straight up); the sun's angle above the horizon.</summary>
    public static float ElevationDegrees;

    /// <summary>0-15, Minecraft-style brightness level — matches how ambient/block light levels are
    /// already expressed elsewhere in this codebase (<c>VolumeGpuResources.BaseSkyLevel</c>,
    /// <c>BlockDef.LightEmission</c>). 15 reproduces the original fixed <c>SUN_STRENGTH = 1.0</c>
    /// ("full brightness") default.</summary>
    public static float Level = 15f;

    static SunLight()
    {
        // Reconstruct the original fixed direction (-0.4,-1,-0.3, normalized) as azimuth/elevation, so
        // nothing changes at startup — behavior only diverges once a debug-panel slider is touched.
        var d = Vector3D.Normalize(new Vector3D<float>(-0.4f, -1f, -0.3f));
        ElevationDegrees = MathF.Asin(System.Math.Clamp(-d.Y, -1f, 1f)) * (180f / MathF.PI);
        AzimuthDegrees   = MathF.Atan2(d.X, d.Z) * (180f / MathF.PI);
    }

    /// <summary>Unit vector pointing FROM the sun TOWARD the scene (the light travel direction) —
    /// what <c>CameraUniform.SunDirection</c> and the ray-traced sun pass both consume.</summary>
    public static Vector3D<float> Direction
    {
        get
        {
            float az = AzimuthDegrees * (MathF.PI / 180f);
            float el = ElevationDegrees * (MathF.PI / 180f);
            float cosEl = MathF.Cos(el);
            var dir = new Vector3D<float>(cosEl * MathF.Sin(az), -MathF.Sin(el), cosEl * MathF.Cos(az));
            return Vector3D.Normalize(dir);
        }
    }

    /// <summary>0-1 multiplier for the fragment shader's direct-sun term (read from
    /// <c>camera.sunDir.w</c>, which used to be unused padding) — simply <see cref="Level"/>/15.</summary>
    public static float Strength => Level / 15f;
}
