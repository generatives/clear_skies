using System.Runtime.InteropServices;
using ClearSkies.Engine.Math;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Per-frame camera uniform block (224 bytes). Must match @group(0) @binding(0) in the WGSL shader.
/// Layout: view (64 B) + projection (64 B) + sunDir as vec4 (16 B: xyz direction, w strength)
/// + lightViewProj (64 B) + lightParams vec4 (16 B: x ray AO strength, y bounce scale).
/// SunDirection is the unit vector pointing FROM the sun TOWARD the scene (i.e. the light direction).
/// The shader scales sky light by max(dot(worldNormal, -SunDirection), 0).
/// SunStrength (the vec4's w component — originally unused padding) is a 0-1 multiplier on the direct-sun
/// term, read from <see cref="Rendering.SunLight.Strength"/> so the debug panel can dial it live.
/// LightViewProj is the directional-sun light-space view-projection: the shadow pass renders depth with
/// it, and the main fragment shader projects voxel centers through it to depth-test against the shadow map.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CameraUniform
{
    public Mat4            View;
    public Mat4            Projection;
    public Vector3D<float> SunDirection; // xyz direction; w is SunStrength (rounds this to 16 bytes/vec4)
    public float           SunStrength;
    public Mat4            LightViewProj;
    public float           RayAoStrength;  // lightParams.x: RayLightingSettings.AoStrength
    public float           RayBounceScale; // lightParams.y: RayLightingSettings.BounceScale
    private float          _pad0, _pad1;
}
