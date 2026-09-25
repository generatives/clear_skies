using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Generates the "island hearts" world: a <see cref="ContinentTerrain"/> of which only what the hearts of
/// <see cref="HeartGrid"/> hold up exists. A block is solid where it is under the terrain surface and inside some
/// heart's support; each island's top is the terrain (grass, sand, snow by height), its sides are where the support
/// cuts through the terrain (vertical cliffs showing rock layers along a cliff support's cliff sides), and its underside
/// is the support's own, hanging deeper under high ground (a root). Overlapping supports merge into one island.
///
/// Per column this is a union of height spans, one per heart whose support covers it, clipped by the terrain surface.
/// </summary>
public sealed class HeartWorldGenerator : IWorldGenerator
{
    private const int S = ChunkData.Size;
    private const int MaxHearts = 256;
    private const int MaxSpans = 8; // per block column, after merging

    /// <summary>More than the terrain can rise from a chunk column's centre to its corners (its steepest ridges climb
    /// several blocks per block).</summary>
    private const float TerrainRiseMargin = 256f;

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

        // Supports reach far above their hearts so the terrain can decide the tops; bound them by the terrain here
        // instead (its height at the column's centre, plus more than it can rise across half a column), which also
        // bounds how deep the root under high ground goes.
        float terrainTop = _terrain.Height(chunkX * S + S * 0.5f, chunkZ * S + S * 0.5f) + TerrainRiseMargin;
        ulong bits = 0;
        for (int i = 0; i < count; i++)
        {
            float root = Heart.RootFactor * MathF.Max(0f, terrainTop - hearts[i].Y);
            int lo = System.Math.Max((int)MathF.Floor((hearts[i].YMin - root) / S) - minChunkY, 0);
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

    // ── Generation ──────────────────────────────────────────────────────────

    /// <summary>One chunk column's solid spans, per block column: computed once and reused for each of the column's
    /// chunks (streaming generates a column's chunks one after another on one thread).</summary>
    private int _profileX = int.MinValue, _profileZ = int.MinValue;
    private readonly (short Lo, short Hi, bool TerrainTop)[] _spans = new (short, short, bool)[S * S * MaxSpans];
    private readonly byte[] _spanCount = new byte[S * S];
    private readonly float[] _strata = new float[S * S];
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
                var (lo, hi, terrainTop) = _spans[col * MaxSpans + s];
                int y0 = System.Math.Max(lo, originY), y1 = System.Math.Min(hi, originY + S - 1);
                for (int y = y0; y <= y1; y++)
                    data.Set(lx, y - originY, lz, ContinentTerrain.Block(y, hi, terrainTop, _strata[col]));
            }
        }
    }

    private void BuildProfile(int chunkX, int chunkZ)
    {
        _profileX = chunkX; _profileZ = chunkZ;
        int count = ColumnHearts(chunkX, chunkZ, _hearts);
        Array.Clear(_spanCount);
        if (count == 0) return;

        Span<(float Lo, float Hi)> raw = stackalloc (float, float)[MaxHearts];
        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            float wx = chunkX * S + lx, wz = chunkZ * S + lz;
            float surface = _terrain.Height(wx, wz);
            float groundLevel = (surface + _terrain.Height(wx + RootBlur, wz) + _terrain.Height(wx - RootBlur, wz)
                               + _terrain.Height(wx, wz + RootBlur) + _terrain.Height(wx, wz - RootBlur)) * 0.2f;
            int n = 0;
            for (int i = 0; i < count; i++)
                if (_hearts[i].Span(wx, wz, groundLevel, out float bottom, out float top))
                    raw[n++] = (MathF.Max(bottom, HeartGrid.LowestBottom), top); // a deep root stops above the cloud sea
            if (n == 0) continue;

            int col = lx + S * lz;
            _strata[col] = _terrain.Strata(wx, wz);

            // Union of the supports' spans (sorted by bottom), each clipped by the terrain surface.
            raw[..n].Sort((a, b) => a.Lo.CompareTo(b.Lo));
            int spans = 0;
            float curLo = float.NaN, curHi = float.NaN;
            for (int i = 0; i <= n; i++)
            {
                if (i < n && !float.IsNaN(curLo) && raw[i].Lo <= curHi + 1f) { curHi = MathF.Max(curHi, raw[i].Hi); continue; }
                if (!float.IsNaN(curLo) && spans < MaxSpans)
                {
                    int lo = (int)MathF.Ceiling(curLo);
                    bool terrainTop = curHi >= surface;
                    int hi = (int)MathF.Floor(MathF.Min(curHi, surface));
                    if (hi - lo >= 1) _spans[col * MaxSpans + spans++] = ((short)lo, (short)hi, terrainTop);
                }
                if (i < n) (curLo, curHi) = raw[i];
            }
            _spanCount[col] = (byte)spans;
        }
    }
}

/// <summary>Clouds bank up over the hearts world's bigger islands (large and medium surface hearts): 1 over one and out
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
        int n = HeartGrid.Collect(_seed, HeartClass.Large, x - Far, z - Far, x + Far, z + Far, hearts, 0);
        n = HeartGrid.Collect(_seed, HeartClass.Medium, x - Far, z - Far, x + Far, z + Far, hearts, n);
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
