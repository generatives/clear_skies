using ClearSkies.Engine.Generation;

namespace ClearSkies.Game.Generation;

/// <summary>
/// Where water settles on a landmass under simple local rules (see <see cref="Hydrology"/>), found directly instead of stepped (Declan, 2026-10-29: fast and
/// simple over realistic). Water falls into the cell below if it can, spreads sideways to where it stands lower, and a
/// full cell pushes what more comes to it on to its lowest neighbour with room, up if need be: standing water is flat,
/// and fills until it overflows at its lowest rim. Ground soaks up to Soak (columns' rain) of what runs over it, unless
/// it's soaked to the top already; in ground water only seeps on if it's SeepSlope lower a block further, so it stands
/// higher the further it is from a way out, and comes out where it first reaches an open cell (a spring). Open water
/// loses Evaporation a column of surface; every land column gets a column's rain.
/// Two priority floods in from where water leaves the land (open cells beside or over the void; ground facing the
/// void is sealed), one through open cells only and one through ground too, give each cell the lowest level water
/// there must reach to get out, and the cell it goes on to (on flats: the nearest way out, wandering by noise). Then,
/// highest level first, running water is added up downhill, soaking in as it goes, and each lake takes what runs into
/// it: at its spill level if that's more than it loses, the rest running on, otherwise as high as it can keep up with;
/// what soaked in goes through the ground to its springs, and the next of a few turns runs on from those.
/// </summary>
public sealed class Flood
{
    public double SeepSlope { get; set; } = 0.02;
    public double Evaporation { get; set; } = 2;
    /// <summary>How much a column's ground soaks up (columns' rain) where it isn't soaked already.</summary>
    public double Soak { get; set; } = 0.5;
    /// <summary>How many cells under its column's top ground still trades water sideways with open air.</summary>
    public int SoilDepth { get; set; } = 1;
    /// <summary>How much seeping water (columns' rain) a cell of ground passes on for each block it drops a block.</summary>
    public double SeepEase { get; set; } = 8000;
    public int Turns { get; set; } = 8;
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

        // Ground only trades water with open cells over it, or beside it near its column's top (its soil): ground
        // facing open air under or beside it deeper down (overhangs, cave roofs, cliffs) is sealed, as it faces the
        // void, so water in the ground comes out of the land's top.
        bool Sealed(int i, int d, int k)
        {
            if (IsOpen(i) == IsOpen(d)) return false;
            int g = IsOpen(i) ? d : i;
            bool openAbove = IsOpen(i) ? k == 0 : k == 1; // the open one is over the ground one
            return !openAbove && (k < 2 || land.Top[Column[g]] - Y[g] > SoilDepth);
        }
        var noise = new FastNoiseLite(_seed + 91);
        noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        noise.SetFractalType(FastNoiseLite.FractalType.FBm);
        noise.SetFractalOctaves(2);
        noise.SetFrequency(1f / 200f);
        var wander = new double[land.Columns];
        for (int c = 0; c < land.Columns; c++)
            wander[c] = Wander * 0.5 * (1 + noise.GetNoise(land.X0 + c % nx * S, land.Z0 + c / nx * S));
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
                if (!IsOpen(i)) continue; // ground beside the void is sealed
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
                    if (d < 0 || done[d] || !ground && !IsOpen(d) || Sealed(i, d, k)) continue;
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

        // Per column: the open cell its rain lands in (just over the ground), and the ground cell under that.
        var surface = new int[land.Columns]; var under = new int[land.Columns]; var top = new double[land.Columns];
        Array.Fill(surface, -1); Array.Fill(under, -1);
        for (int c = 0; c < land.Columns; c++)
        {
            if (!land.HasLand(c)) continue;
            int t = land.Index(c, land.Top[c]);
            if (t < 0) continue;
            if (IsOpen(t)) { surface[c] = t; under[c] = land.Top[c] > land.Lo[c] ? land.Index(c, land.Top[c] - 1) : -1; }
            else { under[c] = t; surface[c] = land.Top[c] < land.Hi[c] ? land.Index(c, land.Top[c] + 1) : -1; }
            if (under[c] >= 0 && IsOpen(under[c])) under[c] = -1;
            top[c] = S * (land.Top[c] + land.Solid[t]);
        }

        var rain = new double[land.Columns];
        for (int c = 0; c < land.Columns; c++)
            if (land.HasLand(c)) rain[c] = Rain?.Invoke(land.X0 + c % nx * S + S / 2, land.Z0 + c / nx * S + S / 2) ?? 1;

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
        // A lake's surface: each column once, at its lowest floor in it.
        var lakeFloors = new double[members.Count][]; var lakeColumns = new int[members.Count][];
        for (int l = 0; l < members.Count; l++)
        {
            var floors = new Dictionary<int, double>();
            foreach (int m in members[l])
                floors[Column[m]] = floors.TryGetValue(Column[m], out double f) ? Math.Min(f, Floor[m]) : Floor[m];
            lakeFloors[l] = floors.Values.OrderBy(v => v).ToArray();
            lakeColumns[l] = floors.Keys.ToArray();
        }
        // Where what a lake's bed takes goes: into the ground under its lowest cell.
        var lakeBed = new int[members.Count];
        for (int l = 0; l < members.Count; l++)
        {
            int deepest = members[l].OrderBy(m => Floor[m]).First();
            lakeBed[l] = Y[deepest] > land.Lo[Column[deepest]] ? land.Index(Column[deepest], Y[deepest] - 1) : -1;
        }

        // Where the water in the ground stands at the ground's top (soaked: it takes no more), what each open cell
        // soaks into (-1: none, or soaked already), and how many of each lake's columns soak in.
        Soaked = new bool[land.Columns];
        var soakCell = new int[n]; var lakeSoaks = new int[members.Count];
        void Wet()
        {
            Array.Fill(soakCell, -1);
            for (int c = 0; c < land.Columns; c++)
            {
                Soaked[c] = under[c] >= 0 && levelG[under[c]] >= top[c] - 0.5;
                if (surface[c] >= 0 && under[c] >= 0 && !Soaked[c]) soakCell[surface[c]] = under[c];
            }
            for (int l = 0; l < members.Count; l++) lakeSoaks[l] = lakeColumns[l].Count(c => !Soaked[c] && under[c] >= 0);
        }
        Wet();

        // In turns: running water downhill (soaking in up to Soak a column as it goes, lakes losing Evaporation and
        // Soak a column), then soaked-in water through the ground to where it comes out into open cells (springs),
        // which the next turn runs on from.
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
                if (lake < 0 && soakCell[i] >= 0)
                {
                    double take = Math.Min(Flow[i], Soak);
                    soaked[soakCell[i]] += take; Flow[i] -= take;
                }
                if (lake >= 0 && (to < 0 || Lake[to] != lake))
                {
                    var floors = lakeFloors[lake];
                    double inflow = Flow[i], perColumn = Evaporation + Soak * lakeSoaks[lake] / floors.Length;
                    double loses = perColumn * floors.Length;
                    if (loses <= inflow) { LakeLevel[lake] = levelA[i]; Flow[i] = inflow - loses; }
                    else
                    {
                        int keep = (int)(inflow / perColumn);
                        LakeLevel[lake] = keep == 0 ? floors[0] : Math.Min(levelA[i], floors[Math.Min(keep, floors.Length - 1)]);
                        loses = inflow; Flow[i] = 0;
                    }
                    double intoGround = loses * (Soak * lakeSoaks[lake] / floors.Length) / perColumn;
                    int bed = lakeBed[lake];
                    if (bed >= 0 && !IsOpen(bed)) soaked[bed] += intoGround;
                }
                if (to >= 0) Flow[to] += Flow[i];
            }
            // Through the ground: on to where it first reaches an open cell.
            Array.Copy(soaked, ground, n);
            Array.Clear(springs);
            for (int k = orderG.Length - 1; k >= 0; k--)
            {
                int i = orderG[k];
                if (IsOpen(i) || ground[i] == 0) continue;
                int to = recvG[i], c = Column[i];
                if (to < 0) continue;
                // Ground soaked to its top lets out what more comes to it, over it (a spring).
                if (Soaked[c] && under[c] == i && surface[c] >= 0) { springs[surface[c]] += ground[i]; continue; }
                if (IsOpen(to)) springs[to] += ground[i];
                else ground[to] += ground[i];
            }
            // The more seeps through ground, the steeper it must drop to pass it on: water stands higher in ground
            // carrying more. Eased in half a turn at a time, or the ground would flip between soaked and not.
            for (int k = 0; k < orderG.Length; k++)
            {
                int i = orderG[k], to = recvG[i];
                seeping[i] = turn == 0 ? ground[i] : 0.5 * (seeping[i] + ground[i]);
                if (to < 0) continue;
                double drop = IsOpen(i) ? 0 : S * (SeepSlope + seeping[i] / SeepEase);
                levelG[i] = Math.Max(levelG[to] + drop, Floor[i]);
                int c = Column[i];
                if (Soaked[c] && under[c] == i) levelG[i] = Math.Min(levelG[i], Math.Max(top[c], Floor[i]));
            }
            Wet();
        }
        GroundFlow = ground;
    }
}
