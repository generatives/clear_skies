using System.Collections.Concurrent;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Lakes in the land's surface: still water filling a bowl carved into the terrain, no flow. The world is cut into
/// <see cref="Cell"/>-block cells; one in a few holds a lake, placed well inside it so each column has at most its own
/// cell's lake, and worked out from the cell alone so every chunk agrees.
///
/// A lake's outline is a wobbly circle. Its level is a block under the lowest of the terrain round its rim, so the water
/// never stands above its banks; inside, the ground is carved down into a bowl under that level, deepest in the middle.
/// A lake only forms where the land round it is there (the whole rim on live pieces of the top row, see
/// <see cref="HeartGrid.At"/>) and fairly level, so it never spills off an island or down a mountainside. Round it the
/// generator makes the land whole a little way down, so cracks between pieces don't drain it.
/// </summary>
public sealed class Lakes
{
    public const int Cell = 384;

    private const int Odds = 3;                           // one cell in this many holds a lake
    private const float MinRadius = 22f, MaxRadius = 56f;
    private const float Wobble = 0.25f;                   // how far the outline strays from a circle, as a share of radius
    private const float MaxRimRise = 40f;                 // a rim higher than its lowest point by more is too steep
    private const int RimSamples = 24;
    private const float Shore = 1.25f;                    // how far out (in radii) a sandy shore can reach

    /// <summary>A lake: its middle, radius, level (world Y of its top water block) and deepest point's depth, and the
    /// phases of its outline's wobble.</summary>
    public readonly record struct Lake(float X, float Z, float Radius, int Level, float Depth, float P1, float P2, float P3);

    private static readonly ConcurrentDictionary<ulong, Lakes> BySeed = new();

    /// <summary>The lakes of the world with this seed.</summary>
    public static Lakes For(ulong seed) => BySeed.GetOrAdd(seed, s => new Lakes(s));

    private readonly ulong _seed;
    private readonly ContinentTerrain _terrain;
    private readonly ConcurrentDictionary<long, Lake?> _cells = new();

    private Lakes(ulong seed)
    {
        _seed = seed;
        _terrain = ContinentTerrain.For(seed);
    }

    /// <summary>Column (x, z) with terrain <paramref name="height"/> as carved by a lake: the ground's new height, the
    /// lake's <paramref name="level"/> out to its shore a little past the outline (int.MinValue elsewhere) and whether
    /// the column is <paramref name="inside"/> the outline, where water fills it up to the level.
    /// <paramref name="near"/> is 0-1, how far into the lake's surroundings the column is (1 inside its outline, 0 from
    /// 1.3 times its radius out).</summary>
    public float Carve(float x, float z, float height, out int level, out bool inside, out float near)
    {
        level = int.MinValue;
        inside = false;
        near = 0f;
        int cx = (int)MathF.Floor(x / Cell), cz = (int)MathF.Floor(z / Cell);
        if (In(cx, cz) is not { } lake) return height;
        float dx = x - lake.X, dz = z - lake.Z;
        float r = Outline(lake, MathF.Atan2(dz, dx));
        float d = MathF.Sqrt(dx * dx + dz * dz) / r;       // 1 on the outline
        near = Math.Clamp((1.3f - d) / 0.3f, 0f, 1f);
        if (d < Shore) level = lake.Level;
        if (d >= 1f) return height;
        inside = true;
        // A bowl: deepest in the middle, rising to the level at the outline (and above it the banks as they were).
        float bed = lake.Level - lake.Depth * (1f - d * d) + 1f;
        return MathF.Min(height, bed);
    }

    /// <summary>The lake in cell (cx, cz), if it has one.</summary>
    public Lake? In(int cx, int cz) => _cells.GetOrAdd(((long)cx << 32) ^ (uint)cz, _ => Make(cx, cz));

    private Lake? Make(int cx, int cz)
    {
        var rng = new SplitMix64Rng(_seed ^ 0x1A4E5UL ^ (ulong)(uint)cx * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)cz * 0xC2B2AE3D27D4EB4FUL);
        if (rng.NextFloat01() * Odds >= 1f) return null;
        float radius = MinRadius + (MaxRadius - MinRadius) * rng.NextFloat01();
        float margin = radius * (1f + Wobble) * 1.3f + 4f;
        float x = cx * Cell + margin + (Cell - 2f * margin) * rng.NextFloat01();
        float z = cz * Cell + margin + (Cell - 2f * margin) * rng.NextFloat01();
        var lake = new Lake(x, z, radius, 0, 0f, 6.28f * rng.NextFloat01(), 6.28f * rng.NextFloat01(), 6.28f * rng.NextFloat01());

        // Round the rim: the land must be sure, and the terrain fairly level.
        float low = float.MaxValue, high = float.MinValue;
        for (int i = 0; i < RimSamples; i++)
        {
            float a = i * MathF.Tau / RimSamples;
            float r = Outline(lake, a) * 1.15f;
            float sx = x + r * MathF.Cos(a), sz = z + r * MathF.Sin(a);
            float h = _terrain.Height(sx, sz);
            if (!OnLand(sx, sz)) return null;
            low = MathF.Min(low, h); high = MathF.Max(high, h);
        }
        if (high - low > MaxRimRise || !OnLand(x, z)) return null;
        float depth = 4f + radius * 0.12f + 4f * rng.NextFloat01();
        return lake with { Level = (int)MathF.Floor(low) - 1, Depth = depth };
    }

    /// <summary>Whether the land's top piece nearest (x, z) is there: the live heart of the top row whose cell holds
    /// the nearest heart point (see <see cref="HeartGrid.At"/>).</summary>
    private bool OnLand(float x, float z)
    {
        float size = HeartGrid.Layers[0].CellSize;
        int cx = (int)MathF.Floor(x / size), cz = (int)MathF.Floor(z / size);
        float best = float.MaxValue;
        bool alive = false;
        for (int j = cz - 1; j <= cz + 1; j++)
        for (int i = cx - 1; i <= cx + 1; i++)
        {
            var heart = HeartGrid.At(_seed, 0, i, -1, j);
            float d = (heart.X - x) * (heart.X - x) + (heart.Z - z) * (heart.Z - z);
            if (d < best) { best = d; alive = heart.Alive; }
        }
        return alive;
    }

    private static float Outline(in Lake lake, float angle) =>
        lake.Radius * (1f + Wobble * (0.5f * MathF.Sin(2f * angle + lake.P1) + 0.3f * MathF.Sin(3f * angle + lake.P2)
                                      + 0.2f * MathF.Sin(5f * angle + lake.P3)));
}
