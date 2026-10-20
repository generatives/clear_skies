using System.Numerics;

namespace ClearSkies.Engine.Rendering;

/// <summary>
/// A colour tint over the world around the camera, for the blocks that take one (<see cref="Voxels.BlockDef.Tint"/>):
/// grass and leaves can shade from green to autumn orange or dry olive across a landscape without a block for every
/// shade. The game fills it (<see cref="Submit"/>) and the renderer samples it per fragment, blended smoothly between
/// texels, so a tint changes gradually rather than block by block.
///
/// It is a box of <see cref="SizeXZ"/> by <see cref="SizeXZ"/> by <see cref="SizeY"/> RGBA8 texels, each
/// <see cref="CellXZ"/> blocks across and <see cref="CellY"/> tall, from <see cref="BottomY"/> up, its low corner at the
/// submitted origin. A texel's rgb is the tint's colour (sRGB) and alpha how strongly it applies: a tinted texture keeps
/// its brightness and takes that colour. Before anything is submitted, and on texels left at zero, nothing is tinted.
/// Past the box's edges the nearest edge texel's tint applies.
/// </summary>
public static class ClimateTint
{
    public const int SizeXZ = 256, CellXZ = 8, SizeY = 16, CellY = 128, BottomY = -256;

    /// <summary>The bytes a submitted volume has: x fastest, then z, then y.</summary>
    public const int Bytes = SizeXZ * SizeXZ * SizeY * 4;

    private static readonly object Gate = new();
    private static (float X, float Z, byte[] Texels)? _pending;

    /// <summary>Where the camera was at the last frame drawn: what the volume should be centred near.</summary>
    public static Vector3 Focus { get; internal set; }

    /// <summary>Hands the renderer a new volume (<see cref="Bytes"/> long; see the class) whose low corner is at
    /// world (<paramref name="originX"/>, <paramref name="originZ"/>). It is drawn from the next frame on. Thread-safe:
    /// a volume can be built on a worker.</summary>
    public static void Submit(float originX, float originZ, byte[] texels)
    {
        if (texels.Length != Bytes) throw new ArgumentException($"A tint volume is {Bytes} bytes.", nameof(texels));
        lock (Gate) _pending = (originX, originZ, texels);
    }

    internal static bool TakePending(out float originX, out float originZ, out byte[] texels)
    {
        lock (Gate)
        {
            if (_pending is not { } p) { originX = originZ = 0f; texels = Array.Empty<byte>(); return false; }
            (originX, originZ, texels) = p;
            _pending = null;
            return true;
        }
    }
}
