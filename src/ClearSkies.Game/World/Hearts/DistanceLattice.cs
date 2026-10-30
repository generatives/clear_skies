using ClearSkies.Engine.Voxels;

namespace ClearSkies.Game.Generation;

/// <summary>
/// One chunk column's DistanceToSurface on a lattice: how far inside the ground (positive) or outside it (negative) each
/// corner of its cells <see cref="Cell"/> blocks on a side is, as HeartWorldGenerator works it out (in its scaled space:
/// across in blocks, up and down VerticalScale times over; clamped to <see cref="Reach"/> either way). A chunk column
/// has <see cref="Corners"/> by <see cref="Corners"/> corner columns, shared with its neighbours along its edges, each
/// from level <see cref="First"/> (world Y First * Cell) up, <see cref="Levels"/> of them. Blocks between corners take
/// it interpolated (see <see cref="Interpolate"/>).
/// </summary>
internal sealed class DistanceLattice
{
    public const int Cell = 8;
    public const int Corners = ChunkData.Size / Cell + 1;

    /// <summary>How far either way a distance is kept: well over a cell, so interpolation between corners near the surface
    /// is never cut short.</summary>
    public const float Reach = 64f;

    public int ChunkX { get; private set; }
    public int ChunkZ { get; private set; }
    public int First { get; private set; }
    public int Levels { get; private set; }

    private float[] _distance = Array.Empty<float>(); // corner column c's levels from c * Levels

    /// <summary>Makes it ready for chunk column (chunkX, chunkZ), from level first, levels high.</summary>
    public void Reset(int chunkX, int chunkZ, int first, int levels)
    {
        ChunkX = chunkX; ChunkZ = chunkZ; First = first; Levels = levels;
        if (_distance.Length < Corners * Corners * levels) _distance = new float[Corners * Corners * levels];
    }

    /// <summary>Corner column c's distances (c = i + Corners * k, i along X and k along Z, from the chunk column's
    /// corner), bottom up.</summary>
    public Span<float> Column(int c) => _distance.AsSpan(c * Levels, Levels);

    /// <summary>Block column (lx, lz)'s distances, per level: its four corner columns' blended.</summary>
    public void Interpolate(int lx, int lz, Span<float> into)
    {
        int i = lx / Cell, k = lz / Cell;
        float tx = (lx - i * Cell) / (float)Cell, tz = (lz - k * Cell) / (float)Cell;
        var a = Column(i + Corners * k);
        var b = Column(i + 1 + Corners * k);
        var c = Column(i + Corners * (k + 1));
        var d = Column(i + 1 + Corners * (k + 1));
        float wa = (1 - tx) * (1 - tz), wb = tx * (1 - tz), wc = (1 - tx) * tz, wd = tx * tz;
        for (int j = 0; j < Levels; j++) into[j] = wa * a[j] + wb * b[j] + wc * c[j] + wd * d[j];
    }
}
