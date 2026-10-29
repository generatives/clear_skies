using System.Collections.Concurrent;
using System.Diagnostics;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Where water settles on the land, worked out a landmass at a time (see <see cref="HeartGrid.Landmass"/>) the first
/// time it's asked for, and kept, like <see cref="Erosion"/>: its pieces as the generator makes them, worn by their
/// rivers, sampled into cells <see cref="Cell"/> blocks on a side, and settled by <see cref="Flood"/> (rain by the
/// climate's wetness, soaking in, springs, lakes). What's kept is per column: the lake over it, how much water runs
/// over it and where that goes on to. <see cref="Water"/> draws the lakes and rivers from that.
/// </summary>
public sealed class Hydrology
{
    /// <summary>Blocks on a side of a cell.</summary>
    public const int Cell = Erosion.Cell;

    private static readonly ConcurrentDictionary<ulong, Hydrology> BySeed = new();

    /// <summary>The water of the world with this seed.</summary>
    public static Hydrology For(ulong seed) => BySeed.GetOrAdd(seed, s => new Hydrology(s));

    private readonly ulong _seed;
    private readonly ConcurrentDictionary<(int X, int Z), Lazy<Field>> _fields = new();

    private Hydrology(ulong seed) => _seed = seed;

    /// <summary>The water of the landmass (x, z) is on, worked out the first time it's asked for (all who ask wait).</summary>
    public Field Of(float x, float z) => Of(HeartGrid.Landmass(_seed, x, z));

    public Field Of((int X, int Z) landmass) =>
        _fields.GetOrAdd(landmass, l => new Lazy<Field>(() => Build(l), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>One landmass's water, per column (from block (X0, Z0), Nx by Nz of them): the level of the lake over it
    /// (world Y of its surface, NaN for none), how much runs over it (columns' rain), and the column that goes on to (-1:
    /// none, or off the land).</summary>
    public sealed class Field
    {
        public readonly int X0, Z0, Nx, Nz;
        public readonly float[] Lake, Flow;
        public readonly int[] Down;

        /// <summary>How long it took to work out, seconds; and how many columns hold a lake.</summary>
        public double Seconds { get; internal set; }
        public int LakeColumns { get; internal set; }

        internal Field(int x0, int z0, int nx, int nz)
        {
            X0 = x0; Z0 = z0; Nx = nx; Nz = nz;
            int n = nx * nz;
            Lake = new float[n]; Flow = new float[n]; Down = new int[n];
            Array.Fill(Lake, float.NaN); Array.Fill(Down, -1);
        }

        /// <summary>The column block (x, z) is in, or -1 off the field.</summary>
        public int ColumnAt(float x, float z)
        {
            int i = (int)MathF.Floor((x - X0) / Cell), k = (int)MathF.Floor((z - Z0) / Cell);
            return i < 0 || k < 0 || i >= Nx || k >= Nz ? -1 : i + Nx * k;
        }
    }

    /// <summary>How much rain falls at (x, z), as a share of the most: none where the climate is as dry as 0.3 or
    /// drier, all of it from 0.85 (see ContinentTerrain.Wet). Erosion wears by it too.</summary>
    public static double Rain(ContinentTerrain terrain, float x, float z)
    {
        double w = Math.Clamp((terrain.Wet(x, z) - 0.3) / 0.55, 0, 1);
        return w * w * (3 - 2 * w);
    }

    private Field Build((int X, int Z) landmass)
    {
        var clock = Stopwatch.StartNew();
        var worn = Erosion.For(_seed).Of(landmass); // (the same bounds)
        var land = new CoarseLand(_seed, landmass, worn.X0, worn.Z0, worn.X0 + worn.Nx * Cell, worn.Z0 + worn.Nz * Cell, size: Cell);
        var terrain = ContinentTerrain.For(_seed);
        var flood = new Flood(land, unchecked((int)_seed))
        {
            Rain = (x, z) => Rain(terrain, x, z),
        };
        flood.Run();

        var field = new Field(land.X0, land.Z0, land.Nx, land.Nz);
        for (int c = 0; c < land.Columns; c++)
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
                if (lake >= 0 && flood.LakeLevel[lake] > ground && !(field.Lake[c] >= flood.LakeLevel[lake]))
                    field.Lake[c] = (float)flood.LakeLevel[lake];
                if (most < 0 || flood.Flow[i] > flood.Flow[most]) most = i;
            }
            if (most < 0) continue;
            field.Flow[c] = (float)flood.Flow[most];
            int to = flood.Receiver[most];
            while (to >= 0 && flood.Column[to] == c) to = flood.Receiver[to];
            field.Down[c] = to >= 0 ? flood.Column[to] : -1;
            if (!float.IsNaN(field.Lake[c])) field.LakeColumns++;
        }
        field.Seconds = clock.Elapsed.TotalSeconds;
        return field;
    }
}
