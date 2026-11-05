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

    public int ChunkX { get; }
    public int ChunkZ { get; }
    public int First { get; }
    public int Levels { get; }
    /// <summary>The landscape that wore it, or null (not worn).</summary>
    public CoarseLandmass? Worn { get; set; }

    private readonly float[] _distance; // corner column c's levels from c * Levels
    private readonly bool[] _byAd = new bool[(Corners - 1) * (Corners - 1)]; // each cell's split: a-d, or else b-c

    /// <summary>Chunk column (chunkX, chunkZ)'s, from level first, levels high: its distances to fill in (see
    /// <see cref="Column"/>), then <see cref="Split"/>.</summary>
    public DistanceLattice(int chunkX, int chunkZ, int first, int levels)
    {
        ChunkX = chunkX; ChunkZ = chunkZ; First = first; Levels = levels;
        _distance = new float[Corners * Corners * levels];
    }

    /// <summary>Corner column c's distances (c = i + Corners * k, i along X and k along Z, from the chunk column's
    /// corner), bottom up.</summary>
    public Span<float> Column(int c) => _distance.AsSpan(c * Levels, Levels);

    /// <summary>Picks each cell's diagonal (see <see cref="Interpolate"/>), once its distances are filled in. It isn't
    /// changed after that, so it can be shared between threads.</summary>
    public void Split()
    {
        for (int k = 0; k < Corners - 1; k++)
        for (int i = 0; i < Corners - 1; i++)
        {
            var a = Column(i + Corners * k);
            var b = Column(i + 1 + Corners * k);
            var c = Column(i + Corners * (k + 1));
            var d = Column(i + 1 + Corners * (k + 1));
            float ad = 0, bc = 0;
            for (int j = 0; j < Levels; j++) { ad += a[j] + d[j]; bc += b[j] + c[j]; }
            _byAd[i + (Corners - 1) * k] = ad <= bc;
        }
    }

    /// <summary>Block column (lx, lz)'s distances, per level: its cell's corner columns' blended. Each cell is split
    /// into two triangles along the diagonal between its two lower corners (the less ground over them), and a block
    /// column blends its triangle's three: a valley running diagonally keeps its floor across the cell, where blending
    /// all four would raise a saddle from the banks in its middle.</summary>
    public void Interpolate(int lx, int lz, Span<float> into)
    {
        int i = lx / Cell, k = lz / Cell, cell = i + (Corners - 1) * k;
        float tx = (lx - i * Cell) / (float)Cell, tz = (lz - k * Cell) / (float)Cell;
        var a = Column(i + Corners * k);
        var b = Column(i + 1 + Corners * k);
        var c = Column(i + Corners * (k + 1));
        var d = Column(i + 1 + Corners * (k + 1));
        float wa, wb, wc, wd;
        if (_byAd[cell])
        {
            if (tx >= tz) { wa = 1 - tx; wb = tx - tz; wc = 0; wd = tz; }
            else { wa = 1 - tz; wb = 0; wc = tz - tx; wd = tx; }
        }
        else if (tx + tz <= 1) { wa = 1 - tx - tz; wb = tx; wc = tz; wd = 0; }
        else { wa = 0; wb = 1 - tz; wc = 1 - tx; wd = tx + tz - 1; }
        for (int j = 0; j < Levels; j++) into[j] = wa * a[j] + wb * b[j] + wc * c[j] + wd * d[j];
    }
}
