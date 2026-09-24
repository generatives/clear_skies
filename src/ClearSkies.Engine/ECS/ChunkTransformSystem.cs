using ClearSkies.Engine.Core;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Places every volume's chunk entities from the volume root's <see cref="Transform"/> and
/// <see cref="ChunkVolume.Pivot"/>: world = rootPos + R·(chunkLocalOrigin − pivot). The same for the static world
/// and dynamic grids; for a grid the root Transform is its body pose (see <see cref="PhysicsTransformSyncSystem"/>,
/// which runs just before this) and the pivot its centre of mass, so meshes and colliders stay aligned. A volume
/// whose root pose and pivot haven't changed since it was last placed is skipped; new chunks are placed as they are
/// added (<see cref="ChunkVolume"/>.AddChunk).
///
/// Lighting note (Tier 2 motion invariance): this system must NEVER enqueue a relight. A grid's light field lives
/// in grid-local coordinates and is carried rigidly by the mesh transform, so movement and rotation re-shade for
/// free (the shader's per-frame N·L term) without recomputing lighting.
/// </summary>
public sealed class ChunkTransformSystem : ISystem
{
    private readonly EntitySet _volumes;

    public ChunkTransformSystem(World world)
    {
        _volumes = world.GetEntities().With<ChunkGrid>().With<Transform>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _volumes.GetEntities())
        {
            var volume = e.Get<ChunkGrid>().Volume;
            ref readonly var root = ref e.Get<Transform>();

            var placedFor = (root.Position, root.Rotation, volume.Pivot);
            if (volume.PlacedFor == placedFor) continue;
            volume.PlacedFor = placedFor;

            foreach (var (pos, entry) in volume.All)
                if (entry.Entity.IsAlive) entry.Entity.Set(volume.ChunkTransform(root, pos));
        }
    }
}
