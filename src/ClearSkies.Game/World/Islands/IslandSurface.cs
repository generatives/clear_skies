using System.Collections.Concurrent;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// What the islands' surfaces are like, across the whole world: their relief (<see cref="Hills"/>, and
/// <see cref="Mountains"/> on the big landmasses), what covers them, which goes by height, hot to cold: sand low down,
/// grass, bare rock, then snow (<see cref="Block"/>), and what grows on them (<see cref="Plant"/>, and the fields
/// <see cref="Trees"/> reads). <see cref="IslandField"/> places the islands and <see cref="IslandWorldGenerator"/> shapes
/// them from these.
///
/// Thread-safe: noise sampling only reads its settings.
/// </summary>
public sealed class IslandSurface
{
    private static readonly ConcurrentDictionary<ulong, IslandSurface> BySeed = new();

    public static IslandSurface For(ulong seed) => BySeed.GetOrAdd(seed, s => new IslandSurface(s));

    // Surface bands (world Y of a top): hot and sandy low down (the underworld under the landmasses), sand patches in
    // grass growing sparser up to DryLine; grass giving way to bare rock from RockStart to RockFull, and rock to snow
    // from SnowStart to SnowFull, both in patches.
    public const float SandLine = -60f, DryLine = 120f;
    public const float RockStart = 550f, RockFull = 720f, SnowStart = 720f, SnowFull = 950f;

    private readonly FastNoiseLite _hills;    // one octave, summed by Hills with its slope damping the finer ones
    private readonly FastNoiseLite _peaks;    // ridged: peaks and valleys within a range
    private readonly FastNoiseLite _ranges;   // where the ranges are
    private readonly FastNoiseLite _rangeSize; // how great each range is
    private readonly FastNoiseLite _relief;   // how hilly the land is
    private readonly FastNoiseLite _strata;   // wobble of the rock layers that cliffs expose
    private readonly FastNoiseLite _patches;  // sand patches in low grass
    private readonly FastNoiseLite _flora, _kind; // where plants grow thick, and which kind
    private readonly FastNoiseLite _woods;        // where trees grow thick, apart from the plants

    private IslandSurface(ulong seed)
    {
        _hills = Noise(seed + 11, FastNoiseLite.FractalType.None, 1, 1f);
        _peaks = Noise(seed + 18, FastNoiseLite.FractalType.Ridged, 4, 0.0012f);
        _ranges = Noise(seed + 13, FastNoiseLite.FractalType.FBm, 2, 0.00035f);
        _rangeSize = Noise(seed + 14, FastNoiseLite.FractalType.FBm, 1, 0.00012f);
        _relief = Noise(seed + 15, FastNoiseLite.FractalType.FBm, 2, 0.0004f);
        _strata = Noise(seed + 16, FastNoiseLite.FractalType.FBm, 2, 0.01f);
        _patches = Noise(seed + 17, FastNoiseLite.FractalType.FBm, 3, 0.012f);
        _flora = Noise(seed + 22, FastNoiseLite.FractalType.FBm, 3, 0.02f);
        _woods = Noise(seed + 24, FastNoiseLite.FractalType.FBm, 2, 0.008f);
        _kind = Noise(seed + 23, FastNoiseLite.FractalType.FBm, 2, 0.035f);
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

    // Hills: HillOctaves octaves from HillScale blocks across down, each half the size and height of the last.
    private const int HillOctaves = 5;
    private const float HillScale = 420f, SlopeStep = 0.01f;

    /// <summary>About -1 to 1 at (x, z): rolling hills and valleys from a few hundred blocks across down to a dozen, worn
    /// as if by water. Each finer octave is damped by how steep the coarser ones already are there (Inigo Quilez's
    /// derivative fBm), so slopes stay smooth and detail gathers on the flats and ridges: soft valleys, crisp
    /// crests.</summary>
    public float Hills(float x, float z)
    {
        float px = x / HillScale, pz = z / HillScale;
        float sum = 0f, amp = 1f, dx = 0f, dz = 0f, norm = 0f;
        for (int o = 0; o < HillOctaves; o++)
        {
            float n = _hills.GetNoise(px, pz);
            dx += (_hills.GetNoise(px + SlopeStep, pz) - n) / SlopeStep;
            dz += (_hills.GetNoise(px, pz + SlopeStep) - n) / SlopeStep;
            sum += amp * n / (1f + 0.15f * (dx * dx + dz * dz));
            norm += amp;
            amp *= 0.5f;
            // Next octave: twice as fine, turned so the octaves' grids don't line up.
            (px, pz) = (1.6f * px - 1.2f * pz + 17.3f, 1.2f * px + 1.6f * pz - 9.1f);
        }
        return sum / norm * 1.6f;
    }

    /// <summary>0.25-1 at (x, z): how hilly the land is, in regions a few kilometres across: rolling lowland, then
    /// hill country, then rough uplands.</summary>
    public float Relief(float x, float z) => 0.25f + 0.75f * Smoothstep(-0.5f, 0.6f, _relief.GetNoise(x, z));

    /// <summary>0-1 at (x, z): mountain ranges where <see cref="IslandField"/>'s landmasses carry them, sharp ridged
    /// peaks with valleys between, in ranges a few kilometres long with lowland between them. Ranges differ: most are
    /// a few hundred blocks high, and only the odd one is great; each rises from foothills at its edges.</summary>
    public float Mountains(float x, float z)
    {
        float range = Smoothstep(0.2f, 0.6f, _ranges.GetNoise(x, z));
        if (range <= 0f) return 0f;
        float size = 0.15f + 0.85f * Smoothstep(-0.2f, 0.7f, _rangeSize.GetNoise(x, z));
        // A broad rise under the whole range, ridged peaks standing on it, both growing in from its edges.
        float p = (_peaks.GetNoise(x, z) + 1f) * 0.5f;
        return size * range * range * (0.3f + 0.7f * p * MathF.Sqrt(p));
    }

    /// <summary>How far the rock layers at column (x, z) are shifted up or down, for <see cref="Block"/>.</summary>
    public float Strata(float x, float z) => 5f * _strata.GetNoise(x, z);

    /// <summary>0-1 at column (x, z), for <see cref="Block"/>: where the sand patches in low grass are.</summary>
    public float Patch(float x, float z) => 0.5f + 0.5f * _patches.GetNoise(x, z);

    /// <summary>0-1 at column (x, z), for <see cref="Plant"/>: how thickly plants grow, in stands a few dozen blocks
    /// across with open ground between.</summary>
    public float Flora(float x, float z) => 0.5f + 0.5f * _flora.GetNoise(x, z);

    /// <summary>0-1 at column (x, z), for <see cref="Trees"/>: how thickly trees grow, in woods a hundred or so blocks
    /// across, laid out independently of the plant stands.</summary>
    public float Woods(float x, float z) => 0.5f + 0.5f * _woods.GetNoise(x, z);

    /// <summary>0-1 at column (x, z), for <see cref="Plant"/>: which kind of plant a stand is mostly made of.</summary>
    public float Kind(float x, float z) => 0.5f + 0.5f * _kind.GetNoise(x, z);

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

    /// <summary>What grows in the open cell (x, y, z) on top of <paramref name="ground"/> (the top block of a solid
    /// span, from <see cref="Block"/>), or air. Plants grow in stands: thick where <paramref name="flora"/> (from
    /// <see cref="Flora"/>) is high, thinning gradually to a sparse scattering between them, each stand mostly one kind by
    /// <paramref name="kind"/> (from <see cref="Kind"/>) with tall grass at its heart and mushrooms around its rim. Grass
    /// on grass, brown grass on bare dirt, dry grass on sand. Pebbles lie on mountain rock (mossy lower down) and snow,
    /// sparse and anywhere. Picked by a hash of the cell, so the same everywhere it's generated.</summary>
    public static BlockId Plant(BlockId ground, int x, int y, int z, float flora, float kind, ulong seed)
    {
        ulong h = (ulong)(uint)x * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL
                ^ (ulong)(uint)z * 0x165667B19E3779F9UL ^ seed * 0xD6E8FEB86659FD93UL;
        h ^= h >> 32; h *= 0xD6E8FEB86659FD93UL; h ^= h >> 32;
        float r = (h & 0xFFFFFF) / 16777216f;            // whether something grows here
        float pick = ((h >> 40) & 0xFFFF) / 65536f;      // and what
        float stand = Smoothstep(0.3f, 0.8f, flora);     // 0 between stands, 1 in their thick
        float rim = MathF.Max(0f, 1f - MathF.Abs(flora - 0.45f) / 0.05f); // the edge of a stand
        switch (ground)
        {
            case BlockId.Grass:
            {
                if (r < 0.06f + 0.44f * stand)
                {
                    // Tall grass towards a stand's heart; elsewhere its kind, with a little mixing.
                    if (pick < Smoothstep(0.62f, 0.85f, flora) * 0.6f) return BlockId.TallGrass;
                    float k = kind + 0.1f * (pick - 0.5f);
                    return k < 0.45f ? BlockId.ShortGrass : k < 0.52f ? BlockId.GrassTuft : k < 0.58f ? BlockId.GrassBlades : BlockId.TallGrass;
                }
                if (r > 1f - 0.03f * rim)
                    return kind < 0.45f ? BlockId.RedMushroom : kind < 0.55f ? BlockId.BrownMushroom : BlockId.TanMushroom;
                return BlockId.Air;
            }
            case BlockId.Dirt:
                if (r < 0.03f + 0.22f * stand) return BlockId.BrownGrass;
                if (r > 1f - 0.03f * rim) return kind < 0.5f ? BlockId.BrownMushroom : BlockId.TanMushroom;
                return BlockId.Air;
            case BlockId.Sand:
                return r < 0.01f + 0.17f * stand ? BlockId.DryGrass : BlockId.Air;
            case BlockId.Rock:
                if (r >= 0.008f) return BlockId.Air;
                return pick < Math.Clamp((RockFull - y) / (RockFull - RockStart), 0f, 1f) ? BlockId.MossyPebbles : BlockId.Pebbles;
            case BlockId.Snow:
                return r < 0.002f ? BlockId.Pebbles : BlockId.Air;
            default:
                return BlockId.Air;
        }
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
