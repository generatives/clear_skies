namespace ClearSkies.Game.Generation;

/// <summary>
/// One landmass as the pipes model sees it: columns of Size blocks on a side, each sampled at its middle block column,
/// and in each column the open gaps above its solid spans (nodes). A node's floor is the top of the span under it and
/// its ceiling the bottom of the span over it (none for the top one, open to the sky). Nodes of neighbouring columns
/// whose gaps overlap share a face (a link from each side). Where a node's gap reaches down into the open space under all of a neighbour's land
/// (or the neighbour has none), it has a drain face there: water through it falls off the land.
/// </summary>
public sealed class Land
{
    public const float NoCeiling = 1e9f;

    public readonly int Size, Nx, Nz, X0, Z0;
    public int Columns => Nx * Nz;

    /// <summary>Nodes of column c are NodeStart[c] .. NodeStart[c + 1] - 1, bottom up.</summary>
    public readonly int[] NodeStart;
    public readonly float[] Floor, Ceiling;
    /// <summary>The bottom of the span each node stands on: its floor can't be worn lower than a little over this.</summary>
    public readonly float[] Base;
    public readonly int[] ColumnOf;
    public int Nodes => Floor.Length;

    /// <summary>Links: node n's are LinkStart[n] .. LinkStart[n + 1] - 1, one for each face it shares with a
    /// neighbouring node (LinkTo, whose link back is LinkBack) or drain (LinkTo and LinkBack -1). Each face so has a link
    /// from both its sides, and everything a node needs lies in its own run of links.</summary>
    public readonly int[] LinkStart, LinkTo, LinkBack;
    /// <summary>The top of each link's opening (the lower of its nodes' ceilings; see LinkBottom for its bottom) and the
    /// distance between its nodes' middles.</summary>
    public readonly float[] LinkTop, LinkLength;
    public int Links => LinkTo.Length;
    /// <summary>Every face's width: columns are taken as octagons, all of whose sides are (√2 - 1) of its width, so
    /// water runs to all eight neighbours alike and doesn't line up with the grid.</summary>
    public readonly float FaceWidth;

    /// <summary>Rain on each column's top node, as a fraction of the most (by the climate's wetness).</summary>
    public readonly float[] Wetness;

    public Land(ulong seed, (int X, int Z) landmass, int x0, int z0, int x1, int z1, int size)
    {
        Size = size; X0 = x0; Z0 = z0;
        Nx = (x1 - x0) / size; Nz = (z1 - z0) / size;
        int columns = Nx * Nz;

        // Each column's solid spans, bottom up, chunk column by chunk column so each lattice is worked out once.
        var spans = new (short Lo, short Hi)[columns][];
        int chunk = 32, perChunk = Math.Max(1, chunk / size);
        int chunksX = (Nx + perChunk - 1) / perChunk, chunksZ = (Nz + perChunk - 1) / perChunk;
        var generators = new ThreadLocal<HeartWorldGenerator>(() => new HeartWorldGenerator(seed));
        Parallel.For(0, chunksZ, cz =>
        {
            var gen = generators.Value!;
            Span<(short Lo, short Hi)> into = stackalloc (short, short)[32];
            for (int cx = 0; cx < chunksX; cx++)
            for (int j = cz * perChunk; j < Math.Min(Nz, (cz + 1) * perChunk); j++)
            for (int i = cx * perChunk; i < Math.Min(Nx, (cx + 1) * perChunk); i++)
            {
                int x = x0 + i * size + size / 2, z = z0 + j * size + size / 2;
                // Other landmasses' ground is left out: void here.
                int n = HeartGrid.Landmass(seed, x, z) == landmass ? gen.SpansAt(x, z, into) : 0;
                spans[i + Nx * j] = into[..n].ToArray();
            }
        });

        NodeStart = new int[columns + 1];
        for (int c = 0; c < columns; c++) NodeStart[c + 1] = NodeStart[c] + spans[c].Length;
        int nodes = NodeStart[columns];
        Floor = new float[nodes]; Ceiling = new float[nodes]; Base = new float[nodes]; ColumnOf = new int[nodes];
        for (int c = 0; c < columns; c++)
        {
            var s = spans[c];
            for (int k = 0; k < s.Length; k++)
            {
                int n = NodeStart[c] + k;
                Floor[n] = s[k].Hi + 1;
                Base[n] = s[k].Lo;
                Ceiling[n] = k + 1 < s.Length ? s[k + 1].Lo : NoCeiling;
                ColumnOf[n] = c;
            }
        }

        // Links to the nodes of all eight neighbouring columns whose gaps overlap this one's, and drains (including off
        // the sampled area).
        var start = new int[nodes + 1];
        var to = new List<int>(nodes * 9); var top = new List<float>(nodes * 9); var length = new List<float>(nodes * 9);
        var sides = new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (-1, 1), (1, -1) };
        float LowestLo(int c) => spans[c].Length > 0 ? spans[c][0].Lo : NoCeiling;
        for (int n = 0; n < nodes; n++)
        {
            start[n] = to.Count;
            int c = ColumnOf[n], i = c % Nx, j = c / Nx;
            foreach (var (di, dj) in sides)
            {
                float apart = size * (di != 0 && dj != 0 ? MathF.Sqrt(2) : 1f);
                int ni = i + di, nj = j + dj;
                int d = ni >= 0 && nj >= 0 && ni < Nx && nj < Nz ? ni + Nx * nj : -1;
                // Drain: the gap reaches below all of the neighbour's land.
                float under = d < 0 ? NoCeiling : LowestLo(d);
                if (Floor[n] < under) { to.Add(-1); top.Add(Math.Min(Ceiling[n], under)); length.Add(apart); }
                if (d < 0) continue;
                for (int m = NodeStart[d]; m < NodeStart[d + 1]; m++)
                {
                    float lo = Math.Max(Floor[n], Floor[m]), hi = Math.Min(Ceiling[n], Ceiling[m]);
                    if (hi <= lo) continue;
                    to.Add(m); top.Add(hi); length.Add(apart);
                }
            }
        }
        start[nodes] = to.Count;
        LinkStart = start; LinkTo = to.ToArray(); LinkTop = top.ToArray(); LinkLength = length.ToArray();
        LinkBack = new int[LinkTo.Length];
        for (int n = 0; n < nodes; n++)
        for (int k = start[n]; k < start[n + 1]; k++)
        {
            int m = LinkTo[k];
            LinkBack[k] = -1;
            if (m < 0) continue;
            for (int r = start[m]; r < start[m + 1]; r++)
                if (LinkTo[r] == n && LinkLength[r] == LinkLength[k] && LinkTop[r] == LinkTop[k]) { LinkBack[k] = r; break; }
        }
        FaceWidth = size * (MathF.Sqrt(2) - 1);

        var climate = ContinentTerrain.For(seed);
        Wetness = new float[columns];
        Parallel.For(0, Nz, j =>
        {
            for (int i = 0; i < Nx; i++)
            {
                float w = Math.Clamp((climate.Wet(X0 + i * size + size / 2f, Z0 + j * size + size / 2f) - 0.3f) / 0.55f, 0f, 1f);
                Wetness[i + Nx * j] = w * w * (3 - 2 * w);
            }
        });
    }

    /// <summary>The bottom of node n's link k's opening: the higher of its nodes' floors (as they are now; erosion moves
    /// them).</summary>
    public float LinkBottom(int n, int k) => LinkTo[k] >= 0 ? Math.Max(Floor[n], Floor[LinkTo[k]]) : Floor[n];

    public bool HasLand(int c) => NodeStart[c + 1] > NodeStart[c];

    /// <summary>The column's top node (open to the sky), or -1.</summary>
    public int TopNode(int c) => HasLand(c) ? NodeStart[c + 1] - 1 : -1;
}
