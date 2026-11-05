using ClearSkies.Game.Ruins;

/// <summary>How good a layout is: how much of its ground it puts rooms on, and how far rooms are to walk to from the
/// entrance (through halls, bridges and cores; a floor down a core counts as FloorPitch blocks).</summary>
public sealed record LayoutMetrics(
    int Rooms, int Reached, long RoomArea, long HallArea, int Bridges, int BridgeCells, double Coverage,
    double MeanWalk, double P90Walk, double MaxWalk, int Districts)
{
    public static LayoutMetrics Of(RuinPlan plan)
    {
        var site = plan.Site;
        int nx = site.Nx, n = nx * site.Nz, floors = site.Floors;
        const float C = RuinSite.Cell;
        // Where you can walk, per floor; and per floor where a core lets you go down.
        var way = new bool[floors * n];
        var shaft = new bool[floors * n];
        int rooms = 0, bridges = 0, bridgeCells = 0;
        long roomArea = 0, hallArea = 0;
        var entrance = new List<int>();
        foreach (var p in plan.Parts)
        {
            if (p.Kind == PartKind.Plot) { rooms++; roomArea += p.Rect.Area * (long)(C * C); continue; }
            for (int f = p.Floor; f <= p.LastFloor; f++)
            for (int j = p.Rect.J0; j < p.Rect.J1; j++)
            for (int i = p.Rect.I0; i < p.Rect.I1; i++)
            {
                if (i < 0 || j < 0 || i >= nx || j >= site.Nz) continue;
                int c = f * n + i + nx * j;
                way[c] = true;
                if (p.Kind == PartKind.Core && f < p.LastFloor) shaft[c] = true;
                if (p.Kind == PartKind.Core && p.Entrance && f == p.Floor) entrance.Add(c);
            }
            if (p.Kind != PartKind.Core) hallArea += p.Rect.Area * (long)(C * C);
            if (p.Kind == PartKind.Bridge) { bridges++; bridgeCells += p.Rect.Width >= p.Rect.Depth ? p.Rect.Width : p.Rect.Depth; }
        }

        // Walking distance, in cells, from the entrance.
        var dist = new float[floors * n];
        Array.Fill(dist, float.MaxValue);
        var q = new PriorityQueue<int, float>();
        foreach (int c in entrance) { dist[c] = 0; q.Enqueue(c, 0); }
        const float Down = RuinSite.FloorPitch / C;
        while (q.TryDequeue(out int c, out float d))
        {
            if (d > dist[c]) continue;
            int f = c / n, cell = c % n, i = cell % nx, j = cell / nx;
            void Go(int e, float cost)
            {
                if (!way[e] || dist[e] <= d + cost) return;
                dist[e] = d + cost; q.Enqueue(e, d + cost);
            }
            if (i > 0) Go(c - 1, 1);
            if (i < nx - 1) Go(c + 1, 1);
            if (j > 0) Go(c - nx, 1);
            if (j < site.Nz - 1) Go(c + nx, 1);
            if (shaft[c]) Go(c + n, Down);
            if (f > 0 && shaft[c - n]) Go(c - n, Down);
        }
        var walks = new List<double>();
        if (Environment.GetEnvironmentVariable("RUINS_DEBUG") != null)
            for (int f = 0; f < floors; f++)
            {
                int all = 0, got = 0;
                foreach (var p in plan.Parts)
                {
                    if (p.Kind != PartKind.Plot || p.Floor != f) continue;
                    all++;
                    int hi2 = p.DoorI + p.DoorDi, hj2 = p.DoorJ + p.DoorDj;
                    if (dist[f * n + hi2 + nx * hj2] < float.MaxValue) got++;
                }
                Console.Error.WriteLine($"  floor {f}: {got}/{all} rooms reached");
            }
        foreach (var p in plan.Parts)
        {
            if (p.Kind != PartKind.Plot) continue;
            int hi = p.DoorI + p.DoorDi, hj = p.DoorJ + p.DoorDj;
            if (hi < 0 || hj < 0 || hi >= nx || hj >= site.Nz) continue;
            float d = dist[p.Floor * n + hi + nx * hj];
            if (d < float.MaxValue) walks.Add(d * C);
        }
        walks.Sort();
        long solid = 0;
        foreach (var g in site.Grounds) foreach (var x in g) if (x == RuinSite.Ground.Solid) solid++;
        double At(double share) => walks.Count == 0 ? 0 : walks[(int)(share * (walks.Count - 1))];
        return new LayoutMetrics(rooms, walks.Count, roomArea, hallArea, bridges, bridgeCells,
            solid == 0 ? 0 : roomArea / (solid * (double)(C * C)),
            walks.Count == 0 ? 0 : walks.Average(), At(0.9), At(1), plan.Uses.Length);
    }
}
