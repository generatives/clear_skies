using System.Runtime.InteropServices;
using ClearSkies.Engine.Math;

namespace ClearSkies.Engine.Rendering;

/// <summary>Per-frame camera uniform block (128 bytes). Must match @group(0) @binding(0) in basic.wgsl.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CameraUniform
{
    public Mat4 View;
    public Mat4 Projection;
}
