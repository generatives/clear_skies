using System.Runtime.InteropServices;
using ClearSkies.Engine.Math;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Per-frame camera uniform block (256 bytes). Must match @group(0) @binding(0) in the WGSL shader.
/// Layout: view (64 B) + projection (64 B) + sunDir as vec4 (16 B: xyz direction, w strength)
/// + lightParams vec4 (16 B: x ray AO strength, y reference-lighting flag, z ambient 0-1, w unused)
/// + camPos vec4 (xyz world position) + fog vec4 (xy: the world's fog start/end, horizontal; zw: the cloud layer's,
/// see <see cref="CloudLayer"/>; blocks from the camera) + zenith and horizon sky colours as vec4s (horizon.w: the
/// haze's strength) + haze vec4 (rgb colour, w distance) + sea vec4 (the cloud sea: altitude, coverage (0 = off), cell
/// size and thickness in blocks; see <see cref="SkySettings"/>).
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
    public float           FogStart, FogEnd, CloudFogStart, CloudFogEnd; // fog, blocks
    public Vector3D<float> ZenithColor;    // zenith.rgb
    private float          _pad2;
    public Vector3D<float> HorizonColor;   // horizon.rgb
    public float           HazeStrength;   // horizon.w
    public Vector3D<float> HazeColor;      // haze.rgb
    public float           HazeDistance;   // haze.w
    public float           SeaAltitude, SeaCoverage, SeaCell, SeaThickness; // sea
}
