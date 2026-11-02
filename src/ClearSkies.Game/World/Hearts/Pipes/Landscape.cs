using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using ClearSkies.Engine.Generation;

namespace ClearSkies.Game.Generation;

/// <summary>
/// How the land is worn and where water stands on it, one landmass (see <see cref="HeartGrid.Landmass"/>) at a time:
/// the landmass is sampled in 8-block columns (see <see cref="Land"/>), rained on and worn by the pipes model (see
/// <see cref="Pipes"/>) for <see cref="LandscapeSettings.ErodeHours"/>, its hollows filled to where they spill, and its
/// water let run <see cref="LandscapeSettings.SettleMinutes"/> more. Worked out whole the first time any of it is
/// asked for (tens of seconds) and kept, so every chunk of it agrees. HeartWorldGenerator moves its ground by the
/// wear (see <see cref="Shift"/>) and fills water to the level the model left (see <see cref="WaterLevel"/>).
/// </summary>
public sealed class Landscape(ulong seed, LandscapeSettings settings)
{
    public ulong Seed => seed;
    public LandscapeSettings Settings => settings;

    /// <summary>How far (blocks) a stream's line bends off a column's middle at most, and how quickly (per column) that
    /// changes from column to column: its meanders.</summary>
    private const float Meander = 4f, MeanderFrequency = 0.35f;
    // (How much of its meander a stream keeps along a hillside, and how much its width varies, see Shape.)
    private const float Hillside = 0.3f, Widen = 0.4f;
    // (How steep the way over the land's edge counts as, for a way picked by slope, see Shape: a cliff.)
    private const float EdgeSlope = 4f;

    private readonly ConcurrentDictionary<(int X, int Z), Lazy<Shaped>> _landmasses = new();

    /// <summary>The landmass (x, z) is on, worked out if it hasn't been.</summary>
    public Shaped At(float x, float z)
    {
        var key = HeartGrid.Landmass(seed, x, z);
        return _landmasses.GetOrAdd(key, k => new Lazy<Shaped>(() => Shape(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private Shaped Shape((int X, int Z) landmass)
    {
        var sw = Stopwatch.StartNew();
        var (x0, z0, x1, z1) = Bounds(seed, landmass);
        var land = new Land(seed, landmass, x0, z0, x1, z1, settings.Size);
        var start = (float[])land.Floor.Clone();
        var pipes = new Pipes(land);
        foreach (var (name, value) in settings.Pipes) Pipes.Set(pipes, name, value);
        if (settings.Groundwater) pipes.UseGroundwater();
        pipes.FillHollows();
        if (settings.ErodeHours > 0)
        {
            pipes.StartEroding(settings.ErodeHours * 3600);
            while (pipes.Time < settings.ErodeHours * 3600) pipes.Step();
            pipes.Eroding = false;
        }
        pipes.Rain *= settings.SettleRain;
        pipes.FillHollows(); // lakes start at their new levels: outlets were cut, and the rain changed
        double until = pipes.Time + settings.SettleMinutes * 60;
        while (pipes.Time < until) pipes.Step();
        Console.WriteLine($"Landscape: landmass {landmass} worn and watered in {sw.Elapsed.TotalSeconds:0.0} s ({pipes.Steps:N0} steps)");
        // Each node's stream: how much rain runs through it (in columns' worth), the way it sends most (to a neighbouring
        // column), and the node there it sends it to, or -1 if that's a drain: over the land's edge.
        var flow = new float[land.Nodes];
        var down = new int[land.Nodes];
        var way = new (sbyte X, sbyte Z)[land.Nodes];
        float column = pipes.Rain * settings.Size * settings.Size;
        for (int n = 0; n < land.Nodes; n++)
        {
            flow[n] = pipes.Through(n) / column;
            down[n] = -1;
            float most = 0;
            for (int k = land.LinkStart[n]; k < land.LinkStart[n + 1]; k++)
                if (pipes.Flow[k] > most) { most = pipes.Flow[k]; down[n] = land.LinkTo[k]; way[n] = (land.LinkDx[k], land.LinkDz[k]); }
            // Where the model hardly runs any water out (less than a stream's worth: what reaches it soaked in), its way
            // is a way down instead: what little moves there is noise, and a stream handed on would follow it across
            // the slope.
            if (most / column >= settings.StreamFlow) continue;
            float surface = land.Floor[n] + pipes.Depth[n], total = 0f;
            for (int k = land.LinkStart[n]; k < land.LinkStart[n + 1]; k++)
            {
                int m = land.LinkTo[k];
                float slope = m < 0 ? EdgeSlope : (surface - land.Floor[m] - pipes.Depth[m]) / land.LinkLength[k];
                if (slope > 0) total += slope * slope;
            }
            if (total <= 0f) continue;
            // (A way down picked at random, weighted by the square of its slope, the same every time: steep ways
            // mostly, but not one straight line down an even slope.)
            float pick = (float)(Hash(seed, n) * total);
            for (int k = land.LinkStart[n]; k < land.LinkStart[n + 1]; k++)
            {
                int m = land.LinkTo[k];
                float slope = m < 0 ? EdgeSlope : (surface - land.Floor[m] - pipes.Depth[m]) / land.LinkLength[k];
                if (slope <= 0) continue;
                down[n] = m; way[n] = (land.LinkDx[k], land.LinkDz[k]);
                if ((pick -= slope * slope) <= 0f) break;
            }
        }
        // What each column carries: what the model gains there (rain and springs: what it lets out past what comes in)
        // plus everything handed to it down the tree of main ways. A column hands on once everything handed to it has
        // arrived (up the tree first). Lakes carry too: water going round in one gains nothing, so only what runs in
        // and the rain on it reach the outlet.
        var carried = new float[land.Nodes];
        var waiting = new int[land.Nodes];
        for (int n = 0; n < land.Nodes; n++)
            if (down[n] >= 0) waiting[down[n]]++;
        void HandOn(int n)
        {
            float ins = 0, outs = 0;
            for (int k = land.LinkStart[n]; k < land.LinkStart[n + 1]; k++)
            {
                float q = pipes.Flow[k] / column;
                if (q > 0) outs += q; else ins -= q;
            }
            carried[n] += Math.Max(0f, outs - ins);
            if (down[n] >= 0) carried[down[n]] += carried[n];
        }
        var ready = new Stack<int>(Enumerable.Range(0, land.Nodes).Where(n => waiting[n] == 0));
        while (ready.Count > 0)
        {
            int n = ready.Pop();
            HandOn(n);
            if (down[n] >= 0 && --waiting[down[n]] == 0) ready.Push(down[n]);
        }
        // (Main ways going round in a ring, inside a lake: those hand on highest water first.)
        foreach (int n in Enumerable.Range(0, land.Nodes).Where(n => waiting[n] > 0).OrderByDescending(n => land.Floor[n] + pipes.Depth[n]))
            HandOn(n);
        // A stream shows where a column carries StreamFlow or more, and more than the columns either side of it across
        // its way (a sheet down a hillside shows one line, not one per column).
        var shown = new float[land.Nodes];
        var beside = new (int A, int B)[land.Nodes]; // the top nodes either side of each one's way, or -1
        int TopAt(int ci, int cj) => ci < 0 || cj < 0 || ci >= land.Nx || cj >= land.Nz ? -1 : land.TopNode(ci + land.Nx * cj);
        for (int c = 0; c < land.Columns; c++)
        {
            int n = land.TopNode(c);
            if (n < 0) continue;
            int i = c % land.Nx, j = c / land.Nx, di = way[n].X, dj = way[n].Z;
            beside[n] = way[n] == (0, 0) ? (-1, -1) : (TopAt(i - dj, j + di), TopAt(i + dj, j - di));
        }
        float least = settings.StreamFlow;
        float CarriedBy(int m) => m < 0 ? 0f : carried[m];
        for (int n = 0; n < land.Nodes; n++)
        {
            var (a, b) = beside[n];
            if (way[n] != (0, 0) && carried[n] >= least && carried[n] > CarriedBy(a) && carried[n] >= CarriedBy(b)) shown[n] = carried[n];
        }
        // A shown stream runs on down its way to wherever it goes, even past a column beside it that carries as much (a
        // stream stepping across the grid diagonally runs beside itself).
        // And water the model shows (see Shaped.WetLevel) running out over dry ground runs on as a stream, however
        // little it carries: a pond's outlet doesn't break off.
        bool Shows(int n) => pipes.Depth[n] > settings.Damp + Shaped.Shallow;
        for (int n = 0; n < land.Nodes; n++)
            if (Shows(n) && way[n] != (0, 0) && down[n] >= 0 && !Shows(down[n]) && shown[n] <= 0) shown[n] = MathF.Max(carried[n], 1e-3f);
        for (int n = 0; n < land.Nodes; n++)
            for (int m = shown[n] > 0 ? down[n] : -1; m >= 0 && shown[m] <= 0 && way[m] != (0, 0); m = down[m]) shown[m] = MathF.Max(carried[m], 1e-3f);
        // Each shown stream's main one running into it (the one carrying most), or -1: its line curves on from that one's.
        var up = new int[land.Nodes];
        Array.Fill(up, -1);
        for (int n = 0; n < land.Nodes; n++)
        {
            int d = down[n];
            if (shown[n] > 0 && d >= 0 && shown[d] > 0 && (up[d] < 0 || shown[n] > shown[up[d]])) up[d] = n;
        }
        // Each column's meander: how far its stream's line bends off the column's middle, across its way: all of it in a
        // valley's bottom (the ground either side of its way Size / 2 or more over its own), none along a hillside (the
        // ground lower on one side), where bending across its way would take it up and down the slope.
        var bendNoise = new FastNoiseLite(unchecked((int)seed + 77));
        bendNoise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        bendNoise.SetFrequency(MeanderFrequency);
        var bend = new float[land.Nodes];
        var wide = new float[land.Nodes]; // (each column's stream's width, times)
        for (int n = 0; n < land.Nodes; n++)
        {
            int c = land.ColumnOf[n];
            var (a, b) = beside[n];
            float sides = a < 0 || b < 0 ? 0f : MathF.Min(land.Floor[a], land.Floor[b]) - land.Floor[n];
            bend[n] = Meander * bendNoise.GetNoise(c % land.Nx, c / land.Nx) * (Hillside + (1 - Hillside) * Math.Clamp(sides / (settings.Size / 2f), 0f, 1f));
            wide[n] = 1f + Widen * bendNoise.GetNoise(c % land.Nx + 500f, c / land.Nx - 500f);
        }
        // A stream's head: a shown stream out of no water, neither a shown stream nor standing water running into it (nor
        // standing in it), where it springs from the ground.
        bool Wet(int n) => shown[n] > 0 || pipes.Depth[n] > settings.Damp;
        var head = new bool[land.Nodes];
        for (int n = 0; n < land.Nodes; n++) head[n] = shown[n] > 0 && pipes.Depth[n] <= settings.Damp;
        for (int n = 0; n < land.Nodes; n++) if (Wet(n) && down[n] >= 0) head[down[n]] = false;
        return new Shaped(land, start, shown, down, up, way, bend, wide, head, pipes.Depth, settings);
    }

    /// <summary>A number in [0, 1) from <paramref name="seed"/> and <paramref name="n"/>, the same every time.</summary>
    private static double Hash(ulong seed, int n)
    {
        ulong h = seed ^ (ulong)n * 0x9E3779B97F4A7C15UL;
        h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
        return (h >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>A landmass's bounds (blocks, on 32-block lines): where its own ground can be, found on a coarse grid
    /// round it, with a margin.</summary>
    public static (int X0, int Z0, int X1, int Z1) Bounds(ulong seed, (int X, int Z) landmass)
    {
        float spacing = HeartGrid.LandmassSpacing;
        int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
        for (float z = (landmass.Z - 0.7f) * spacing; z <= (landmass.Z + 1.7f) * spacing; z += 64f)
        for (float x = (landmass.X - 0.7f) * spacing; x <= (landmass.X + 1.7f) * spacing; x += 64f)
        {
            if (HeartGrid.Landmass(seed, x, z) != landmass) continue;
            x0 = Math.Min(x0, (int)x); z0 = Math.Min(z0, (int)z); x1 = Math.Max(x1, (int)x); z1 = Math.Max(z1, (int)z);
        }
        static int FloorTo(int v, int step) => (int)Math.Floor(v / (double)step) * step;
        return (FloorTo(x0 - 64, 32), FloorTo(z0 - 64, 32), FloorTo(x1 + 96, 32), FloorTo(z1 + 96, 32));
    }
}

/// <summary>One landmass as the model left it: each column's top floor before and after, the water over it, and its
/// stream (see HeartWorldGenerator.PlaceWater). The first Damp blocks of water only dampen the ground:
/// thin water running over a slope shows as none.</summary>
public sealed class Shaped(Land land, float[] startFloor, float[] flow, int[] down, int[] up, (sbyte X, sbyte Z)[] way,
                           float[] bend, float[] wide, bool[] head, float[] depth, LandscapeSettings settings)
{
    // (How wide a spring's stream is per sqrt(flow / StreamFlow), see HeartWorldGenerator.PlaceSprings.)
    public const float StreamWide = 3f;
    // (How far ground on a stream's line may stand over its line's ground and still be cut through; how deep a
    // channel is cut at most.)
    private const float Hump = 3f, MostDeep = 4f;
    /// <summary>How deep (past the damp) the model's water must be to show as its own surface: shallower, it runs in
    /// its stream's channel (a channel that deep would be a column wide).</summary>
    public const float Shallow = 1f;
    // (How deep the shown water blended over a block column must be for it to show there, see WetLevel.)
    private const float ShowsFrom = 0.25f;
    // (How narrow a channel is at least.)
    private const float LeastWide = 2f;

    /// <summary>The top water block over block column (x, z), whose ground is topped at <paramref name="top"/>, or
    /// int.MinValue, and the block its ground is cut down to (int.MaxValue for none): full detail and distant terrain
    /// both place water by this (distant terrain <paramref name="widen"/>s by half a voxel, so water thinner than a
    /// voxel still shows, and doesn't cut). Two rules:
    /// - wet columns (the model's water deeper than Shallow past the damp, running or standing) show the model's
    ///   own surface, blended between the nearest wet columns (see WetLevel): a river fills its valley as deep and
    ///   wide as the model has it, and a lake is level; a block lower than the model's surface, its water taking the
    ///   top ground block's place (a block cut where it's that shallow), so its edges sit level with the ground beside;
    /// - every stream runs in a channel cut along its line: the model's water across its column (the column's width
    ///   times its depth) gathered into a channel three times as wide as it's deep (a column wide and MostDeep deep at
    ///   most, LeastWide wide and a block deep at least), along its curve (see Curve), its bed rounded, its surface flat across and level with the line's ground: the
    ///   valley floor keeps its banks and the stream sits in it. Ground up to Hump blocks over the line's is cut
    ///   through, so it runs on unbroken over small rises; higher ground, down a steep drop (its curve falling more than half a
    ///   column; no higher than the drop's top), it runs down over along its line, a block deep. Under the model's own surface nothing is cut.</summary>
    public (int Water, int Bed) Water(int x, int z, int top, float widen)
    {
        // (The shown surface is a block under the model's: its water takes the top ground block's place, as a stream's
        // does, so a shallow edge sits level with the ground beside it, not a block over it; distant terrain, which
        // doesn't cut, keeps it over its voxel.)
        float shown = WetLevel(x, z, top);
        int size = land.Size, bed = int.MaxValue, r = 2 + (int)MathF.Ceiling(widen / size);
        int water = float.IsNaN(shown) ? int.MinValue : (int)MathF.Floor(shown + (widen > 0f ? 0.5f : -0.5f));
        if (water < top || (widen > 0f && water == top)) water = int.MinValue;
        bool shows = water != int.MinValue;
        if (shows && widen <= 0f && water <= top) bed = water - 1;
        int ic = (int)MathF.Round((x - land.X0) / (float)size), kc = (int)MathF.Round((z - land.Z0) / (float)size);
        float px = x + 0.5f, pz = z + 0.5f;
        Span<Point> curve = stackalloc Point[3];
        for (int k = kc - r; k <= kc + r; k++)
        for (int i = ic - r; i <= ic + r; i++)
        {
            if (i < 0 || k < 0 || i >= land.Nx || k >= land.Nz) continue;
            int t = land.TopNode(i + land.Nx * k);
            if (t < 0 || flow[t] <= 0 || way[t] == (0, 0)) continue;
            Curve(t, curve);
            // (The nearest point of its curve, a few straight pieces along it.)
            const int Pieces = 6;
            float best2 = float.MaxValue, bestAt = 0f;
            var prev = At(curve, 0f);
            for (int q = 1; q <= Pieces; q++)
            {
                var next = At(curve, q / (float)Pieces);
                float dx = next.X - prev.X, dz = next.Z - prev.Z, length2 = dx * dx + dz * dz;
                float along = length2 <= 0 ? 0f : Math.Clamp(((px - prev.X) * dx + (pz - prev.Z) * dz) / length2, 0f, 1f);
                float ox = px - prev.X - along * dx, oz = pz - prev.Z - along * dz;
                if (ox * ox + oz * oz < best2) { best2 = ox * ox + oz * oz; bestAt = (q - 1 + along) / Pieces; }
                prev = next;
            }
            var on = At(curve, bestAt);
            float g = on.Floor, highest = MathF.Max(curve[0].Floor, MathF.Max(curve[1].Floor, curve[2].Floor));
            float lowest = MathF.Min(curve[0].Floor, MathF.Min(curve[1].Floor, curve[2].Floor));
            bool steep = highest - lowest > size / 2f;
            if (top < g - size || top > (steep ? highest : g) - 1 + Hump) continue; // (land far below its line: past its edge; or a hill)
            float area = size * MathF.Max(0f, on.Depth);
            float width = Math.Clamp(MathF.Sqrt(3f * area) * on.Wide, LeastWide, size), deep = Math.Clamp(1.5f * area / width, 1f, MostDeep);
            float half = width / 2 + widen, off2 = best2;
            if (off2 > half * half) continue;
            if (top > g - 1 + Hump)
            {
                if (off2 > 1f) continue; // (only down its line itself, down a steep drop: not up its banks)
                // (Ground over its line's down a steep drop, no higher than its top: it runs down over it, uncut.)
                water = Math.Max(water, top + 1);
                continue;
            }
            int surface = (int)MathF.Round(g) - 1; // (the line's top ground block: water takes its place)
            water = Math.Max(water, widen > 0f ? Math.Max(surface, top + 1) : surface); // (distant terrain: over its voxel)
            if (widen <= 0f && !shows) // (under water the model already shows, nothing is cut)
                bed = Math.Min(bed, surface - Math.Max(1, (int)MathF.Round(deep * (1f - off2 / (half * half)))));
        }
        if (water <= Math.Min(top, bed)) return (int.MinValue, int.MaxValue);
        return (water, bed < top ? bed : int.MaxValue);
    }

    /// <summary>A point on a stream's line: where it is, and the ground and the model's water depth there.</summary>
    private readonly record struct Point(float X, float Z, float Floor, float Depth, float Wide);

    /// <summary>Node <paramref name="n"/>'s stream's line, as the three points of a curve (a quadratic Bézier): from
    /// halfway between it and its main stream in (its own middle, bent by its meander, at a head) round its middle to
    /// halfway to where it runs (where the stream it joins passes, if it's not that one's main stream in; the middle of
    /// the column past the land's edge if it runs off). One stream's curves meet end to end, without corners.</summary>
    private void Curve(int n, Span<Point> curve)
    {
        var b = Here(n);
        int u = up[n], d = down[n];
        curve[0] = u < 0 ? b : Half(Here(u), b);
        curve[1] = b;
        if (d < 0)
        {
            var (mx, mz) = Middle(n);
            curve[2] = new Point(mx + way[n].X * land.Size, mz + way[n].Z * land.Size, b.Floor, b.Depth, b.Wide);
        }
        else if (up[d] == n || flow[d] <= 0) curve[2] = Half(b, Here(d));
        else
        {
            Span<Point> joined = stackalloc Point[3];
            Curve3(d, joined);
            curve[2] = At(joined, 0.5f);
        }
    }

    /// <summary>Curve without the joining case (the stream joined's own curve, whose ends don't depend on others').</summary>
    private void Curve3(int n, Span<Point> curve)
    {
        var b = Here(n);
        int u = up[n], d = down[n];
        curve[0] = u < 0 ? b : Half(Here(u), b);
        curve[1] = b;
        if (d < 0) { var (mx, mz) = Middle(n); curve[2] = new Point(mx + way[n].X * land.Size, mz + way[n].Z * land.Size, b.Floor, b.Depth, b.Wide); }
        else curve[2] = Half(b, Here(d));
    }

    private Point Here(int n) { var (x, z) = Bent(n); return new Point(x, z, land.Floor[n], depth[n], wide[n]); }
    private static Point Half(Point a, Point b) => new((a.X + b.X) / 2, (a.Z + b.Z) / 2, (a.Floor + b.Floor) / 2, (a.Depth + b.Depth) / 2, (a.Wide + b.Wide) / 2);
    private static Point At(ReadOnlySpan<Point> c, float s)
    {
        float a = (1 - s) * (1 - s), b = 2 * s * (1 - s), e = s * s;
        return new Point(a * c[0].X + b * c[1].X + e * c[2].X, a * c[0].Z + b * c[1].Z + e * c[2].Z,
                         a * c[0].Floor + b * c[1].Floor + e * c[2].Floor, a * c[0].Depth + b * c[1].Depth + e * c[2].Depth,
                         a * c[0].Wide + b * c[1].Wide + e * c[2].Wide);
    }

    /// <summary>The top water block over block column (x, z) (see Water).</summary>
    public int WaterTop(int x, int z, int top, float widen) => Water(x, z, top, widen).Water;

    /// <summary>The surface the model's water shows over block column (x, z), a block under its own (Shallow past
    /// the damp), or NaN: each of the four nearest columns' shown water (none where it's that shallow) on its floor,
    /// blended by how near each is, shown where that's at least ShowsFrom deep. Every column counts alike, wet or dry, so
    /// the surface runs down into the ground toward dry columns as the water thins, wherever it ends, rather than in a
    /// face on the grid's lines; a dry column's ground counts no higher than the highest wet one's surface (water doesn't
    /// climb hills), and a column counts only over ground (<paramref name="top"/>) no more than a column under its floor
    /// (not past its edge).</summary>
    private float WetLevel(int x, int z, int top)
    {
        int size = land.Size;
        float gx = (x - land.X0) / (float)size, gz = (z - land.Z0) / (float)size;
        int i0 = (int)MathF.Floor(gx), k0 = (int)MathF.Floor(gz);
        float fx = gx - i0, fz = gz - k0, highest = float.NaN, past = settings.Damp + Shallow;
        Span<int> corner = stackalloc int[4];
        for (int c = 0; c < 4; c++)
        {
            int i = i0 + (c & 1), k = k0 + (c >> 1);
            int t = i < 0 || k < 0 || i >= land.Nx || k >= land.Nz ? -1 : land.TopNode(i + land.Nx * k);
            corner[c] = t = t < 0 || top < land.Floor[t] - size ? -1 : t;
            if (t >= 0 && depth[t] > past && !(land.Floor[t] + depth[t] - past <= highest)) highest = land.Floor[t] + depth[t] - past;
        }
        if (float.IsNaN(highest)) return float.NaN;
        float sum = 0f, deep = 0f, weights = 0f;
        for (int c = 0; c < 4; c++)
        {
            int t = corner[c];
            if (t < 0) continue;
            float w = ((c & 1) == 0 ? 1 - fx : fx) * ((c >> 1) == 0 ? 1 - fz : fz) + 1e-4f, d = MathF.Max(0f, depth[t] - past);
            sum += w * MathF.Min(land.Floor[t] + d, highest); deep += w * d; weights += w;
        }
        return deep < ShowsFrom * weights ? float.NaN : sum / weights;
    }

    /// <summary>The stream of the column holding (x, z): how wide it runs (sqrt(flow / StreamFlow) blocks; 0 for
    /// none), its line, from the column's middle bent across its way by the column's meander to the same point of the
    /// column it runs to (in blocks, block centres at .5), and the ground at both ends. A stream that runs over the
    /// land's edge runs on level to the middle of the column past it. Head: no stream runs into it (it springs here).</summary>
    public (float Width, float FromX, float FromZ, float ToX, float ToZ, float From, float To, bool Head) Stream(int x, int z)
    {
        int t = TopNodeAt(x, z);
        if (t < 0 || settings.StreamFlow <= 0 || flow[t] <= 0f || way[t] == (0, 0)) return default;
        float width = MathF.Sqrt(flow[t] / settings.StreamFlow);
        var (fx, fz) = Bent(t);
        if (down[t] < 0)
        {
            var (mx, mz) = Middle(t);
            return (width, fx, fz, mx + way[t].X * land.Size, mz + way[t].Z * land.Size, land.Floor[t], land.Floor[t], head[t]);
        }
        var (tx, tz) = Bent(down[t]);
        return (width, fx, fz, tx, tz, land.Floor[t], land.Floor[down[t]], head[t]);
    }

    /// <summary>The middle of node <paramref name="n"/>'s column (a block centre).</summary>
    private (float X, float Z) Middle(int n)
    {
        int c = land.ColumnOf[n], size = land.Size;
        return (land.X0 + c % land.Nx * size + 0.5f, land.Z0 + c / land.Nx * size + 0.5f);
    }

    /// <summary>Where node <paramref name="n"/>'s stream passes through its column: the middle, bent across its way by
    /// its meander.</summary>
    private (float X, float Z) Bent(int n)
    {
        var (mx, mz) = Middle(n);
        var (wx, wz) = way[n];
        if (wx == 0 && wz == 0) return (mx, mz);
        float length = MathF.Sqrt(wx * wx + wz * wz);
        return (mx - wz / length * bend[n], mz + wx / length * bend[n]);
    }

    /// <summary>A column's width, in blocks.</summary>
    public int Size => land.Size;

    /// <summary>How far the ground at lattice corner (x, z) (on the 8-block lines, the middle of a column) moved, if
    /// the column's top stood within Near blocks of <paramref name="top"/> (the corner's own top before); else 0.</summary>
    public float Shift(int x, int z, float top)
    {
        const float Near = 16f;
        int t = TopNodeAt(x, z);
        return t < 0 || MathF.Abs(startFloor[t] - top) > Near ? 0f : land.Floor[t] - startFloor[t];
    }

    /// <summary>Whether any of the columns round block column (x, z) or beside those has water over it or a stream
    /// through it (water reaches that far across: a stream's channel, the model's surface blended between columns).</summary>
    public bool Watered(int x, int z)
    {
        for (int c = 0; c < 16; c++)
        {
            int t = TopNodeAt(x + (c % 4 - 1) * land.Size, z + (c / 4 - 1) * land.Size);
            if (t >= 0 && (depth[t] > settings.Damp || flow[t] > 0f)) return true;
        }
        return false;
    }

    /// <summary>The top node of the column with its middle at (x, z), or -1.</summary>
    private int TopNodeAt(int x, int z)
    {
        int size = land.Size, i = (x - land.X0) / size, k = (z - land.Z0) / size;
        if (x < land.X0 || z < land.Z0 || i >= land.Nx || k >= land.Nz) return -1;
        return land.TopNode(i + land.Nx * k);
    }
}

/// <summary>What the landscape model does (see <see cref="Landscape"/>), from --terrain: comma-separated name=value
/// pairs. off turns it off; erode-hours, settle-minutes, settle-rain (times the rain while settling), groundwater (true/false), damp (how deep water only dampens the ground) and size (column width) are the landscape's own; any other name is one of Pipes' numbers (e.g.
/// creep=1e-5, speedup=900, rain=3e-4, manning=0.035, carry=0.05; dashes ignored, any case).</summary>
public sealed class LandscapeSettings
{
    public bool Enabled = true;
    public double ErodeHours = 1, SettleMinutes = 20;
    /// <summary>How many times as much rain falls while the water settles, after the erosion.</summary>
    public float SettleRain = 0.35f;
    public bool Groundwater = true;
    /// <summary>How deep water over a column only dampens its ground, in blocks: the rest goes into its blocks.</summary>
    public float Damp = 0.1f;
    /// <summary>How much rain (in columns' worth) runs through a column whose stream shows a block wide: each column's
    /// stream shows as a block-deep line from its middle to the column it runs to, sqrt(flow / StreamFlow) blocks wide
    /// (narrower than a block shows nothing). 0 for no streams.</summary>
    public float StreamFlow = 10f;
    public int Size = 8;
    public readonly List<(string Name, string Value)> Pipes = new();

    public static LandscapeSettings Parse(string? text)
    {
        var s = new LandscapeSettings();
        if (string.IsNullOrWhiteSpace(text)) return s;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            string name = (eq < 0 ? part : part[..eq]).ToLowerInvariant(), value = eq < 0 ? "true" : part[(eq + 1)..];
            var c = CultureInfo.InvariantCulture;
            switch (name)
            {
                case "off": s.Enabled = false; break;
                case "erode-hours": s.ErodeHours = double.Parse(value, c); break;
                case "settle-minutes": s.SettleMinutes = double.Parse(value, c); break;
                case "settle-rain": s.SettleRain = float.Parse(value, c); break;
                case "groundwater": s.Groundwater = bool.Parse(value); break;
                case "damp": s.Damp = float.Parse(value, c); break;
                case "stream-flow": s.StreamFlow = float.Parse(value, c); break;
                case "size": s.Size = int.Parse(value, c); break;
                default:
                    if (!Generation.Pipes.Set(null, name, value)) throw new ArgumentException($"--terrain: no setting called {name}");
                    s.Pipes.Add((name, value));
                    break;
            }
        }
        return s;
    }
}
