using System.Diagnostics;
using ClearSkies.Game.Generation;
using ClearSkies.Pipes;
using StbImageWriteSharp;

// Prototype of the pipes water model (see Pipes) on the distance lattice terrain: samples one landmass cell (see
// HeartGrid.Landmass) at 8-block cells, settles surface water on it and reports how long each step takes. Writes a
// top-down map of the result.
//
//   dotnet run -c Release --project tools/ClearSkies.Pipes -- [--seed 1337] [--cell 1 -1] [--size 8] [--hours 6]
//       [--piece-size 2] [--rain 3e-4] [--soak 1.5e-4] [--manning 0.035] [--no-fill] [--report 600] [--out pipes-out] [--snapshots]
//
// With no --cell it takes the spawn's landmass. It runs until --hours of simulated time pass, or until as much water
// leaves the land (over its edges and into the ground) as rains on it, within 1%. --no-fill starts the land dry instead
// of with every hollow full to where it spills. --erode H then wears the land for H more hours (see Pipes.Erosion;
// --carry, --pick, --drop, --speedup), refills the hollows to their new spill levels and lets the water run --settle minutes (20) more. --ground adds groundwater (see Pipes.Ground;
// --permeability, --shell, --porosity, --storage) in place of soaking away.

ulong seed = 1337;
(int X, int Z)? cell = null;
int size = 8;
double hours = 6, report = 600, erodeHours = 0, settleMinutes = 20;
bool fill = true, snapshots = false, ground = false, denseBench = false;
string outDir = "pipes-out";
var settings = new List<Action<Pipes>>();
bool widened = false;
for (int a = 0; a < args.Length; a++)
{
    switch (args[a])
    {
        case "--seed": seed = ulong.Parse(args[++a]); break;
        case "--cell": cell = (int.Parse(args[++a]), int.Parse(args[++a])); break;
        case "--size": size = int.Parse(args[++a]); break;
        case "--hours": hours = double.Parse(args[++a]); break;
        case "--report": report = double.Parse(args[++a]); break;
        case "--out": outDir = args[++a]; break;
        case "--snapshots": snapshots = true; break;
        case "--no-fill": fill = false; break;
        case "--piece-size": HeartGrid.WidenPieces(float.Parse(args[++a])); widened = true; break;
        case "--courant": { var v = float.Parse(args[++a]); settings.Add(p => p.Courant = v); break; }
        case "--wave-depth": { var v = float.Parse(args[++a]); settings.Add(p => p.WaveDepth = v); break; }
        case "--erode": erodeHours = double.Parse(args[++a]); break;
        case "--settle": settleMinutes = double.Parse(args[++a]); break;
        case "--carry": { var v = float.Parse(args[++a]); settings.Add(p => p.Carry = v); break; }
        case "--pick": { var v = float.Parse(args[++a]); settings.Add(p => p.Pick = v); break; }
        case "--drop": { var v = float.Parse(args[++a]); settings.Add(p => p.Drop = v); break; }
        case "--slide": { var v = float.Parse(args[++a]); settings.Add(p => p.Slide = v); break; }
        case "--slide-rate": { var v = float.Parse(args[++a]); settings.Add(p => p.SlideRate = v); break; }
        case "--hardening": { var v = float.Parse(args[++a]); settings.Add(p => p.Hardening = v); break; }
        case "--erode-every": { var v = int.Parse(args[++a]); settings.Add(p => p.ErodeEvery = v); break; }
        case "--ground": ground = true; break;
        case "--dense-bench": denseBench = true; break;
        case "--permeability": { var v = float.Parse(args[++a]); settings.Add(p => p.Permeability = v); break; }
        case "--shell": { var v = float.Parse(args[++a]); settings.Add(p => p.Shell = v); break; }
        case "--porosity": { var v = float.Parse(args[++a]); settings.Add(p => p.Porosity = v); break; }
        case "--storage": { var v = float.Parse(args[++a]); settings.Add(p => p.Storage = v); break; }
        case "--ground-every": { var v = int.Parse(args[++a]); settings.Add(p => p.GroundEvery = v); break; }
        case "--speedup": { var v = float.Parse(args[++a]); settings.Add(p => p.Speedup = v); break; }
        case "--rain": { var v = float.Parse(args[++a]); settings.Add(p => p.Rain = v); break; }
        case "--soak": { var v = float.Parse(args[++a]); settings.Add(p => p.Soak = v); break; }
        case "--manning": { var v = float.Parse(args[++a]); settings.Add(p => p.Manning = v); break; }
        default: Console.Error.WriteLine($"Unknown argument {args[a]}"); return 1;
    }
}
if (!widened) HeartGrid.WidenPieces(2f); // pieces twice the game's width unless told otherwise
var landmass = cell ?? HeartGrid.Landmass(seed, 11250f, -6250f);
Console.WriteLine($"Seed {seed}, landmass {landmass}, {Environment.ProcessorCount} cores");

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
int FloorTo(int v, int step) => (int)Math.Floor(v / (double)step) * step;
x0 = FloorTo(x0 - 64, 32); z0 = FloorTo(z0 - 64, 32); x1 = FloorTo(x1 + 96, 32); z1 = FloorTo(z1 + 96, 32);
Console.WriteLine($"Bounds x {x0}..{x1}, z {z0}..{z1} ({(x1 - x0) / 1000f:0.0} x {(z1 - z0) / 1000f:0.0} km)");

var sw = Stopwatch.StartNew();
var land = new Land(seed, landmass, x0, z0, x1, z1, size);
int landColumns = Enumerable.Range(0, land.Columns).Count(land.HasLand);
Console.WriteLine($"{size}-block columns: {land.Columns:N0} ({landColumns:N0} with land), {land.Nodes:N0} nodes, " +
                  $"{land.Links:N0} links, sampled in {sw.Elapsed.TotalSeconds:0.0} s");

if (denseBench) { DenseBench.Run(land, 2000, 2.4f); return 0; }
var pipes = new Pipes(land);
foreach (var set in settings) set(pipes);
if (ground) pipes.UseGroundwater();
Directory.CreateDirectory(outDir);
if (fill)
{
    sw.Restart();
    int wet = pipes.FillHollows();
    Console.WriteLine($"Filled hollows: {wet:N0} nodes wet, in {sw.Elapsed.TotalSeconds:0.00} s");
}

var startFloor = (float[])land.Floor.Clone();
Run(hours * 3600, stopWhenSettled: true);
if (erodeHours > 0)
{
    pipes.Eroding = true;
    Console.WriteLine($"Eroding for {erodeHours} h");
    Run(pipes.Time + erodeHours * 3600, stopWhenSettled: false);
    pipes.Eroding = false;
    // Lakes whose outlets were cut start at their new spill level rather than draining down to it.
    if (fill) pipes.FillHollows();
    Console.WriteLine("Settling the water on the worn land");
    Run(pipes.Time + settleMinutes * 60, stopWhenSettled: false);
    WriteWear(Path.Combine(outDir, "wear.png"));
}
WriteMap(Path.Combine(outDir, "pipes.png"));
if (ground) Console.WriteLine($"Ground balance: {pipes.GroundBalance()}");
Console.WriteLine($"Steps: flow {pipes.FlowTime.Elapsed.TotalSeconds:0.0} s, apply {pipes.ApplyTime.Elapsed.TotalSeconds:0.0} s, " +
                  $"ground {pipes.GroundTime.Elapsed.TotalSeconds:0.0} s, erosion {pipes.ErodeTime.Elapsed.TotalSeconds:0.0} s");
Console.WriteLine($"Total {total.Elapsed.TotalSeconds:0.0} s");
return 0;

// Steps until the given time, reporting every so often; stops early once settled, if asked: checked every Check
// simulated seconds, when the budget balances within 1% and the water stored changes by under 1% in 15 minutes.
void Run(double until, bool stopWhenSettled)
{
    const double Check = 300;
    sw.Restart();
    int steps = pipes.Steps;
    double nextReport = pipes.Time + report, nextCheck = pipes.Time + Check, stored = 0;
    bool settled = false;
    while (pipes.Time < until)
    {
        pipes.Step();
        bool reporting = pipes.Time >= nextReport, checking = stopWhenSettled && pipes.Time >= nextCheck;
        if (!reporting && !checking) continue;
        var b = pipes.Budget();
        double off = (b.Drained + b.Soaked - b.Rain) / b.Rain;
        if (reporting)
        {
            nextReport += report;
            Console.WriteLine($"{pipes.Time / 60,6:0} min, {pipes.Steps,7:N0} steps, {sw.Elapsed.TotalSeconds,6:0.0} s: rain {b.Rain:0} " +
                              $"drained {b.Drained:0} soaked {b.Soaked:0} ({100 * off:+0.0;-0.0}%), stored {b.Stored / 1e6:0.00} M" +
                              (pipes.Eroding ? $", sediment {pipes.Sediment!.Sum() / 1e3:0.0} k" : ""));
            if (snapshots) WriteMap(Path.Combine(outDir, $"pipes-{pipes.Time / 60:00000}.png"));
        }
        if (!checking) continue;
        nextCheck += Check;
        // With groundwater, also once the ground takes in as much as it gives back.
        bool groundSettled = pipes.Groundwater == null || Math.Abs(b.Soaked) < 0.01 * b.Rain;
        if (groundSettled && Math.Abs(off) < 0.01 && Math.Abs(b.Stored - stored) < 0.01 * b.Stored * Check / 900) { settled = true; break; }
        stored = b.Stored;
    }
    steps = pipes.Steps - steps;
    Console.WriteLine($"{(settled ? "Settled" : "Stopped")} at {pipes.Time / 3600:0.00} h simulated, {steps:N0} steps, " +
                      $"{sw.Elapsed.TotalSeconds:0.0} s ({sw.Elapsed.TotalMilliseconds / Math.Max(1, steps):0.00} ms a step)");
}

// Top-down: how far each column's top floor moved: worn (red) or built up (green), up to 16 blocks.
void WriteWear(string path)
{
    int nx = land.Nx;
    var rgb = new byte[land.Columns * 3];
    double worn = 0, built = 0, deepest = 0;
    for (int c = 0; c < land.Columns; c++)
    {
        int o = c * 3;
        if (!land.HasLand(c)) { rgb[o] = 18; rgb[o + 1] = 22; rgb[o + 2] = 34; continue; }
        for (int n = land.NodeStart[c]; n < land.NodeStart[c + 1]; n++)
        {
            float d = land.Floor[n] - startFloor[n];
            if (d < 0) worn -= d; else built += d;
            deepest = Math.Max(deepest, -d);
        }
        int t = land.TopNode(c);
        float moved = land.Floor[t] - startFloor[t];
        double w = Math.Clamp(Math.Abs(moved) / 16, 0, 1);
        rgb[o] = (byte)(110 + (moved < 0 ? 145 * w : -60 * w)); rgb[o + 1] = (byte)(110 + (moved > 0 ? 145 * w : -60 * w)); rgb[o + 2] = (byte)(110 - 60 * w);
    }
    using (var f = File.Create(path))
        new ImageWriter().WritePng(rgb, nx, land.Nz, ColorComponents.RedGreenBlue, f);
    Console.WriteLine($"Worn {worn * size * size / 1e6:0.00} M blocks, built up {built * size * size / 1e6:0.00} M, deepest cut {deepest:0.0} blocks");
}

// Top-down: the land shaded by height and slope, standing water (half a block or more) blue by depth, running water
// by how much runs through (in columns' worth of the wettest rain), ground full to the top green.
void WriteMap(string path)
{
    int nx = land.Nx, nz = land.Nz;
    var rgb = new byte[land.Columns * 3];
    float column = pipes.Rain * size * size;
    int columns = 0, lakes = 0, creeks = 0, streams = 0, rivers = 0, full = 0, springing = 0;
    float Ground(int c) => land.Floor[land.TopNode(c)];
    for (int c = 0; c < land.Columns; c++)
    {
        int o = c * 3, ci = c % nx, cj = c / nx;
        if (!land.HasLand(c)) { rgb[o] = 18; rgb[o + 1] = 22; rgb[o + 2] = 34; continue; }
        columns++;
        int n = land.TopNode(c);
        if (pipes.Groundwater != null && pipes.Groundwater[n] >= land.Floor[n] - 0.01f) full++;
        if (pipes.Springs != null && pipes.Springs[n] > 0) springing++;
        float ground = Ground(c), depth = pipes.Depth[n], runs = pipes.Through(n) / column;
        float Hat(int di, int dj)
        {
            int ni = Math.Clamp(ci + di, 0, nx - 1), nj = Math.Clamp(cj + dj, 0, nz - 1), d = ni + nx * nj;
            return land.HasLand(d) ? Ground(d) : ground;
        }
        double light = Math.Clamp(0.6 + (Hat(-1, -1) - Hat(1, 1)) / (2.0 * size) * 0.8, 0.15, 1.0);
        double t = Math.Clamp((ground - 300) / 900.0, 0, 1), g = (90 + 130 * t) * light;
        if (runs >= 40)
        {
            if (runs >= 1000) rivers++; else if (runs >= 200) streams++; else creeks++;
        }
        if (depth >= 0.5f && runs < 200)
        {
            lakes++;
            double d = Math.Clamp(depth / 16, 0, 1);
            rgb[o] = (byte)(60 - 40 * d); rgb[o + 1] = (byte)(130 - 60 * d); rgb[o + 2] = (byte)(230 - 50 * d);
        }
        else if (runs >= 40)
        {
            double w = Math.Clamp(Math.Log10(runs / 40) / 2, 0.35, 1);
            rgb[o] = (byte)(g * (1 - w) + 40 * w); rgb[o + 1] = (byte)(g * (1 - w) + 120 * w); rgb[o + 2] = (byte)(g * 0.85 * (1 - w) + 255 * w);
        }
        else if (pipes.Groundwater != null && pipes.Groundwater[n] >= land.Floor[n] - 0.01f)
        {
            // Ground full to the top (springs, marsh): greener.
            rgb[o] = (byte)(g * 0.5); rgb[o + 1] = (byte)(g * 0.85); rgb[o + 2] = (byte)(g * 0.55);
        }
        else { rgb[o] = (byte)(g * 0.95); rgb[o + 1] = (byte)g; rgb[o + 2] = (byte)(g * 0.8); }
    }
    using (var f = File.Create(path))
        new ImageWriter().WritePng(rgb, nx, nz, ColorComponents.RedGreenBlue, f);
    Console.WriteLine($"Of {columns:N0} land columns: standing water {100.0 * lakes / columns:0.0}%, creeks (40+ columns' rain) " +
                      $"{100.0 * creeks / columns:0.0}%, streams (200+) {100.0 * streams / columns:0.0}%, rivers (1000+) {100.0 * rivers / columns:0.0}%" +
                      (pipes.Groundwater != null ? $", ground full {100.0 * full / columns:0.0}%, springs {100.0 * springing / columns:0.0}%" : ""));
}
