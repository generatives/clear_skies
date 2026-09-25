using System.Collections.Concurrent;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// The whole-world continental terrain that <see cref="HeartGrid"/>'s hearts cut islands out of: a heightmap of low
/// plains, rising gradually through foothills to wide mountain ranges several kilometres apart. Only what a heart
/// supports exists; the terrain itself is never generated whole. Most of it is low plain, so islands are dense near the
/// bottom of the world and sparse up in the ranges. What covers it goes by height, hot to cold: sand low down, grass, bare rock, then snow. Its
/// highest peaks stay well under the world's top, so none are cut flat.
///
/// Thread-safe: noise sampling only reads its settings.
/// </summary>
public sealed class ContinentTerrain
{
    private static readonly ConcurrentDictionary<ulong, ContinentTerrain> BySeed = new();

    public static ContinentTerrain For(ulong seed) => BySeed.GetOrAdd(seed, s => new ContinentTerrain(s));

    // Surface bands (world Y of a top): hot and sandy low down (the underworld under the plains), sand patches in
    // grass growing sparser up to DryLine; grass giving way to bare rock from RockStart to RockFull, and rock to snow
    // from SnowStart to SnowFull, both in patches.
    public const float SandLine = -60f, DryLine = 120f;
    public const float RockStart = 800f, RockFull = 1050f, SnowStart = 1050f, SnowFull = 1400f;

    private readonly FastNoiseLite _plains;   // broad, gently rolling lowlands
    private readonly FastNoiseLite _hills;    // hills in the foothills
    private readonly FastNoiseLite _ranges;   // mountain ranges run along where this crosses zero
    private readonly FastNoiseLite _warpX, _warpZ; // bend the ranges
    private readonly FastNoiseLite _peaks;    // ridged detail: peaks and valleys within a range
    private readonly FastNoiseLite _minor, _minorMask; // smaller, shorter ranges, in patches
    private readonly FastNoiseLite _detail;   // small bumps everywhere
    private readonly FastNoiseLite _strata;   // wobble of the rock layers that cliffs expose
    private readonly FastNoiseLite _patches;  // sand patches in low grass

    private ContinentTerrain(ulong seed)
    {
        _plains = Noise(seed + 11, FastNoiseLite.FractalType.FBm, 3, 0.0004f);
        _hills = Noise(seed + 12, FastNoiseLite.FractalType.FBm, 3, 0.0012f);
        _ranges = Noise(seed + 13, FastNoiseLite.FractalType.FBm, 1, 0.00007f);
        _warpX = Noise(seed + 14, FastNoiseLite.FractalType.FBm, 2, 0.0002f);
        _warpZ = Noise(seed + 15, FastNoiseLite.FractalType.FBm, 2, 0.0002f);
        _peaks = Noise(seed + 18, FastNoiseLite.FractalType.Ridged, 4, 0.0009f);
        _minor = Noise(seed + 19, FastNoiseLite.FractalType.Ridged, 3, 0.0004f);
        _minorMask = Noise(seed + 20, FastNoiseLite.FractalType.FBm, 2, 0.00015f);
        _detail = Noise(seed + 21, FastNoiseLite.FractalType.FBm, 2, 0.004f);
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

    /// <summary>World Y of the terrain surface at (x, z): plains, rising very gradually through highlands to sharp
    /// mountain ranges a few kilometres across and several apart, peaking at about 1,700; smaller, shorter ranges in
    /// patches across the plains and highlands; small bumps everywhere.</summary>
    public float Height(float x, float z)
    {
        float plains = PlainsLevel + 45f * _plains.GetNoise(x, z) + 14f * _detail.GetNoise(x, z);
        float spine = Spine(x, z);
        float high = 1f - Smoothstep(HighCore, HighEdge, spine);   // the long, gentle rise, with hills
        float core = RangeCore(spine);                             // the range itself

        float hills = high * (HighRise * high + 180f * MathF.Max(0f, _hills.GetNoise(x, z)));
        float r = (_minor.GetNoise(x, z) + 1f) * 0.5f;
        float minor = MinorRise * r * r * Smoothstep(0f, 0.35f, _minorMask.GetNoise(x, z));
        // Sharp peaks: ridged detail, its ridges sharpened, standing on the range's rise.
        float p = (_peaks.GetNoise(x, z) + 1f) * 0.5f;
        float mountains = RangeRise * MathF.Pow(core, 1.6f) * (0.5f + 0.5f * p * MathF.Sqrt(p));
        return Math.Clamp(plains + hills + minor + mountains, IslandGrid.WorldBottom + 64f, IslandGrid.WorldTop - 32f);
    }

    /// <summary>0-1: how far into a mountain range (x, z) is: 1 on its spine, 0 past its edge.</summary>
    public float RangeCore(float x, float z) => RangeCore(Spine(x, z));

    private static float RangeCore(float spine) => 1f - Smoothstep(0f, RangeEdge, spine);

    /// <summary>Nearness to a range's spine: 0 on it, rising away from it.</summary>
    private float Spine(float x, float z)
    {
        float wx = x + RangeWarp * _warpX.GetNoise(x, z), wz = z + RangeWarp * _warpZ.GetNoise(x, z);
        return MathF.Abs(_ranges.GetNoise(wx, wz));
    }

    // Ranges: the spine's noise within RangeEdge of zero is mountains, within HighEdge highlands (full height inside
    // HighCore); bent by up to RangeWarp blocks.
    private const float RangeEdge = 0.3f, HighCore = 0.05f, HighEdge = 1f, RangeWarp = 800f;
    private const float HighRise = 330f, RangeRise = 1000f, MinorRise = 420f;

    /// <summary>The plains' average height: a floor of land under the broken-up highlands and ranges, with a sparser
    /// underworld of smaller pieces below it, down to the cloud sea.</summary>
    public const float PlainsLevel = 150f;

    /// <summary>How far the rock layers at column (x, z) are shifted up or down, for <see cref="Block"/>.</summary>
    public float Strata(float x, float z) => 5f * _strata.GetNoise(x, z);

    /// <summary>0-1 at column (x, z), for <see cref="Block"/>: where the sand patches in low grass are.</summary>
    public float Patch(float x, float z) => 0.5f + 0.5f * _patches.GetNoise(x, z);

    /// <summary>The block at height y in a column whose solid span ends at <paramref name="top"/>: cover by the top's
    /// height (whether it is the terrain surface or a support's own top; those are to be decorated differently
    /// later), then rock layers (<paramref name="strata"/> from <see cref="Strata"/>, <paramref name="patch"/> from
    /// <see cref="Patch"/>). <paramref name="bare"/>, 0-1, is how little the top is out in the open.</summary>
    public static BlockId Block(int y, int top, float strata, float patch, float bare)
    {
        int depth = top - y;
        // A bare top (shaded by land above it, or well below where the surface was) grows nothing and holds no snow:
        // where it is more bare than the patch field, dirt, then rock the barer it is (or sand, low down). Higher up,
        // grass gives way to rock and rock to snow in patches over a range of heights.
        bool isBare = bare > 0.2f + 0.6f * patch;
        bool rocky = patch < Math.Clamp((top - RockStart) / (RockFull - RockStart), 0f, 1f) * 1.2f - 0.1f;
        bool snowy = !isBare && patch < Math.Clamp((top - SnowStart) / (SnowFull - SnowStart), 0f, 1f) * 1.2f - 0.1f;
        if (snowy) { if (depth < 3) return BlockId.Snow; }
        else if (!rocky)
        {
            // Sand where the patch field is under a threshold that falls from mostly sand at SandLine to none by
            // DryLine, bare dirt along the patches' edges, grass elsewhere.
            float sandy = Math.Clamp((DryLine - top) / (DryLine - SandLine), 0f, 1f);
            float edge = patch - (0.1f + 0.62f * sandy);
            if (sandy > 0f && edge < 0f) { if (depth < 4) return BlockId.Sand; }
            else if (isBare && bare > 0.7f) { if (depth < 1) return BlockId.Rock; }
            else if (depth == 0) return isBare || (sandy > 0f && edge < 0.03f) ? BlockId.Dirt : BlockId.Grass;
            else if (depth < 4) return BlockId.Dirt;
        }
        else if (depth < 2) return BlockId.Rock;

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
