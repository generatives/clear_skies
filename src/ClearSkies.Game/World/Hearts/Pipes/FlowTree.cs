namespace ClearSkies.Game.Generation;

/// <summary>
/// Where a landmass's water goes, from the pipes model's flows (see <see cref="CoarseLandmass.Down"/>): each node's main
/// way out and how much water it carries, handed down that tree from the tops of the land.
/// </summary>
internal static class FlowTree
{
    // (How steep the way over the land's edge counts as, for a way picked by slope: a cliff.)
    private const float EdgeSlope = 4f;

    /// <summary>Works out <paramref name="coarse"/>'s Down, Way and Carried, with the model's rain (blocks a second on the
    /// wettest land) as <paramref name="rain"/>.</summary>
    public static void Build(CoarseLandmass coarse, float rain, ulong seed)
    {
        // Each node's way: the way it sends most (to a neighbouring column), and the node there it sends it to, or -1 if
        // that's a drain: over the land's edge.
        var down = new int[coarse.Nodes];
        var way = new (sbyte X, sbyte Z)[coarse.Nodes];
        float column = rain * coarse.Settings.Size * coarse.Settings.Size;
        for (int n = 0; n < coarse.Nodes; n++)
        {
            down[n] = -1;
            float most = 0;
            for (int k = coarse.LinkStart[n]; k < coarse.LinkStart[n + 1]; k++)
                if (coarse.Flow[k] > most) { most = coarse.Flow[k]; down[n] = coarse.LinkTo[k]; way[n] = (coarse.LinkDx[k], coarse.LinkDz[k]); }
            // Where the model hardly runs any water out (less than a stream's worth: what reaches it soaked in), its way
            // is a way down instead: what little moves there is noise, and a stream handed on would follow it across
            // the slope.
            if (most / column >= coarse.Settings.StreamFlow) continue;
            float surface = coarse.Floor[n] + coarse.Depth[n], total = 0f;
            float Slope(int k) => coarse.LinkTo[k] is var m && m < 0 ? EdgeSlope : (surface - coarse.Floor[m] - coarse.Depth[m]) / coarse.LinkLength[k];
            for (int k = coarse.LinkStart[n]; k < coarse.LinkStart[n + 1]; k++)
                if (Slope(k) is var slope && slope > 0) total += slope * slope;
            if (total <= 0f) continue;
            // (A way down picked at random, weighted by the square of its slope, the same every time: steep ways
            // mostly, but not one straight line down an even slope.)
            float pick = (float)(Hash(seed, n) * total);
            for (int k = coarse.LinkStart[n]; k < coarse.LinkStart[n + 1]; k++)
            {
                float slope = Slope(k);
                if (slope <= 0) continue;
                down[n] = coarse.LinkTo[k]; way[n] = (coarse.LinkDx[k], coarse.LinkDz[k]);
                if ((pick -= slope * slope) <= 0f) break;
            }
        }
        // What each column carries: what the model gains there (rain and springs: what it lets out past what comes in)
        // plus everything handed to it down the tree of main ways. A column hands on once everything handed to it has
        // arrived (up the tree first). Lakes carry too: water going round in one gains nothing, so only what runs in
        // and the rain on it reach the outlet.
        var carried = new float[coarse.Nodes];
        var waiting = new int[coarse.Nodes];
        for (int n = 0; n < coarse.Nodes; n++)
            if (down[n] >= 0) waiting[down[n]]++;
        void HandOn(int n)
        {
            float ins = 0, outs = 0;
            for (int k = coarse.LinkStart[n]; k < coarse.LinkStart[n + 1]; k++)
            {
                float q = coarse.Flow[k] / column;
                if (q > 0) outs += q; else ins -= q;
            }
            carried[n] += Math.Max(0f, outs - ins);
            if (down[n] >= 0) carried[down[n]] += carried[n];
        }
        var ready = new Stack<int>(Enumerable.Range(0, coarse.Nodes).Where(n => waiting[n] == 0));
        while (ready.Count > 0)
        {
            int n = ready.Pop();
            HandOn(n);
            if (down[n] >= 0 && --waiting[down[n]] == 0) ready.Push(down[n]);
        }
        // (Main ways going round in a ring, inside a lake: those hand on highest water first.)
        foreach (int n in Enumerable.Range(0, coarse.Nodes).Where(n => waiting[n] > 0).OrderByDescending(n => coarse.Floor[n] + coarse.Depth[n]))
            HandOn(n);
        coarse.Down = down; coarse.Way = way; coarse.Carried = carried;
    }

    /// <summary>A number in [0, 1) from <paramref name="seed"/> and <paramref name="n"/>, the same every time.</summary>
    private static double Hash(ulong seed, int n)
    {
        ulong h = seed ^ (ulong)n * 0x9E3779B97F4A7C15UL;
        h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
        return (h >> 11) * (1.0 / (1UL << 53));
    }
}
