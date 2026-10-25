using ClearSkies.Game.Generation;

namespace ClearSkies.Hydrology;

/// <summary>
/// One landmass sampled into cells <see cref="Size"/> blocks on a side (8 for the model itself, more for a rough first
/// settle to start it from). Each coarse column (Size by Size blocks) spans
/// from the bottom of its lowest ground (Lo) up to just above the highest ground around it (Hi); over that is open sky,
/// under it the void. Of those cells only the ones water can reach are kept: solid stone more than
/// <see cref="Shell"/> cells inside the ground is left out (inert: it holds and passes no water). What's kept is in
/// runs of cells, bottom up, packed one after another: column c's runs are RunStart[c] to RunStart[c + 1], run r's
/// cells RunY0[r] to RunY1[r], the first at RunCell[r].
/// </summary>
public sealed class CoarseLand
{
    public readonly int Size;               // blocks on a side of a cell

    public readonly int X0, Z0, Nx, Nz;     // the first column's corner (blocks) and the columns across and along
    public readonly int[] Lo, Hi, Top;      // per column: its lowest and highest cell, and its ground's top cell
    public readonly int[] RunStart;         // per column: its first run (RunStart[Nx * Nz] is the run count)
    public readonly int[] RunY0, RunY1, RunCell;
    public readonly float[] Solid;          // per cell: how much of it is ground, 0-1
    public readonly int Shell;
    public readonly long Spanned;           // cells between every column's Lo and Hi, kept or not

    public int Columns => Nx * Nz;
    public int Runs => RunStart[Columns];
    public int Cells { get; }
    public bool HasLand(int c) => Hi[c] >= Lo[c];

    /// <summary>Samples landmass <paramref name="cell"/> between blocks (x0, z0) and (x1, z1): each column at its
    /// middle block column, on all cores. Keeps solid cells up to <paramref name="shell"/> cells inside the ground
    /// (int.MaxValue keeps them all).</summary>
    public CoarseLand(ulong seed, (int X, int Z) cell, int x0, int z0, int x1, int z1, int shell = 2, int size = 8)
    {
        Size = size;
        X0 = x0; Z0 = z0; Nx = (x1 - x0) / Size; Nz = (z1 - z0) / Size; Shell = shell;
        var spans = new (short Lo, short Hi)[Columns][];
        // A chunk's worth of columns at a time, so each sampler loads that chunk's hearts once.
        int PerChunk = System.Math.Max(1, 32 / Size);
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

        // Every cell from Lo to Hi first, then the ones kept.
        var start = new long[Columns + 1];
        for (int c = 0; c < Columns; c++) start[c + 1] = start[c] + System.Math.Max(0, Hi[c] - Lo[c] + 1);
        Spanned = start[Columns];
        var solid = new float[Spanned];
        Parallel.For(0, Columns, c =>
        {
            foreach (var (lo, hi) in spans[c])
                for (int y = FloorDiv(lo, Size); y <= FloorDiv(hi, Size); y++)
                {
                    int from = System.Math.Max(lo, y * Size), to = System.Math.Min(hi + 1, y * Size + Size);
                    solid[start[c] + y - Lo[c]] += (to - from) / (float)Size;
                }
        });
        var kept = Kept(solid, start);

        RunStart = new int[Columns + 1];
        var y0 = new List<int>(); var y1 = new List<int>(); var at = new List<int>();
        var keptSolid = new List<float>();
        for (int c = 0; c < Columns; c++)
        {
            RunStart[c] = y0.Count;
            for (int y = Lo[c]; y <= Hi[c];)
            {
                if (!kept[start[c] + y - Lo[c]]) { y++; continue; }
                y0.Add(y); at.Add(keptSolid.Count);
                for (; y <= Hi[c] && kept[start[c] + y - Lo[c]]; y++) keptSolid.Add(solid[start[c] + y - Lo[c]]);
                y1.Add(y - 1);
            }
        }
        RunStart[Columns] = y0.Count;
        RunY0 = y0.ToArray(); RunY1 = y1.ToArray(); RunCell = at.ToArray();
        Solid = keptSolid.ToArray();
        Cells = Solid.Length;
    }

    /// <summary>Which cells to keep: all but solid ones more than Shell cells (steps across faces) from anything that
    /// isn't solid ground, the void and open sky included.</summary>
    private bool[] Kept(float[] solid, long[] start)
    {
        var kept = new bool[solid.Length];
        if (Shell == int.MaxValue) { Array.Fill(kept, true); return kept; }
        // Peel solid ground a layer at a time: what's left after Shell + 1 peels is inert.
        var inner = new bool[solid.Length];
        for (long k = 0; k < solid.Length; k++) inner[k] = solid[k] >= 1f;
        for (int pass = 0; pass <= Shell; pass++)
        {
            var next = new bool[solid.Length];
            Parallel.For(0, Columns, c =>
            {
                int i = c % Nx, j = c / Nx;
                for (int y = Lo[c]; y <= Hi[c]; y++)
                {
                    long k = start[c] + y - Lo[c];
                    if (!inner[k]) continue;
                    next[k] = Inner(c, y + 1) && Inner(c, y - 1)
                           && i + 1 < Nx && Inner(c + 1, y) && i > 0 && Inner(c - 1, y)
                           && j + 1 < Nz && Inner(c + Nx, y) && j > 0 && Inner(c - Nx, y);
                }
            });
            inner = next;
        }
        for (long k = 0; k < solid.Length; k++) kept[k] = !inner[k];
        return kept;

        bool Inner(int c, int y) => y >= Lo[c] && y <= Hi[c] && inner[start[c] + y - Lo[c]];
    }

    /// <summary>The kept cell y of column c, or -1 where it has none there.</summary>
    public int Index(int c, int y)
    {
        for (int r = RunStart[c]; r < RunStart[c + 1]; r++)
            if (y >= RunY0[r] && y <= RunY1[r]) return RunCell[r] + y - RunY0[r];
        return -1;
    }

    private int[]? _cellY;
    /// <summary>The height (cells) of kept cell i.</summary>
    public int RunY0Of(int i)
    {
        if (_cellY == null)
        {
            var y = new int[Cells];
            for (int r = 0; r < Runs; r++) for (int k = 0; k <= RunY1[r] - RunY0[r]; k++) y[RunCell[r] + k] = RunY0[r] + k;
            _cellY = y;
        }
        return _cellY[i];
    }

    public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
