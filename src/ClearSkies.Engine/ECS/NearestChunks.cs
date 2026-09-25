using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Picks the chunk entities nearest the camera from a set of flagged chunks, for systems that work through a
/// flag a few chunks per frame (meshing, GPU upload). Taking them in the set's own order does the newest — i.e. the
/// furthest, since streaming loads closest first — first: removing an entity from a DefaultEcs set moves the set's
/// last entity into its slot, so each frame's batch starts with the most recently flagged chunks.
/// </summary>
internal static class NearestChunks
{
    private const int S = ChunkData.Size;

    /// <summary>Fills <paramref name="into"/> with up to <paramref name="count"/> entities of <paramref name="set"/>
    /// (which must have <see cref="Chunk"/>), closest first. Ship chunks come before any world chunk: ships are few
    /// and are what the player is usually standing on.</summary>
    public static void Select(EntitySet set, EntitySet cameras, int count, List<Entity> into)
    {
        into.Clear();
        if (count <= 0) return;
        bool haveCam = CameraUtil.TryGetActive(cameras, out var cam);

        Span<float> best = stackalloc float[count];
        foreach (ref readonly Entity e in set.GetEntities())
        {
            var entry = e.Get<Chunk>().Entry;
            float d = 0f;
            if (haveCam && entry.Volume.Gpu.IsWorld)
            {
                var c = entry.Position.WorldOrigin + new Vector3D<float>(S * 0.5f) - cam.Position;
                d = c.X * c.X + c.Y * c.Y + c.Z * c.Z;
            }
            if (into.Count == count && d >= best[count - 1]) continue;

            // Insertion into the sorted short list.
            int i = System.Math.Min(into.Count, count - 1);
            if (into.Count < count) into.Add(default);
            while (i > 0 && best[i - 1] > d) { best[i] = best[i - 1]; into[i] = into[i - 1]; i--; }
            best[i] = d;
            into[i] = e;
        }
    }
}
