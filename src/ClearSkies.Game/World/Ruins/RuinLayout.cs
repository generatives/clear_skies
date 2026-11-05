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

    public static ILayoutModel[] Models => [new CityGridModel(), new ArterialModel(), new HubSpokeModel(), new SpineModel()];

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

    /// <summary>Extra entrances: cores whose top floor is at most ExtraEntranceDepth blocks under the ground over them,
    /// at least ExtraEntranceSpacing cells from every other entrance.</summary>
    public const int ExtraEntranceDepth = 24, ExtraEntranceSpacing = 100;

    /// <summary>Stair cores, each reaching down through every floor from the top while its ground isn't open: the main
    /// entrance at the site's <see cref="RuinSite.Centre"/> (joined to the nearest hall), then one on a hall about every
    /// CoreSpacing square. Cores where the ground over the top floor is thin become extra entrances.</summary>
    private static List<Part> PlaceCores(RuinSite site, Skeleton sk, RuinPlan plan)
    {
        int nx = site.Nx, nz = site.Nz, half = CoreSize / 2;
        var cores = new List<Part>();
        bool Fits(int f, CellRect r)
        {
            for (int j = r.J0; j < r.J1; j++)
            for (int i = r.I0; i < r.I1; i++)
                if (site.At(f, i, j) is RuinSite.Ground.Out or RuinSite.Ground.Open) return false;
            return true;
        }
        Part Core(int i, int j, bool entrance)
        {
            var rect = new CellRect(i - half, j - half, i - half + CoreSize, j - half + CoreSize);
            int last = 0;
            while (last + 1 < site.Floors && Fits(last + 1, rect)) last++;
            return new Part { Rect = rect, Kind = PartKind.Core, Floor = 0, LastFloor = (byte)last, District = -1, Entrance = entrance };
        }
        int ei = site.Centre % nx, ej = site.Centre / nx;
        LinkToHalls(site, sk, ei, ej, c => site.Region[c]);
        cores.Add(Core(ei, ej, true));

        var hall = HallMask(site, sk);
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
            int bi2 = best % nx, bj2 = best / nx;
            if ((bi2 - ei) * (bi2 - ei) + (bj2 - ej) * (bj2 - ej) < CoreSpacing * CoreSpacing / 4) continue;
            var core = Core(bi2, bj2, false);
            if (!Fits(0, core.Rect)) continue;
            cores.Add(core);
        }

        // Extra entrances, shallowest first.
        int ceiling = site.FloorY(0) + RuinSite.FloorPitch - 1;
        int Depth(Part p)
        {
            int surface = int.MinValue;
            for (int j = p.Rect.J0; j < p.Rect.J1; j++)
            for (int i = p.Rect.I0; i < p.Rect.I1; i++) surface = Math.Max(surface, site.Surface[i + nx * j]);
            return surface - ceiling;
        }
        foreach (int k in Enumerable.Range(1, cores.Count - 1).OrderBy(k => Depth(cores[k])))
        {
            if (Depth(cores[k]) > ExtraEntranceDepth) break;
            var r = cores[k].Rect;
            if (cores.Any(o => o.Entrance && Math.Abs(o.Rect.I0 - r.I0) + Math.Abs(o.Rect.J0 - r.J0) < ExtraEntranceSpacing)) continue;
            var e = cores[k]; e.Entrance = true; cores[k] = e;
        }
        return cores;
    }

    internal static bool[] HallMask(RuinSite site, Skeleton sk)
    {
        var hall = new bool[site.Nx * site.Nz];
        foreach (var (rect, _) in sk.Halls) Mark(hall, site.Nx, rect.Clip(site.Nx, site.Nz));
        return hall;
    }

    /// <summary>Joins cell (i, j) to the nearest hall reachable through cells where <paramref name="through"/> holds,
    /// with an L of avenue.</summary>
    internal static void LinkToHalls(RuinSite site, Skeleton sk, int i, int j, Func<int, bool> through, int width = 2, PartKind kind = PartKind.Avenue)
    {
        int nx = site.Nx, nz = site.Nz, start = i + nx * j;
        var hall = HallMask(site, sk);
        if (hall[start]) return;
        var seen = new bool[nx * nz];
        var q = new Queue<int>();
        q.Enqueue(start); seen[start] = true;
        int found = -1;
        while (found < 0 && q.TryDequeue(out int c))
        {
            int ci = c % nx, cj = c / nx;
            foreach (var (di, dj) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1)])
            {
                int a = ci + di, b = cj + dj;
                if (a < 0 || b < 0 || a >= nx || b >= nz) continue;
                int e = a + nx * b;
                if (seen[e]) continue;
                seen[e] = true;
                if (hall[e]) { found = e; break; }
                if (through(e)) q.Enqueue(e);
            }
        }
        if (found < 0) return;
        int fi = found % nx, fj = found / nx;
        // Across then along, or along then across: whichever keeps more to cells it may go through.
        int lo = -(width / 2), hi = lo + width;
        CellRect[] acrossFirst = [new(Math.Min(i, fi), j + lo, Math.Max(i, fi) + 1, j + hi), new(fi + lo, Math.Min(j, fj), fi + hi, Math.Max(j, fj) + 1)];
        CellRect[] alongFirst = [new(i + lo, Math.Min(j, fj), i + hi, Math.Max(j, fj) + 1), new(Math.Min(i, fi), fj + lo, Math.Max(i, fi) + 1, fj + hi)];
        int Good(CellRect[] legs)
        {
            int good = 0;
            foreach (var l in legs)
            {
                var r = l.Clip(nx, nz);
                for (int b = r.J0; b < r.J1; b++)
                for (int a = r.I0; a < r.I1; a++) if (through(a + nx * b)) good++; else good -= 4;
            }
            return good;
        }
        foreach (var leg in Good(acrossFirst) >= Good(alongFirst) ? acrossFirst : alongFirst) sk.Halls.Add((leg, kind));
    }

    /// <summary>Ground on the top floor (lower floors share its halls), and its pieces: 4-way connected, so cracks part
    /// them. (Ground on any floor would join pieces whose rounded edges overlap at different heights.)</summary>
    internal static (bool[] Ground, int[] Piece, List<int> Sizes) Pieces(RuinSite site)
    {
        int nx = site.Nx, nz = site.Nz, n = nx * nz;
        var ground = new bool[n];
        for (int c = 0; c < n; c++) ground[c] = site.Grounds[0][c] is RuinSite.Ground.Solid or RuinSite.Ground.Partial;
        var piece = new int[n]; Array.Fill(piece, -1);
        var sizes = new List<int>();
        var q = new Queue<int>();
        for (int s = 0; s < n; s++)
        {
            if (!ground[s] || piece[s] >= 0) continue;
            int id = sizes.Count, size = 0;
            piece[s] = id; q.Enqueue(s);
            while (q.TryDequeue(out int c))
            {
                size++;
                int i = c % nx, j = c / nx;
                if (i > 0 && ground[c - 1] && piece[c - 1] < 0) { piece[c - 1] = id; q.Enqueue(c - 1); }
                if (i < nx - 1 && ground[c + 1] && piece[c + 1] < 0) { piece[c + 1] = id; q.Enqueue(c + 1); }
                if (j > 0 && ground[c - nx] && piece[c - nx] < 0) { piece[c - nx] = id; q.Enqueue(c - nx); }
                if (j < nz - 1 && ground[c + nx] && piece[c + nx] < 0) { piece[c + nx] = id; q.Enqueue(c + nx); }
            }
            sizes.Add(size);
        }
        return (ground, piece, sizes);
    }

    /// <summary>Crossings between pieces: up to MaxLinks per pair, LinkSpacing cells apart.</summary>
    public const int MaxLinks = 3, LinkSpacing = 60, LinkWidth = 2;

    /// <summary>Joins pieces (at least <paramref name="minPiece"/> cells) across short gaps: from every edge cell of a
    /// piece, straight out each way through what isn't ground; where that reaches another piece within BridgeCells,
    /// that's a crossing. The shortest few per pair of pieces get an avenue across, joined to each side's halls.</summary>
    internal static void LinkPieces(RuinSite site, Skeleton sk, bool[] ground, int[] piece, List<int> pieceSizes, int minPiece)
    {
        int nx = site.Nx, nz = site.Nz, n = nx * nz;
        const int AvenueWidth = LinkWidth;
        var links = new Dictionary<(int, int), List<(int Len, int A, int Di, int Dj)>>();
        for (int c = 0; c < n; c++)
        {
            int p = piece[c];
            if (p < 0 || pieceSizes[p] < minPiece) continue;
            int i = c % nx, j = c / nx;
            foreach (var (di, dj) in (ReadOnlySpan<(int, int)>)[(1, 0), (0, 1)])
            {
                if (!(i + di < nx && j + dj < nz) || ground[c + di + nx * dj]) continue;
                for (int len = 1; len <= RuinSite.BridgeCells; len++)
                {
                    int a2 = i + di * (len + 1), b2 = j + dj * (len + 1);
                    if (a2 >= nx || b2 >= nz) break;
                    int e = a2 + nx * b2;
                    if (!ground[e]) continue;
                    int o = piece[e];
                    if (o != p && pieceSizes[o] >= minPiece)
                    {
                        var key = (Math.Min(p, o), Math.Max(p, o));
                        if (!links.TryGetValue(key, out var list)) links[key] = list = [];
                        list.Add((len, c, di, dj));
                    }
                    break;
                }
            }
        }
        foreach (var list in links.Values)
        {
            // Up to MaxLinks crossings per pair, the shortest first, LinkSpacing cells apart.
            var chosen = new List<(int I, int J)>();
            foreach (var (len, from, di, dj) in list.OrderBy(l => l.Len))
            {
                int ai = from % nx, aj = from / nx;
                if (chosen.Count >= MaxLinks) break;
                if (chosen.Any(o => Math.Abs(o.I - ai) + Math.Abs(o.J - aj) < LinkSpacing)) continue;
                chosen.Add((ai, aj));
                int bi = ai + di * (len + 1), bj = aj + dj * (len + 1);
                // (Joined to each side's halls first: once the crossing is a hall, its ends already count as halls.)
                LinkToHalls(site, sk, ai, aj, c => ground[c]);
                LinkToHalls(site, sk, bi, bj, c => ground[c]);
                sk.Halls.Add((di != 0 ? new CellRect(ai, aj, bi + 1, aj + AvenueWidth) : new CellRect(ai, aj, ai + AvenueWidth, bj + 1), PartKind.Avenue));
            }
        }
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

    internal static bool InRegion(RuinSite site, int i, int j) =>
        i >= 0 && j >= 0 && i < site.Nx && j < site.Nz && site.Region[i + site.Nx * j];
}

/// <summary>
/// City grid, fitted to the land: each piece of ground (rock between cracks, on the top floor) gets its own grid. Avenues
/// split a piece's districts recursively, each cut where the land is narrowest, until none is wider than MaxDistrict;
/// after each cut a district shrinks to the ground it holds, and an avenue runs only as far as the ground under it.
/// Streets split districts into blocks, each street only along the ground on its line, and stubs shorter than MinStreet
/// are dropped. Pieces are joined by avenues across the gaps between them where those are short enough to bridge.
/// </summary>
public sealed class CityGridModel : ILayoutModel
{
    public const int MaxDistrict = 90, AvenueWidth = 2, MinStreet = 8, MinPiece = 150;
    public string Name => "grid";

    public Skeleton Build(RuinSite site, ref LayoutRng rng)
    {
        int nx = site.Nx, nz = site.Nz, n = nx * nz;
        var sk = new Skeleton(n);

        var (ground, piece, pieceSizes) = RuinLayout.Pieces(site);
        int pieces = pieceSizes.Count;
        var bounds = new CellRect[pieces];
        for (int p = 0; p < pieces; p++) bounds[p] = new CellRect(int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
        for (int c = 0; c < n; c++)
        {
            int p = piece[c];
            if (p < 0) continue;
            int i = c % nx, j = c / nx; var b = bounds[p];
            bounds[p] = new CellRect(Math.Min(b.I0, i), Math.Min(b.J0, j), Math.Max(b.I1, i + 1), Math.Max(b.J1, j + 1));
        }
        bool On(int p, int i, int j) => i >= 0 && j >= 0 && i < nx && j < nz && piece[i + nx * j] == p;
        // A rect shrunk to piece p's ground in it.
        CellRect Shrink(int p, CellRect r)
        {
            int i0 = int.MaxValue, j0 = int.MaxValue, i1 = int.MinValue, j1 = int.MinValue;
            for (int j = r.J0; j < r.J1; j++)
            for (int i = r.I0; i < r.I1; i++)
                if (On(p, i, j)) { i0 = Math.Min(i0, i); j0 = Math.Min(j0, j); i1 = Math.Max(i1, i + 1); j1 = Math.Max(j1, j + 1); }
            return i0 == int.MaxValue ? default : new CellRect(i0, j0, i1, j1);
        }
        // The ground of piece p along a line of a rect: the first and last cells on it (across = true: line i = at).
        (int From, int To) Span(int p, CellRect r, bool across, int at, int width)
        {
            int from = int.MaxValue, to = int.MinValue;
            int t0 = across ? r.J0 : r.I0, t1 = across ? r.J1 : r.I1;
            for (int t = t0; t < t1; t++)
            for (int w = 0; w < width; w++)
                if (across ? On(p, at + w, t) : On(p, t, at + w)) { from = Math.Min(from, t); to = Math.Max(to, t + 1); }
            return (from, to);
        }

        var leaves = new List<(int Piece, CellRect Rect)>();
        var stack = new Stack<CellRect>();
        for (int p = 0; p < pieces; p++)
        {
            if (pieceSizes[p] < MinPiece) continue;
            stack.Push(bounds[p]);
            while (stack.TryPop(out var r))
            {
                r = Shrink(p, r);
                if (r.IsEmpty) continue;
                if (Math.Max(r.Width, r.Depth) <= MaxDistrict) { leaves.Add((p, r)); continue; }
                bool across = r.Width >= r.Depth; // cut across i (a line of constant i) when wide
                int len = across ? r.Width : r.Depth, lo = (across ? r.I0 : r.J0) + len * 3 / 10, hi = (across ? r.I0 : r.J0) + len * 7 / 10;
                // The cut: where the fewest of the piece's cells lie on the line (a neck), a little at random among near-ties.
                int at = lo; float best = float.MaxValue;
                for (int t = lo; t <= hi; t++)
                {
                    var (f, e) = Span(p, r, across, t, AvenueWidth);
                    int count = 0;
                    if (f < e)
                        for (int u = f; u < e; u++)
                        for (int w = 0; w < AvenueWidth; w++) if (across ? On(p, t + w, u) : On(p, u, t + w)) count++;
                    float score = count + rng.Range(0f, 3f);
                    if (score < best) { best = score; at = t; }
                }
                var (from, to) = Span(p, r, across, at, AvenueWidth);
                if (from < to)
                    sk.Halls.Add((across ? new CellRect(at, from, at + AvenueWidth, to) : new CellRect(from, at, to, at + AvenueWidth), PartKind.Avenue));
                stack.Push(across ? new CellRect(r.I0, r.J0, at, r.J1) : new CellRect(r.I0, r.J0, r.I1, at));
                stack.Push(across ? new CellRect(at + AvenueWidth, r.J0, r.I1, r.J1) : new CellRect(r.I0, at + AvenueWidth, r.I1, r.J1));
            }
        }

        // Streets: along each line of a district, every run of its ground (reaching the district's edge, so it meets the
        // avenue there), if long enough.
        void Streets(int p, CellRect l, bool across, int at)
        {
            int t0 = across ? l.J0 : l.I0, t1 = across ? l.J1 : l.I1, t = t0;
            while (t < t1)
            {
                while (t < t1 && !(across ? On(p, at, t) : On(p, t, at))) t++;
                int start = t;
                while (t < t1 && (across ? On(p, at, t) : On(p, t, at))) t++;
                int a = start == t0 ? start - 1 : start, b = t == t1 ? t + 1 : t;
                if (t - start >= MinStreet)
                    sk.Halls.Add((across ? new CellRect(at, a, at + 1, b) : new CellRect(a, at, b, at + 1), PartKind.Hall));
            }
        }
        for (int d = 0; d < leaves.Count; d++)
        {
            var (p, l) = leaves[d];
            for (int j = l.J0; j < l.J1; j++)
            for (int i = l.I0; i < l.I1; i++) if (On(p, i, j)) sk.DistrictOf[i + nx * j] = d;
            int s = rng.Range(13, 16);
            for (int i = l.I0 + rng.Range(4, s); i < l.I1 - 4; i += s) Streets(p, l, true, i);
            s = rng.Range(13, 16);
            for (int j = l.J0 + rng.Range(4, s); j < l.J1 - 4; j += s) Streets(p, l, false, j);
        }
        sk.Districts = leaves.Count;

        RuinLayout.LinkPieces(site, sk, ground, piece, pieceSizes, MinPiece);
        return sk;
    }
}

/// <summary>
/// Arterial: roads grown out from the entrance along the shape of the land. Major roads run from a root (the entrance,
/// or a piece's most interior cell) along the land's backbone, preferring cells far from its edges, to the far reaches
/// of each piece, branching where the land forks: each next road goes to the ground farthest (walking) from the roads so
/// far, until none is more than MajorReach away, and is laid as few long straight legs as keep it on the ground (no leg
/// shorter than MinLeg is split further), so roads run long and square rather than stepping along diagonals. Minor roads leave the major ones every MinorSpacing cells, square to them, out to the ground's edge; then the
/// ground still far from any road gets lanes to the nearest one. Districts are the major roads' reaches.
/// </summary>
public sealed class ArterialModel : ILayoutModel
{
    public const int MajorWidth = 3, MajorReach = 50, MinLeg = 16, MinDepth = 4, MinorSpacing = 16, MinMinor = 6,
        InfillReach = 16, MinPiece = 150;
    public string Name => "arterial";

    public Skeleton Build(RuinSite site, ref LayoutRng rng)
    {
        int nx = site.Nx, nz = site.Nz, n = nx * nz;
        var sk = new Skeleton(n);
        var (ground, piece, sizes) = RuinLayout.Pieces(site);
        bool G(int i, int j) => i >= 0 && j >= 0 && i < nx && j < nz && ground[i + nx * j];

        // Steps to the ground's edge.
        var depth = new int[n];
        var q = new Queue<int>();
        for (int c = 0; c < n; c++) if (!ground[c]) q.Enqueue(c); else depth[c] = int.MaxValue;
        while (q.TryDequeue(out int c))
        {
            int i = c % nx, j = c / nx;
            foreach (var (di, dj) in Dirs)
            {
                int a = i + di, b = j + dj;
                if (a < 0 || b < 0 || a >= nx || b >= nz) continue;
                int e = a + nx * b;
                if (depth[e] == int.MaxValue) { depth[e] = depth[c] + 1; q.Enqueue(e); }
            }
        }

        var hall = new bool[n];
        var road = new int[n]; Array.Fill(road, -1); // which major road a cell is on
        void Lay(CellRect r, PartKind kind, int id)
        {
            r = r.Clip(nx, nz);
            if (r.IsEmpty) return;
            sk.Halls.Add((r, kind));
            for (int j = r.J0; j < r.J1; j++)
            for (int i = r.I0; i < r.I1; i++) { hall[i + nx * j] = true; if (id >= 0) road[i + nx * j] = id; }
        }
        // A leg from (ai, aj) to (bi, bj): straight if they line up, else an L turning where more of it stays on ground.
        void Leg(int ai, int aj, int bi, int bj, int width, PartKind kind, int id)
        {
            int lo = -(width / 2), hi = lo + width;
            CellRect H(int i0, int i1, int j) => new(Math.Min(i0, i1) + lo, j + lo, Math.Max(i0, i1) + hi, j + hi);
            CellRect V(int i, int j0, int j1) => new(i + lo, Math.Min(j0, j1) + lo, i + hi, Math.Max(j0, j1) + hi);
            int OnGround(CellRect r) { int g = 0; for (int j = r.J0; j < r.J1; j++) for (int i = r.I0; i < r.I1; i++) g += G(i, j) ? 1 : -3; return g; }
            CellRect a1 = H(ai, bi, aj), a2 = V(bi, aj, bj), b1 = V(ai, aj, bj), b2 = H(ai, bi, bj);
            if (OnGround(a1) + OnGround(a2) >= OnGround(b1) + OnGround(b2)) { Lay(a1, kind, id); Lay(a2, kind, id); }
            else { Lay(b1, kind, id); Lay(b2, kind, id); }
        }

        // Whether an L (either way round) from a to b keeps its middle line on ground at least MajorWidth/2 + 1 from the edge.
        bool LFits(int a, int b)
        {
            int ai = a % nx, aj = a / nx, bi = b % nx, bj = b / nx;
            bool Clear(int i, int j) => G(i, j) && depth[i + nx * j] > MajorWidth / 2;
            bool Run(int i0, int j0, int i1, int j1)
            {
                for (int i = Math.Min(i0, i1); i <= Math.Max(i0, i1); i++)
                for (int j = Math.Min(j0, j1); j <= Math.Max(j0, j1); j++)
                    if (!Clear(i, j)) return false;
                return true;
            }
            return Run(ai, aj, bi, aj) && Run(bi, aj, bi, bj) || Run(ai, aj, ai, bj) && Run(ai, bj, bi, bj);
        }

        // Major roads, piece by piece (biggest first), from each piece's root.
        int majors = 0;
        var legs = new List<(int Ai, int Aj, int Bi, int Bj)>();
        foreach (int p in Enumerable.Range(0, sizes.Count).Where(p => sizes[p] >= MinPiece).OrderByDescending(p => sizes[p]))
        {
            int root = piece[site.Centre] == p ? site.Centre : -1;
            if (root < 0)
                for (int c = 0; c < n; c++) if (piece[c] == p && (root < 0 || depth[c] > depth[root])) root = c;
            // Walking cost: dearer near the edge, so roads keep to the backbone; a tree of cheapest ways from the root.
            var cost = new float[n]; Array.Fill(cost, float.MaxValue);
            var parent = new int[n];
            var pq = new PriorityQueue<int, float>();
            cost[root] = 0; parent[root] = -1; pq.Enqueue(root, 0);
            while (pq.TryDequeue(out int c, out float d))
            {
                if (d > cost[c]) continue;
                int i = c % nx, j = c / nx;
                foreach (var (di, dj) in Dirs)
                {
                    int a = i + di, b = j + dj;
                    if (!G(a, b) || piece[a + nx * b] != p) continue;
                    int e = a + nx * b;
                    float step = 1 + 8f / (1 + depth[e]);
                    if (d + step < cost[e]) { cost[e] = d + step; parent[e] = c; pq.Enqueue(e, d + step); }
                }
            }
            // Steps from the roads so far, through this piece.
            var far = new int[n];
            void FromRoads(IEnumerable<int> sources, bool reset)
            {
                if (reset) for (int c = 0; c < n; c++) far[c] = piece[c] == p ? int.MaxValue : -1;
                var fq = new Queue<int>();
                foreach (int s in sources) if (far[s] != 0 && piece[s] == p) { far[s] = 0; fq.Enqueue(s); }
                while (fq.TryDequeue(out int c))
                {
                    int i = c % nx, j = c / nx;
                    foreach (var (di, dj) in Dirs)
                    {
                        int a = i + di, b = j + dj;
                        if (a < 0 || b < 0 || a >= nx || b >= nz) continue;
                        int e = a + nx * b;
                        if (far[e] > far[c] + 1) { far[e] = far[c] + 1; fq.Enqueue(e); }
                    }
                }
            }
            FromRoads([root], true);
            var onRoad = new bool[n]; onRoad[root] = true;
            while (true)
            {
                int target = -1;
                for (int c = 0; c < n; c++)
                    if (piece[c] == p && depth[c] >= MinDepth && cost[c] < float.MaxValue && (target < 0 || far[c] > far[target])) target = c;
                if (target < 0 || far[target] <= MajorReach) break;
                // Its way back along the tree to the roads so far.
                var way = new List<int>();
                for (int c = target; c >= 0; c = parent[c]) { way.Add(c); if (onRoad[c]) break; }
                // As few long, straight legs as will do: join the ends with one L if it keeps to ground away from the
                // edge; if not, split the way at its middle and do each half the same way.
                var points = new List<int> { way[0] };
                void Simplify(int lo, int hi)
                {
                    if (hi - lo <= MinLeg || LFits(way[lo], way[hi])) { points.Add(way[hi]); return; }
                    int mid = (lo + hi) / 2;
                    Simplify(lo, mid); Simplify(mid, hi);
                }
                Simplify(0, way.Count - 1);
                int id = majors++;
                for (int k = 0; k + 1 < points.Count; k++)
                    Leg(points[k] % nx, points[k] / nx, points[k + 1] % nx, points[k + 1] / nx, MajorWidth, PartKind.Avenue, id);
                foreach (int c in way) onRoad[c] = true;
                for (int k = 0; k + 1 < points.Count; k++) legs.Add((points[k] % nx, points[k] / nx, points[k + 1] % nx, points[k + 1] / nx));
                var laid = new List<int>();
                for (int c = 0; c < n; c++) if (road[c] == id) { laid.Add(c); onRoad[c] = true; }
                FromRoads(laid.Concat(way), false);
            }
        }

        // Minor roads: square off every major leg's straight runs, both sides, out to the ground's edge or a road.
        void Minor(int i, int j, int di, int dj)
        {
            int len = 0;
            while (G(i + di * (len + 1), j + dj * (len + 1)) && !hall[(i + di * (len + 1)) + nx * (j + dj * (len + 1))]) len++;
            bool meets = i + di * (len + 1) >= 0 && j + dj * (len + 1) >= 0 && i + di * (len + 1) < nx && j + dj * (len + 1) < nz
                && hall[(i + di * (len + 1)) + nx * (j + dj * (len + 1))];
            if (len < MinMinor) return;
            int end = meets ? len + 1 : len;
            Lay(di != 0 ? new CellRect(Math.Min(i + di, i + di * end), j, Math.Max(i + di, i + di * end) + 1, j + 1)
                        : new CellRect(i, Math.Min(j + dj, j + dj * end), i + 1, Math.Max(j + dj, j + dj * end) + 1), PartKind.Hall, -1);
        }
        foreach (var (ai, aj, bi, bj) in legs)
        {
            // An L's two runs: (ai, aj)-(bi, aj) and (bi, aj)-(bi, bj), or the other way; minors along whichever lie on road.
            foreach (var (i0, j0, i1, j1) in new[] { (ai, aj, bi, aj), (bi, aj, bi, bj), (ai, aj, ai, bj), (ai, bj, bi, bj) })
            {
                bool alongI = j0 == j1;
                int t0 = alongI ? Math.Min(i0, i1) : Math.Min(j0, j1), t1 = alongI ? Math.Max(i0, i1) : Math.Max(j0, j1);
                for (int t = t0 + rng.Range(3, MinorSpacing - 3); t < t1 - 2; t += MinorSpacing)
                {
                    int ci = alongI ? t : i0, cj = alongI ? j0 : t;
                    if (!G(ci, cj) || !hall[ci + nx * cj]) continue;
                    int off = MajorWidth / 2 + 1;
                    if (alongI) { Minor(ci, cj - off + 1, 0, -1); Minor(ci, cj + off - 1, 0, 1); }
                    else { Minor(ci - off + 1, cj, -1, 0); Minor(ci + off - 1, cj, 1, 0); }
                }
            }
        }

        // Lanes to whatever ground is still far from every road.
        var reach = new int[n];
        var rq = new Queue<int>();
        for (int c = 0; c < n; c++) { reach[c] = hall[c] ? 0 : int.MaxValue; if (hall[c]) rq.Enqueue(c); }
        void Spread()
        {
            while (rq.TryDequeue(out int c))
            {
                int i = c % nx, j = c / nx;
                foreach (var (di, dj) in Dirs)
                {
                    int a = i + di, b = j + dj;
                    if (!G(a, b)) continue;
                    int e = a + nx * b;
                    if (reach[e] > reach[c] + 1) { reach[e] = reach[c] + 1; rq.Enqueue(e); }
                }
            }
        }
        Spread();
        for (int lanes = 0; lanes < 400; lanes++)
        {
            int worst = -1;
            for (int c = 0; c < n; c++)
                if (ground[c] && reach[c] != int.MaxValue && depth[c] >= 2 && (worst < 0 || reach[c] > reach[worst])) worst = c;
            if (worst < 0 || reach[worst] <= InfillReach) break;
            int before = sk.Halls.Count;
            RuinLayout.LinkToHalls(site, sk, worst % nx, worst / nx, c => ground[c], 1, PartKind.Hall);
            if (sk.Halls.Count == before) { reach[worst] = 0; continue; }
            for (int k = before; k < sk.Halls.Count; k++)
            {
                var r = sk.Halls[k].Rect.Clip(nx, nz);
                for (int j = r.J0; j < r.J1; j++)
                for (int i = r.I0; i < r.I1; i++) { int c = i + nx * j; hall[c] = true; if (reach[c] != 0) { reach[c] = 0; rq.Enqueue(c); } }
            }
            Spread();
        }

        RuinLayout.LinkPieces(site, sk, ground, piece, sizes, MinPiece);

        // Districts: the major road each cell is nearest to.
        var dq = new Queue<int>();
        for (int c = 0; c < n; c++) if (road[c] >= 0) { sk.DistrictOf[c] = road[c]; dq.Enqueue(c); }
        while (dq.TryDequeue(out int c))
        {
            int i = c % nx, j = c / nx;
            foreach (var (di, dj) in Dirs)
            {
                int a = i + di, b = j + dj;
                if (!G(a, b) || sk.DistrictOf[a + nx * b] >= 0) continue;
                sk.DistrictOf[a + nx * b] = sk.DistrictOf[c]; dq.Enqueue(a + nx * b);
            }
        }
        sk.Districts = majors;
        return sk;
    }

    private static readonly (int, int)[] Dirs = [(1, 0), (-1, 0), (0, 1), (0, -1)];
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
