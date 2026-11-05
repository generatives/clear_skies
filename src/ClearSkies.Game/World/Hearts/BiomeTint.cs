using System.Numerics;
using ClearSkies.Engine.Rendering;

namespace ClearSkies.Game.Generation;

/// <summary>The biomes' tints (<see cref="Biomes.Tint"/>) for the renderer's climate tint, by each column's climate,
/// height and nearness to water: water from the <see cref="Landscape"/>'s landmasses as they are worked out (the tint
/// is sampled again as each one is; none is worked out just for the tint).</summary>
public sealed class BiomeTintMap(ulong seed, Landscape? landscape = null) : IClimateTintMap
{
    private readonly ContinentTerrain _terrain = ContinentTerrain.For(seed);

    public int Version => landscape?.Finished ?? 0;

    public void Column(float x, float z, ReadOnlySpan<float> heights, Span<Vector4> tints)
    {
        float regional = _terrain.Heat(x, z), wet = _terrain.Rain(x, z), marsh = 0f;
        if (landscape?.Done(x, z) is { } shaped)
        {
            float wx = x, wz = z;
            _terrain.Warp(ref wx, ref wz, Biomes.ShoreWarp);
            wet = Biomes.Wet(wet, shaped.Watered(wx, wz));
            marsh = shaped.Marsh(wx, wz);
        }
        for (int j = 0; j < heights.Length; j++)
        {
            var (r, g, b, a) = Biomes.Tint(Biomes.Heat(heights[j], regional), wet, marsh);
            tints[j] = new Vector4(r, g, b, a);
        }
    }
}
