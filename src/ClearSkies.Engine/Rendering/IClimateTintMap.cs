using System.Numerics;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// The colour tint the world gives the blocks that take one (<see cref="Voxels.BlockDef.Tint"/>), so grass and leaves
/// can shade from green to autumn orange or dry straw across a landscape without a block for every shade.
/// <see cref="ECS.ClimateTintSystem"/> samples it on a coarse grid around the camera, on a worker thread, so
/// implementations must be thread-safe.
/// </summary>
public interface IClimateTintMap
{
    /// <summary>Fills <paramref name="tints"/> with the tint of column (x, z) at each of <paramref name="heights"/>:
    /// rgb a colour (sRGB, 0-1) and w how strongly it applies (0 none, 1 fully).</summary>
    void Column(float x, float z, ReadOnlySpan<float> heights, Span<Vector4> tints);
}
