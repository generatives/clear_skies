using System.Collections.Concurrent;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// The whole-world continental terrain that <see cref="HeartGrid"/>'s hearts cut islands out of: a heightmap of plains
/// low in the band, hills, and mountain ranges reaching near its top. Only what a heart supports exists; the terrain
/// itself is never generated whole. Most of it is low, so islands are dense near the bottom of the world and rare near
/// the top (the peaks). What covers it goes by height, hot to cold: sand low down, grass, bare rock, then snow. Its
/// highest peaks stay well under the world's top, so none are cut flat.
///
/// Thread-safe: noise sampling only reads its settings.
/// </summary>
public sealed class ContinentTerrain
{
    private static readonly ConcurrentDictionary<ulong, ContinentTerrain> BySeed = new();

    public static ContinentTerrain For(ulong seed) => BySeed.GetOrAdd(seed, s => new ContinentTerrain(s));

    // Surface bands (world Y of a top): hot and sandy low down, sand patches in grass growing sparser up to DryLine,
    // grass up to RockLine, bare rock up to SnowLine, snow above.
    public const float SandLine = 100f, DryLine = 450f, RockLine = 980f, SnowLine = 1150f;

    private readonly FastNoiseLite _plains;   // broad rolling lowlands
    private readonly FastNoiseLite _hills;    // hills, gated by _hillMask
    private readonly FastNoiseLite _hillMask;
    private readonly FastNoiseLite _ridges;   // ridged mountain ranges, gated by _rangeMask
    private readonly FastNoiseLite _rangeMask;
    private readonly FastNoiseLite _strata;   // wobble of the rock layers that cliffs expose
    private readonly FastNoiseLite _patches;  // sand patches in low grass

    private ContinentTerrain(ulong seed)
    {
        _plains = Noise(seed + 11, FastNoiseLite.FractalType.FBm, 3, 0.00035f);
        _hills = Noise(seed + 12, FastNoiseLite.FractalType.FBm, 3, 0.0012f);
        _hillMask = Noise(seed + 13, FastNoiseLite.FractalType.FBm, 2, 0.0003f);
        _ridges = Noise(seed + 14, FastNoiseLite.FractalType.Ridged, 4, 0.0005f);
        _rangeMask = Noise(seed + 15, FastNoiseLite.FractalType.FBm, 2, 0.00012f);
        _strata = Noise(seed + 16, FastNoiseLite.FractalType.FBm, 2, 0.01f);
        _patches = Noise(seed + 17, FastNoiseLite.FractalType.FBm, 3, 0.012f);
    }

    private static FastNoiseLite Noise(ulong seed, FastNoiseLite.FractalType fractal, int octaves, float frequency)
    {
        var n = new FastNoiseLite(unchecked((int)seed));
        n.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        n.SetFractalType(fractal);
        n.SetFractalOctaves(octaves);
        n.SetFrequency(frequency);
        return n;
    }

    /// <summary>World Y of the terrain surface at (x, z).</summary>
    public float Height(float x, float z)
    {
        // Mostly plains and hills; mountain ranges only where their mask is high, peaking at about 1,650.
        float plains = 430f + 170f * _plains.GetNoise(x, z);
        float hills = 300f * MathF.Max(0f, _hills.GetNoise(x, z)) * Smoothstep(-0.1f, 0.5f, _hillMask.GetNoise(x, z));
        float r = (_ridges.GetNoise(x, z) + 1f) * 0.5f;
        float ranges = 1000f * r * r * Smoothstep(0.1f, 0.5f, _rangeMask.GetNoise(x, z));
        return Math.Clamp(plains + hills + ranges, IslandGrid.WorldBottom + 64f, IslandGrid.WorldTop - 32f);
    }

    /// <summary>How far the rock layers at column (x, z) are shifted up or down, for <see cref="Block"/>.</summary>
    public float Strata(float x, float z) => 5f * _strata.GetNoise(x, z);

    /// <summary>0-1 at column (x, z), for <see cref="Block"/>: where the sand patches in low grass are.</summary>
    public float Patch(float x, float z) => 0.5f + 0.5f * _patches.GetNoise(x, z);

    /// <summary>The block at height y in a column whose solid span ends at <paramref name="top"/>: cover by the top's
    /// height (whether it is the terrain surface or a buried heart's slab top; those are to be decorated differently
    /// later), then rock layers (<paramref name="strata"/> from <see cref="Strata"/>, <paramref name="patch"/> from
    /// <see cref="Patch"/>).</summary>
    public static BlockId Block(int y, int top, float strata, float patch)
    {
        int depth = top - y;
        if (top < RockLine)
        {
            // Sand where the patch field is under a threshold that falls from mostly sand at SandLine to none by
            // DryLine, bare dirt along the patches' edges, grass elsewhere.
            float sandy = Math.Clamp((DryLine - top) / (DryLine - SandLine), 0f, 1f);
            float edge = patch - (0.1f + 0.62f * sandy);
            if (sandy > 0f && edge < 0f) { if (depth < 4) return BlockId.Sand; }
            else if (depth == 0) return sandy > 0f && edge < 0.03f ? BlockId.Dirt : BlockId.Grass;
            else if (depth < 4) return BlockId.Dirt;
        }
        else if (top < SnowLine) { if (depth < 2) return BlockId.Rock; }
        else if (depth < 3) return BlockId.Snow;

        // Rock layers: bands a few blocks thick, wobbling a little, alternating stone with darker rock, so cliff faces
        // show strata.
        uint h = (uint)(int)MathF.Floor((y + strata) / 6f) * 0x9E3779B1u;
        h ^= h >> 15; h *= 0x85EBCA77u; h ^= h >> 13;
        return (h & 0xFF) < 90 ? BlockId.Rock : BlockId.Stone;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
