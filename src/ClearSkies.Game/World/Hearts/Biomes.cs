namespace ClearSkies.Game.Generation;

/// <summary>What the land on a top is like (see <see cref="Biomes"/>).</summary>
public enum Biome : byte { Temperate, Autumn, Desert, Badlands }

/// <summary>
/// Biomes by climate: each sits at a point of heat and wetness, and a top takes the nearest. Heat goes mostly by
/// height, cold up high and hot deep down (the underworld's caves are deserts and badlands), shifted by a broad
/// regional field; wetness is a broad field of its own (see <see cref="ContinentTerrain.Heat"/> and
/// <see cref="ContinentTerrain.Wet"/>). Snow and bare rock still come with height, over whatever biome is there.
/// </summary>
public static class Biomes
{
    // Each biome's heat (0 cold, 1 hot, more deep underground) and wetness (0-1).
    private static readonly (Biome Biome, float Heat, float Wet)[] Table =
    {
        (Biome.Temperate, 0.55f, 0.55f),
        (Biome.Autumn,    0.30f, 0.75f),
        (Biome.Desert,    0.80f, 0.30f),
        (Biome.Badlands,  1.00f, 0.00f),
    };

    /// <summary>How hot a top at <paramref name="top"/> is, with <paramref name="regional"/> (-1 to 1) from
    /// <see cref="ContinentTerrain.Heat"/>: cooler by a unit every 900 blocks up from the plains.</summary>
    public static float Heat(float top, float regional) => 0.6f - (top - ContinentTerrain.PlainsLevel) / 900f + 0.6f * regional;

    /// <summary>The biome nearest to (heat, wet).</summary>
    public static Biome Pick(float heat, float wet)
    {
        var best = Biome.Temperate;
        float bestD = float.MaxValue;
        foreach (var (biome, h, w) in Table)
        {
            float d = (heat - h) * (heat - h) + (wet - w) * (wet - w);
            if (d < bestD) { bestD = d; best = biome; }
        }
        return best;
    }
}
