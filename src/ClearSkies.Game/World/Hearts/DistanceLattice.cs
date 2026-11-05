using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// One chunk column's DistanceToSurface on a lattice: how far inside the ground (positive) or outside it (negative) each
/// corner of its cells <see cref="Cell"/> blocks on a side is, as HeartWorldGenerator works it out (in blocks, clamped
/// to <see cref="Reach"/> either way). A chunk column has <see cref="Corners"/> by <see cref="Corners"/> corner columns
/// of its own, shared with its neighbours along its edges, and a ring of one more round them (so <see cref="Side"/> by
/// <see cref="Side"/> in all), each from level <see cref="First"/> (world Y First * Cell) up, <see cref="Levels"/> of
/// them. Blocks between corners take it interpolated smoothly across (see <see cref="Interpolate"/>), so the ground has
/// no 8-block facets.
/// </summary>
internal sealed class DistanceLattice
{
    public const int Cell = 8;
    public const int Corners = ChunkData.Size / Cell + 1;

    /// <summary>Corner columns a side, with the ring round the chunk column's own: corner (i, k), i and k from -1 to
    /// Corners, is column <see cref="Index"/>(i, k).</summary>
    public const int Side = Corners + 2;

    public static int Index(int i, int k) => i + 1 + Side * (k + 1);

    /// <summary>How far either way a distance is kept: well over a cell, so interpolation between corners near the surface
    /// is never cut short.</summary>
    public const float Reach = 64f;

    public int ChunkX { get; }
    public int ChunkZ { get; }
    public int First { get; }
    public int Levels { get; }
    private readonly float[] _distance; // corner column c's levels from c * Levels
    private readonly float[] _rough = new float[Side * Side]; // corner column c's (see Roughness)

    /// <summary>Chunk column (chunkX, chunkZ)'s, from level first, levels high: its distances to fill in (see
    /// <see cref="Column"/>).</summary>
    public DistanceLattice(int chunkX, int chunkZ, int first, int levels)
    {
        ChunkX = chunkX; ChunkZ = chunkZ; First = first; Levels = levels;
        _distance = new float[Side * Side * levels];
    }

    /// <summary>Corner column c's distances (see <see cref="Index"/>), bottom up.</summary>
    public Span<float> Column(int c) => _distance.AsSpan(c * Levels, Levels);

    /// <summary>How rough corner column c's top is, 0 (smooth) to 1, as the landscape that wore it says (see
    /// HeartWorldGenerator.Wear); 0 unless set.</summary>
    public void SetRoughness(int c, float rough) => _rough[c] = rough;

    /// <summary>How rough block column (lx, lz)'s top is: its cell's corners' blended.</summary>
    public float Roughness(int lx, int lz)
    {
        int i = Math.Min(lx / Cell, Corners - 2), k = Math.Min(lz / Cell, Corners - 2);
        float tx = (lx - i * Cell) / (float)Cell, tz = (lz - k * Cell) / (float)Cell;
        float near = _rough[Index(i, k)] + (_rough[Index(i + 1, k)] - _rough[Index(i, k)]) * tx;
        float far = _rough[Index(i, k + 1)] + (_rough[Index(i + 1, k + 1)] - _rough[Index(i, k + 1)]) * tx;
        return near + (far - near) * tz;
    }

    /// <summary>Block column (lx, lz)'s distances, per level: the corner columns round its cell blended, four by four,
    /// on Catmull-Rom curves across and along. Unlike blending a cell's own corners, that's smooth from cell to cell (and
    /// chunk column to chunk column, the ring round it being its neighbours' corners), so a slope doesn't show as flat
    /// facets 8 blocks across. Blended along z first, a row of corner columns at a time, kept in
    /// <paramref name="rows"/> for the next block column in the same row.</summary>
    public void Interpolate(int lx, int lz, Span<float> into, Rows rows)
    {
        int i = Math.Min(lx / Cell, Corners - 2);
        if (rows.Lattice != this || rows.Lz != lz)
        {
            rows.Lattice = this; rows.Lz = lz;
            if (rows.Blend.Length < Side * Levels) rows.Blend = new float[Side * Levels];
            int k = Math.Min(lz / Cell, Corners - 2);
            Span<float> wz = stackalloc float[4];
            Weights((lz - k * Cell) / (float)Cell, wz);
            for (int c = 0; c < Side; c++)
            {
                var blend = rows.Blend.AsSpan(c * Levels, Levels);
                var a = Column(Index(c - 1, k - 1)); var b = Column(Index(c - 1, k));
                var d = Column(Index(c - 1, k + 1)); var e = Column(Index(c - 1, k + 2));
                for (int j = 0; j < Levels; j++) blend[j] = wz[0] * a[j] + wz[1] * b[j] + wz[2] * d[j] + wz[3] * e[j];
            }
        }
        Span<float> wx = stackalloc float[4];
        Weights((lx - i * Cell) / (float)Cell, wx);
        // (Ring column c is corner i = c - 1, so corners i - 1 .. i + 2 are rows i .. i + 3.)
        var r0 = rows.Blend.AsSpan(i * Levels, Levels); var r1 = rows.Blend.AsSpan((i + 1) * Levels, Levels);
        var r2 = rows.Blend.AsSpan((i + 2) * Levels, Levels); var r3 = rows.Blend.AsSpan((i + 3) * Levels, Levels);
        for (int j = 0; j < Levels; j++) into[j] = wx[0] * r0[j] + wx[1] * r1[j] + wx[2] * r2[j] + wx[3] * r3[j];
    }

    /// <summary>What <see cref="Interpolate"/> keeps between block columns in the same row: a worker's own.</summary>
    public sealed class Rows
    {
        public DistanceLattice? Lattice;
        public int Lz;
        public float[] Blend = Array.Empty<float>();
    }

    /// <summary>Catmull-Rom weights of the four corners round a point t (0-1) of the way from the second to the third.</summary>
    private static void Weights(float t, Span<float> w)
    {
        float t2 = t * t, t3 = t2 * t;
        w[0] = 0.5f * (-t3 + 2f * t2 - t);
        w[1] = 0.5f * (3f * t3 - 5f * t2 + 2f);
        w[2] = 0.5f * (-3f * t3 + 4f * t2 + t);
        w[3] = 0.5f * (t3 - t2);
    }
}
