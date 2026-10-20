namespace ClearSkies.Game.Generation;

/// <summary>What the land on a top is like (see <see cref="Biomes"/>).</summary>
public enum Biome : byte { Temperate, Autumn, Desert, Badlands }

/// <summary>
/// Biomes by climate: each sits at a point of heat and wetness, and a top takes the nearest. Heat goes mostly by
/// height, cold up high and hot deep down (the underworld's caves are deserts and badlands), shifted by a broad
/// regional field; wetness is a broad field of its own (see <see cref="ContinentTerrain.Heat"/> and
/// <see cref="ContinentTerrain.Wet"/>). Snow and bare rock still come with height, over whatever biome is there.
///
/// Each biome also tints grass, leaves and plants (see <see cref="Tint"/>), blended between nearby biomes by how near
/// each is in the climate, so the colour shifts gradually across a border while the ground blocks change at it.
/// </summary>
public static class Biomes
{
    // Each biome's heat (0 cold, 1 hot, more deep underground) and wetness (0-1), and the tint it gives grass and
    // leaves: a colour (sRGB) and how strongly it applies (0: their own green).
    private static readonly (Biome Biome, float Heat, float Wet, float R, float G, float B, float Strength)[] Table =
    {
        (Biome.Temperate, 0.55f, 0.55f, 0f,    0f,    0f,    0f),
        (Biome.Autumn,    0.30f, 0.75f, 0.93f, 0.52f, 0.18f, 0.85f),
        (Biome.Desert,    0.75f, 0.35f, 0.78f, 0.74f, 0.42f, 0.6f),
        (Biome.Badlands,  0.95f, 0.10f, 0.72f, 0.48f, 0.30f, 0.6f),
    };

    /// <summary>How far apart in the climate (squared) biomes' tints blend over.</summary>
    private const float TintBlend = 0.015f;

    /// <summary>How hot a top at <paramref name="top"/> is, with <paramref name="regional"/> (-1 to 1) from
    /// <see cref="ContinentTerrain.Heat"/>: cooler by a unit every 900 blocks up from the plains.</summary>
    public static float Heat(float top, float regional) => 0.6f - (top - ContinentTerrain.PlainsLevel) / 900f + 0.6f * regional;

    /// <summary>The biome nearest to (heat, wet).</summary>
    public static Biome Pick(float heat, float wet)
    {
        var best = Biome.Temperate;
        float bestD = float.MaxValue;
        foreach (var (biome, h, w, _, _, _, _) in Table)
        {
            float d = (heat - h) * (heat - h) + (wet - w) * (wet - w);
            if (d < bestD) { bestD = d; best = biome; }
        }
        return best;
    }

    /// <summary>How far apart in the climate (squared) a desert's sandy fringe reaches.</summary>
    private const float DryBlend = 0.012f;

    /// <summary>0-1: how near (heat, wet) is to being desert, reaching 1 at its border, for the sand patches that thicken
    /// ahead of one (see <see cref="ContinentTerrain.Block"/>).</summary>
    public static float Dry(float heat, float wet)
    {
        float desert = float.MaxValue, other = float.MaxValue;
        foreach (var e in Table)
        {
            float d = (heat - e.Heat) * (heat - e.Heat) + (wet - e.Wet) * (wet - e.Wet);
            if (e.Biome == Biome.Desert) desert = d; else other = MathF.Min(other, d);
        }
        return MathF.Min(1f, 2f / (1f + MathF.Exp((desert - other) / DryBlend)));
    }

    /// <summary>The tint at (heat, wet): each biome's weighted by how near it is, the nearest most. rgb is the colour
    /// (sRGB) and a how strongly it applies, all 0-1.</summary>
    public static (float R, float G, float B, float A) Tint(float heat, float wet)
    {
        float nearest = float.MaxValue;
        foreach (var e in Table) nearest = MathF.Min(nearest, (heat - e.Heat) * (heat - e.Heat) + (wet - e.Wet) * (wet - e.Wet));
        float sum = 0f, r = 0f, g = 0f, b = 0f, a = 0f;
        foreach (var e in Table)
        {
            float d = (heat - e.Heat) * (heat - e.Heat) + (wet - e.Wet) * (wet - e.Wet);
            float weight = MathF.Exp(-(d - nearest) / TintBlend);
            sum += weight;
            r += weight * e.Strength * e.R; g += weight * e.Strength * e.G; b += weight * e.Strength * e.B;
            a += weight * e.Strength;
        }
        if (a <= 0f) return (0f, 0f, 0f, 0f);
        return (r / a, g / a, b / a, a / sum);
    }
}
