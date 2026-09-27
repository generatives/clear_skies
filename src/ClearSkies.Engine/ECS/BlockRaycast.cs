using ClearSkies.Engine.Math;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>The block a ray hits first across every volume (the static world and each grid).</summary>
public readonly record struct BlockHit(Entity Root, ChunkVolume Volume, Vector3D<int> Block, Vector3D<int> Normal, float Distance,
                                       Vector3D<float> OriginInVolume);

public static class BlockRaycast
{
    /// <summary>Casts in each volume's own space; rotation preserves length, so hit distances compare directly.
    /// <paramref name="volumes"/> must be queried with at least <c>With&lt;ChunkGrid&gt;().With&lt;Transform&gt;()</c>.
    /// With <paramref name="drawn"/>, volumes are where they're drawn (for a ray from the drawn camera: a moving ship's
    /// true pose is up to a tick ahead of its drawing); otherwise at their true poses, for a ray from a tick.</summary>
    public static BlockHit? Nearest(EntitySet volumes, Vector3D<float> origin, Vector3D<float> dir, float reach, bool drawn = false)
    {
        BlockHit? best = null;
        foreach (ref readonly Entity e in volumes.GetEntities())
        {
            var volume = e.Get<ChunkGrid>().Volume;
            var root = drawn ? e.DrawnPose() : e.Get<Transform>();
            var lo = volume.WorldToVoxel(root, origin);
            var ld = Vec.Rotate(Vec.Conjugate(root.Rotation), dir);
            if (VoxelRaycaster.Cast(volume, lo, ld, reach, out var b, out var n, out var d) && (best is null || d < best.Value.Distance))
                best = new BlockHit(e, volume, b, n, d, lo);
        }
        return best;
    }

    /// <summary>The view direction for look angles (yaw about up, then pitch).</summary>
    public static Vector3D<float> Direction(float yaw, float pitch) =>
        Vector3D.Normalize(Vec.Rotate(Quaternion<float>.CreateFromYawPitchRoll(yaw, pitch, 0f), new Vector3D<float>(0, 0, -1)));
}
