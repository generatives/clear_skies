namespace ClearSkies.Game.Generation;

/// <summary>
/// Where water settles on a landmass, per column of <see cref="Cell"/> blocks: what <see cref="Flood"/> works out
/// over its land (rain by the climate's wetness, soaking in, springs, lakes), boiled down to the lake over each column,
/// how much water runs over it and where that goes on to. <see cref="Erosion"/> wears the land by it, round after round,
/// and keeps the last one, over the land as it ends up, for <see cref="Water"/> to lay the lakes and rivers out by.
/// </summary>
public static class Hydrology
{
    /// <summary>Blocks on a side of a cell.</summary>
    public const int Cell = Erosion.Cell;

    // Running water: where RiverFrom columns' rain runs, FlowDepth times the square root of how many times that deep,
    // at most FlowDeepest.
    private const double RiverFrom = Erosion.RiverFrom, FlowDepth = 1.5, FlowDeepest = 6;

    /// <summary>The water of the landmass (x, z) is on (see Erosion: worked out with it).</summary>
    public static Field Of(ulong seed, float x, float z) => Erosion.For(seed).Of(x, z).Water;

    /// <summary>How much rain falls at (x, z), as a share of the most: none where the climate is as dry as 0.3 or
    /// drier, all of it from 0.85 (see ContinentTerrain.Wet).</summary>
    public static double Rain(ContinentTerrain terrain, float x, float z)
    {
        double w = Math.Clamp((terrain.Wet(x, z) - 0.3) / 0.55, 0, 1);
        return w * w * (3 - 2 * w);
    }

    /// <summary>One landmass's water, per column (from block (X0, Z0), Nx by Nz of them): the level of the lake over it
    /// (world Y of its surface, NaN for none), how much runs over it (columns' rain), the column that goes on to (-1:
    /// none, or off the land), and the level its water must reach to get out (its spill level); and the columns with
    /// land in order, downstream first.</summary>
    public sealed class Field
    {
        public readonly int X0, Z0, Nx, Nz;
        public readonly float[] Lake, Flow, Spill;
        public readonly int[] Down;
        public readonly int[] Order;
        public readonly long[] Piece; // which piece each column's top is in

        /// <summary>Per column: its ground (world Y over its top), and its water's surface: where it stands (a lake's
        /// level), or where enough runs (RiverFrom columns' rain), FlowDepth times the square root of how many times
        /// that over the ground, at most FlowDeepest; the ground where it's dry. NaN off the land.</summary>
        public readonly float[] Ground, Surface;

        /// <summary>How many columns hold a lake.</summary>
        public int LakeColumns { get; }

        /// <summary>The water <paramref name="flood"/> (run) settled over <paramref name="land"/>.</summary>
        internal Field(CoarseLand land, Flood flood)
        {
            X0 = land.X0; Z0 = land.Z0; Nx = land.Nx; Nz = land.Nz;
            Piece = land.Piece;
            int n = Nx * Nz;
            Lake = new float[n]; Flow = new float[n]; Spill = new float[n]; Down = new int[n];
            Ground = new float[n]; Surface = new float[n];
            Array.Fill(Lake, float.NaN); Array.Fill(Down, -1);
            Array.Fill(Ground, float.NaN); Array.Fill(Surface, float.NaN);
            var rank = new int[land.Cells];
            Array.Fill(rank, int.MaxValue);
            for (int k = 0; k < flood.Order.Length; k++) rank[flood.Order[k]] = k;
            var first = new int[n];
            Array.Fill(first, int.MaxValue);
            for (int c = 0; c < n; c++)
            {
                if (!land.HasLand(c)) continue;
                // Over the ground: the lake's level, and the cell most runs through, which says where it goes on to.
                float ground = Cell * (land.Top[c] + land.Solid[land.Index(c, land.Top[c])]);
                int most = -1;
                for (int y = land.Top[c]; y <= land.Hi[c]; y++)
                {
                    int i = land.Index(c, y);
                    if (i < 0 || !flood.IsOpen(i)) continue;
                    int lake = flood.Lake[i];
                    if (lake >= 0 && flood.LakeLevel[lake] > ground && !(Lake[c] >= flood.LakeLevel[lake]))
                        Lake[c] = (float)flood.LakeLevel[lake];
                    if (most < 0 || flood.Flow[i] > flood.Flow[most]) most = i;
                }
                if (most < 0) continue;
                Flow[c] = (float)flood.Flow[most];
                first[c] = rank[most];
                Spill[c] = (float)flood.OpenLevel[most];
                int to = flood.Receiver[most];
                while (to >= 0 && flood.Column[to] == c) to = flood.Receiver[to];
                Down[c] = to >= 0 ? flood.Column[to] : -1;
                if (!float.IsNaN(Lake[c])) LakeColumns++;
                Ground[c] = ground;
                float runs = Flow[c] >= RiverFrom ? ground + (float)Math.Min(FlowDeepest, FlowDepth * Math.Sqrt(Flow[c] / RiverFrom)) : ground;
                Surface[c] = MathF.Max(runs, float.IsNaN(Lake[c]) ? ground : Lake[c]);
            }
            Order = Enumerable.Range(0, n).Where(c => first[c] != int.MaxValue).OrderBy(c => first[c]).ToArray();
        }

        /// <summary>The column block (x, z) is in, or -1 off the field.</summary>
        public int ColumnAt(float x, float z)
        {
            int i = (int)MathF.Floor((x - X0) / Cell), k = (int)MathF.Floor((z - Z0) / Cell);
            return i < 0 || k < 0 || i >= Nx || k >= Nz ? -1 : i + Nx * k;
        }
    }
}
