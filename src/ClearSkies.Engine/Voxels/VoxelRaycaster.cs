using Silk.NET.Maths;

namespace ClearSkies.Engine.Voxels;

/// <summary>
/// Amanatides &amp; Woo fast-voxel traversal. Advances along the ray one grid cell at a time,
/// always stepping on the nearest cell boundary (X, Y, or Z), until a solid block is found
/// or <paramref name="maxDist"/> is exceeded.
/// </summary>
public static class VoxelRaycaster
{
    /// <summary>
    /// Casts against a single <see cref="ChunkVolume"/> in that volume's own coordinate space. For a
    /// rotated dynamic grid the caller transforms <paramref name="origin"/>/<paramref name="dir"/> into
    /// grid-local space first; because that transform is a rigid motion (no scale), the returned
    /// <paramref name="hitDistance"/> is identical in world space and can be compared across volumes.
    /// </summary>
    public static bool Cast(
        ChunkVolume         volume,
        Vector3D<float>     origin,
        Vector3D<float>     dir,
        float               maxDist,
        out Vector3D<int>   hitBlock,
        out Vector3D<int>   hitNormal,
        out float           hitDistance)
    {
        hitBlock    = default;
        hitNormal   = default;
        hitDistance = 0f;

        int x = (int)MathF.Floor(origin.X);
        int y = (int)MathF.Floor(origin.Y);
        int z = (int)MathF.Floor(origin.Z);

        // Step direction and per-unit-distance ray parameters.
        int   sx = dir.X >= 0 ? 1 : -1,   sy = dir.Y >= 0 ? 1 : -1,   sz = dir.Z >= 0 ? 1 : -1;
        float dx = MathF.Abs(dir.X) > 1e-7f ? 1f / MathF.Abs(dir.X) : float.MaxValue;
        float dy = MathF.Abs(dir.Y) > 1e-7f ? 1f / MathF.Abs(dir.Y) : float.MaxValue;
        float dz = MathF.Abs(dir.Z) > 1e-7f ? 1f / MathF.Abs(dir.Z) : float.MaxValue;

        // Distance along the ray to the first boundary crossing on each axis.
        float tx = sx > 0 ? (x + 1 - origin.X) * dx : (origin.X - x) * dx;
        float ty = sy > 0 ? (y + 1 - origin.Y) * dy : (origin.Y - y) * dy;
        float tz = sz > 0 ? (z + 1 - origin.Z) * dz : (origin.Z - z) * dz;

        var normal = Vector3D<int>.Zero;

        for (;;)
        {
            // Check the current cell.
            var id = volume.GetBlock(x, y, z);
            if (id != BlockId.Air && BlockRegistry.Get(id).IsSolid)
            {
                hitBlock  = new(x, y, z);
                hitNormal = normal;
                return true;
            }

            // Advance to the next nearest cell boundary. tx/ty/tz are the distances to the next
            // crossing on each axis; the smallest is the next step and the distance at which we
            // enter the next cell. If that distance already exceeds maxDist we are done.
            if (tx < ty && tx < tz)
            {
                if (tx > maxDist) return false;
                hitDistance = tx;
                x      += sx;
                normal  = new(-sx, 0, 0);
                tx     += dx;
            }
            else if (ty < tz)
            {
                if (ty > maxDist) return false;
                hitDistance = ty;
                y      += sy;
                normal  = new(0, -sy, 0);
                ty     += dy;
            }
            else
            {
                if (tz > maxDist) return false;
                hitDistance = tz;
                z      += sz;
                normal  = new(0, 0, -sz);
                tz     += dz;
            }
        }
    }
}
