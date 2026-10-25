using System.Diagnostics;
using ClearSkies.Engine.Generation;
using ClearSkies.Game.Generation;
using ClearSkies.Hydrology;
using StbImageWriteSharp;

// Prototype of the coarse water model: samples one landmass cell (see HeartGrid.Landmass) at 8-block cells, settles
// water on it with the uniform head/permeability model (see Settle), and reports how long each step takes. Writes a
// top-down map and a cross-section of the result.
//
//   dotnet run -c Release --project tools/ClearSkies.Hydrology -- [--seed 1337] [--cell 1 -1] [--passes 20000]
//       [--rain 0.002] [--report 500] [--out hydrology-out] [--snapshots]
//       [--saturate 1] [--fill-hollows] [--local-steps] [--routing] [--shell 2|all] [--cascade 32,16] [--no-falls] [--soil-capacity 2]
//       [--stone-capacity 0.05] [--air 0.15] [--soil 0.01] [--stone 0.0005] [--low-stone 0.00003] [--low-window 256]
//
// With no --cell it takes the spawn's landmass. --snapshots also writes the map at every report. It stops early once
// as much water drains off the land as rains on it (within 1%, either way). --saturate N starts the ground full of
// water up to N cells under its top; --fill-hollows starts every hollow in the land's top full; --no-falls makes free
// water fall a cell or so a pass, as it spreads. --local-steps lets slow ground take bigger steps (see
// Settle.UseLocalSteps). --shell is how many cells into solid stone the model keeps (all: every cell). --cascade settles
// on bigger cells first, each size starting from the one before, and 8-block cells from the last (stored water is
// given in 8-block cells throughout). --routing routes water in open air instantly each pass (see Settle.Routing.cs):
// only the ground is simulated. --solve solves for the settled state directly (see Settle.Solve.cs), with open air
// passing --solve-air (default: as --air; several, comma separated, go up in steps); --check N then steps N passes from it to see how far it drifts. --last is
// the cell size to end on (8). --export FILE writes the last size's solve problem out instead (see Settle.Export).
// --carve N lowers the land where water runs over it and solves again, N times (--carve-from: how many columns' rain
// must run through a column; --carve-depth, --carve-most: blocks a round; --carve-width: blocks either side a valley
// reaches, sloping up to nothing at its rim, instead of the 3x3 half-depth sides). --pieces solves each piece of land
// (land columns joined side by side, at the last cell size) on its own, through the whole cascade, and times each.
// --piece-scale N makes the generator's pieces N times wider (HeartGrid.WidenPieces). --erode N reshapes the land's top first: its broad shape (the surface blurred over --erode-blur blocks), carved by
// rivers N rounds (stream power: each column cut towards the one it drains to, more the more drains through it,
// --erode-k), then --erode-detail of the blurred-away detail added back. --erode-broad takes the broad shape from the
// generator instead (ContinentTerrain.BroadHeight: its noise without bumps, crags and dells). --rivers then carves
// the trunk streams (--river-from: columns draining through them) as channels, winding on flat ground (--meander
// blocks of noise in the routing there), with lakes dammed along them (--lake-chance) and no detail added back in them.

ulong seed = 1337;
(int X, int Z)? cell = null;
int passes = 20000, report = 500;
float rain = 0.002f;
string outDir = "hydrology-out";
bool snapshots = false, falls = true, hollows = false, localSteps = false, routing = false, solve = false, pieces = false;
int last = 8, check = 0, carve = 0, erode = 0;
float carveFrom = 20f, carveDepth = 2f, carveMost = 8f, carveWidth = 0f, erodeK = 0.02f, erodeBlur = 48f, erodeDetail = 0.3f;
bool erodeBroad = false, rivers = false;
float riverFrom = 200f, meander = 40f, lakeChance = 0.15f, marshDepth = 2f;
string? writeChannels = null;
float[] solveAir = { 0f };
var solveSet = new List<Action<Settle>>();
int saturate = -1, shell = 2;
int[] cascade = Array.Empty<int>();
var m = new Materials();
for (int a = 0; a < args.Length; a++)
{
    switch (args[a])
    {
        case "--seed": seed = ulong.Parse(args[++a]); break;
        case "--cell": cell = (int.Parse(args[++a]), int.Parse(args[++a])); break;
        case "--passes": passes = int.Parse(args[++a]); break;
        case "--report": report = int.Parse(args[++a]); break;
        case "--rain": rain = float.Parse(args[++a]); break;
        case "--out": outDir = args[++a]; break;
        case "--snapshots": snapshots = true; break;
        case "--no-falls": falls = false; break;
        case "--fill-hollows": hollows = true; break;
        case "--local-steps": localSteps = true; break;
        case "--routing": routing = true; break;
        case "--solve": solve = true; break;
        case "--solve-air": solveAir = args[++a].Split(',').Select(float.Parse).ToArray(); break;
        case "--shrink": { var v = double.Parse(args[++a]); solveSet.Add(st => st.SolveShrink = v); break; }
        case "--wet": { var v = float.Parse(args[++a]); solveSet.Add(st => st.WetFill = v); break; }
        case "--walls": { var v = float.Parse(args[++a]); solveSet.Add(st => st.WallPermeability = v); break; }
        case "--carve": carve = int.Parse(args[++a]); break;
        case "--carve-from": carveFrom = float.Parse(args[++a]); break;
        case "--carve-depth": carveDepth = float.Parse(args[++a]); break;
        case "--carve-most": carveMost = float.Parse(args[++a]); break;
        case "--carve-width": carveWidth = float.Parse(args[++a]); break;
        case "--pieces": pieces = true; break;
        case "--piece-scale": HeartGrid.WidenPieces(float.Parse(args[++a])); break;
        case "--erode": erode = int.Parse(args[++a]); break;
        case "--erode-k": erodeK = float.Parse(args[++a]); break;
        case "--erode-blur": erodeBlur = float.Parse(args[++a]); break;
        case "--erode-detail": erodeDetail = float.Parse(args[++a]); break;
        case "--erode-broad": erodeBroad = true; break;
        case "--rivers": rivers = true; break;
        case "--marsh-depth": marshDepth = float.Parse(args[++a]); break;
        case "--river-from": riverFrom = float.Parse(args[++a]); break;
        case "--meander": meander = float.Parse(args[++a]); break;
        case "--lake-chance": lakeChance = float.Parse(args[++a]); break;
        case "--new-tau": solveSet.Add(st => st.NewTau = true); break;
        case "--adaptive": solveSet.Add(st => st.AdaptiveShrink = true); break;
        case "--separate-pieces": solveSet.Add(st => st.SeparatePieces = true); break;
        case "--coarse-scale": Settle.CoarseScale = double.Parse(args[++a]); break;
        case "--gauss-seidel": Settle.GaussSeidel = true; break;
        case "--tau-growth": { var v = double.Parse(args[++a]); solveSet.Add(st => st.TauGrowth = v); break; }
        case "--start-fill": { var v = float.Parse(args[++a]); solveSet.Add(st => st.StartFill = v); break; }
        case "--rounds": { var v = int.Parse(args[++a]); solveSet.Add(st => st.SolveRounds = v); break; }
        case "--check": check = int.Parse(args[++a]); break;
        case "--last": last = int.Parse(args[++a]); break;
        case "--export": { var v = args[++a]; solveSet.Add(st => st.ExportTo = v); break; }
        case "--shell": { string v = args[++a]; shell = v == "all" ? int.MaxValue : int.Parse(v); break; }
        case "--cascade": cascade = args[++a].Split(',').Select(int.Parse).ToArray(); break;
        case "--saturate": saturate = int.Parse(args[++a]); break;
        case "--soil-capacity": m = m with { SoilCapacity = float.Parse(args[++a]) }; break;
        case "--stone-capacity": m = m with { StoneCapacity = float.Parse(args[++a]) }; break;
        case "--air": m = m with { AirPermeability = float.Parse(args[++a]) }; break;
        case "--soil-depth": m = m with { SoilDepth = int.Parse(args[++a]) }; break;
        case "--soil": m = m with { SoilPermeability = float.Parse(args[++a]) }; break;
        case "--stone": m = m with { StonePermeability = float.Parse(args[++a]) }; break;
        case "--low-stone": m = m with { LowStone = float.Parse(args[++a]) }; break;
        case "--low-window": m = m with { LowWindow = float.Parse(args[++a]) }; break;
        default: Console.Error.WriteLine($"Unknown argument {args[a]}"); return 1;
    }
}
var landmass = cell ?? HeartGrid.Landmass(seed, 11250f, -6250f);
Console.WriteLine($"Seed {seed}, landmass {landmass}, {Environment.ProcessorCount} cores, {m}");

// The cell's bounds: where its own ground can be, found on a coarse grid around it.
var total = Stopwatch.StartNew();
float spacing = HeartGrid.LandmassSpacing;
int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
for (float z = (landmass.Z - 0.7f) * spacing; z <= (landmass.Z + 1.7f) * spacing; z += 64f)
for (float x = (landmass.X - 0.7f) * spacing; x <= (landmass.X + 1.7f) * spacing; x += 64f)
{
    if (HeartGrid.Landmass(seed, x, z) != landmass) continue;
    x0 = Math.Min(x0, (int)x); z0 = Math.Min(z0, (int)z); x1 = Math.Max(x1, (int)x); z1 = Math.Max(z1, (int)z);
}
x0 = CoarseLand.FloorDiv(x0 - 64, 32) * 32; z0 = CoarseLand.FloorDiv(z0 - 64, 32) * 32;
x1 = CoarseLand.FloorDiv(x1 + 96, 32) * 32; z1 = CoarseLand.FloorDiv(z1 + 96, 32) * 32;
Console.WriteLine($"Bounds x {x0}..{x1}, z {z0}..{z1} ({(x1 - x0) / 1000f:0.0} x {(z1 - z0) / 1000f:0.0} km)");

var lift = erode > 0 ? Erode() : null;
if (pieces) return SolvePieces();

// Each cell size in turn, the last 8: each settles from the one before (see Settle.StartFrom).
CoarseLand land = null!;
Settle settle = null!, rough = null!;
foreach (int size in cascade.Append(last))
{
    var sw = Stopwatch.StartNew();
    land = new CoarseLand(seed, landmass, x0, z0, x1, z1, shell, size, lift: lift);
    int landColumns = 0;
    for (int c = 0; c < land.Columns; c++) if (land.HasLand(c)) landColumns++;
    Console.WriteLine($"{size}-block cells: sampled {land.Columns:N0} columns ({landColumns:N0} with land) in " +
                      $"{sw.Elapsed.TotalSeconds:0.00} s: {land.Cells:N0} cells kept of {land.Spanned:N0}, " +
                      $"{land.Cells * 10L * 4 / 1e6:0} MB as the model holds them (10 floats a cell), " +
                      $"{land.Piece.Where((p, c) => land.HasLand(c)).Distinct().Count()} pieces on top");

    sw.Restart();
    settle = new Settle(land, m, rain) { Falls = falls };
    if (routing) settle.UseRouting();
    if (localSteps) settle.UseLocalSteps();
    if (rough != null) settle.StartFrom(rough);
    else
    {
        if (saturate >= 0) settle.Saturate(saturate);
        if (hollows && !routing) Console.WriteLine($"Filled hollows: {settle.FillHollows():N0} columns under water");
    }
    Console.WriteLine($"Set up in {sw.Elapsed.TotalSeconds:0.00} s");
    if (solve)
    {
        foreach (var set in solveSet) set(settle);
        if (rough != null) settle.StartHeadsFrom(rough);
        // Open air's permeability raised in steps (if given more than one), each solve starting from the one before:
        // the further from where it starts, the harder the solve.
        for (int s = 0; s < solveAir.Length; s++)
        {
            if (rough != null && s < solveAir.Length - 1) continue; // bigger cells already went up the steps
            settle.SolveAir = solveAir[s];
            Console.WriteLine($"Open air passes {(solveAir[s] > 0 ? solveAir[s] : m.AirPermeability)}");
            settle.SolveSteady(fromHeads: rough != null || s > 0);
        }
        Console.WriteLine($"Total so far {total.Elapsed.TotalSeconds:0.0} s");
        rough = settle;
        if (size != last || check == 0) continue;
        passes = check; // then step from the solve, to see how far it drifts
    }

    sw.Restart();
    var window = Stopwatch.StartNew();
    double rained = 0, drained = 0;
    int done = 0;
    for (int p = 1; p <= passes; p++)
    {
        settle.Pass();
        rained += settle.RainedLastPass; drained += settle.DrainedLastPass;
        done = p;
        if (p % report != 0) continue;
        double ratio = rained > 0 ? drained / rained : 0;
        Console.WriteLine($"pass {p,6}: {window.Elapsed.TotalMilliseconds / report:0.0} ms/pass, stored {settle.Stored * size * size * size / 512:0}, " +
                          $"drained/rained over the last {report} {ratio:0.000}");
        if (snapshots) { Directory.CreateDirectory(outDir); WriteMap(Path.Combine(outDir, $"map-{size}-{p:D6}.png")); }
        window.Restart();
        if (Math.Abs(ratio - 1) < 0.01) { Console.WriteLine($"Settled: as much drains as rains, after {p} passes"); break; }
        rained = drained = 0;
    }
    Console.WriteLine($"{done} passes in {sw.Elapsed.TotalSeconds:0.0} s ({sw.Elapsed.TotalMilliseconds / done:0.0} ms/pass); " +
                      $"total so far {total.Elapsed.TotalSeconds:0.0} s");
    Console.WriteLine("Per pass: " + string.Join(", ", new[] { "falls and heads", "flows", "shares", "update" }
        .Select((name, k) => $"{name} {settle.SweepSeconds[k] * 1000 / done:0.0} ms")));
    rough = settle;
}

// Carving: where water runs over the land, lower it (more, the more runs), the valley sides too, then solve again
// from where it was; the water gathers into what's carved, and the next round carves that deeper.
for (int round = 1; round <= carve; round++)
{
    Directory.CreateDirectory(outDir);
    if (settle.Runs != null) WriteFlow(Path.Combine(outDir, $"flow-{round - 1}.png"));
    writeChannels = Path.Combine(outDir, $"channels-{round - 1}.png");
    var depth = Channels();
    var sides = (float[])depth.Clone();
    int reach = carveWidth > 0f ? (int)MathF.Ceiling(carveWidth / land.Size) : 1;
    for (int j = 0; j < land.Nz; j++)
    for (int i = 0; i < land.Nx; i++)
    {
        int c = i + land.Nx * j;
        for (int dj = -reach; dj <= reach; dj++)
        for (int di = -reach; di <= reach; di++)
        {
            int ni = i + di, nj = j + dj;
            if (ni < 0 || nj < 0 || ni >= land.Nx || nj >= land.Nz) continue;
            float share = carveWidth > 0f ? 1f - land.Size * MathF.Sqrt(di * di + dj * dj) / carveWidth : 0.5f;
            if (share > 0f) sides[c] = Math.Max(sides[c], share * depth[ni + land.Nx * nj]);
        }
    }
    double carved = 0; int columnsCarved = 0;
    for (int c = 0; c < land.Columns; c++)
        if (sides[c] > 0f) { carved += land.Carve(c, sides[c]); columnsCarved++; }
    Console.WriteLine($"Carving round {round}: {columnsCarved:N0} columns lowered, {carved / Math.Max(1, columnsCarved):0.0} blocks on average");
    var before = settle;
    settle = new Settle(land, m, rain) { Falls = falls };
    foreach (var set in solveSet) set(settle);
    settle.SolveAir = solveAir[^1];
    settle.StartHeadsFrom(before);
    settle.SolveSteady(fromHeads: true);
}

Directory.CreateDirectory(outDir);
WriteMap(Path.Combine(outDir, "map.png"));
WriteSection(Path.Combine(outDir, "section.png"));
if (settle.Runs != null) WriteFlow(Path.Combine(outDir, "flow.png"));
Console.WriteLine($"Wrote {Path.GetFullPath(outDir)}/map.png and section.png");
return 0;

// Each piece on its own: pieces are found as land columns joined side by side at the last cell size, then each one
// goes through the cascade alone, its land sampled over its own bounds with only its columns kept.
int SolvePieces()
{
    var sw = Stopwatch.StartNew();
    var whole = new CoarseLand(seed, landmass, x0, z0, x1, z1, shell, last);
    int nx = whole.Nx, nz = whole.Nz;
    var piece = new int[whole.Columns];
    Array.Fill(piece, -1);
    var found = new List<(int Columns, int I0, int J0, int I1, int J1)>();
    var stack = new Stack<int>();
    for (int c0 = 0; c0 < whole.Columns; c0++)
    {
        if (!whole.HasLand(c0) || piece[c0] >= 0) continue;
        int id = found.Count, count = 0, i0 = int.MaxValue, j0 = int.MaxValue, i1 = 0, j1 = 0;
        piece[c0] = id; stack.Push(c0);
        while (stack.TryPop(out int c))
        {
            int i = c % nx, j = c / nx;
            count++; i0 = Math.Min(i0, i); j0 = Math.Min(j0, j); i1 = Math.Max(i1, i); j1 = Math.Max(j1, j);
            foreach (int d in stackalloc[] { i + 1 < nx ? c + 1 : -1, i > 0 ? c - 1 : -1, j + 1 < nz ? c + nx : -1, j > 0 ? c - nx : -1 })
                if (d >= 0 && whole.HasLand(d) && piece[d] < 0) { piece[d] = id; stack.Push(d); }
        }
        found.Add((count, i0, j0, i1, j1));
    }
    Console.WriteLine($"{found.Count} pieces at {last}-block cells in {sw.Elapsed.TotalSeconds:0.0} s, columns: " +
                      string.Join(", ", found.Select(f => f.Columns).OrderDescending().Take(40)));

    double slowest = 0, sum = 0;
    int solved = 0;
    foreach (int k in Enumerable.Range(0, found.Count).OrderByDescending(k => found[k].Columns))
    {
        var f = found[k];
        if (f.Columns < 16) continue; // specks
        int px0 = CoarseLand.FloorDiv(x0 + f.I0 * last - 64, 32) * 32, pz0 = CoarseLand.FloorDiv(z0 + f.J0 * last - 64, 32) * 32;
        int px1 = CoarseLand.FloorDiv(x0 + (f.I1 + 1) * last + 95, 32) * 32, pz1 = CoarseLand.FloorDiv(z0 + (f.J1 + 1) * last + 95, 32) * 32;
        bool Keep(int x, int z)
        {
            int i = CoarseLand.FloorDiv(x - x0, last), j = CoarseLand.FloorDiv(z - z0, last);
            return i >= 0 && j >= 0 && i < nx && j < nz && piece[i + nx * j] == k;
        }
        var time = Stopwatch.StartNew();
        Settle? before = null;
        var output = Console.Out;
        var log = new StringWriter();
        Console.SetOut(log);
        foreach (int size in cascade.Append(last))
        {
            var pland = new CoarseLand(seed, landmass, px0, pz0, px1, pz1, shell, size, Keep, lift);
            var s = new Settle(pland, m, rain) { Falls = falls };
            foreach (var set in solveSet) set(s);
            if (before != null) s.StartHeadsFrom(before);
            else if (saturate >= 0) s.Saturate(saturate);
            s.SolveAir = solveAir[^1];
            s.SolveSteady(fromHeads: before != null);
            before = s;
        }
        Console.SetOut(output);
        double t = time.Elapsed.TotalSeconds;
        slowest = Math.Max(slowest, t); sum += t; solved++;
        var lines = log.ToString().Split('\n');
        var rounds = lines.Where(l => l.StartsWith("round")).ToList();
        Console.WriteLine("  " + lines.LastOrDefault(l => l.StartsWith("Dry cells"))?.Trim());
        Console.WriteLine($"piece {k}: {f.Columns:N0} columns, {before!.Land.Cells:N0} cells, {t:0.0} s, last size " +
                          (rounds.Count > 0 ? rounds[^1].Trim() : "no rounds"));
    }
    Console.WriteLine($"{solved} pieces in {sum:0.0} s one after another, the slowest {slowest:0.0} s; total {total.Elapsed.TotalSeconds:0.0} s");
    return 0;
}

// The land's top reshaped by rivers (see --erode): returns how far each column's top moves, by block position.
Func<int, int, int> Erode()
{
    const int S = 8;
    var sw = Stopwatch.StartNew();
    var land8 = new CoarseLand(seed, landmass, x0, z0, x1, z1, shell, S);
    int nx = land8.Nx, nz = land8.Nz, n = land8.Columns;
    var has = new bool[n]; var h0 = new double[n];
    var piece = land8.Piece; // cracks between pieces are edges, however narrow
    for (int c = 0; c < n; c++)
    {
        has[c] = land8.HasLand(c);
        if (has[c]) h0[c] = S * (land8.Top[c] + land8.Solid[land8.Index(c, land8.Top[c])]);
    }
    // The broad shape: a box blur over land only, three times each way (close to a gaussian).
    int r = Math.Max(1, (int)(erodeBlur / S / 2));
    var b = (double[])h0.Clone();
    if (erodeBroad)
    {
        var terrain = ContinentTerrain.For(seed);
        Parallel.For(0, n, c =>
        {
            if (!has[c]) return;
            float x = x0 + c % nx * S + S / 2, z = z0 + c / nx * S + S / 2;
            b[c] = h0[c] - (terrain.Height(x, z) - terrain.BroadHeight(x, z));
        });
    }
    else for (int pass = 0; pass < 3; pass++) { b = Blur(b, true); b = Blur(b, false); }
    var h = (double[])b.Clone();
    var recv = new int[n]; var area = new double[n]; var level = new double[n];
    const double EdgeDrop = 64, EdgeKeep = 48;
    var floor = new double[n];
    for (int c = 0; c < n; c++) floor[c] = has[c] ? S * land8.Lo[c] + EdgeKeep : 0;
    var order = new List<int>(n);
    int hollowsBefore = Route(h0);
    for (int round = 0; round < erode; round++)
    {
        Route(h);
        // Downstream first: each column moves towards where it drains (implicitly, so any K is stable).
        foreach (int c in order)
        {
            int to = recv[c];
            // An edge drains over the cliff into the void: it wears down towards the land's underside (keeping
            // EdgeKeep blocks of it), so big rivers notch the piece's edge and the hollows behind it drain.
            double below = to < 0 ? Math.Max(floor[c], h[c] - EdgeDrop) : h[to];
            if (below >= h[c]) continue; // in a hollow
            double dist = to >= 0 && c % nx != to % nx && c / nx != to / nx ? 1.414 : 1.0;
            double f = erodeK * Math.Sqrt(area[c]) / dist;
            h[c] = Math.Max(floor[c], (h[c] + f * below) / (1 + f));
        }
    }
    var mask = new double[n];
    int riverColumns = 0, lakes = 0;
    if (rivers) (riverColumns, lakes) = Rivers(h, mask);
    var final = new double[n];
    var delta = new int[n];
    double cut = 0; int moved = 0, most = 0;
    for (int c = 0; c < n; c++)
    {
        if (!has[c]) continue;
        final[c] = h[c] + erodeDetail * (1 - mask[c]) * (h0[c] - b[c]);
        delta[c] = (int)Math.Round(final[c] - h0[c]);
        if (delta[c] != 0) moved++;
        cut += Math.Abs(delta[c]); most = Math.Max(most, Math.Abs(delta[c]));
    }
    int hollowsAfter = Route(final);
    {
        int land = 0, deep = 0; foreach (int c in order) { land++; if (level[c] > final[c] + 8) deep++; }
        int bare = Route(h); int bareDeep = 0; foreach (int c in order) if (level[c] > h[c] + 8) bareDeep++;
        Route(final);
        Console.WriteLine($"Hollow check: {land:N0} land columns; final {hollowsAfter:N0} (over 8 deep {deep:N0}); eroded without detail {bare:N0} (over 8 deep {bareDeep:N0})");
    }
    Console.WriteLine($"Eroded {erode} rounds in {sw.Elapsed.TotalSeconds:0.0} s: {moved:N0} columns moved, {cut / Math.Max(1, moved):0} blocks on average " +
                      $"(most {most}); columns in hollows {hollowsBefore:N0} before, {hollowsAfter:N0} after" +
                      (rivers ? $"; {riverColumns:N0} river columns, {lakes} lakes dammed" : ""));
    Directory.CreateDirectory(outDir);
    Relief(Path.Combine(outDir, "relief-before.png"), h0);
    Relief(Path.Combine(outDir, "relief-after.png"), final);
    return (x, z) =>
    {
        int i = CoarseLand.FloorDiv(x - x0, S), j = CoarseLand.FloorDiv(z - z0, S);
        return i >= 0 && j >= 0 && i < nx && j < nz ? delta[i + nx * j] : 0;
    };

    double[] Blur(double[] v, bool alongX)
    {
        var o = new double[n];
        int lines = alongX ? nz : nx, len = alongX ? nx : nz;
        Parallel.For(0, lines, line =>
        {
            for (int k = 0; k < len; k++)
            {
                int c = alongX ? k + nx * line : line + nx * k;
                if (!has[c]) continue;
                double sum = 0; int count = 0;
                for (int q = Math.Max(0, k - r); q <= Math.Min(len - 1, k + r); q++)
                {
                    int d = alongX ? q + nx * line : line + nx * q;
                    if (has[d]) { sum += v[d]; count++; }
                }
                o[c] = sum / count;
            }
        });
        return o;
    }

    // Where each column drains (a priority flood in from the edges, so hollows drain by their spill points), the
    // columns draining through each, and how many columns sit in hollows.
    // Trunk rivers carved into the land: routed with noise on flat ground (so they wind), their beds never rising
    // downstream, channels wider and deeper the more drains through them with banks sloping up into the land, and now
    // and then a lake dammed where one runs down a valley. Marks where it carved (1 in channels, fading over the banks).
    (int, int) Rivers(double[] height, double[] carved)
    {
        var noise = new FastNoiseLite(unchecked((int)seed + 77));
        noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        noise.SetFractalType(FastNoiseLite.FractalType.FBm);
        noise.SetFractalOctaves(2);
        noise.SetFrequency(1f / 300f);
        var slope = new double[n]; var prio = new double[n];
        for (int c = 0; c < n; c++)
        {
            if (!has[c]) continue;
            int i = c % nx, j = c / nx;
            double drop = 0;
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                int ni = i + di, nj = j + dj;
                if ((di | dj) == 0 || ni < 0 || nj < 0 || ni >= nx || nj >= nz || !has[ni + nx * nj]) continue;
                drop = Math.Max(drop, (height[c] - height[ni + nx * nj]) / (S * Math.Sqrt(di * di + dj * dj)));
            }
            slope[c] = drop;
            double flat = Math.Clamp(1 - drop / 0.05, 0, 1);
            prio[c] = height[c] + meander * Math.Abs(noise.GetNoise(x0 + i * S, z0 + j * S)) * flat;
        }
        Route(prio);
        var bed = new double[n];
        var uncut = new bool[n];
        Array.Fill(bed, double.NaN);
        int columns = 0;
        foreach (int c in order)
        {
            if (area[c] < riverFrom) continue;
            // Only big rivers are cut into flats; a smaller one crossing a flat keeps its bed at the ground there, so
            // the channel above it stays above it too (rather than ending in a pit that fills as a round pond).
            bool cutHere = slope[c] >= 0.01 || area[c] >= 5 * riverFrom;
            double target = cutHere ? height[c] - Math.Min(10, 3 * Math.Sqrt(area[c] / riverFrom)) : height[c];
            int to = recv[c];
            bed[c] = to >= 0 && !double.IsNaN(bed[to]) ? Math.Max(bed[to], target) : target;
            if (cutHere) columns++;
            else uncut[c] = true;
        }
        var cut = (double[])height.Clone();
        // Channels and banks.
        const double BankReach = 24, BankGrade = 0.15;
        foreach (int c in order)
        {
            if (double.IsNaN(bed[c]) || uncut[c]) continue;
            double width = Math.Clamp(0.6 * Math.Sqrt(area[c] / riverFrom), 0.5, 4); // cells either side
            int reach = (int)Math.Ceiling(width + BankReach / S), ci = c % nx, cj = c / nx;
            for (int dj = -reach; dj <= reach; dj++)
            for (int di = -reach; di <= reach; di++)
            {
                int ni = ci + di, nj = cj + dj;
                if (ni < 0 || nj < 0 || ni >= nx || nj >= nz || !has[ni + nx * nj]) continue;
                int k = ni + nx * nj;
                double past = Math.Max(0, Math.Sqrt(di * di + dj * dj) - width) * S;
                if (past > BankReach) continue;
                cut[k] = Math.Min(cut[k], bed[c] + past * BankGrade);
                carved[k] = Math.Max(carved[k], 1 - past / BankReach);
            }
        }
        // Lakes: now and then, where a bigger river runs down a valley, a sill across it a little way downstream
        // (a natural dam: a rockfall, a moraine) raised LakeDepth over the bed, so the river backs up behind it and
        // fills the valley to that level, its shore wherever the land meets it.
        int dug = 0;
        var lakeAt = new List<int>();
        const double LakeDepth = 8, LakeSpacing = 160;
        foreach (int c in order)
        {
            if (double.IsNaN(bed[c]) || area[c] < 4 * riverFrom || slope[c] < 0.01 || slope[c] > 0.1) continue;
            uint hash = (uint)(c * 2654435761u ^ (uint)seed);
            if ((hash >> 8) / (double)(1 << 24) >= lakeChance) continue;
            int sill = recv[c] >= 0 ? recv[recv[c]] : -1;
            if (sill < 0 || double.IsNaN(bed[sill])) continue;
            int si = sill % nx, sj = sill / nx;
            if (lakeAt.Any(l => Math.Abs(l % nx - si) + Math.Abs(l / nx - sj) < LakeSpacing / S)) continue;
            lakeAt.Add(sill); dug++;
            double level = bed[c] + LakeDepth;
            int reach = (int)Math.Ceiling(Math.Clamp(0.6 * Math.Sqrt(area[sill] / riverFrom), 0.5, 4) + 3);
            for (int dj = -reach; dj <= reach; dj++)
            for (int di = -reach; di <= reach; di++)
            {
                int ni = si + di, nj = sj + dj;
                if (ni < 0 || nj < 0 || ni >= nx || nj >= nz || !has[ni + nx * nj] || di * di + dj * dj > reach * reach) continue;
                int k = ni + nx * nj;
                if (cut[k] < level) { cut[k] = level; carved[k] = 1; }
            }
        }
        Array.Copy(cut, height, n);
        return (columns, dug);
    }

    int Route(double[] height)
    {
        Array.Fill(recv, -1); Array.Fill(level, double.NaN); order.Clear();
        var queue = new PriorityQueue<int, double>();
        for (int c = 0; c < n; c++)
        {
            if (!has[c]) continue;
            int i = c % nx, j = c / nx;
            bool edge = false;
            for (int dj = -1; dj <= 1 && !edge; dj++)
            for (int di = -1; di <= 1 && !edge; di++)
            {
                int ni = i + di, nj = j + dj;
                edge = ni < 0 || nj < 0 || ni >= nx || nj >= nz || !has[ni + nx * nj] || piece[ni + nx * nj] != piece[c];
            }
            if (edge) { level[c] = height[c]; queue.Enqueue(c, height[c]); }
        }
        while (queue.TryDequeue(out int c, out double at))
        {
            order.Add(c);
            int i = c % nx, j = c / nx;
            for (int dj = -1; dj <= 1; dj++)
            for (int di = -1; di <= 1; di++)
            {
                int ni = i + di, nj = j + dj;
                if (ni < 0 || nj < 0 || ni >= nx || nj >= nz) continue;
                int d = ni + nx * nj;
                if (!has[d] || piece[d] != piece[c] || !double.IsNaN(level[d])) continue;
                level[d] = Math.Max(at, height[d]);
                recv[d] = c;
                queue.Enqueue(d, level[d]);
            }
        }
        Array.Clear(area);
        for (int c = 0; c < n; c++) if (has[c]) area[c] = 1;
        for (int k = order.Count - 1; k >= 0; k--) if (recv[order[k]] >= 0) area[recv[order[k]]] += area[order[k]];
        int hollows = 0;
        foreach (int c in order) if (level[c] > height[c] + 1) hollows++;
        return hollows;
    }

    // Shaded relief: height as brightness, lit from the north-west, with where much drains through it blue.
    void Relief(string path, double[] height)
    {
        Route(height);
        var rgb = new byte[n * 3];
        for (int c = 0; c < n; c++)
        {
            int o = c * 3, i = c % nx, j = c / nx;
            if (!has[c]) { rgb[o] = 18; rgb[o + 1] = 22; rgb[o + 2] = 34; continue; }
            double Hat(int di, int dj)
            {
                int ni = Math.Clamp(i + di, 0, nx - 1), nj = Math.Clamp(j + dj, 0, nz - 1), d = ni + nx * nj;
                return has[d] ? height[d] : height[c];
            }
            double light = Math.Clamp(0.6 + ((Hat(-1, -1) - Hat(1, 1)) / (2 * S)) * 0.8, 0.15, 1.0);
            double t = Math.Clamp((height[c] - 300) / 900, 0, 1);
            double w = Math.Clamp(Math.Log10(Math.Max(area[c], 1)) / 3.5 - 0.3, 0, 1);
            bool lake = level[c] > height[c] + 1;
            double g = (90 + 130 * t) * light;
            rgb[o] = (byte)(lake ? 50 : g * (1 - w) + 40 * w);
            rgb[o + 1] = (byte)(lake ? 110 : g * (1 - w) + 120 * w);
            rgb[o + 2] = (byte)(lake ? 210 : (g * 0.85) * (1 - w) + 255 * w);
        }
        Save(path, nx, nz, rgb);
    }
}

// Top-down: land shaded by height and greener where its top cell is wetter, open water blue by depth, void dark.
// Drainage lines over the land's surface: a priority flood in from the columns beside a crack or the void (where water
// leaves) gives each column the one it drains to (over hollows, by their spill point); water coming out of the ground
// (springs) is added up along those, and columns carrying enough get a channel, deeper the more (lakes left alone).
float[] Channels()
{
    int nx = land.Nx, nz = land.Nz, n = land.Columns;
    var surface = new float[n];
    for (int c = 0; c < n; c++)
        if (land.HasLand(c)) surface[c] = land.Size * (land.Top[c] + land.Solid[land.Index(c, land.Top[c])]);
    var level = new float[n]; var to = new int[n]; var order = new List<int>(n);
    Array.Fill(level, float.NaN); Array.Fill(to, -1);
    var queue = new PriorityQueue<int, float>();
    for (int c = 0; c < n; c++)
    {
        if (!land.HasLand(c)) continue;
        int i = c % nx, j = c / nx;
        bool edge = false;
        for (int dj = -1; dj <= 1 && !edge; dj++)
        for (int di = -1; di <= 1 && !edge; di++)
        {
            int ni = i + di, nj = j + dj;
            edge = ni < 0 || nj < 0 || ni >= nx || nj >= nz || !land.HasLand(ni + nx * nj);
        }
        if (edge) { level[c] = surface[c]; queue.Enqueue(c, surface[c]); }
    }
    while (queue.TryDequeue(out int c, out float at))
    {
        order.Add(c);
        int i = c % nx, j = c / nx;
        for (int dj = -1; dj <= 1; dj++)
        for (int di = -1; di <= 1; di++)
        {
            int ni = i + di, nj = j + dj;
            if (ni < 0 || nj < 0 || ni >= nx || nj >= nz) continue;
            int d = ni + nx * nj;
            if (!land.HasLand(d) || !float.IsNaN(level[d])) continue;
            level[d] = Math.Max(at, surface[d]);
            to[d] = c;
            queue.Enqueue(d, level[d]);
        }
    }
    var gathered = new float[n];
    for (int k = order.Count - 1; k >= 0; k--)
    {
        int c = order[k];
        gathered[c] += settle.Springs![c];
        if (to[c] >= 0) gathered[to[c]] += gathered[c];
    }
    var depth = new float[n];
    int channels = 0;
    for (int c = 0; c < n; c++)
    {
        float columns = gathered[c] / settle.Rain;
        bool lake = level[c] > surface[c] + 1f;
        if (lake || columns < carveFrom) continue;
        depth[c] = Math.Min(carveMost, carveDepth * MathF.Pow(columns / carveFrom, 0.4f));
        channels++;
    }
    Console.WriteLine($"Channels: {channels:N0} columns carry {carveFrom} columns' rain or more");
    if (writeChannels != null)
    {
        var rgb = new byte[n * 3];
        for (int c = 0; c < n; c++)
        {
            int o = c * 3;
            if (!land.HasLand(c)) { rgb[o] = 18; rgb[o + 1] = 22; rgb[o + 2] = 34; continue; }
            float t = Math.Clamp((surface[c] - 300f) / 900f, 0f, 1f), g = 70 + 60 * t;
            float w = Math.Clamp(MathF.Log10(Math.Max(gathered[c] / settle.Rain, 1f)) / 3.5f, 0f, 1f);
            bool lake = level[c] > surface[c] + 1f;
            rgb[o] = (byte)(lake ? 40 : g * (1 - w) + 60 * w); rgb[o + 1] = (byte)(lake ? 90 : g * (1 - w) + 160 * w);
            rgb[o + 2] = (byte)(lake ? 200 : g * (1 - w) + 255 * w);
        }
        Save(writeChannels, nx, nz, rgb);
        writeChannels = null;
    }
    return depth;
}

// Where water goes, once solved: land grey by height, water running over it blue (brighter, more of it, on a log
// scale from a column's own rain to a thousand columns' worth), springs (water out of the ground) red.
void WriteFlow(string path)
{
    var rgb = new byte[land.Columns * 3];
    float unit = settle.Rain;
    for (int c = 0; c < land.Columns; c++)
    {
        int o = c * 3;
        if (!land.HasLand(c)) { rgb[o] = 18; rgb[o + 1] = 22; rgb[o + 2] = 34; continue; }
        float t = Math.Clamp((land.Top[c] * land.Size - 300f) / 900f, 0f, 1f);
        float run = Math.Clamp(MathF.Log10(Math.Max(settle.Runs![c] / unit, 1f)) / 3f, 0f, 1f);
        float spring = Math.Clamp(MathF.Log10(Math.Max(settle.Springs![c] / unit, 1f)) / 2f, 0f, 1f);
        float g = 70 + 60 * t;
        rgb[o] = (byte)Math.Clamp(g * (1 - run) + 40 * run + 200 * spring, 0, 255);
        rgb[o + 1] = (byte)Math.Clamp(g * (1 - run) + 140 * run, 0, 255);
        rgb[o + 2] = (byte)Math.Clamp(g * (1 - run) + 255 * run, 0, 255);
    }
    Save(path, land.Nx, land.Nz, rgb);
}

void WriteMap(string path)
{
    var rgb = new byte[land.Columns * 3];
    var water = settle.Water;
    var depths = new int[6]; // open water over land columns: none, under 1/4 block, 1, 4, 16 blocks, deeper
    int marsh = 0;
    for (int c = 0; c < land.Columns; c++)
    {
        int o = c * 3;
        if (!land.HasLand(c)) { rgb[o] = 18; rgb[o + 1] = 22; rgb[o + 2] = 34; continue; }
        int top = land.Index(c, land.Top[c]);
        float open = 0f;
        for (int y = land.Top[c]; y <= land.Hi[c]; y++)
        {
            int i = land.Index(c, y);
            if (i < 0) continue;
            open += settle.Solved ? settle.OpenBlocks(i) / land.Size : water[i] * (1f - land.Solid[i]) / settle.Capacity(i);
        }
        float blocks = open * land.Size;
        depths[blocks <= 0.01f ? 0 : blocks < 0.25f ? 1 : blocks < 1 ? 2 : blocks < 4 ? 3 : blocks < 16 ? 4 : 5]++;
        // Marsh: shallow water standing on flat ground (its top within a cell of its neighbours'), shown as wet ground.
        int ci = c % land.Nx, cj = c / land.Nx, rise = 0;
        foreach (int d in new[] { ci + 1 < land.Nx ? c + 1 : -1, ci > 0 ? c - 1 : -1, cj + 1 < land.Nz ? c + land.Nx : -1, cj > 0 ? c - land.Nx : -1 })
            if (d >= 0 && land.HasLand(d)) rise = Math.Max(rise, Math.Abs(land.Top[d] - land.Top[c]));
        if (blocks > 0.01f && blocks < marshDepth && rise == 0)
        {
            marsh++;
            rgb[o] = 95; rgb[o + 1] = 110; rgb[o + 2] = 60;
            continue;
        }
        float t = Math.Clamp((land.Top[c] * land.Size - 300f) / 900f, 0f, 1f);
        float wet = Math.Clamp(water[top] / settle.Capacity(top), 0f, 1f);
        if (open > 0.1f)
        {
            float d = Math.Clamp(open / 4f, 0f, 1f);
            rgb[o] = (byte)(70 - 50 * d); rgb[o + 1] = (byte)(140 - 80 * d); rgb[o + 2] = (byte)(230 - 70 * d);
        }
        else
        {
            rgb[o] = (byte)(120 + 100 * t - 60 * wet); rgb[o + 1] = (byte)(130 + 90 * t); rgb[o + 2] = (byte)(90 + 120 * t - 40 * wet);
        }
    }
    Save(path, land.Nx, land.Nz, rgb);
    if (Environment.GetEnvironmentVariable("HYD_DUMP") is { } dump)
        using (var w = new StreamWriter(dump))
            for (int c = 0; c < land.Columns; c++)
            {
                if (!land.HasLand(c)) continue;
                float open = 0f;
                for (int y = land.Top[c]; y <= land.Hi[c]; y++) { int i = land.Index(c, y); if (i >= 0 && settle.Solved) open += settle.OpenBlocks(i); }
                int top = land.Index(c, land.Top[c]);
                w.WriteLine($"{c % land.Nx},{c / land.Nx},{land.Size * (land.Top[c] + land.Solid[top]):0},{open:0.0},{land.Lo[c] * land.Size}");
            }
    float all = depths.Sum() / 100f;
    Console.WriteLine($"Open water over land columns: none {depths[0] / all:0}%, under 1/4 block {depths[1] / all:0}%, " +
                      $"to 1 {depths[2] / all:0}%, 1-4 {depths[3] / all:0}%, 4-16 {depths[4] / all:0}%, deeper {depths[5] / all:0}%; " +
                      $"marsh (under {marshDepth} blocks on flat ground) {marsh / all:0}%");
}

// A slice across x through the row with the most land: ground grey (bluer where wet), water blue, sky dark.
void WriteSection(string path)
{
    int j = 0, most = -1, lo = int.MaxValue, hi = int.MinValue;
    for (int r = 0; r < land.Nz; r++)
    {
        int count = 0;
        for (int i = 0; i < land.Nx; i++) if (land.HasLand(i + land.Nx * r)) count++;
        if (count > most) { most = count; j = r; }
    }
    for (int i = 0; i < land.Nx; i++)
    {
        int c = i + land.Nx * j;
        if (!land.HasLand(c)) continue;
        lo = Math.Min(lo, land.Lo[c]); hi = Math.Max(hi, land.Hi[c]);
    }
    if (lo > hi) return;
    int h = hi - lo + 1;
    var rgb = new byte[land.Nx * h * 3];
    var water = settle.Water;
    for (int i = 0; i < land.Nx; i++)
    for (int y = lo; y <= hi; y++)
    {
        int o = ((hi - y) * land.Nx + i) * 3;
        int k = land.Index(i + land.Nx * j, y);
        if (k < 0)
        {
            // Inert stone, or nothing.
            int c = i + land.Nx * j;
            bool inert = land.HasLand(c) && y >= land.Lo[c] && y <= land.Hi[c];
            rgb[o] = (byte)(inert ? 85 : 18); rgb[o + 1] = (byte)(inert ? 80 : 22); rgb[o + 2] = (byte)(inert ? 78 : 34);
            continue;
        }
        float s = land.Solid[k], fill = Math.Clamp(water[k] / settle.Capacity(k), 0f, 1f);
        if (s > 0.5f) { rgb[o] = (byte)(130 - 60 * fill); rgb[o + 1] = (byte)(125 - 30 * fill); rgb[o + 2] = (byte)(120 + 60 * fill); }
        else if (fill > 0.05f) { rgb[o] = 40; rgb[o + 1] = (byte)(90 + 60 * fill); rgb[o + 2] = 230; }
        else { rgb[o] = 30; rgb[o + 1] = 36; rgb[o + 2] = 52; }
    }
    Save(path, land.Nx, h, rgb);
}

static void Save(string path, int w, int h, byte[] rgb)
{
    using var f = File.Create(path);
    new ImageWriter().WritePng(rgb, w, h, ColorComponents.RedGreenBlue, f);
}
