namespace ClearSkies.Pipes;

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
    /// without groundwater.</summary>
    public float[]? Groundwater;
    /// <summary>What each node's surface water soaks in and what comes up from its ground, in blocks³ a second.</summary>
    public float[]? Soaking, Springs;
    private float[]? _shellBottom;
    private int _sweeps;

    public void UseGroundwater()
    {
        var land = _land;
        Groundwater = new float[land.Nodes]; Soaking = new float[land.Nodes]; Springs = new float[land.Nodes];
        _shellBottom = new float[land.Nodes];
        for (int n = 0; n < land.Nodes; n++)
        {
            _shellBottom[n] = Math.Max(land.Base[n], land.Floor[n] - Shell);
            Groundwater[n] = (_shellBottom[n] + land.Floor[n]) / 2; // half full to start
        }
    }

    /// <summary>How much link k (of node n, to m) passes per block of difference in table: through the wet part of the height its two
    /// shells share (wet on the higher table's side). Shells that don't overlap, like a ledge's and the ground at its
    /// foot, pass nothing.</summary>
    private float Conductance(int k, int n, int m, float bottomN, float bottomM)
    {
        var land = _land; var table = Groundwater!;
        float low = Math.Max(bottomN, bottomM);
        float shared = Math.Min(land.Floor[n], land.Floor[m]) - low;
        float wet = Math.Max(table[n], table[m]) - low; // on the higher table's side
        float through = Math.Min(shared, wet);
        return through <= 0 ? 0 : Permeability * land.FaceWidth * through / land.LinkLength[k];
    }

    /// <summary>One sweep of the water tables, alternating direction; blocks of nodes in parallel.</summary>
    private void Seep(float dt)
    {
        var land = _land;
        var table = Groundwater!; var bottom = _shellBottom!; var soaking = Soaking!; var springs = Springs!;
        bool forward = (_sweeps++ & 1) == 0;
        float stored = Porosity * _area / Storage;
        Parallel.For(0, (land.Nodes + 2047) / 2048, block =>
        {
            int start = block * 2048, end = Math.Min(land.Nodes, start + 2048);
            for (int i = 0; i < end - start; i++)
            {
                int n = forward ? start + i : end - 1 - i;
                // Erosion moves floors: the shell follows.
                float floor = land.Floor[n], under = bottom[n] = Math.Max(land.Base[n], floor - Shell);
                double sum = stored, rhs = stored * table[n] + soaking[n];
                for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
                {
                    int m = _other[k];
                    if (m < 0) continue; // edges pass nothing
                    float c = Conductance(k, n, m, under, bottom[m]);
                    if (c <= 0) continue;
                    sum += c; rhs += c * table[m];
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
        var land = _land; var table = Groundwater!; var bottom = _shellBottom!;
        double soak = 0, springs = 0, sideways = 0, dry = 0, fullShort = 0;
        for (int n = 0; n < land.Nodes; n++)
        {
            double net = 0;
            for (int k = _faceStart[n]; k < _faceStart[n + 1]; k++)
            {
                int m = _other[k];
                if (m < 0) continue;
                net += Conductance(k, n, m, bottom[n], bottom[m]) * (table[m] - table[n]);
            }
            soak += Soaking![n]; springs += Springs![n]; sideways += net;
            double balance = net + Soaking[n];
            if (table[n] <= bottom[n] + 1e-3f && balance < 0) dry += balance;
            if (table[n] >= land.Floor[n] - 1e-3f) fullShort += balance - Springs[n];
        }
        return (soak, springs, sideways, dry, fullShort);
    }
}
