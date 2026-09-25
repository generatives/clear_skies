using System.Collections.Concurrent;
using ClearSkies.Engine.Rendering;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Clouds bank up around islands: 1 over an island and out to <see cref="Near"/> blocks past its rim, easing to 0 by
/// <see cref="Far"/> blocks past it. Islands come straight from <see cref="RegionGrid"/>'s placement, so this needs no
/// generated terrain and covers islands far past the streamed world.
/// </summary>
public sealed class IslandCloudDensity : ICloudDensityMap
{
    private const float Near = 150f;
    private const float Far  = 1100f;

    private readonly ulong _seed;
    private readonly ConcurrentDictionary<(int, int), IslandDef[]> _cells = new();

    public IslandCloudDensity(ulong seed) => _seed = seed;

    public float Density(float x, float z)
    {
        int cx = (int)MathF.Floor(x / RegionGrid.CellSize), cz = (int)MathF.Floor(z / RegionGrid.CellSize);
        float best = 0f;
        // Islands stay well inside their own region cell, but their cloud halo reaches into the neighbours.
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            foreach (var island in Islands(cx + dx, cz + dz))
            {
                float ex = x - island.CenterX, ez = z - island.CenterZ;
                float reach = island.Radius * MathF.Max(island.StretchMajor, island.StretchMinor);
                float past = MathF.Sqrt(ex * ex + ez * ez) - reach;
                float t = Math.Clamp((past - Near) / (Far - Near), 0f, 1f);
                best = MathF.Max(best, 1f - t * t * (3f - 2f * t));
            }
        return best;
    }

    private IslandDef[] Islands(int cellX, int cellZ)
    {
        if (_cells.TryGetValue((cellX, cellZ), out var islands)) return islands;
        Span<IslandDef> buf = stackalloc IslandDef[4];
        int n = RegionGrid.ResolveIslandsForCell(_seed, cellX, cellZ, buf);
        islands = buf[..n].ToArray();
        if (_cells.Count > 4096) _cells.Clear(); // a long flight; recomputing a cell is cheap
        _cells[(cellX, cellZ)] = islands;
        return islands;
    }
}
