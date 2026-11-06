using System.Diagnostics;
using ClearSkies.Game.Generation;

namespace ClearSkies.Game.Ruins;

/// <summary>
/// One landmass (see <see cref="HeartGrid.Landmass"/>) sampled for ruins, a column per <see cref="RuinSite.Cell"/>
/// blocks: each column's top two solid spans. A landmass is a partition of the world with open sky round it, and may
/// hold several islands well apart; land counts as one island across gaps a bridge can cross
/// (<see cref="RuinSite.BridgeCells"/>), and each island gets its own ruin.
/// </summary>
public sealed class LandmassSample
{
    private const int Cell = RuinSite.Cell;

    public readonly ulong Seed;
    public readonly (int X, int Z) Landmass;
    public readonly int X0, Z0, Nx, Nz;
    public readonly short[] TopLo, TopHi, LowLo, LowHi;
    public readonly bool[] Land;
    /// <summary>Steps to the nearest land, and to the nearest cell without land.</summary>
    public readonly int[] FromLand, FromSky;
    /// <summary>Each cell's island (-1 for none), islands numbered biggest first.</summary>
    public readonly int[] IslandOf;
    /// <summary>Per island: its land cells, and its most interior land cell.</summary>
    public readonly int[] IslandLand, IslandCentre;
    public readonly TimeSpan SampleTime, IslandTime;
    public int Islands => IslandLand.Length;

    public LandmassSample(ulong seed, (int X, int Z) landmass)
    {
        Seed = seed; Landmass = landmass;
        var sw = Stopwatch.StartNew();
        var (bx0, bz0, bx1, bz1) = Landscape.Bounds(seed, landmass);
        X0 = bx0; Z0 = bz0;
        int nx = Nx = (bx1 - bx0) / Cell, nz = Nz = (bz1 - bz0) / Cell, n = nx * nz;
        var topLo = TopLo = new short[n]; var topHi = TopHi = new short[n];
        var lowLo = LowLo = new short[n]; var lowHi = LowHi = new short[n];
        Array.Fill(topHi, short.MinValue); Array.Fill(lowHi, short.MinValue);
        const int perChunk = 32 / Cell;
        var generators = new ThreadLocal<HeartWorldGenerator>(() => new HeartWorldGenerator(seed));
        Parallel.For(0, (nz + perChunk - 1) / perChunk, cz =>
        {
            var gen = generators.Value!;
            Span<(short Lo, short Hi)> spans = stackalloc (short, short)[32];
            for (int i = 0; i < nx; i++)
            for (int j = cz * perChunk; j < Math.Min(nz, (cz + 1) * perChunk); j++)
            {
                int x = bx0 + i * Cell + Cell / 2, z = bz0 + j * Cell + Cell / 2, c = i + nx * j;
                if (HeartGrid.Landmass(seed, x, z) != landmass) continue;
                int k = gen.SpansAt(x, z, spans);
                if (k >= 1) { topLo[c] = spans[k - 1].Lo; topHi[c] = spans[k - 1].Hi; }
                if (k >= 2) { lowLo[c] = spans[k - 2].Lo; lowHi[c] = spans[k - 2].Hi; }
            }
        });
        SampleTime = sw.Elapsed;
        sw.Restart();

        var land = Land = new bool[n];
        for (int c = 0; c < n; c++) land[c] = topHi[c] != short.MinValue;
        FromSky = Distances(nx, nz, c => !land[c]);
        var fromLand = FromLand = Distances(nx, nz, c => land[c]);

        // Islands: land and the gaps a bridge crosses, joined 8-way.
        var raw = new int[n]; Array.Fill(raw, -1);
        var sizes = new List<int>(); var centres = new List<int>();
        var q = new Queue<int>();
        for (int s = 0; s < n; s++)
        {
            if (!land[s] || raw[s] >= 0) continue;
            int id = sizes.Count, size = 0, centre = s;
            raw[s] = id; q.Enqueue(s);
            while (q.TryDequeue(out int c))
            {
                if (land[c]) { size++; if (FromSky[c] > FromSky[centre]) centre = c; }
                int i = c % nx, j = c / nx;
                for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    int a = i + di, b = j + dj;
                    if (a < 0 || b < 0 || a >= nx || b >= nz) continue;
                    int e = a + nx * b;
                    if (raw[e] < 0 && fromLand[e] <= RuinSite.BridgeCells / 2) { raw[e] = id; q.Enqueue(e); }
                }
            }
            sizes.Add(size); centres.Add(centre);
        }
        var order = Enumerable.Range(0, sizes.Count).OrderByDescending(k => sizes[k]).ToArray();
        var rank = new int[order.Length];
        for (int r = 0; r < order.Length; r++) rank[order[r]] = r;
        IslandOf = raw.Select(id => id < 0 ? -1 : rank[id]).ToArray();
        IslandLand = order.Select(k => sizes[k]).ToArray();
        IslandCentre = order.Select(k => centres[k]).ToArray();
        IslandTime = sw.Elapsed;
    }

    /// <summary>Each cell's steps (4-neighbour) to the nearest cell where <paramref name="source"/> holds.</summary>
    internal static int[] Distances(int nx, int nz, Func<int, bool> source)
    {
        var d = new int[nx * nz];
        var q = new Queue<int>();
        for (int c = 0; c < d.Length; c++)
            if (source(c)) q.Enqueue(c);
            else d[c] = int.MaxValue;
        while (q.TryDequeue(out int c))
        {
            int i = c % nx, j = c / nx;
            void Visit(int e) { if (d[e] == int.MaxValue) { d[e] = d[c] + 1; q.Enqueue(e); } }
            if (i > 0) Visit(c - 1);
            if (i < nx - 1) Visit(c + 1);
            if (j > 0) Visit(c - nx);
            if (j < nz - 1) Visit(c + nx);
        }
        return d;
    }
}
