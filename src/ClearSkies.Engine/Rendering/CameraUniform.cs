using System.Runtime.InteropServices;
using ClearSkies.Engine.Math;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Per-frame camera uniform block (160 bytes). Must match @group(0) @binding(0) in the WGSL shader.
/// Layout: view (64 B) + projection (64 B) + sunDir as vec4 (16 B: xyz direction, w strength)
/// + lightParams vec4 (16 B: x ray AO strength, y unused, z ambient 0-1, w unused).
/// SunDirection is the unit vector pointing FROM the sun TOWARD the scene (i.e. the light direction).
/// The shader scales sky light by max(dot(worldNormal, -SunDirection), 0).
/// SunStrength (the vec4's w component) is a 0-1 multiplier on the direct-sun term, read from
/// <see cref="Rendering.SunLight.Strength"/> so the debug panel can dial it live.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CameraUniform
{
    public Mat4            View;
    public Mat4            Projection;
    public Vector3D<float> SunDirection; // xyz direction; w is SunStrength (rounds this to 16 bytes/vec4)
    public float           SunStrength;
    public float           RayAoStrength;  // lightParams.x: RayLightingSettings.AoStrength
    private float          _unused;        // lightParams.y
    public float           Ambient;        // lightParams.z: RayLightingSettings.Ambient
    private float          _pad0;
}
