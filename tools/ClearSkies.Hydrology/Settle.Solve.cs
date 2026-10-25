using System.Collections.Concurrent;

namespace ClearSkies.Hydrology;

/// <summary>
/// The settled state found directly, rather than by stepping until nothing changes: the heads where every cell's flow
/// balances, rain in plus what its neighbours send equal to what it sends out (or drains into the void). Same cells,
/// same flow rule (permeability times head difference), so the same answer the passes head towards; but how fast water
/// moves no longer matters, only where it ends up, so open air can pass water as freely as it really does.
/// The flow only depends on how wet a cell is through how freely it passes water: a dry cell sends nothing (its
/// permeability falls to almost nothing as it empties). That makes the balance nonlinear, so it's solved a few times
/// over, each time with every face's permeability taken from how wet the cell water would cross it from is now (the
/// higher head's), which is linear: a sparse symmetric system, solved by conjugate gradients with a multigrid
/// preconditioner (cells merged 2x2x2 a level, only with cells of about the same permeability).
/// </summary>
public sealed partial class Settle
{
    /// <summary>Open air's permeability for the solve (0: as for passes). Nothing limits it here.</summary>
    public float SolveAir { get; set; }
    /// <summary>How full (of a cell) a cell must be to pass water freely; emptier, it passes proportionally less.</summary>
    public float WetFill { get; set; } = 0.1f;
    public float StartFill { get; set; } = 0f;
    public double TauGrowth { get; set; } = 2;
    public int SolveRounds { get; set; } = 300;
    public double SolveShrink { get; set; } = 0.1; // how far each round's linear solve brings its residual down
    public double SolveTolerance { get; set; } = 0.01; // of the rain: how far from balanced the whole land may be

    /// <summary>Instead of solving, write the problem out (for trying other solvers): see Export.</summary>
    public string? ExportTo { get; set; }

    /// <summary>Writes the cell graph: a JSON header (sizes, cell size, rain, air permeability) then raw little-endian
    /// arrays in order: per face a, b (int32); per cell permeability, solid, capacity (float32), void faces, height,
    /// column (int32), rain (float64).</summary>
    private void Export(string path, List<(int, int)> pairs, double[] voidFaces, int[] y, int[] column, double[] rain)
    {
        int n = _land.Cells;
        using var w = new BinaryWriter(File.Create(path));
        var header = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"cells\":{n},\"faces\":{pairs.Count},\"size\":{_land.Size},\"rain\":{Rain},\"air\":{_airPermeability},\"nx\":{_land.Nx},\"nz\":{_land.Nz}}}\n");
        w.Write(header);
        foreach (var (a, _) in pairs) w.Write(a);
        foreach (var (_, b) in pairs) w.Write(b);
        foreach (float v in _permeability) w.Write(v);
        foreach (float v in _land.Solid) w.Write(v);
        foreach (float v in _capacity) w.Write(v);
        foreach (double v in voidFaces) w.Write((int)v);
        foreach (int v in y) w.Write(v);
        foreach (int v in column) w.Write(v);
        foreach (double v in rain) w.Write(v);
        Console.WriteLine($"Wrote {path}: {n:N0} cells, {pairs.Count:N0} faces");
    }

    private double[]? _solved; // per cell: the heads, once solved
    private double _drainedShare;

    /// <summary>Once solved, per column: water out of its ground into open cells, and across its open cells' sides.</summary>
    public float[]? Springs, Runs;

    /// <summary>The open water in cell i (blocks deep): its head over the ground in it, taking that ground to fill the
    /// cell's bottom. Only once solved.</summary>
    public float OpenBlocks(int i)
    {
        if (_solved == null) return 0f;
        double bottom = _land.Size * _land.RunY0Of(i), ground = bottom + _land.Size * _land.Solid[i];
        return (float)Math.Max(0, Math.Min(_solved[i], bottom + _land.Size) - ground);
    }
    public bool Solved => _solved != null;

    private sealed class Level
    {
        public int N;
        public int[] Start = null!, Col = null!;
        public double[] Val = null!, Diag = null!;      // off the diagonal (minus the face's conductance), and on it
        public int[] X = null!, Z = null!, Y = null!, Cls = null!;
        public int[]? Agg, MemberStart, Members, Map;   // to the next level: each cell's, each coarse cell's, each entry's
        public double[] R = null!, B = null!, X0 = null!, T = null!;
        public double[]? Chol;
    }

    /// <summary>Solves for the settled heads and sets the water from them. Starts from the heads already there
    /// (<see cref="StartHeadsFrom"/>), or every cell just wet.</summary>
    public void SolveSteady(bool fromHeads = false)
    {
        var land = _land;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int n = land.Cells;
        float airScale = SolveAir > 0f ? SolveAir / _airPermeability : 1f;

        // Per cell: its column, height, bottom (blocks), permeability, rain, and faces into the void.
        var column = new int[n]; var y = new int[n];
        for (int c = 0; c < land.Columns; c++)
            for (int r = land.RunStart[c]; r < land.RunStart[c + 1]; r++)
                for (int yy = land.RunY0[r], i = land.RunCell[r]; yy <= land.RunY1[r]; yy++, i++) { column[i] = c; y[i] = yy; }
        var k = new double[n];
        Parallel.For(0, n, i => k[i] = _permeability[i] * Math.Pow(airScale, 1f - land.Solid[i]));
        var bottom = new double[n]; var target = new double[n]; var rain = new double[n]; var voidK = new double[n];
        for (int i = 0; i < n; i++) { bottom[i] = land.Size * y[i]; target[i] = land.Size * (y[i] - 1); }
        double rainTotal = 0;
        for (int c = 0; c < land.Columns; c++)
        {
            int at = RainLanding(c);
            if (at >= 0) { rain[at] += Rain; rainTotal += Rain; }
        }

        // The cells' faces with each other, and with the void.
        var counts = new int[n + 1];
        var pairs = new List<(int, int)>(n * 3);
        int nx = land.Nx, nz = land.Nz;
        for (int c = 0; c < land.Columns; c++)
        {
            int ci = c % nx, cj = c / nx;
            for (int r = land.RunStart[c]; r < land.RunStart[c + 1]; r++)
            {
                int s = land.RunCell[r], len = land.RunY1[r] - land.RunY0[r] + 1;
                for (int v = 0; v + 1 < len; v++) pairs.Add((s + v, s + v + 1));
                if (land.RunY0[r] == land.Lo[c]) voidK[s] += 1;
                foreach (int nc in stackalloc[] { ci + 1 < nx ? c + 1 : -1, ci > 0 ? c - 1 : -1, cj + 1 < nz ? c + nx : -1, cj > 0 ? c - nx : -1 })
                {
                    for (int yy = land.RunY0[r]; yy <= land.RunY1[r]; yy++)
                        if (nc < 0 || !land.HasLand(nc) || yy < land.Lo[nc]) voidK[s + yy - land.RunY0[r]] += 1;
                    if (nc < c) continue; // each side face once, from the lower column
                    for (int q = land.RunStart[nc]; q < land.RunStart[nc + 1]; q++)
                        if (Overlap(r, q, out int mine, out int theirs, out int ln))
                            for (int v = 0; v < ln; v++) pairs.Add((mine + v, theirs + v));
                }
            }
        }
        foreach (var (a, b) in pairs) { counts[a + 1]++; counts[b + 1]++; }
        for (int i = 0; i < n; i++) counts[i + 1] += counts[i];
        var fine = new Level { N = n, Start = counts, Col = new int[counts[n]], Val = new double[counts[n]], Diag = new double[n] };
        var faceK = new double[counts[n]];
        var fill = (int[])counts.Clone();
        foreach (var (a, b) in pairs)
        {
            double kf = Math.Min(k[a], k[b]) * PerBlock;
            faceK[fill[a]] = kf; fine.Col[fill[a]++] = b;
            faceK[fill[b]] = kf; fine.Col[fill[b]++] = a;
        }
        if (ExportTo != null) { Export(ExportTo, pairs, voidK, y, column, rain); return; }
        pairs = null;
        for (int i = 0; i < n; i++) voidK[i] *= k[i] * PerBlock;
        fine.X = new int[n]; fine.Z = new int[n]; fine.Y = y; fine.Cls = new int[n];
        for (int i = 0; i < n; i++)
        {
            fine.X[i] = column[i] % nx; fine.Z[i] = column[i] / nx;
            fine.Cls[i] = (int)Math.Floor(Math.Log10(k[i]) / 2);
        }
        var levels = Hierarchy(fine);
        Console.WriteLine($"Solve set up in {watch.Elapsed.TotalSeconds:0.00} s: {counts[n]:N0} face entries, levels " +
                          string.Join(" > ", levels.Select(l => l.N.ToString("N0"))));

        // Start just wet everywhere (or from the heads given), then step towards balanced: each round, Newton's method
        // (the balance's slope at the heads now, how each cell's balance changes with its own and its neighbours' heads)
        // held back by a pseudo-time step: each cell's own slope over Tau more on its diagonal. Small Tau moves each
        // cell a little towards its own balance (slow, but safe); large is Newton (fast near the answer). Tau grows as
        // the balance improves, and shrinks when a step makes it worse (which is then not taken).
        var h = new double[n];
        for (int i = 0; i < n; i++) h[i] = fromHeads && _solved != null ? _solved[i] : bottom[i] + StartFill * land.Size;
        var f = new double[n]; var step = new double[n]; var trial = new double[n]; var ft = new double[n];
        var ws = new Work(n);
        double wet = land.Size * WetFill, tau = 1;
        double off = Balance(h, f, false) / rainTotal;
        for (int round = 1; round <= SolveRounds; round++)
        {
            var lap = System.Diagnostics.Stopwatch.StartNew();
            Console.Write($"round {round,3}: off balance {off:0.0000} of the rain, drained/rained {_drainedShare:0.000}, tau {tau:0.##E0}");
            if (off < SolveTolerance) { Console.WriteLine(" - settled"); break; }
            Balance(h, f, true);
            double held = 1 + 1 / tau;
            // Only cells near their wet edge are held back: elsewhere (soaked through, or under water) the balance is
            // linear in the heads, and Newton's step is right however far it goes. A dry cell's own slope is next to
            // nothing: hold it back by (a little of) its storage too.
            for (int i = 0; i < n; i++)
                if (h[i] - bottom[i] < 2 * wet) fine.Diag[i] = fine.Diag[i] * held + 1e-4 * _capacity[i] * PerBlock / tau;
            for (int l = 0; l + 1 < levels.Count; l++) Galerkin(levels[l], levels[l + 1]);
            Factor(levels[^1]);
            Array.Clear(step);
            int its = BiCgStab(levels, f, step, ws, SolveShrink);
            if (Environment.GetEnvironmentVariable("HYD_DEBUG") != null)
            {
                Residual(fine, f, step, fine.R);
                Console.Write($" [linear left {SumAbs(fine.R) / SumAbs(f):0.###}, |step| max {step.Max(Math.Abs):0.##}]");
            }
            // No more than a cell's height a round: the slope only says so much about heads further away.
            // No more than a cell's height a round: the slope only says so much about heads further away. If the
            // whole step makes things worse, try less of it.
            double most = land.Size, next = double.MaxValue, lambda = 1;
            for (int tries = 0; tries < 3; tries++, lambda *= 0.5)
            {
                for (int i = 0; i < n; i++) {
                    // Near its wet edge a cell moves little at a time: whether it's wet changes everything about it.
                    double reach = Math.Min(most, Math.Max(wet, Math.Abs(h[i] - bottom[i])));
                    trial[i] = Math.Max(bottom[i], h[i] + lambda * Math.Clamp(step[i], -reach, reach));
                }
                next = Balance(trial, ft, false) / rainTotal;
                if (next < off) break;
            }
            bool taken = next < 1.5 * off;
            if (taken) { (h, trial) = (trial, h); tau = Math.Min(1e12, tau * (next < off && lambda == 1 ? TauGrowth : lambda == 1 ? 1 : 0.5)); off = next; }
            else tau = Math.Max(1e-4, tau / 4);
            Console.WriteLine($", {its} solver steps{(taken ? lambda < 1 ? $", {lambda} of it" : "" : ", not taken")}, {lap.Elapsed.TotalSeconds:0.00} s");
        }
        Balance(h, f, false);
        if (Environment.GetEnvironmentVariable("HYD_DEBUG") != null)
        {
            var worst = Enumerable.Range(0, n).OrderByDescending(i => Math.Abs(f[i])).Take(12).ToArray();
            double sum = SumAbs(f);
            Console.WriteLine($"worst 12 hold {worst.Sum(i => Math.Abs(f[i])) / sum:P0} of the imbalance");
            foreach (int i in worst)
            {
                Console.Write($"  cell {i} col {column[i]} y {y[i]} solid {land.Solid[i]:0.00} k {k[i]:0.##E0} void {voidK[i]:0.##E0} h-b {h[i] - bottom[i]:0.###} rain {rain[i]:0.##E0} gain {f[i]:0.##E0} |");
                for (int e = fine.Start[i]; e < fine.Start[i + 1]; e++) { int j = fine.Col[e]; Console.Write($" [{j}: dy {y[j] - y[i]} s {land.Solid[j]:0.0} h-b {h[j] - bottom[j]:0.##} hdiff {h[i] - h[j]:0.##}]"); }
                Console.WriteLine();
            }
        }
        _solved = h;
        // Per column: how much water runs through its open cells (out across their sides), and how much comes out of
        // its ground into them (springs).
        Springs = new float[land.Columns]; Runs = new float[land.Columns];
        for (int i = 0; i < n; i++)
        {
            bool open = land.Solid[i] < 0.5f;
            for (int e = fine.Start[i]; e < fine.Start[i + 1]; e++)
            {
                int j = fine.Col[e];
                if (h[i] <= h[j]) continue;
                double q = faceK[e] * Kr(i, h[i]) * (h[i] - h[j]);
                bool openJ = land.Solid[j] < 0.5f;
                if (!open && openJ) Springs[column[j]] += (float)q;
                else if (open && openJ && column[i] != column[j]) Runs[column[i]] += (float)q;
            }
        }
        for (int i = 0; i < n; i++)
        {
            _head[i] = (float)h[i];
            _water[i] = _capacity[i] * (float)Math.Clamp(h[i] * PerBlock - y[i], 0.0, 1.0);
        }
        Console.WriteLine($"Solved in {watch.Elapsed.TotalSeconds:0.00} s");

        // How wet a cell is to pass water (and how fast that changes with its head). Heads are never under a cell's
        // bottom: there it's dry, and sends nothing.
        double Kr(int i, double head) => Math.Min(1, (head - bottom[i]) / wet);
        double Dkr(int i, double head) => head - bottom[i] < wet ? 1 / wet : 0;

        // What each cell gains (rain and flow in, less flow out and into the void) at heads hh, into ff; with slope,
        // also how that changes with the heads, into the fine level's matrix (the negative). Returns the sum of sizes.
        double Balance(double[] hh, double[] ff, bool slope)
        {
            double drained = 0, total = 0; object gate = new();
            Parallel.ForEach(Partitioner.Create(0, n, 16384), () => (0.0, 0.0), (range, _, acc) =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    double gain = rain[i], diag = 0, hi = hh[i];
                    for (int e = fine.Start[i]; e < fine.Start[i + 1]; e++)
                    {
                        int j = fine.Col[e];
                        double hj = hh[j], dh = hi - hj, ke = faceK[e];
                        bool mine = hi > hj || (hi == hj && i < j);
                        double kr = mine ? Kr(i, hi) : Kr(j, hj);
                        gain -= ke * kr * dh;
                        if (!slope) continue;
                        if (mine) { diag += ke * (kr + Dkr(i, hi) * dh); fine.Val[e] = -ke * kr; }
                        else { diag += ke * kr; fine.Val[e] = ke * (Dkr(j, hj) * dh - kr); }
                    }
                    if (voidK[i] > 0)
                    {
                        double kr = Kr(i, hi), out_ = voidK[i] * kr * (hi - target[i]);
                        gain -= out_; acc.Item2 += out_;
                        if (slope) diag += voidK[i] * (kr + Dkr(i, hi) * (hi - target[i]));
                    }
                    if (slope) fine.Diag[i] = diag + 1e-30;
                    ff[i] = gain;
                    acc.Item1 += Math.Abs(gain);
                }
                return acc;
            }, acc => { lock (gate) { total += acc.Item1; drained += acc.Item2; } });
            _drainedShare = drained / rainTotal;
            return total;
        }
    }

    /// <summary>Where rain on column c lands: the first cell down from the top that isn't open air through and
    /// through (or the bottom of the open air, over the void).</summary>
    private int RainLanding(int c)
    {
        var land = _land;
        if (!land.HasLand(c)) return -1;
        int r = land.RunStart[c + 1] - 1;
        if (r < land.RunStart[c]) return -1;
        int i = land.RunCell[r] + land.RunY1[r] - land.RunY0[r];
        while (i > land.RunCell[r] && land.Solid[i] == 0f) i--;
        return i;
    }

    /// <summary>Starts the solve's heads from a solve on bigger cells over the same ground (the head of the bigger cell
    /// each cell is in), so only local detail is left to settle.</summary>
    public void StartHeadsFrom(Settle rough)
    {
        var land = _land; var big = rough._land;
        var h = new double[land.Cells];
        Parallel.For(0, land.Columns, c =>
        {
            int bx = CoarseLand.FloorDiv(land.X0 + (c % land.Nx) * land.Size + land.Size / 2 - big.X0, big.Size);
            int bz = CoarseLand.FloorDiv(land.Z0 + (c / land.Nx) * land.Size + land.Size / 2 - big.Z0, big.Size);
            bool inside = bx >= 0 && bz >= 0 && bx < big.Nx && bz < big.Nz;
            int bc = inside ? bx + big.Nx * bz : -1;
            for (int r = land.RunStart[c]; r < land.RunStart[c + 1]; r++)
            for (int y = land.RunY0[r], i = land.RunCell[r]; y <= land.RunY1[r]; y++, i++)
            {
                int k = bc >= 0 && big.HasLand(bc) ? big.Index(bc, CoarseLand.FloorDiv(y * land.Size + land.Size / 2, big.Size)) : -1;
                // The bigger cell's head, but never under this one's bottom (dry there is dry here).
                h[i] = Math.Max(land.Size * (double)y, k >= 0 && rough._solved != null ? rough._solved[k] : double.MinValue);
            }
        });
        _solved = h;
    }

    // ── The multigrid ─────────────────────────────────────────────────────

    private static List<Level> Hierarchy(Level fine)
    {
        var levels = new List<Level> { fine };
        var l = fine;
        Alloc(l);
        while (l.N > 600)
        {
            var keys = new Dictionary<(int, int, int, int), int>();
            l.Agg = new int[l.N];
            for (int i = 0; i < l.N; i++)
            {
                var key = (l.X[i] >> 1, l.Z[i] >> 1, l.Y[i] >> 1, l.Cls[i]);
                if (!keys.TryGetValue(key, out int a)) keys[key] = a = keys.Count;
                l.Agg[i] = a;
            }
            int m = keys.Count;
            if (m > 0.8 * l.N) { l.Agg = null; break; } // not coarsening: stop here
            var c = new Level { N = m, X = new int[m], Z = new int[m], Y = new int[m], Cls = new int[m] };
            foreach (var ((kx, kz, ky, kc), a) in keys) { c.X[a] = kx; c.Z[a] = kz; c.Y[a] = ky; c.Cls[a] = kc; }
            // Coarse cells' members, and the coarse matrix's shape: each fine entry lands on the diagonal (-1) or an entry.
            l.MemberStart = new int[m + 1];
            foreach (int a in l.Agg) l.MemberStart[a + 1]++;
            for (int a = 0; a < m; a++) l.MemberStart[a + 1] += l.MemberStart[a];
            l.Members = new int[l.N];
            var at = (int[])l.MemberStart.Clone();
            for (int i = 0; i < l.N; i++) l.Members[at[l.Agg[i]]++] = i;
            l.Map = new int[l.Col.Length];
            c.Start = new int[m + 1];
            var cols = new List<int>();
            var where = new int[m]; Array.Fill(where, -1);
            var seen = new List<int>();
            for (int a = 0; a < m; a++)
            {
                c.Start[a] = cols.Count;
                for (int p = l.MemberStart[a]; p < l.MemberStart[a + 1]; p++)
                {
                    int i = l.Members[p];
                    for (int e = l.Start[i]; e < l.Start[i + 1]; e++)
                    {
                        int b = l.Agg[l.Col[e]];
                        if (b == a) { l.Map[e] = -1; continue; }
                        if (where[b] < 0) { where[b] = cols.Count; cols.Add(b); seen.Add(b); }
                        l.Map[e] = where[b];
                    }
                }
                foreach (int b in seen) where[b] = -1;
                seen.Clear();
            }
            c.Start[m] = cols.Count;
            c.Col = cols.ToArray(); c.Val = new double[c.Col.Length]; c.Diag = new double[m];
            Alloc(c);
            levels.Add(c);
            l = c;
        }
        return levels;

        static void Alloc(Level l) { l.R = new double[l.N]; l.B = new double[l.N]; l.X0 = new double[l.N]; l.T = new double[l.N]; }
    }

    /// <summary>The coarse matrix from the fine one: each coarse cell's entries summed from its members'.</summary>
    private static void Galerkin(Level f, Level c)
    {
        Rows(c.N, (lo, hi) =>
        {
            for (int a = lo; a < hi; a++)
            {
                double diag = 0;
                for (int e = c.Start[a]; e < c.Start[a + 1]; e++) c.Val[e] = 0;
                for (int p = f.MemberStart![a]; p < f.MemberStart[a + 1]; p++)
                {
                    int i = f.Members![p];
                    diag += f.Diag[i];
                    for (int e = f.Start[i]; e < f.Start[i + 1]; e++)
                    {
                        int to = f.Map![e];
                        if (to < 0) diag += f.Val[e]; else c.Val[to] += f.Val[e];
                    }
                }
                c.Diag[a] = diag;
            }
        });
    }

    /// <summary>The coarsest level, factored (LU, no pivoting: the matrix is heavy on its diagonal) to be solved
    /// exactly, if it's small enough.</summary>
    private static void Factor(Level l)
    {
        if (l.N > 800) { l.Chol = null; return; }
        int n = l.N;
        var a = l.Chol ??= new double[n * n];
        Array.Clear(a);
        for (int i = 0; i < n; i++)
        {
            a[i * n + i] = l.Diag[i];
            for (int e = l.Start[i]; e < l.Start[i + 1]; e++) a[i * n + l.Col[e]] += l.Val[e];
        }
        for (int j = 0; j < n; j++)
        {
            double d = a[j * n + j];
            if (Math.Abs(d) < 1e-300) a[j * n + j] = d = 1e-300;
            for (int i = j + 1; i < n; i++)
            {
                double m = a[i * n + j] /= d;
                if (m == 0) continue;
                for (int p = j + 1; p < n; p++) a[i * n + p] -= m * a[j * n + p];
            }
        }
    }

    private const double Omega = 0.6;

    /// <summary>One V-cycle on level l: x roughly solves A x = b (x starts at zero).</summary>
    private static void Cycle(List<Level> levels, int l, double[] b, double[] x)
    {
        var L = levels[l];
        if (L.Chol != null)
        {
            int n = L.N; var a = L.Chol;
            for (int i = 0; i < n; i++)
            {
                double t = b[i];
                for (int p = 0; p < i; p++) t -= a[i * n + p] * x[p];
                x[i] = t;
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double t = x[i];
                for (int p = i + 1; p < n; p++) t -= a[i * n + p] * x[p];
                x[i] = t / a[i * n + i];
            }
            return;
        }
        Rows(L.N, (lo, hi) => { for (int i = lo; i < hi; i++) x[i] = Omega * b[i] / L.Diag[i]; });
        Jacobi(L, b, x);
        if (L.Agg == null) { for (int s = 0; s < 20; s++) Jacobi(L, b, x); return; }
        var C = levels[l + 1];
        Residual(L, b, x, L.R);
        Rows(C.N, (lo, hi) =>
        {
            for (int a = lo; a < hi; a++)
            {
                double s = 0;
                for (int p = L.MemberStart![a]; p < L.MemberStart[a + 1]; p++) s += L.R[L.Members![p]];
                C.B[a] = s;
            }
        });
        Cycle(levels, l + 1, C.B, C.X0);
        Rows(L.N, (lo, hi) => { for (int i = lo; i < hi; i++) x[i] += C.X0[L.Agg[i]]; });
        Jacobi(L, b, x);
        Jacobi(L, b, x);
    }

    private static void Jacobi(Level L, double[] b, double[] x)
    {
        Residual(L, b, x, L.T);
        Rows(L.N, (lo, hi) => { for (int i = lo; i < hi; i++) x[i] += Omega * L.T[i] / L.Diag[i]; });
    }

    private static void Residual(Level L, double[] b, double[] x, double[] r)
    {
        Rows(L.N, (lo, hi) =>
        {
            for (int i = lo; i < hi; i++)
            {
                double s = L.Diag[i] * x[i];
                for (int e = L.Start[i]; e < L.Start[i + 1]; e++) s += L.Val[e] * x[L.Col[e]];
                r[i] = b[i] - s;
            }
        });
    }

    private sealed class Work(int n)
    {
        public readonly double[] R = new double[n], Rh = new double[n], P = new double[n], V = new double[n],
                                 Y = new double[n], S = new double[n], Z = new double[n], T = new double[n];
    }

    /// <summary>BiCGStab (the matrix isn't symmetric: water's way across a face depends on which side is higher),
    /// preconditioned by a V-cycle, from x until the residual is a share of what it started at. Returns the steps.</summary>
    private static int BiCgStab(List<Level> levels, double[] b, double[] x, Work w, double shrink, int most = 60)
    {
        var A = levels[0];
        int n = A.N;
        Residual(A, b, x, w.R);
        double start = SumAbs(w.R);
        if (start == 0) return 0;
        Array.Copy(w.R, w.Rh, n);
        Array.Clear(w.P); Array.Clear(w.V);
        double rho = 1, alpha = 1, omega = 1;
        for (int it = 1; it <= most; it++)
        {
            double rho2 = Dot(w.Rh, w.R), beta = rho2 / rho * (alpha / omega);
            rho = rho2;
            Rows(n, (lo, hi) => { for (int i = lo; i < hi; i++) w.P[i] = w.R[i] + beta * (w.P[i] - omega * w.V[i]); });
            Cycle(levels, 0, w.P, w.Y);
            Times(A, w.Y, w.V);
            alpha = rho / Dot(w.Rh, w.V);
            Rows(n, (lo, hi) => { for (int i = lo; i < hi; i++) w.S[i] = w.R[i] - alpha * w.V[i]; });
            if (SumAbs(w.S) < shrink * start)
            {
                Rows(n, (lo, hi) => { for (int i = lo; i < hi; i++) x[i] += alpha * w.Y[i]; });
                return it;
            }
            Cycle(levels, 0, w.S, w.Z);
            Times(A, w.Z, w.T);
            omega = Dot(w.T, w.S) / Dot(w.T, w.T);
            Rows(n, (lo, hi) => { for (int i = lo; i < hi; i++) { x[i] += alpha * w.Y[i] + omega * w.Z[i]; w.R[i] = w.S[i] - omega * w.T[i]; } });
            if (SumAbs(w.R) < shrink * start) return it;
        }
        return most;
    }

    private static void Times(Level L, double[] x, double[] y)
    {
        Rows(L.N, (lo, hi) =>
        {
            for (int i = lo; i < hi; i++)
            {
                double s = L.Diag[i] * x[i];
                for (int e = L.Start[i]; e < L.Start[i + 1]; e++) s += L.Val[e] * x[L.Col[e]];
                y[i] = s;
            }
        });
    }

    private static double Dot(double[] a, double[] b)
    {
        double total = 0; object gate = new();
        Parallel.ForEach(Partitioner.Create(0, a.Length, 16384), () => 0.0, (range, _, acc) =>
        {
            for (int i = range.Item1; i < range.Item2; i++) acc += a[i] * b[i];
            return acc;
        }, acc => { lock (gate) total += acc; });
        return total;
    }

    private static double SumAbs(double[] a)
    {
        double total = 0; object gate = new();
        Parallel.ForEach(Partitioner.Create(0, a.Length, 16384), () => 0.0, (range, _, acc) =>
        {
            for (int i = range.Item1; i < range.Item2; i++) acc += Math.Abs(a[i]);
            return acc;
        }, acc => { lock (gate) total += acc; });
        return total;
    }

    private static void Rows(int n, Action<int, int> body)
    {
        if (n < 16384) { body(0, n); return; }
        Parallel.ForEach(Partitioner.Create(0, n, 16384), range => body(range.Item1, range.Item2));
    }
}
