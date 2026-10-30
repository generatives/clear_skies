using ClearSkies.Game.Generation;

namespace ClearSkies.Pipes;

/// <summary>
/// One landmass as the pipes model sees it: columns of Size blocks on a side, each sampled at its middle block column,
/// and in each column the open gaps above its solid spans (nodes). A node's floor is the top of the span under it and
/// its ceiling the bottom of the span over it (none for the top one, open to the sky). Nodes of neighbouring columns
/// whose gaps overlap share a face. Where a node's gap reaches down into the open space under all of a neighbour's land
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
    public readonly int[] ColumnOf;
    public int Nodes => Floor.Length;

    /// <summary>Faces: water through face f goes from node FaceA[f] to FaceB[f] (-1: off the land) when positive.</summary>
    public readonly int[] FaceA, FaceB;
    /// <summary>The bottom of each face's opening (the higher of its nodes' floors) and its top (the lower ceiling).</summary>
    public readonly float[] FaceBottom, FaceTop;
    public int Faces => FaceA.Length;

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
        Floor = new float[nodes]; Ceiling = new float[nodes]; ColumnOf = new int[nodes];
        for (int c = 0; c < columns; c++)
        {
            var s = spans[c];
            for (int k = 0; k < s.Length; k++)
            {
                int n = NodeStart[c] + k;
                Floor[n] = s[k].Hi + 1;
                Ceiling[n] = k + 1 < s.Length ? s[k + 1].Lo : NoCeiling;
                ColumnOf[n] = c;
            }
        }

        // Faces to the east and south neighbour (and drains on all four sides, including off the sampled area).
        var a = new List<int>(nodes * 3); var b = new List<int>(nodes * 3);
        var bottom = new List<float>(nodes * 3); var top = new List<float>(nodes * 3);
        float LowestLo(int c) => spans[c].Length > 0 ? spans[c][0].Lo : NoCeiling;
        for (int j = 0; j < Nz; j++)
        for (int i = 0; i < Nx; i++)
        {
            int c = i + Nx * j;
            for (int n = NodeStart[c]; n < NodeStart[c + 1]; n++)
            {
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int ni = i + di, nj = j + dj;
                    bool inside = ni >= 0 && nj >= 0 && ni < Nx && nj < Nz;
                    int d = inside ? ni + Nx * nj : -1;
                    // Drain: the gap reaches below all of the neighbour's land.
                    float under = d < 0 ? NoCeiling : LowestLo(d);
                    if (Floor[n] < under)
                    {
                        a.Add(n); b.Add(-1); bottom.Add(Floor[n]); top.Add(Math.Min(Ceiling[n], under));
                    }
                    if (d < 0 || di < 0 || dj < 0) continue;
                    for (int m = NodeStart[d]; m < NodeStart[d + 1]; m++)
                    {
                        float lo = Math.Max(Floor[n], Floor[m]), hi = Math.Min(Ceiling[n], Ceiling[m]);
                        if (hi <= lo) continue;
                        a.Add(n); b.Add(m); bottom.Add(lo); top.Add(hi);
                    }
                }
            }
        }
        FaceA = a.ToArray(); FaceB = b.ToArray(); FaceBottom = bottom.ToArray(); FaceTop = top.ToArray();

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

    public bool HasLand(int c) => NodeStart[c + 1] > NodeStart[c];

    /// <summary>The column's top node (open to the sky), or -1.</summary>
    public int TopNode(int c) => HasLand(c) ? NodeStart[c + 1] - 1 : -1;
}
