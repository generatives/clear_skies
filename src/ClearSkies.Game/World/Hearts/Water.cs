using System.Collections.Concurrent;

namespace ClearSkies.Game.Generation;

/// <summary>The water planned over a block column (see <see cref="Water.At"/>).</summary>
/// <param name="Level">World Y of the top water block, or int.MinValue for none near: water fills the column up to it
/// where the ground is under it.</param>
/// <param name="Rise">How deep running water stands over the column's own ground at most (blocks; int.MaxValue for
/// standing water, which is level).</param>
public readonly record struct WaterColumn(int Level, int Rise = int.MaxValue)
{
    public static readonly WaterColumn None = new(int.MinValue);

    /// <summary>The top water block over ground whose top block is <paramref name="ground"/>: running water follows
    /// the ground down a slope, standing water is level.</summary>
    public int Over(int ground) => Level == int.MinValue || Rise == int.MaxValue ? Level : Math.Min(Level, ground + Rise);
}

/// <summary>
/// Where water stands, read off the landmass's cells (see <see cref="Hydrology"/>: the flood settles it per cell over
/// the land as <see cref="Erosion"/> wore it, each cell with one water surface, its ground where it's dry). A block
/// column is read from where <see cref="Erosion.Warped"/> moves it (as the land's wear is), from the four cells round
/// that (of its piece): its water surface is the blend of theirs (a dry cell's at its ground, so water rises in a
/// channel between higher banks), but no lower than the blend of just the wet ones' (so ground lower than standing water
/// beside it doesn't drag it down) and no higher than the highest of them. The generator fills it with water where the
/// ground is under that (running water no deeper over each block's ground than the cells' running depth, so it follows
/// a slope down rather than standing level across a cell), and lets it run on where it would still stand over lower dry ground. Nothing here cuts the
/// land, and nothing is told apart: lakes and rivers are just where the cells' water stands over their ground.
/// </summary>
public sealed class Water
{
    private const int Cell = Hydrology.Cell;
    /// <summary>How deep a cell's water must stand over its ground to count as wet: under half a block it rounds to none.</summary>
    private const float MinDepth = 0.5f;

    private static readonly ConcurrentDictionary<ulong, Water> BySeed = new();

    /// <summary>The water of the world with this seed.</summary>
    public static Water For(ulong seed) => BySeed.GetOrAdd(seed, s => new Water(s));

    private readonly ulong _seed;
    private readonly ConcurrentDictionary<long, Heart> _hearts = new();

    private Water(ulong seed) => _seed = seed;

    /// <summary>The water over column (x, z) of the landmass whose water is <paramref name="field"/>.</summary>
    public WaterColumn At(Hydrology.Field field, int x, int z)
    {
        var (wx, wz) = Erosion.Warped(x + 0.5f, z + 0.5f);
        int own = field.ColumnAt(wx, wz);
        if (own < 0) return WaterColumn.None;
        float fx = (wx - field.X0) / Cell - 0.5f, fz = (wz - field.Z0) / Cell - 0.5f;
        int i0 = (int)MathF.Floor(fx), k0 = (int)MathF.Floor(fz);
        float tx = fx - i0, tz = fz - k0;
        double all = 0, allWeights = 0, wet = 0, wetWeights = 0, highest = double.NegativeInfinity, deep = 0;
        bool standing = false;
        for (int dk = 0; dk <= 1; dk++)
        for (int di = 0; di <= 1; di++)
        {
            int i = i0 + di, k = k0 + dk;
            if (i < 0 || k < 0 || i >= field.Nx || k >= field.Nz) continue;
            int c = i + field.Nx * k;
            float s = field.Surface[c];
            if (field.Piece[c] != field.Piece[own] || float.IsNaN(s)) continue;
            double w = (di == 0 ? 1 - tx : tx) * (dk == 0 ? 1 - tz : tz);
            all += w * s; allWeights += w;
            if (!(s >= field.Ground[c] + MinDepth)) continue; // (a film too thin for a block of water is dry)
            wet += w * s; wetWeights += w;
            highest = Math.Max(highest, s);
            deep += w * (s - field.Ground[c]);
            standing |= !float.IsNaN(field.Lake[c]);
        }
        if (wetWeights == 0) return WaterColumn.None;
        int level = (int)Math.Round(Math.Min(Math.Max(all / allWeights, wet / wetWeights), highest)) - 1;
        return new(level, standing ? int.MaxValue : Math.Max(1, (int)Math.Round(deep / wetWeights)));
    }

    /// <summary>The top row's heart whose piece holds (x, z) at the surface (nearest, counting depth VerticalScale
    /// times over), and its cell (hi, hk); null if none.</summary>
    internal Heart? Nearest(float x, float z, out int hi, out int hk)
    {
        float size = HeartGrid.Layers[0].CellSize;
        int i0 = (int)MathF.Floor(x / size), k0 = (int)MathF.Floor(z / size);
        Heart? best = null;
        float bestD = float.MaxValue;
        hi = hk = 0;
        for (int k = k0 - 1; k <= k0 + 1; k++)
        for (int i = i0 - 1; i <= i0 + 1; i++)
        {
            var h = TopHeart(i, k);
            if (!h.Exists) continue;
            float hy = h.Depth * HeartGrid.VerticalScale;
            float d = (h.X - x) * (h.X - x) + hy * hy + (h.Z - z) * (h.Z - z);
            if (d < bestD) { bestD = d; best = h; hi = i; hk = k; }
        }
        return best;
    }

    // Asked per block column: the lambdas are static, given what they need, so a lookup allocates nothing (one capturing
    // hi and hk allocated a closure and a delegate every call, most of what generating terrain allocated).
    private Heart TopHeart(int i, int k)
        => _hearts.GetOrAdd(Key(i, k), static (_, a) => HeartGrid.At(a.Seed, 0, a.I, -1, a.K), (Seed: _seed, I: i, K: k));

    private static long Key(int i, int k) => ((long)i << 32) ^ (uint)k;
}
