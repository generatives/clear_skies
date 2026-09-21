using System.Runtime.InteropServices;
using ClearSkies.Engine.Math;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Per-frame camera uniform block (224 bytes). Must match @group(0) @binding(0) in the WGSL shader.
/// Layout: view (64 B) + projection (64 B) + sunDir as vec4 (16 B: xyz direction, w strength)
/// + lightParams vec4 (16 B: x ray AO strength, y reference-lighting flag, z ambient 0-1, w unused)
/// + camPos vec4 (xyz world position) + fog vec4 (horizontal start/end, vertical start/end, in blocks)
/// + zenith and horizon sky colours as vec4s (see <see cref="SkySettings"/>).
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
    public float           ReferenceLighting; // lightParams.y: 1 = the shader's slow reference light/AO path (A/B)
    public float           Ambient;        // lightParams.z: RayLightingSettings.Ambient
    private float          _pad0;
    public Vector3D<float> CameraPosition; // camPos.xyz: fog distances and view directions are measured from here
    private float          _pad1;
    public float           FogHorizontalStart, FogHorizontalEnd, FogVerticalStart, FogVerticalEnd; // fog, blocks
    public Vector3D<float> ZenithColor;    // zenith.rgb
    private float          _pad2;
    public Vector3D<float> HorizonColor;   // horizon.rgb
    private float          _pad3;
}
