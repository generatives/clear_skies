using ClearSkies.Game.Generation;

namespace ClearSkies.Hydrology;

/// <summary>
/// One landmass sampled into cells <see cref="Size"/> blocks on a side. Each coarse column (Size by Size blocks) keeps
/// only the cells from the bottom of its lowest ground up to just above the highest ground around it, packed one
/// column after another (column c's cells start at Start[c], cell y at Start[c] + y - Lo[c]). Cells outside a column's
/// range are open sky over it or the void under it.
/// </summary>
public sealed class CoarseLand
{
    public const int Size = 8;

    public readonly int X0, Z0, Nx, Nz;     // the first column's corner (blocks) and the columns across and along
    public readonly int[] Lo, Hi, Top;      // per column: its lowest and highest cell, and its ground's top cell
    public readonly int[] Start;            // per column: where its cells start (Start[Nx * Nz] is the cell count)
    public readonly float[] Solid;          // per cell: how much of it is ground, 0-1

    public int Columns => Nx * Nz;
    public int Cells => Start[Columns];
    public int Count(int c) => Start[c + 1] - Start[c];

    /// <summary>Samples landmass <paramref name="cell"/> between blocks (x0, z0) and (x1, z1): each column at its
    /// middle block column, on all cores.</summary>
    public CoarseLand(ulong seed, (int X, int Z) cell, int x0, int z0, int x1, int z1)
    {
        X0 = x0; Z0 = z0; Nx = (x1 - x0) / Size; Nz = (z1 - z0) / Size;
        var spans = new (short Lo, short Hi)[Columns][];
        // A chunk's worth of columns at a time, so each sampler loads that chunk's hearts once.
        const int PerChunk = 32 / Size;
        int chunksX = (Nx + PerChunk - 1) / PerChunk, chunksZ = (Nz + PerChunk - 1) / PerChunk;
        var generators = new ThreadLocal<(HeartWorldGenerator Gen, List<(short, short)> List)>(
            () => (new HeartWorldGenerator(seed), new List<(short, short)>()));
        Parallel.For(0, chunksX * chunksZ, k =>
        {
            var (gen, list) = generators.Value;
            int cx = k % chunksX, cz = k / chunksX;
            for (int j = cz * PerChunk; j < System.Math.Min(Nz, cz * PerChunk + PerChunk); j++)
            for (int i = cx * PerChunk; i < System.Math.Min(Nx, cx * PerChunk + PerChunk); i++)
            {
                int x = x0 + i * Size + Size / 2, z = z0 + j * Size + Size / 2;
                if (HeartGrid.Landmass(seed, x, z) != cell) { spans[i + Nx * j] = Array.Empty<(short, short)>(); continue; }
                gen.ColumnSpans(x, z, list);
                spans[i + Nx * j] = list.ToArray();
            }
        });

        Lo = new int[Columns]; Hi = new int[Columns]; Top = new int[Columns];
        for (int c = 0; c < Columns; c++)
        {
            var s = spans[c];
            Top[c] = s.Length == 0 ? int.MinValue : FloorDiv(s[^1].Hi, Size);
            Lo[c] = s.Length == 0 ? 0 : FloorDiv(s[0].Lo, Size);
        }
        // Up to just above the highest ground beside it, so water can always step across onto lower ground.
        Start = new int[Columns + 1];
        for (int j = 0; j < Nz; j++)
        for (int i = 0; i < Nx; i++)
        {
            int c = i + Nx * j;
            if (Top[c] == int.MinValue) { Hi[c] = Lo[c] - 1; continue; }
            int hi = Top[c];
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                int ni = i + di, nj = j + dj;
                if (ni >= 0 && nj >= 0 && ni < Nx && nj < Nz) hi = System.Math.Max(hi, Top[ni + Nx * nj]);
            }
            Hi[c] = hi + 1;
        }
        for (int c = 0; c < Columns; c++) Start[c + 1] = Start[c] + System.Math.Max(0, Hi[c] - Lo[c] + 1);

        Solid = new float[Cells];
        Parallel.For(0, Columns, c =>
        {
            foreach (var (lo, hi) in spans[c])
                for (int y = FloorDiv(lo, Size); y <= FloorDiv(hi, Size); y++)
                {
                    int from = System.Math.Max(lo, y * Size), to = System.Math.Min(hi + 1, y * Size + Size);
                    Solid[Start[c] + y - Lo[c]] += (to - from) / (float)Size;
                }
        });
    }

    /// <summary>Cell y of column c, or -1 where the column has none there.</summary>
    public int Index(int c, int y) => y < Lo[c] || y > Hi[c] ? -1 : Start[c] + y - Lo[c];

    public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
