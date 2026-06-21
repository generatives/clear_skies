using System.Runtime.InteropServices;
using ClearSkies.Engine.Math;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// Per-frame camera uniform block (208 bytes). Must match @group(0) @binding(0) in the WGSL shader.
/// Layout: view (64 B) + projection (64 B) + sunDir as vec4 (16 B; w is padding, xyz is direction)
/// + lightViewProj (64 B).
/// SunDirection is the unit vector pointing FROM the sun TOWARD the scene (i.e. the light direction).
/// The shader scales sky light by max(dot(worldNormal, -SunDirection), 0).
/// LightViewProj is the directional-sun light-space view-projection: the shadow pass renders depth with
/// it, and the main fragment shader projects voxel centers through it to depth-test against the shadow map.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CameraUniform
{
    public Mat4            View;
    public Mat4            Projection;
    public Vector3D<float> SunDirection; // xyz direction; w/_pad rounds this to 16 bytes (vec4)
    private float          _pad;
    public Mat4            LightViewProj;
}
