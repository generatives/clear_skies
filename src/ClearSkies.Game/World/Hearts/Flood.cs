using ClearSkies.Engine.Generation;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Where water settles on a landmass under simple local rules, the same for every cell (see <see cref="Hydrology"/>),
/// found directly instead of stepped (Declan, 2026-10-29: fast and simple over realistic). Water moves from a cell to
/// a neighbour where it stands lower, as freely as their face lets it: freely between open cells (so standing water
/// is flat, and fills until it overflows at its lowest rim), Soak (columns' rain) through a face between open air and
/// ground that isn't full, and through ground only if it's SeepSlope lower a cell further, more the more passes. So in
/// ground it stands higher the further it is from a way out, and comes out where it reaches open air (a spring), or
/// out of ground full to its top. Rain falls on each column; open water loses Evaporation a column of surface; the
/// void takes whatever reaches it. Most of the stone is watertight (it passes nothing, as if it weren't there): all of it
/// more than Permeable cells under its column's top, and the rim facing the void but for stretches of it (RimLeak), so
/// ground water mostly comes out in springs on the land, and the flood has far fewer cells to cover.
/// Two priority floods in from where water leaves the land (cells beside the void), one through open cells only (as
/// free as they are, they carry all the water ground can't) and one through ground too, give each cell the lowest
/// level water there must reach to get out, and the cell it goes on to (on flats: the nearest way out, wandering by
/// noise). Then,
/// highest level first, running water is added up downhill, soaking in as it goes, and each lake takes what runs into
/// it: at its spill level if that's more than it loses, the rest running on, otherwise as high as it can keep up with;
/// what soaked in goes through the ground to its springs, and the next of a few turns runs on from those.
/// </summary>
public sealed class Flood
{
    public double SeepSlope { get; set; } = 0.02;
    public double Evaporation { get; set; } = 2;
    /// <summary>How much water (columns' rain) passes from an open cell into a ground cell beside it, through their
    /// face, where the ground isn't full.</summary>
    public double Soak { get; set; } = 0.25;
    /// <summary>How much seeping water (columns' rain) a cell of ground passes on for each block it drops a block.</summary>
    public double SeepEase { get; set; } = 8000;
    public int Turns { get; set; } = 8;
    /// <summary>How many cells of ground under a column's top pass water: under that the stone is watertight.</summary>
    public int Permeable { get; set; } = 3;
    /// <summary>What share of a piece's rim (ground facing the void) passes water: the rest is watertight.</summary>
    public double RimLeak { get; set; } = 0.1;
    /// <summary>Rain on a column (columns' rain) by its middle block (x, z); a full column's everywhere if not given.</summary>
    public Func<int, int, double>? Rain { get; set; }
    public bool[] Soaked = null!;
    public double[] GroundFlow = null!;

    private readonly CoarseLand _land;
    public double[] Level = null!, OpenLevel = null!, Flow = null!, Floor = null!;
    public int[] Receiver = null!, Lake = null!, Column = null!, Y = null!;
    public double[] LakeLevel = null!;
    public int[] Order = null!;

    /// <summary>On flat ground water heads for the nearest way out, wandering by noise up to this many cells.</summary>
    public double Wander { get; set; } = 20;
    private readonly int _seed;

    public Flood(CoarseLand land, int seed) { _land = land; _seed = seed; }

    public bool IsOpen(int i) => _land.Solid[i] < 0.5f;

    /// <summary>Ground water can't pass at all (as if it weren't there).</summary>
    public bool[] Tight = null!;

    public void Run()
    {
        var land = _land;
        int n = land.Cells, nx = land.Nx, nz = land.Nz, S = land.Size;
        Column = new int[n]; Y = new int[n]; Floor = new double[n];
        for (int c = 0; c < land.Columns; c++)
            for (int r = land.RunStart[c]; r < land.RunStart[c + 1]; r++)
                for (int y = land.RunY0[r], i = land.RunCell[r]; y <= land.RunY1[r]; y++, i++) { Column[i] = c; Y[i] = y; }
        // Water stands in an open cell over the ground in it; in ground, from the cell's bottom (it fills the pores).
        for (int i = 0; i < n; i++) Floor[i] = S * (Y[i] + (IsOpen(i) ? land.Solid[i] : 0f));

        // A cell's neighbours (-1: none, rock or kept-out stone; -2: the void). Sideways over a lower column's top is
        // that column's top cell (water there falls onto it).
        int Side(int c, int nc, int y)
        {
            if (nc < 0 || !land.HasLand(nc) || land.Piece[nc] != land.Piece[c] || y < land.Lo[nc]) return -2;
            if (y > land.Hi[nc]) return land.Index(nc, land.Hi[nc]);
            return land.Index(nc, y);
        }
        int Neighbour(int i, int k)
        {
            int c = Column[i], y = Y[i], ci = c % nx, cj = c / nx;
            switch (k)
            {
                case 0: return y - 1 < land.Lo[c] ? -2 : land.Index(c, y - 1);
                case 1: return y + 1 > land.Hi[c] ? -1 : land.Index(c, y + 1);
                case 2: return Side(c, ci + 1 < nx ? c + 1 : -1, y);
                case 3: return Side(c, ci > 0 ? c - 1 : -1, y);
                case 4: return Side(c, cj + 1 < nz ? c + nx : -1, y);
                default: return Side(c, cj > 0 ? c - nx : -1, y);
            }
        }

        var noise = new FastNoiseLite(_seed + 91);
        noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        noise.SetFractalType(FastNoiseLite.FractalType.FBm);
        noise.SetFractalOctaves(2);
        noise.SetFrequency(1f / 200f);
        var wander = new double[land.Columns];
        for (int c = 0; c < land.Columns; c++)
            wander[c] = Wander * 0.5 * (1 + noise.GetNoise(land.X0 + c % nx * S, land.Z0 + c / nx * S));
        // Watertight stone: deep under each column's top, and most of the rim (a stretch of it leaks, by noise).
        var leak = new FastNoiseLite(_seed + 92);
        leak.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        leak.SetFrequency(1f / 120f);
        double leakFrom = 1 - 2 * RimLeak; // (noise roughly even over -1..1)
        Tight = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (IsOpen(i)) continue;
            int c = Column[i];
            if (land.Top[c] - Y[i] >= Permeable) { Tight[i] = true; continue; }
            bool rim = false;
            for (int k = 0; k < 6 && !rim; k++) rim = k != 1 && Neighbour(i, k) == -2;
            Tight[i] = rim && leak.GetNoise(land.X0 + c % nx * S, Y[i] * S, land.Z0 + c / nx * S) < leakFrom;
        }

        // Two floods in from where water leaves: through open cells only (where running and standing water goes),
        // and through ground too (where soaked-in water goes, and how high it stands in the ground).
        (double[] level, int[] recv, int[] order) Spread(bool ground)
        {
            var level = new double[n]; var recv = new int[n];
            Array.Fill(level, double.NaN); Array.Fill(recv, -1);
            // Ties (flat ground, standing water) go to the cell fewest steps on from where the level was last
            // reached, give or take the noise: water on a flat heads for its way out, wandering a little.
            var steps = new int[n];
            var queue = new PriorityQueue<int, (double Level, double Steps)>();
            for (int i = 0; i < n; i++)
            {
                if (!ground && !IsOpen(i) || Tight[i]) continue;
                for (int k = 0; k < 6; k++)
                    if (k != 1 && Neighbour(i, k) == -2) { level[i] = Floor[i]; queue.Enqueue(i, (Floor[i], wander[Column[i]])); break; }
            }
            var order = new List<int>(n);
            var done = new bool[n];
            double seep = SeepSlope * S;
            while (queue.TryDequeue(out int i, out var key))
            {
                if (done[i]) continue; // queued again lower since
                done[i] = true;
                double at = key.Level;
                order.Add(i);
                for (int k = 0; k < 6; k++)
                {
                    int d = Neighbour(i, k);
                    if (d < 0 || done[d] || !ground && !IsOpen(d) || Tight[d]) continue;
                    double l = Math.Max(at + (IsOpen(d) ? 0 : seep), Floor[d]);
                    if (!double.IsNaN(level[d]) && level[d] <= l) continue;
                    level[d] = l; recv[d] = i;
                    steps[d] = l == at ? steps[i] + 1 : 0;
                    queue.Enqueue(d, (l, steps[d] + wander[Column[d]]));
                }
            }
            return (level, recv, order.ToArray());
        }
        // (The two at once: neither changes what the other reads.)
        (double[] level, int[] recv, int[] order) groundFlood = default, openFlood = default;
        Parallel.Invoke(() => groundFlood = Spread(true), () => openFlood = Spread(false));
        var (levelG, recvG, orderG) = groundFlood;
        var (levelA, recvA, orderA) = openFlood;
        Level = levelG; OpenLevel = levelA; Receiver = recvA; Order = orderA;

        // Per column: the open cell its rain lands in (just over the ground), and its top ground cell.
        var surface = new int[land.Columns]; var under = new int[land.Columns];
        Array.Fill(surface, -1); Array.Fill(under, -1);
        for (int c = 0; c < land.Columns; c++)
        {
            if (!land.HasLand(c)) continue;
            int t = land.Index(c, land.Top[c]);
            if (t < 0) continue;
            if (IsOpen(t)) { surface[c] = t; under[c] = land.Top[c] > land.Lo[c] ? land.Index(c, land.Top[c] - 1) : -1; }
            else { under[c] = t; surface[c] = land.Top[c] < land.Hi[c] ? land.Index(c, land.Top[c] + 1) : -1; }
            if (under[c] >= 0 && IsOpen(under[c])) under[c] = -1;
        }

        var rain = new double[land.Columns];
        for (int c = 0; c < land.Columns; c++)
            if (land.HasLand(c)) rain[c] = Rain?.Invoke(land.X0 + c % nx * S + S / 2, land.Z0 + c / nx * S + S / 2) ?? 1;

        // Each cell's faces where water passes between open air and ground: an open cell's ground neighbours, and a
        // ground cell's open neighbour it lets out to when it's full (the one over it if open, else beside it, else
        // under it). Ground is full when the water in it stands at its top.
        var groundFaces = new int[n][];
        var outlet = new int[n];
        Array.Fill(outlet, -1);
        var faces = new List<int>(6);
        for (int i = 0; i < n; i++)
        {
            faces.Clear();
            foreach (int k in new[] { 1, 2, 3, 4, 5, 0 })
            {
                int d = Neighbour(i, k);
                if (d < 0 || IsOpen(d) == IsOpen(i) || Tight[d] || Tight[i]) continue;
                if (IsOpen(i)) faces.Add(d);
                else if (outlet[i] < 0) outlet[i] = d;
            }
            groundFaces[i] = faces.Count == 0 ? Array.Empty<int>() : faces.ToArray();
        }
        double CellTop(int g) => S * (Y[g] + land.Solid[g]);
        var full = new bool[n];
        void Fill()
        {
            for (int i = 0; i < n; i++) full[i] = !IsOpen(i) && levelG[i] >= CellTop(i) - 0.5;
        }
        Fill();

        // Lakes: open cells under their level, joined with the one they go on to when that's the same lake (as level).
        Lake = new int[n];
        Array.Fill(Lake, -1);
        var members = new List<List<int>>();
        foreach (int i in orderA)
        {
            if (!(levelA[i] > Floor[i] + 0.01)) continue;
            int to = recvA[i];
            if (to >= 0 && Lake[to] >= 0 && Math.Abs(levelA[to] - levelA[i]) < 1e-6) Lake[i] = Lake[to];
            else { Lake[i] = members.Count; members.Add(new List<int>()); }
            members[Lake[i]].Add(i);
        }
        // A lake's columns, lowest floor first: each with that floor and its cells in the lake, which fill as the lake
        // rises over it.
        var lakeFloors = new double[members.Count][]; var lakeCells = new int[members.Count][][];
        for (int l = 0; l < members.Count; l++)
        {
            var byColumn = members[l].GroupBy(m => Column[m]).Select(g => (Floor: g.Min(m => Floor[m]), Cells: g.ToArray()))
                                     .OrderBy(g => g.Floor).ToArray();
            lakeFloors[l] = byColumn.Select(g => g.Floor).ToArray();
            lakeCells[l] = byColumn.Select(g => g.Cells).ToArray();
        }

        // In turns: running water downhill (each open cell soaking Soak into each of its ground faces that isn't full,
        // a lake losing Evaporation a column and Soak a face), then soaked-in water through the ground to where it
        // comes out into open cells (springs, and full ground letting out what more reaches it), which the next turn
        // runs on from.
        Flow = new double[n];
        var springs = new double[n]; var soaked = new double[n]; var ground = new double[n];
        LakeLevel = new double[members.Count];
        var seeping = new double[n]; // what seeps through each cell of ground, eased in over the turns
        for (int turn = 0; turn < Turns; turn++)
        {
            Array.Clear(Flow); Array.Clear(soaked);
            for (int c = 0; c < land.Columns; c++) if (surface[c] >= 0) Flow[surface[c]] += rain[c];
            for (int i = 0; i < n; i++) Flow[i] += springs[i];
            for (int k = orderA.Length - 1; k >= 0; k--)
            {
                int i = orderA[k], to = recvA[i], lake = Lake[i];
                if (lake < 0)
                    foreach (int g in groundFaces[i])
                    {
                        if (full[g] || Flow[i] <= 0) continue;
                        double take = Math.Min(Flow[i], Soak);
                        soaked[g] += take; Flow[i] -= take;
                    }
                if (lake >= 0 && (to < 0 || Lake[to] != lake))
                {
                    // It fills its columns lowest first, as far as what runs in keeps up with what they lose: to its
                    // spill level with the rest running on, or to the floor of the first it can't keep.
                    var floors = lakeFloors[lake];
                    double inflow = Flow[i], loses = 0;
                    int keep = 0;
                    for (; keep < floors.Length; keep++)
                    {
                        double column = Evaporation;
                        foreach (int m in lakeCells[lake][keep])
                            foreach (int g in groundFaces[m]) if (!full[g]) column += Soak;
                        if (loses + column > inflow) break;
                        loses += column;
                        foreach (int m in lakeCells[lake][keep])
                            foreach (int g in groundFaces[m]) if (!full[g]) soaked[g] += Soak;
                    }
                    LakeLevel[lake] = keep == floors.Length ? levelA[i] : Math.Min(levelA[i], floors[keep]);
                    Flow[i] = keep == floors.Length ? inflow - loses : 0;
                }
                if (to >= 0) Flow[to] += Flow[i];
            }
            // Through the ground: on to where it first reaches an open cell, or out of full ground.
            Array.Copy(soaked, ground, n);
            Array.Clear(springs);
            for (int k = orderG.Length - 1; k >= 0; k--)
            {
                int i = orderG[k];
                if (IsOpen(i) || ground[i] == 0) continue;
                int to = recvG[i];
                if (full[i] && outlet[i] >= 0) { springs[outlet[i]] += ground[i]; continue; }
                if (to < 0) continue;
                if (IsOpen(to)) springs[to] += ground[i];
                else ground[to] += ground[i];
            }
            // The more seeps through ground, the steeper it must drop to pass it on: water stands higher in ground
            // carrying more. Eased in half a turn at a time, or the ground would flip between full and not. Ground
            // that can let water out stands no higher than its top.
            for (int k = 0; k < orderG.Length; k++)
            {
                int i = orderG[k], to = recvG[i];
                seeping[i] = turn == 0 ? ground[i] : 0.5 * (seeping[i] + ground[i]);
                if (to < 0) continue;
                double drop = IsOpen(i) ? 0 : S * (SeepSlope + seeping[i] / SeepEase);
                levelG[i] = Math.Max(levelG[to] + drop, Floor[i]);
                if (!IsOpen(i) && outlet[i] >= 0) levelG[i] = Math.Min(levelG[i], Math.Max(CellTop(i), Floor[i]));
            }
            Fill();
        }
        Soaked = new bool[land.Columns];
        for (int c = 0; c < land.Columns; c++) Soaked[c] = under[c] >= 0 && full[under[c]];
        GroundFlow = ground;
    }
}
