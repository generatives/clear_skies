using ClearSkies.Engine.Rendering;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Clouds bank up around islands: 1 over an island and out to <see cref="Near"/> blocks past its rim, easing to 0 by
/// <see cref="Far"/> blocks past it. Only large and medium islands draw clouds. Islands come straight from
/// <see cref="IslandGrid"/>'s placement, so this needs no generated terrain and covers islands far past the streamed
/// world.
/// </summary>
public sealed class IslandCloudDensity : ICloudDensityMap
{
    private const float Near = 150f;
    private const float Far  = 1100f;

    private readonly ulong _seed;

    public IslandCloudDensity(ulong seed) => _seed = seed;

    public float Density(float x, float z)
    {
        // Islands stay inside their own cells, but their cloud halo reaches past them: look a halo further out.
        Span<IslandDef> islands = stackalloc IslandDef[16];
        int n = IslandGrid.Collect(_seed, IslandClass.Large, x - Far, IslandGrid.WorldBottom, z - Far,
                                   x + Far, IslandGrid.WorldTop - 1, z + Far, islands, 0);
        n = IslandGrid.Collect(_seed, IslandClass.Medium, x - Far, IslandGrid.WorldBottom, z - Far,
                               x + Far, IslandGrid.WorldTop - 1, z + Far, islands, n);
        float best = 0f;
        foreach (var island in islands[..n])
        {
            float ex = x - island.CenterX, ez = z - island.CenterZ;
            float reach = island.Radius * MathF.Max(island.StretchMajor, island.StretchMinor);
            float past = MathF.Sqrt(ex * ex + ez * ez) - reach;
            float t = Math.Clamp((past - Near) / (Far - Near), 0f, 1f);
            best = MathF.Max(best, 1f - t * t * (3f - 2f * t));
        }
        return best;
    }
}
