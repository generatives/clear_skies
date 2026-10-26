namespace ClearSkies.Hydrology;

/// <summary>
/// Surface water routed instantly instead of flowing a cell a pass: rain, and water seeping out of the ground, go
/// straight downhill to where it pools or leaves the land, the way real surface water outruns rain by far.
/// Open cells (mostly air) form gaps: each column's runs of them, the bottom one resting on the ground below (its floor).
/// A priority flood from the gaps water can leave by (into the void beside or under the land) finds where each gap
/// sends its water (its receiver) and how high water must stand in it before it does (its level): over its floor, it's
/// in a hollow, a lake whose spill level that is. That only depends on the land, so it's worked out once.
/// Each pass the slow sim moves ground water, with no flow between open cells (they only trade with the ground), then
/// the routing gathers what each gap gains (rain, springs) or loses (seeping into the ground), adds it up downhill, and
/// sets the water in open cells: lakes stand at their level, rivers by how much flows through them.
/// </summary>
public sealed partial class Settle
{
    private const float Open = 1e9f; // the permeability open cells get when routed: their faces with each other pass nothing

    /// <summary>How much faster than a pass a lake's level moves towards balance (it's only the balance that's wanted).</summary>
    public float LakeRelax { get; set; } = 20f;

    private Routes? _routes;

    private sealed class Routes
    {
        public int Count;
        public int[] Column = null!, Y0 = null!, Y1 = null!, Cell0 = null!;
        public float[] Floor = null!, Level = null!, Q = null!, Net = null!;
        public int[] Receiver = null!, Order = null!, Lake = null!, TopGap = null!;
        public bool[] Drain = null!;
        // Lakes: their spill level, where they spill to (-1: nowhere), the gaps in them, and their level now; per gap
        // the lakes spilling into it (from LakesAt[g] to LakesAt[g + 1] in LakesInto).
        public float[] Spill = null!, Now = null!, In = null!;
        public int[] Outlet = null!, Area = null!, LakesAt = null!, LakesInto = null!;
        public float[] Set = null!; // per cell: the water the routing last put in an open cell
        public double Drained;
    }

    private bool IsOpen(int i) => _land.Solid[i] < 0.5f;

    /// <summary>Routes surface water instantly from now on (see the class notes). Call before UseLocalSteps.</summary>
    public void UseRouting()
    {
        var land = _land;
        var r = new Routes();
        for (int i = 0; i < land.Cells; i++) if (IsOpen(i)) _permeability[i] = Open;

        // Gaps, column by column, bottom up.
        var column = new List<int>(); var y0 = new List<int>(); var y1 = new List<int>(); var cell0 = new List<int>();
        var gapsAt = new int[land.Columns + 1];
        for (int c = 0; c < land.Columns; c++)
        {
            gapsAt[c] = column.Count;
            for (int q = land.RunStart[c]; q < land.RunStart[c + 1]; q++)
                for (int y = land.RunY0[q], i = land.RunCell[q]; y <= land.RunY1[q];)
                {
                    if (!IsOpen(i)) { y++; i++; continue; }
                    column.Add(c); y0.Add(y); cell0.Add(i);
                    for (; y <= land.RunY1[q] && IsOpen(i); y++, i++) { }
                    y1.Add(y - 1);
                }
        }
        gapsAt[land.Columns] = column.Count;
        int n = r.Count = column.Count;
        r.Column = column.ToArray(); r.Y0 = y0.ToArray(); r.Y1 = y1.ToArray(); r.Cell0 = cell0.ToArray();
        r.Floor = new float[n]; r.Drain = new bool[n]; r.TopGap = new int[land.Columns];
        Array.Fill(r.TopGap, -1);
        for (int g = 0; g < n; g++)
        {
            int c = r.Column[g], y = r.Y0[g];
            r.Floor[g] = land.Size * (y + land.Solid[r.Cell0[g]]);
            // Under it: ground (a solid cell, or inert stone), or the void (the column's bottom).
            r.Drain[g] = y == land.Lo[c] && land.Solid[r.Cell0[g]] == 0f;
            if (r.Y1[g] == land.Hi[c]) r.TopGap[c] = g;
        }

        // Neighbouring gaps (whose cells are beside each other): water crosses at the higher floor. Beside the void
        // (no land there, or under it), water leaves at its own floor.
        var edgeAt = new int[n + 1]; var edgeTo = new List<int>();
        var voidPass = new float[n];
        Array.Fill(voidPass, float.PositiveInfinity);
        int nx = land.Nx, nz = land.Nz;
        Span<int> around = stackalloc int[4]; // once, not per pass: a stackalloc in a loop keeps growing the stack
        for (int g = 0; g < n; g++)
        {
            edgeAt[g] = edgeTo.Count;
            int c = r.Column[g], ci = c % nx, cj = c / nx;
            int top = r.Y1[g] == land.Hi[c] ? int.MaxValue : r.Y1[g]; // a top gap reaches up into the open sky
            around[0] = ci + 1 < nx ? c + 1 : -1;
            around[1] = ci > 0 ? c - 1 : -1;
            around[2] = cj + 1 < nz ? c + nx : -1;
            around[3] = cj > 0 ? c - nx : -1;
            foreach (int nc in around)
            {
                if (nc < 0 || !land.HasLand(nc) || r.Y0[g] < land.Lo[nc]) { voidPass[g] = r.Floor[g]; continue; }
                for (int h = gapsAt[nc]; h < gapsAt[nc + 1]; h++)
                {
                    int htop = r.Y1[h] == land.Hi[nc] ? int.MaxValue : r.Y1[h];
                    if (r.Y0[h] <= top && r.Y0[g] <= htop) edgeTo.Add(h);
                }
            }
        }
        edgeAt[n] = edgeTo.Count;
        var to = edgeTo.ToArray();

        // The priority flood, in from where water leaves.
        r.Level = new float[n]; r.Receiver = new int[n];
        Array.Fill(r.Level, float.NaN); Array.Fill(r.Receiver, -1);
        var queue = new PriorityQueue<int, float>();
        for (int g = 0; g < n; g++)
        {
            float leave = r.Drain[g] ? r.Floor[g] : voidPass[g];
            if (float.IsPositiveInfinity(leave)) continue;
            r.Level[g] = leave; r.Drain[g] = true;
            queue.Enqueue(g, leave);
        }
        var order = new List<int>(n);
        while (queue.TryDequeue(out int g, out float level))
        {
            order.Add(g);
            for (int e = edgeAt[g]; e < edgeAt[g + 1]; e++)
            {
                int h = to[e];
                if (!float.IsNaN(r.Level[h])) continue;
                r.Level[h] = MathF.Max(level, MathF.Max(r.Floor[g], r.Floor[h]));
                r.Receiver[h] = g;
                queue.Enqueue(h, r.Level[h]);
            }
        }
        // Gaps water can't leave (sealed hollows): each a lake to its own top.
        var sealedGaps = new List<int>();
        for (int g = 0; g < n; g++)
            if (float.IsNaN(r.Level[g])) { r.Level[g] = land.Size * (r.Y1[g] + 1f); order.Add(g); sealedGaps.Add(g); }
        r.Order = order.ToArray();

        // Lakes: gaps whose level stands over their floor, one lake per spill (where it spills to, and how high).
        r.Lake = new int[n];
        Array.Fill(r.Lake, -1);
        var spill = new List<float>(); var outlet = new List<int>(); var area = new List<int>();
        foreach (int g in r.Order)
        {
            bool sealedGap = r.Receiver[g] < 0 && !r.Drain[g];
            if (!sealedGap && r.Level[g] <= r.Floor[g] + 0.01f) continue;
            int up = r.Receiver[g];
            if (up >= 0 && r.Lake[up] >= 0 && spill[r.Lake[up]] == r.Level[g]) r.Lake[g] = r.Lake[up];
            else { r.Lake[g] = spill.Count; spill.Add(r.Level[g]); outlet.Add(up); area.Add(0); }
            area[r.Lake[g]]++;
        }
        r.Spill = spill.ToArray(); r.Outlet = outlet.ToArray(); r.Area = area.ToArray();
        // Lakes start full, but sealed hollows (caves, mostly) empty: only the ground can fill them.
        r.Now = (float[])r.Spill.Clone();
        foreach (int g in sealedGaps) r.Now[r.Lake[g]] = MathF.Min(r.Now[r.Lake[g]], r.Floor[g]);
        r.In = new float[r.Spill.Length];
        r.LakesAt = new int[n + 1];
        foreach (int o in r.Outlet) if (o >= 0) r.LakesAt[o + 1]++;
        for (int g = 0; g < n; g++) r.LakesAt[g + 1] += r.LakesAt[g];
        r.LakesInto = new int[r.Outlet.Length];
        var fill = (int[])r.LakesAt.Clone();
        for (int k = 0; k < r.Outlet.Length; k++) if (r.Outlet[k] >= 0) r.LakesInto[fill[r.Outlet[k]]++] = k;

        r.Q = new float[n]; r.Net = new float[n]; r.Set = new float[land.Cells];
        _routes = r;
        Place(r);
        Console.WriteLine($"Routing: {n:N0} gaps, {r.Order.Length - sealedGaps.Count:N0} reach an edge, " +
                          $"{r.Spill.Length:N0} lakes ({sealedGaps.Count:N0} sealed hollows)");
    }

    /// <summary>After a pass of the slow sim: what each gap gained or lost, added up downhill, and the open cells' water
    /// set from it.</summary>
    private void Route()
    {
        var r = _routes!;
        var land = _land;
        // What each gap's cells traded with the ground since they were set, and the rain on each column's top gap.
        Parallel.For(0, r.Count, g =>
        {
            float net = 0f;
            for (int y = r.Y0[g], i = r.Cell0[g]; y <= r.Y1[g]; y++, i++) net += _water[i] - r.Set[i];
            r.Net[g] = net;
        });
        foreach (int g in r.TopGap) if (g >= 0) r.Net[g] += Rain;
        Array.Clear(r.Q); Array.Clear(r.In);
        double drained = 0;
        // Downhill: every gap after the ones that send it water.
        for (int k = r.Order.Length - 1; k >= 0; k--)
        {
            int g = r.Order[k];
            for (int l = r.LakesAt[g]; l < r.LakesAt[g + 1]; l++) r.Q[g] += Settle1(r, r.LakesInto[l]);
            float q = r.Q[g] + r.Net[g];
            r.Q[g] = MathF.Max(q, 0f);
            int lake = r.Lake[g], up = r.Receiver[g];
            if (lake >= 0 && (up < 0 || r.Lake[up] != lake)) r.In[lake] += q;
            else if (lake >= 0) r.Q[up] += q; // within a lake, seepage counts against it too
            else if (up >= 0) r.Q[up] += r.Q[g];
            else drained += r.Q[g];
        }
        for (int lake = 0; lake < r.Spill.Length; lake++) if (r.Outlet[lake] < 0) Settle1(r, lake);
        r.Drained = drained;
        Place(r);
    }

    /// <summary>A lake's level moves towards where what flows in balances what seeps out; full, it passes on the rest.
    /// Returns what it passes on.</summary>
    private float Settle1(Routes r, int lake)
    {
        float inflow = r.In[lake];
        if (r.Now[lake] >= r.Spill[lake] && inflow >= 0f) return r.Outlet[lake] >= 0 ? inflow : 0f;
        r.Now[lake] = MathF.Min(r.Spill[lake], r.Now[lake] + LakeRelax * inflow * _land.Size / r.Area[lake]);
        return 0f;
    }

    /// <summary>Sets the open cells' water: lakes to their level, rivers by what flows through them.</summary>
    private void Place(Routes r)
    {
        var land = _land;
        float riverAt = 50f * Rain, riverFor = 1000f * Rain;
        Parallel.For(0, r.Count, g =>
        {
            int lake = r.Lake[g];
            float level = lake >= 0 ? r.Now[lake]
                        : r.Q[g] > riverAt ? r.Floor[g] + MathF.Min(4f * land.Size, 6f * MathF.Pow(r.Q[g] / riverFor, 0.4f))
                        : float.NegativeInfinity;
            for (int y = r.Y0[g], i = r.Cell0[g]; y <= r.Y1[g]; y++, i++)
            {
                float w = level > land.Size * y ? _capacity[i] * MathF.Min(1f, (level - land.Size * y) * PerBlock) : 0f;
                _water[i] = r.Set[i] = w;
            }
        });
    }
}
