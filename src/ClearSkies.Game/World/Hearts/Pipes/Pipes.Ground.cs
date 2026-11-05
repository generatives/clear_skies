namespace ClearSkies.Game.Generation;

/// <summary>
/// Groundwater, by the same face rule with the ground's numbers: water in the ground under each node (down to Shell
/// blocks under its floor; deeper ground and the land's edges pass nothing) flows to its neighbours' as Permeability ×
/// the face's width × the wet height of ground their shells share × the difference in their water tables ÷ the distance,
/// with no momentum. Water stands in the ground at the same table all the way down a column (the column pass: within a
/// column water settles at once). Surface water soaks in at up to Soak a second, and where the ground is full the
/// rest comes up as springs.
///
/// Only where the tables end up matters here, so they're found by sweeps rather than stepped through time: one sweep
/// every GroundEvery water steps, each node setting its table to balance its faces at its neighbours' latest tables (Gauss-Seidel),
/// slowed only by a little storage (Storage seconds' worth) so it moves smoothly. (Overshooting each move, to speed
/// it up, made water from nothing: springs ran on tables that hadn't settled.)
/// </summary>
public sealed partial class Pipes
{
    public float Shell = 24f, Permeability = 1e-3f, Porosity = 0.3f, Storage = 3600f;
    /// <summary>The ground is swept once every this many water steps: it changes slowly.</summary>
    public int GroundEvery = 4;

    /// <summary>Each node's water table (in blocks; at its shell's bottom when dry, at its floor when full), or null
    /// without groundwater (the landmass model's, see <see cref="CoarseLandmass.Groundwater"/>).</summary>
    public float[]? Groundwater { get => _coarse.Groundwater; private set => _coarse.Groundwater = value; }
    /// <summary>What each node's surface water soaks in and what comes up from its ground, in blocks³ a second.</summary>
    public float[]? Soaking, Springs;
    private float[]? _shellBottom;
    private int _sweeps;
    private float[]? _tableBefore;
    /// <summary>How many nodes a sweep's parallel task takes. Part of the result, not only of the speed: a node reads its
    /// own block's tables as they're swept and other blocks' as they were before the sweep.</summary>
    private const int SweepBlock = 2048;

    public void UseGroundwater()
    {
        var coarse = _coarse;
        Groundwater = new float[coarse.Nodes]; Soaking = new float[coarse.Nodes]; Springs = new float[coarse.Nodes];
        _shellBottom = new float[coarse.Nodes];
        for (int n = 0; n < coarse.Nodes; n++)
        {
            _shellBottom[n] = Math.Max(coarse.Base[n], coarse.Floor[n] - Shell);
            Groundwater[n] = (_shellBottom[n] + coarse.Floor[n]) / 2; // half full to start
        }
    }

    /// <summary>How much link k (of node n, to m) passes per block of difference in table: through the wet part of the height its two
    /// shells share (wet on the higher table's side). Shells that don't overlap, like a ledge's and the ground at its
    /// foot, pass nothing.</summary>
    private float Conductance(int k, int n, int m, float bottomN, float bottomM, float tableN, float tableM)
    {
        var coarse = _coarse;
        float low = Math.Max(bottomN, bottomM);
        float shared = Math.Min(coarse.Floor[n], coarse.Floor[m]) - low;
        float wet = Math.Max(tableN, tableM) - low; // on the higher table's side
        float through = Math.Min(shared, wet);
        return through <= 0 ? 0 : Permeability * coarse.FaceWidth * through / coarse.LinkLength[k];
    }

    /// <summary>One sweep of the water tables, alternating direction; blocks of nodes in parallel. A node in another
    /// block is read as it was before the sweep, so the result doesn't depend on which block runs first.</summary>
    private void Seep(float dt)
    {
        var coarse = _coarse;
        var table = Groundwater!; var bottom = _shellBottom!; var soaking = Soaking!; var springs = Springs!;
        var before = _tableBefore ??= new float[coarse.Nodes];
        bool forward = (_sweeps++ & 1) == 0;
        float stored = Porosity * _area / Storage;
        // Erosion moves floors: the shells follow.
        ForBlocks(coarse.Nodes, (first, end) =>
        {
            for (int n = first; n < end; n++) bottom[n] = Math.Max(coarse.Base[n], coarse.Floor[n] - Shell);
        });
        Array.Copy(table, before, coarse.Nodes);
        Parallel.For(0, (coarse.Nodes + SweepBlock - 1) / SweepBlock, block =>
        {
            int start = block * SweepBlock, end = Math.Min(coarse.Nodes, start + SweepBlock);
            for (int i = 0; i < end - start; i++)
            {
                int n = forward ? start + i : end - 1 - i;
                float floor = coarse.Floor[n], under = bottom[n];
                double sum = stored, rhs = stored * table[n] + soaking[n];
                for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
                {
                    int m = _other[k];
                    if (m < 0) continue; // edges pass nothing
                    float tableM = m >= start && m < end ? table[m] : before[m];
                    float c = Conductance(k, n, m, under, bottom[m], table[n], tableM);
                    if (c <= 0) continue;
                    sum += c; rhs += c * tableM;
                }
                float next = (float)(rhs / sum);
                if (next >= floor)
                {
                    // Full: what's left over comes up.
                    springs[n] = Math.Max(0f, (float)(rhs - sum * floor));
                    next = floor;
                }
                else springs[n] = 0;
                table[n] = Math.Max(under, next);
            }
        });
    }
}

public sealed partial class Pipes
{
    /// <summary>Where the ground's balance stands (blocks³ a second, summed): soaking in, springs, net sideways flow
    /// (zero if no water is made or lost), and the shortfall of dry nodes (giving more than they get).</summary>
    public (double Soak, double Springs, double Sideways, double DryShort, double FullShort) GroundBalance()
    {
        var coarse = _coarse; var table = Groundwater!; var bottom = _shellBottom!;
        double soak = 0, springs = 0, sideways = 0, dry = 0, fullShort = 0;
        for (int n = 0; n < coarse.Nodes; n++)
        {
            double net = 0;
            for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
            {
                int m = _other[k];
                if (m < 0) continue;
                net += Conductance(k, n, m, bottom[n], bottom[m], table[n], table[m]) * (table[m] - table[n]);
            }
            soak += Soaking![n]; springs += Springs![n]; sideways += net;
            double balance = net + Soaking[n];
            if (table[n] <= bottom[n] + 1e-3f && balance < 0) dry += balance;
            if (table[n] >= coarse.Floor[n] - 1e-3f) fullShort += balance - Springs[n];
        }
        return (soak, springs, sideways, dry, fullShort);
    }
}
