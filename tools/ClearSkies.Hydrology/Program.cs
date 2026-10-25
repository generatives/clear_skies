using System.Diagnostics;
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
//       [--stone-capacity 0.05] [--air 0.15] [--soil 0.01] [--stone 0.0005]
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
// must run through a column; --carve-depth, --carve-most: blocks a round).

ulong seed = 1337;
(int X, int Z)? cell = null;
int passes = 20000, report = 500;
float rain = 0.002f;
string outDir = "hydrology-out";
bool snapshots = false, falls = true, hollows = false, localSteps = false, routing = false, solve = false;
int last = 8, check = 0, carve = 0;
float carveFrom = 20f, carveDepth = 2f, carveMost = 8f;
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

// Each cell size in turn, the last 8: each settles from the one before (see Settle.StartFrom).
CoarseLand land = null!;
Settle settle = null!, rough = null!;
foreach (int size in cascade.Append(last))
{
    var sw = Stopwatch.StartNew();
    land = new CoarseLand(seed, landmass, x0, z0, x1, z1, shell, size);
    int landColumns = 0;
    for (int c = 0; c < land.Columns; c++) if (land.HasLand(c)) landColumns++;
    Console.WriteLine($"{size}-block cells: sampled {land.Columns:N0} columns ({landColumns:N0} with land) in " +
                      $"{sw.Elapsed.TotalSeconds:0.00} s: {land.Cells:N0} cells kept of {land.Spanned:N0}, " +
                      $"{land.Cells * 10L * 4 / 1e6:0} MB as the model holds them (10 floats a cell)");

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
    for (int j = 0; j < land.Nz; j++)
    for (int i = 0; i < land.Nx; i++)
    {
        int c = i + land.Nx * j;
        for (int dj = -1; dj <= 1; dj++)
        for (int di = -1; di <= 1; di++)
        {
            int ni = i + di, nj = j + dj;
            if (ni >= 0 && nj >= 0 && ni < land.Nx && nj < land.Nz) sides[c] = Math.Max(sides[c], 0.5f * depth[ni + land.Nx * nj]);
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
    float all = depths.Sum() / 100f;
    Console.WriteLine($"Open water over land columns: none {depths[0] / all:0}%, under 1/4 block {depths[1] / all:0}%, " +
                      $"to 1 {depths[2] / all:0}%, 1-4 {depths[3] / all:0}%, 4-16 {depths[4] / all:0}%, deeper {depths[5] / all:0}%");
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
