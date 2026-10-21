using System.Numerics;
using ClearSkies.Engine.Rendering;

namespace ClearSkies.Game.Generation;

/// <summary>The biomes' tints (<see cref="Biomes.Tint"/>) for the renderer's climate tint, by each column's climate
/// and height.</summary>
public sealed class BiomeTintMap : IClimateTintMap
{
    private readonly ContinentTerrain _terrain;

    public BiomeTintMap(ulong seed) => _terrain = ContinentTerrain.For(seed);

    public void Column(float x, float z, ReadOnlySpan<float> heights, Span<Vector4> tints)
    {
        float regional = _terrain.Heat(x, z), wet = _terrain.Wet(x, z);
        for (int j = 0; j < heights.Length; j++)
        {
            var (r, g, b, a) = Biomes.Tint(Biomes.Heat(heights[j], regional), wet);
            tints[j] = new Vector4(r, g, b, a);
        }
    }
}
