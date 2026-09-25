using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Generates the "island hearts" world: a <see cref="ContinentTerrain"/> of which only what the hearts of
/// <see cref="HeartGrid"/> hold up exists. A block is solid where it is under the terrain surface and inside some
/// heart's support. A surface heart's island has the terrain for its top (grass, sand, snow by height) and hangs deeper
/// under high ground (a root); a buried heart's island is a slab with its own rolling top, unless the terrain is lower
/// there. Sides are where supports cut through the ground (vertical cliffs showing rock layers along a cliff support's
/// cliff sides). Overlapping supports merge into one island.
///
/// Per column this is a union of height spans, one per heart whose support covers it, clipped by the terrain surface.
/// </summary>
public sealed class HeartWorldGenerator : IWorldGenerator
{
    private const int S = ChunkData.Size;
    private const int MaxHearts = 512;
    public const int MaxSpans = 12; // per block column, after merging

    /// <summary>More than the terrain can rise above the highest of a chunk column's corners and centre.</summary>
    private const float TerrainRiseMargin = 96f;

    /// <summary>How far apart the terrain samples averaged for a root are, in blocks (see Heart.Span).</summary>
    private const float RootBlur = 96f;

    private readonly ulong _seed;
    private readonly ContinentTerrain _terrain;

    public HeartWorldGenerator(ulong seed)
    {
        _seed = seed;
        _terrain = ContinentTerrain.For(seed);
    }

    public ulong ColumnLayers(int chunkX, int chunkZ, int minChunkY)
    {
        Span<Heart> hearts = stackalloc Heart[MaxHearts];
        int count = ColumnHearts(chunkX, chunkZ, hearts);
        if (count == 0) return 0;

        // Nothing is above the terrain: bound it over the column (its highest sample, plus more than it can rise
        // between samples), which also bounds how deep a surface heart's root goes.
        float terrainTop = float.MinValue;
        for (int j = 0; j <= 2; j++)
        for (int i = 0; i <= 2; i++)
            terrainTop = MathF.Max(terrainTop, _terrain.Height(chunkX * S + i * S * 0.5f, chunkZ * S + j * S * 0.5f));
        terrainTop += TerrainRiseMargin;

        ulong bits = 0;
        for (int i = 0; i < count; i++)
        {
            float bottom = MathF.Max(hearts[i].YMin - hearts[i].Root(terrainTop), HeartGrid.LowestBottom);
            int lo = System.Math.Max((int)MathF.Floor(bottom / S) - minChunkY, 0);
            float top = MathF.Min(MathF.Min(hearts[i].YMax, terrainTop), IslandGrid.WorldTop - 1);
            int hi = System.Math.Min((int)MathF.Floor(top / S) - minChunkY, 63);
            if (lo > hi) continue;
            bits |= (ulong.MaxValue >> (63 - hi)) & (ulong.MaxValue << lo);
        }
        return bits;
    }

    /// <summary>The hearts whose supports can reach chunk column (chunkX, chunkZ).</summary>
    private int ColumnHearts(int chunkX, int chunkZ, Span<Heart> hearts)
    {
        float x0 = chunkX * S, z0 = chunkZ * S;
        int count = HeartGrid.CollectAll(_seed, x0, z0, x0 + S, z0 + S, hearts);
        int kept = 0;
        for (int i = 0; i < count; i++)
        {
            ref readonly var h = ref hearts[i];
            float dx = MathF.Max(0f, MathF.Abs(h.X - (x0 + S * 0.5f)) - S * 0.5f);
            float dz = MathF.Max(0f, MathF.Abs(h.Z - (z0 + S * 0.5f)) - S * 0.5f);
            if (dx * dx + dz * dz < h.Reach * h.Reach) hearts[kept++] = h;
        }
        return kept;
    }

    /// <summary>The solid spans of block column (wx, wz), bottom up, given the hearts that may reach it: the union of
    /// their supports' spans, each clipped by the terrain surface. Returns how many were written.</summary>
    public static int ColumnSpans(ContinentTerrain terrain, ReadOnlySpan<Heart> hearts, float wx, float wz,
                                  Span<(short Lo, short Hi)> output)
    {
        Span<(float Lo, float Hi)> raw = stackalloc (float, float)[64];
        float surface = terrain.Height(wx, wz);
        float groundLevel = float.NaN;
        int n = 0;
        foreach (ref readonly var h in hearts)
        {
            if (MathF.Abs(h.X - wx) >= h.Reach || MathF.Abs(h.Z - wz) >= h.Reach) continue;
            if (h.Surface && float.IsNaN(groundLevel))
                groundLevel = (surface + terrain.Height(wx + RootBlur, wz) + terrain.Height(wx - RootBlur, wz)
                               + terrain.Height(wx, wz + RootBlur) + terrain.Height(wx, wz - RootBlur)) * 0.2f;
            if (!h.Span(wx, wz, groundLevel, out float bottom, out float top)) continue;
            bottom = MathF.Max(bottom, HeartGrid.LowestBottom); // a deep root stops above the cloud sea
            top = MathF.Min(top, surface);
            if (top > bottom && n < raw.Length) raw[n++] = (bottom, top);
        }
        if (n == 0) return 0;

        raw[..n].Sort((a, b) => a.Lo.CompareTo(b.Lo));
        int spans = 0;
        float curLo = raw[0].Lo, curHi = raw[0].Hi;
        for (int i = 1; i <= n; i++)
        {
            if (i < n && raw[i].Lo <= curHi + 1f) { curHi = MathF.Max(curHi, raw[i].Hi); continue; }
            int lo = (int)MathF.Ceiling(curLo), hi = (int)MathF.Floor(curHi);
            if (hi - lo >= 1 && spans < output.Length) output[spans++] = ((short)lo, (short)hi);
            if (i < n) (curLo, curHi) = raw[i];
        }
        return spans;
    }

    // ── Generation ──────────────────────────────────────────────────────────

    /// <summary>One chunk column's solid spans, per block column: computed once and reused for each of the column's
    /// chunks (streaming generates a column's chunks one after another on one thread).</summary>
    private int _profileX = int.MinValue, _profileZ = int.MinValue;
    private readonly (short Lo, short Hi)[] _spans = new (short, short)[S * S * MaxSpans];
    private readonly byte[] _spanCount = new byte[S * S];
    private readonly float[] _strata = new float[S * S];
    private readonly float[] _patch = new float[S * S];
    private readonly Heart[] _hearts = new Heart[MaxHearts];

    public void Generate(ChunkData data, ChunkPosition pos)
    {
        if (pos.X != _profileX || pos.Z != _profileZ) BuildProfile(pos.X, pos.Z);

        int originY = pos.Y * S;
        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            int col = lx + S * lz;
            for (int s = 0; s < _spanCount[col]; s++)
            {
                var (lo, hi) = _spans[col * MaxSpans + s];
                int y0 = System.Math.Max(lo, originY), y1 = System.Math.Min(hi, originY + S - 1);
                for (int y = y0; y <= y1; y++)
                    data.Set(lx, y - originY, lz, ContinentTerrain.Block(y, hi, _strata[col], _patch[col]));
            }
        }
    }

    private void BuildProfile(int chunkX, int chunkZ)
    {
        _profileX = chunkX; _profileZ = chunkZ;
        int count = ColumnHearts(chunkX, chunkZ, _hearts);
        Array.Clear(_spanCount);
        if (count == 0) return;

        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            float wx = chunkX * S + lx, wz = chunkZ * S + lz;
            int col = lx + S * lz;
            int n = ColumnSpans(_terrain, _hearts.AsSpan(0, count), wx, wz, _spans.AsSpan(col * MaxSpans, MaxSpans));
            _spanCount[col] = (byte)n;
            if (n == 0) continue;
            _strata[col] = _terrain.Strata(wx, wz);
            _patch[col] = _terrain.Patch(wx, wz);
        }
    }
}

/// <summary>Clouds bank up over the hearts world's bigger surface islands (large and medium surface hearts): 1 over one and out
/// to <see cref="Near"/> blocks past its support, easing to 0 by <see cref="Far"/> blocks past it.</summary>
public sealed class HeartCloudDensity : ICloudDensityMap
{
    private const float Near = 150f;
    private const float Far  = 1100f;

    private readonly ulong _seed;

    public HeartCloudDensity(ulong seed) => _seed = seed;

    public float Density(float x, float z)
    {
        Span<Heart> hearts = stackalloc Heart[128];
        int n = HeartGrid.Collect(_seed, HeartKind.SurfaceLarge, x - Far, z - Far, x + Far, z + Far, hearts, 0);
        n = HeartGrid.Collect(_seed, HeartKind.SurfaceMedium, x - Far, z - Far, x + Far, z + Far, hearts, n);
        float best = 0f;
        foreach (var h in hearts[..n])
        {
            float ex = x - h.X, ez = z - h.Z;
            float past = MathF.Sqrt(ex * ex + ez * ez) - h.Radius;
            float t = Math.Clamp((past - Near) / (Far - Near), 0f, 1f);
            best = MathF.Max(best, 1f - t * t * (3f - 2f * t));
        }
        return best;
    }
}
