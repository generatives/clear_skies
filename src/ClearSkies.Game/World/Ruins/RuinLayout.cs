using System.Diagnostics;
using ClearSkies.Game.Generation;

namespace ClearSkies.Game.Ruins;

/// <summary>Deterministic draws for layouts.</summary>
public struct LayoutRng(ulong seed)
{
    private SplitMix64Rng _rng = new(seed);
    /// <summary>0 .. n-1.</summary>
    public int Next(int n) => (int)(_rng.NextFloat01() * n);
    /// <summary>lo .. hi, both included.</summary>
    public int Range(int lo, int hi) => lo + Next(hi - lo + 1);
    public float Range(float lo, float hi) => _rng.NextRange(lo, hi);
}

/// <summary>A layout model's floor-independent part: its halls, and which district each cell is in (-1 for none).
/// Every floor shares it, so halls stack; the rooms along them are laid out floor by floor.</summary>
public sealed class Skeleton(int cells)
{
    public readonly List<(CellRect Rect, PartKind Kind)> Halls = [];
    public readonly int[] DistrictOf = Enumerable.Repeat(-1, cells).ToArray();
    public int Districts;
}

public interface ILayoutModel
{
    string Name { get; }
    Skeleton Build(RuinSite site, ref LayoutRng rng);
}

/// <summary>
/// Lays a ruin out on its site with a layout model: the model's skeleton of halls, then stair cores on them (at the same
/// places on every floor, so floors connect), then floor by floor: the halls cut to the ground that floor has (bridges
/// across gaps up to <see cref="RuinSite.BridgeCells"/>, nothing where it's wider or outside the region), and rooms
/// along both sides of every hall wherever solid ground and free cells allow.
/// </summary>
public static class RuinLayout
{
    /// <summary>Stair cores: one about every CoreSpacing cells, CoreSize cells square.</summary>
    public const int CoreSpacing = 40, CoreSize = 3;
    public const int MinRoom = 3, MaxRoomWidth = 6, MaxRoomDepth = 7;

    public static ILayoutModel[] Models => [new CityGridModel(), new HubSpokeModel(), new SpineModel()];

    public static RuinPlan Generate(RuinSite site, ILayoutModel model)
    {
        var plan = new RuinPlan(site, model.Name);
        // (string.GetHashCode differs between runs: hash the name by hand so layouts are the same every time.)
        ulong seed = site.Seed * 0x9E3779B97F4A7C15UL ^ ((ulong)(uint)site.Landmass.X << 32 | (uint)site.Landmass.Z);
        foreach (char ch in model.Name) seed = (seed ^ ch) * 0x100000001B3UL;
        var rng = new LayoutRng(seed);
        var sw = Stopwatch.StartNew();
        var sk = model.Build(site, ref rng);
        plan.DistrictOf = sk.DistrictOf;
        plan.Uses = new DistrictUse[sk.Districts];
        for (int d = 0; d < sk.Districts; d++)
        {
            int roll = rng.Next(10);
            plan.Uses[d] = roll < 6 ? DistrictUse.Residential : roll < 8 ? DistrictUse.Admin : DistrictUse.Military;
        }
        var cores = PlaceCores(site, sk, plan);
        plan.SkeletonTime = sw.Elapsed;
        sw.Restart();

        int n = site.Nx * site.Nz;
        var occupied = new bool[n];
        var halls = new List<Part>();
        for (int f = 0; f < site.Floors; f++)
        {
            Array.Clear(occupied);
            halls.Clear();
            foreach (var (rect, kind) in sk.Halls) ClipHall(site, f, rect, kind, halls);
            foreach (var h in halls) Mark(occupied, site.Nx, h.Rect);
            foreach (var c in cores)
                if (f >= c.Floor && f <= c.LastFloor) Mark(occupied, site.Nx, c.Rect);
            if (f == 0) PickEntrance(site, halls, cores);
            plan.Parts.AddRange(halls);
            foreach (var h in halls)
                if (h.Kind != PartKind.Bridge) LineRooms(site, f, h.Rect, occupied, ref rng, plan);
        }
        plan.Parts.AddRange(cores);
        plan.RoomsTime = sw.Elapsed;
        return plan;
    }

    private static void Mark(bool[] occupied, int nx, CellRect r)
    {
        for (int j = r.J0; j < r.J1; j++)
        for (int i = r.I0; i < r.I1; i++) occupied[i + nx * j] = true;
    }

    /// <summary>Cores on hall cells, about one per CoreSpacing square, each reaching down through every floor from the
    /// top while its ground isn't open; one becomes the entrance (see PickEntrance).</summary>
    private static List<Part> PlaceCores(RuinSite site, Skeleton sk, RuinPlan plan)
    {
        int nx = site.Nx, nz = site.Nz;
        var hall = new bool[nx * nz];
        foreach (var (rect, _) in sk.Halls)
        {
            var r = rect.Clip(nx, nz);
            Mark(hall, nx, r);
        }
        var cores = new List<Part>();
        int half = CoreSize / 2;
        bool Fits(int f, CellRect r)
        {
            for (int j = r.J0; j < r.J1; j++)
            for (int i = r.I0; i < r.I1; i++)
                if (site.At(f, i, j) is RuinSite.Ground.Out or RuinSite.Ground.Open) return false;
            return true;
        }
        for (int bj = 0; bj < nz; bj += CoreSpacing)
        for (int bi = 0; bi < nx; bi += CoreSpacing)
        {
            int ci = bi + CoreSpacing / 2, cj = bj + CoreSpacing / 2, best = -1, bestD = int.MaxValue;
            for (int j = bj; j < Math.Min(nz, bj + CoreSpacing); j++)
            for (int i = bi; i < Math.Min(nx, bi + CoreSpacing); i++)
            {
                int c = i + nx * j;
                if (!hall[c] || site.At(0, i, j) != RuinSite.Ground.Solid) continue;
                int d = (i - ci) * (i - ci) + (j - cj) * (j - cj);
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best < 0) continue;
            var rect = new CellRect(best % nx - half, best / nx - half, best % nx - half + CoreSize, best / nx - half + CoreSize);
            if (!Fits(0, rect)) continue;
            int last = 0;
            while (last + 1 < site.Floors && Fits(last + 1, rect)) last++;
            cores.Add(new Part { Rect = rect, Kind = PartKind.Core, Floor = 0, LastFloor = (byte)last, District = -1 });
        }
        return cores;
    }

    /// <summary>Makes the entrance the core nearest the region's centre among those in the biggest connected stretch
    /// of the top floor's halls, so it leads somewhere.</summary>
    private static void PickEntrance(RuinSite site, List<Part> halls, List<Part> cores)
    {
        if (cores.Count == 0) return;
        int nx = site.Nx, nz = site.Nz;
        var way = new bool[nx * nz];
        foreach (var h in halls) Mark(way, nx, h.Rect);
        foreach (var c in cores) Mark(way, nx, c.Rect.Clip(nx, nz));
        var component = new int[nx * nz];
        var sizes = new List<int> { 0 };
        var q = new Queue<int>();
        for (int s = 0; s < way.Length; s++)
        {
            if (!way[s] || component[s] != 0) continue;
            int id = sizes.Count, size = 0;
            component[s] = id; q.Enqueue(s);
            while (q.TryDequeue(out int c))
            {
                size++;
                int i = c % nx, j = c / nx;
                void Visit(int e) { if (way[e] && component[e] == 0) { component[e] = id; q.Enqueue(e); } }
                if (i > 0) Visit(c - 1);
                if (i < nx - 1) Visit(c + 1);
                if (j > 0) Visit(c - nx);
                if (j < nz - 1) Visit(c + nx);
            }
            sizes.Add(size);
        }
        int ei = site.Centre % nx, ej = site.Centre / nx, best = -1;
        (int Size, float Dist) bestKey = (-1, 0);
        for (int k = 0; k < cores.Count; k++)
        {
            var r = cores[k].Rect;
            int ci = (r.I0 + r.I1) / 2, cj = (r.J0 + r.J1) / 2;
            float dx = ci - ei, dz = cj - ej, d = dx * dx + dz * dz;
            int size = sizes[component[ci + nx * cj]];
            if (size > bestKey.Size || size == bestKey.Size && d < bestKey.Dist) { bestKey = (size, d); best = k; }
        }
        var e = cores[best]; e.Entrance = true; cores[best] = e;
    }

    /// <summary>Cuts a hall to floor <paramref name="f"/>'s ground, slice by slice along its length: runs on ground (solid
    /// or partial: it tunnels) stay, gaps between them up to BridgeCells long become bridges, the rest goes.</summary>
    private static void ClipHall(RuinSite site, int f, CellRect rect, PartKind kind, List<Part> into)
    {
        rect = rect.Clip(site.Nx, site.Nz);
        if (rect.IsEmpty) return;
        bool alongI = rect.Width >= rect.Depth;
        int t0 = alongI ? rect.I0 : rect.J0, t1 = alongI ? rect.I1 : rect.J1;
        int c0 = alongI ? rect.J0 : rect.I0, c1 = alongI ? rect.J1 : rect.I1;
        // 0 out, 1 gap, 2 ground
        int Slice(int t)
        {
            int worst = 2;
            for (int c = c0; c < c1; c++)
            {
                var g = alongI ? site.At(f, t, c) : site.At(f, c, t);
                if (g == RuinSite.Ground.Out) return 0;
                if (g == RuinSite.Ground.Open) worst = 1;
            }
            return worst;
        }
        CellRect Piece(int a, int b) => alongI ? new CellRect(a, c0, b, c1) : new CellRect(c0, a, c1, b);
        int t = t0, prevKind = -1;
        while (t < t1)
        {
            int k = Slice(t), end = t + 1;
            while (end < t1 && Slice(end) == k) end++;
            if (k == 2) into.Add(new Part { Rect = Piece(t, end), Kind = kind, Floor = (byte)f, LastFloor = (byte)f, District = -1 });
            else if (k == 1 && prevKind == 2 && end < t1 && Slice(end) == 2 && end - t <= RuinSite.BridgeCells)
                into.Add(new Part { Rect = Piece(t, end), Kind = PartKind.Bridge, Floor = (byte)f, LastFloor = (byte)f, District = -1 });
            prevKind = k;
            t = end;
        }
    }

    /// <summary>Rooms along both sides of a hall, as deep and wide as free solid cells allow (MinRoom to MaxRoom*), each
    /// with its door in the middle of the side facing the hall.</summary>
    private static void LineRooms(RuinSite site, int f, CellRect hall, bool[] occupied, ref LayoutRng rng, RuinPlan plan)
    {
        int nx = site.Nx, nz = site.Nz;
        bool alongI = hall.Width >= hall.Depth;
        int t0 = alongI ? hall.I0 : hall.J0, t1 = alongI ? hall.I1 : hall.J1;
        bool Fits(CellRect r)
        {
            if (r.I0 < 0 || r.J0 < 0 || r.I1 > nx || r.J1 > nz) return false;
            for (int j = r.J0; j < r.J1; j++)
            for (int i = r.I0; i < r.I1; i++)
                if (occupied[i + nx * j] || site.Grounds[f][i + nx * j] != RuinSite.Ground.Solid) return false;
            return true;
        }
        foreach (int side in (ReadOnlySpan<int>)[-1, 1])
        {
            int t = t0;
            while (t + MinRoom <= t1)
            {
                int w = rng.Range(MinRoom, MaxRoomWidth), d = rng.Range(MinRoom, MaxRoomDepth);
                bool placed = false;
                for (int dd = d; dd >= MinRoom && !placed; dd--)
                for (int ww = Math.Min(w, t1 - t); ww >= MinRoom && !placed; ww--)
                {
                    CellRect r = alongI
                        ? side < 0 ? new CellRect(t, hall.J0 - dd, t + ww, hall.J0) : new CellRect(t, hall.J1, t + ww, hall.J1 + dd)
                        : side < 0 ? new CellRect(hall.I0 - dd, t, hall.I0, t + ww) : new CellRect(hall.I1, t, hall.I1 + dd, t + ww);
                    if (!Fits(r)) continue;
                    Mark(occupied, nx, r);
                    int mid = t + ww / 2;
                    var p = new Part { Rect = r, Kind = PartKind.Room, Floor = (byte)f, LastFloor = (byte)f };
                    if (alongI) { p.DoorI = (short)mid; p.DoorJ = (short)(side < 0 ? hall.J0 - 1 : hall.J1); p.DoorDj = (sbyte)-side; }
                    else { p.DoorJ = (short)mid; p.DoorI = (short)(side < 0 ? hall.I0 - 1 : hall.I1); p.DoorDi = (sbyte)-side; }
                    int ci = (r.I0 + r.I1) / 2, cj = (r.J0 + r.J1) / 2;
                    p.District = (short)plan.DistrictOf[ci + nx * cj];
                    plan.Parts.Add(p);
                    t += ww;
                    placed = true;
                }
                if (!placed) t++;
            }
        }
    }

    internal static bool InRegion(RuinSite site, int i, int j) => site.At(0, i, j) != RuinSite.Ground.Out;
}

/// <summary>City grid: wide avenues split the region into districts (recursively, until none is wider than
/// MaxDistrict), and a grid of streets splits each district into blocks.</summary>
public sealed class CityGridModel : ILayoutModel
{
    public const int MaxDistrict = 90, AvenueWidth = 2;
    public string Name => "grid";

    public Skeleton Build(RuinSite site, ref LayoutRng rng)
    {
        var sk = new Skeleton(site.Nx * site.Nz);
        var leaves = new List<CellRect>();
        var avenues = new List<CellRect>();
        var stack = new Stack<CellRect>();
        stack.Push(new CellRect(0, 0, site.Nx, site.Nz));
        while (stack.TryPop(out var r))
        {
            if (Math.Max(r.Width, r.Depth) <= MaxDistrict) { leaves.Add(r); continue; }
            if (r.Width >= r.Depth)
            {
                int at = r.I0 + (int)(r.Width * rng.Range(0.35f, 0.65f));
                avenues.Add(new CellRect(at, r.J0, at + AvenueWidth, r.J1));
                stack.Push(new CellRect(r.I0, r.J0, at, r.J1));
                stack.Push(new CellRect(at + AvenueWidth, r.J0, r.I1, r.J1));
            }
            else
            {
                int at = r.J0 + (int)(r.Depth * rng.Range(0.35f, 0.65f));
                avenues.Add(new CellRect(r.I0, at, r.I1, at + AvenueWidth));
                stack.Push(new CellRect(r.I0, r.J0, r.I1, at));
                stack.Push(new CellRect(r.I0, at + AvenueWidth, r.I1, r.J1));
            }
        }
        for (int d = 0; d < leaves.Count; d++)
        {
            var l = leaves[d];
            for (int j = l.J0; j < l.J1; j++)
            for (int i = l.I0; i < l.I1; i++) sk.DistrictOf[i + site.Nx * j] = d;
            int s = rng.Range(13, 16);
            for (int i = l.I0 + rng.Range(4, s); i < l.I1 - 4; i += s) sk.Halls.Add((new CellRect(i, l.J0, i + 1, l.J1), PartKind.Hall));
            s = rng.Range(13, 16);
            for (int j = l.J0 + rng.Range(4, s); j < l.J1 - 4; j += s) sk.Halls.Add((new CellRect(l.I0, j, l.I1, j + 1), PartKind.Hall));
        }
        foreach (var a in avenues) sk.Halls.Add((a, PartKind.Avenue));
        sk.Districts = leaves.Count;
        return sk;
    }
}

/// <summary>Hub and spoke: big hub halls at the region's thickest points, HubSpacing apart; spokes out from each, rings
/// round each, and links between neighbouring hubs. Each cell's district is its nearest hub's.</summary>
public sealed class HubSpokeModel : ILayoutModel
{
    public const int HubSpacing = 90, HubSize = 10, SpokeLength = 60, SpokeWidth = 2, MaxLink = 250;
    public static readonly int[] Rings = [20, 40];
    public string Name => "hub";

    public Skeleton Build(RuinSite site, ref LayoutRng rng)
    {
        int nx = site.Nx, nz = site.Nz, n = nx * nz;
        var sk = new Skeleton(n);
        // Steps to the region's edge.
        var depth = new int[n];
        var q = new Queue<int>();
        for (int c = 0; c < n; c++)
            if (!RuinLayout.InRegion(site, c % nx, c / nx)) q.Enqueue(c); else depth[c] = int.MaxValue;
        while (q.TryDequeue(out int c))
        {
            int i = c % nx, j = c / nx;
            foreach (var (di, dj) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1)])
            {
                int a = i + di, b = j + dj;
                if (a < 0 || b < 0 || a >= nx || b >= nz) continue;
                int e = a + nx * b;
                if (depth[e] == int.MaxValue) { depth[e] = depth[c] + 1; q.Enqueue(e); }
            }
        }
        // Hubs: thickest first, HubSpacing apart. (A random tie-break keeps flat stretches from lining up.)
        var order = Enumerable.Range(0, n).Where(c => depth[c] != int.MaxValue && depth[c] >= HubSize).ToArray();
        var keys = new long[order.Length];
        for (int k = 0; k < order.Length; k++) keys[k] = -((long)depth[order[k]] << 16 | (uint)rng.Next(65536));
        Array.Sort(keys, order);
        var hubs = new List<(int I, int J)>();
        foreach (int c in order)
        {
            int i = c % nx, j = c / nx;
            if (hubs.All(h => (h.I - i) * (h.I - i) + (h.J - j) * (h.J - j) >= HubSpacing * HubSpacing)) hubs.Add((i, j));
        }
        int half = HubSize / 2;
        foreach (var (i, j) in hubs)
        {
            sk.Halls.Add((new CellRect(i - half, j - half, i + half, j + half), PartKind.Hub));
            // Spokes, as far as the region goes.
            foreach (var (di, dj) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1)])
            {
                int len = 0;
                while (len < SpokeLength && RuinLayout.InRegion(site, i + di * (half + len), j + dj * (half + len))) len++;
                if (len < 4) continue;
                int a = i + di * half, b = j + dj * half, e = i + di * (half + len), f = j + dj * (half + len);
                sk.Halls.Add((di != 0
                    ? new CellRect(Math.Min(a, e), j - SpokeWidth / 2, Math.Max(a, e), j - SpokeWidth / 2 + SpokeWidth)
                    : new CellRect(i - SpokeWidth / 2, Math.Min(b, f), i - SpokeWidth / 2 + SpokeWidth, Math.Max(b, f)), PartKind.Avenue));
            }
            foreach (int r in Rings)
            {
                sk.Halls.Add((new CellRect(i - r, j - r, i + r + 1, j - r + 1), PartKind.Hall));
                sk.Halls.Add((new CellRect(i - r, j + r, i + r + 1, j + r + 1), PartKind.Hall));
                sk.Halls.Add((new CellRect(i - r, j - r + 1, i - r + 1, j + r), PartKind.Hall));
                sk.Halls.Add((new CellRect(i + r, j - r + 1, i + r + 1, j + r), PartKind.Hall));
            }
        }
        // Links: a spanning tree of the hubs plus each one's two nearest, as L-shaped halls.
        var links = new HashSet<(int, int)>();
        float D(int a, int b) { float dx = hubs[a].I - hubs[b].I, dz = hubs[a].J - hubs[b].J; return MathF.Sqrt(dx * dx + dz * dz); }
        if (hubs.Count > 1)
        {
            var inTree = new bool[hubs.Count]; inTree[0] = true;
            for (int added = 1; added < hubs.Count; added++)
            {
                int ba = -1, bb = -1; float bd = float.MaxValue;
                for (int a = 0; a < hubs.Count; a++) if (inTree[a])
                for (int b = 0; b < hubs.Count; b++) if (!inTree[b] && D(a, b) < bd) { bd = D(a, b); ba = a; bb = b; }
                inTree[bb] = true;
                links.Add((Math.Min(ba, bb), Math.Max(ba, bb)));
            }
            for (int a = 0; a < hubs.Count; a++)
                foreach (int b in Enumerable.Range(0, hubs.Count).Where(b => b != a).OrderBy(b => D(a, b)).Take(2))
                    links.Add((Math.Min(a, b), Math.Max(a, b)));
        }
        foreach (var (a, b) in links)
        {
            if (D(a, b) > MaxLink) continue;
            var (ai, aj) = hubs[a]; var (bi, bj) = hubs[b];
            sk.Halls.Add((new CellRect(Math.Min(ai, bi), aj - 1, Math.Max(ai, bi) + 1, aj + 1), PartKind.Avenue));
            sk.Halls.Add((new CellRect(bi - 1, Math.Min(aj, bj), bi + 1, Math.Max(aj, bj) + 1), PartKind.Avenue));
        }
        // Districts: nearest hub.
        for (int c = 0; c < n; c++)
        {
            if (!RuinLayout.InRegion(site, c % nx, c / nx)) continue;
            int i = c % nx, j = c / nx, best = -1; long bd = long.MaxValue;
            for (int h = 0; h < hubs.Count; h++)
            {
                long dx = hubs[h].I - i, dz = hubs[h].J - j, d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = h; }
            }
            sk.DistrictOf[c] = best;
        }
        sk.Districts = hubs.Count;
        return sk;
    }
}

/// <summary>Spine: one main hall down the region's long axis, following its middle (with jogs where the middle moves),
/// and branch halls off both sides every BranchSpacing cells, out to the region's edge. Districts are runs of
/// BranchesPerDistrict branches on each side.</summary>
public sealed class SpineModel : ILayoutModel
{
    public const int SpineWidth = 3, BranchSpacing = 14, BranchWidth = 2, Jog = 6, Smooth = 20, BranchesPerDistrict = 3;
    public string Name => "spine";

    public Skeleton Build(RuinSite site, ref LayoutRng rng)
    {
        int nx = site.Nx, nz = site.Nz;
        var sk = new Skeleton(nx * nz);
        bool alongI = nx >= nz;
        int len = alongI ? nx : nz, across = alongI ? nz : nx;
        bool In(int t, int c) => alongI ? RuinLayout.InRegion(site, t, c) : RuinLayout.InRegion(site, c, t);
        CellRect R(int t0, int t1, int c0, int c1) => alongI ? new CellRect(t0, c0, t1, c1) : new CellRect(c0, t0, c1, t1);

        // The region's middle across, at each step along; smoothed.
        var mid = new float[len]; var has = new bool[len];
        for (int t = 0; t < len; t++)
        {
            long sum = 0; int count = 0;
            for (int c = 0; c < across; c++) if (In(t, c)) { sum += c; count++; }
            if (count > 0) { mid[t] = sum / (float)count; has[t] = true; }
        }
        var smooth = new float[len];
        for (int t = 0; t < len; t++)
        {
            float sum = 0; int count = 0;
            for (int u = Math.Max(0, t - Smooth); u <= Math.Min(len - 1, t + Smooth); u++) if (has[u]) { sum += mid[u]; count++; }
            smooth[t] = count > 0 ? sum / count : -1;
        }
        int first = Array.IndexOf(has, true), last = Array.LastIndexOf(has, true);
        if (first < 0) return sk;

        // The spine, in straight runs, jogging across where the middle has moved more than Jog.
        var at = new int[len];
        int p = (int)MathF.Round(smooth[first]), start = first;
        for (int t = first; t <= last; t++)
        {
            int c = smooth[t] < 0 ? p : (int)MathF.Round(smooth[t]);
            if (Math.Abs(c - p) > Jog)
            {
                sk.Halls.Add((R(start, t, p - 1, p - 1 + SpineWidth), PartKind.Avenue));
                sk.Halls.Add((R(t, t + SpineWidth, Math.Min(p, c) - 1, Math.Max(p, c) - 1 + SpineWidth), PartKind.Avenue));
                p = c; start = t;
            }
            at[t] = p;
        }
        sk.Halls.Add((R(start, last + 1, p - 1, p - 1 + SpineWidth), PartKind.Avenue));

        // Branches out to the region's edge.
        int branch = 0;
        for (int t = first + rng.Range(4, BranchSpacing); t <= last - 2; t += BranchSpacing, branch++)
        {
            int c0 = at[t] - 1, c1 = c0 + SpineWidth;
            int lo = c0; while (lo > 0 && In(t, lo - 1)) lo--;
            int hi = c1; while (hi < across && In(t, hi)) hi++;
            if (c0 - lo >= 4) sk.Halls.Add((R(t, t + BranchWidth, lo, c0), PartKind.Hall));
            if (hi - c1 >= 4) sk.Halls.Add((R(t, t + BranchWidth, c1, hi), PartKind.Hall));
        }

        // Districts: each side of the spine, BranchesPerDistrict branches at a time.
        int span = BranchSpacing * BranchesPerDistrict;
        for (int t = 0; t < len; t++)
        for (int c = 0; c < across; c++)
        {
            if (!In(t, c)) continue;
            int d = (Math.Clamp(t, first, last) - first) / span * 2 + (c >= at[Math.Clamp(t, first, last)] ? 1 : 0);
            sk.DistrictOf[alongI ? t + nx * c : c + nx * t] = d;
            sk.Districts = Math.Max(sk.Districts, d + 1);
        }
        return sk;
    }
}
