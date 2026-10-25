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
//       [--saturate 1] [--fill-hollows] [--local-steps] [--no-falls] [--soil-capacity 2]
//       [--stone-capacity 0.05] [--air 0.15] [--soil 0.01] [--stone 0.0005]
//
// With no --cell it takes the spawn's landmass. --snapshots also writes the map at every report. It stops early once
// as much water drains off the land as rains on it (within 1%, either way). --saturate N starts the ground full of
// water up to N cells under its top; --fill-hollows starts every hollow in the land's top full; --no-falls makes free
// water fall a cell or so a pass, as it spreads. --local-steps lets slow ground take bigger steps (see
// Settle.UseLocalSteps).

ulong seed = 1337;
(int X, int Z)? cell = null;
int passes = 20000, report = 500;
float rain = 0.002f;
string outDir = "hydrology-out";
bool snapshots = false, falls = true, hollows = false, localSteps = false;
int saturate = -1;
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
        case "--saturate": saturate = int.Parse(args[++a]); break;
        case "--soil-capacity": m = m with { SoilCapacity = float.Parse(args[++a]) }; break;
        case "--stone-capacity": m = m with { StoneCapacity = float.Parse(args[++a]) }; break;
        case "--air": m = m with { AirPermeability = float.Parse(args[++a]) }; break;
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

var sw = Stopwatch.StartNew();
var land = new CoarseLand(seed, landmass, x0, z0, x1, z1);
int landColumns = 0;
for (int c = 0; c < land.Columns; c++) if (land.Count(c) > 0) landColumns++;
Console.WriteLine($"Sampled {land.Columns:N0} columns ({landColumns:N0} with land) in {sw.Elapsed.TotalSeconds:0.00} s: " +
                  $"{land.Cells:N0} cells, {land.Cells * 5L * 4 / 1e6:0} MB as the model holds them (5 floats a cell)");

sw.Restart();
var settle = new Settle(land, m, rain) { Falls = falls };
if (localSteps) settle.UseLocalSteps();
if (saturate >= 0) settle.Saturate(saturate);
if (hollows) Console.WriteLine($"Filled hollows: {settle.FillHollows():N0} columns under water");
Console.WriteLine($"Set up materials in {sw.Elapsed.TotalSeconds:0.00} s");

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
    Console.WriteLine($"pass {p,6}: {window.Elapsed.TotalMilliseconds / report:0.0} ms/pass, stored {settle.Stored:0}, " +
                      $"drained/rained over the last {report} {ratio:0.000}");
    if (snapshots) { Directory.CreateDirectory(outDir); WriteMap(Path.Combine(outDir, $"map-{p:D6}.png")); }
    window.Restart();
    if (Math.Abs(ratio - 1) < 0.01) { Console.WriteLine($"Settled: as much drains as rains, after {p} passes"); break; }
    rained = drained = 0;
}
Console.WriteLine($"{done} passes in {sw.Elapsed.TotalSeconds:0.0} s ({sw.Elapsed.TotalMilliseconds / done:0.0} ms/pass); " +
                  $"total {total.Elapsed.TotalSeconds:0.0} s");

Directory.CreateDirectory(outDir);
WriteMap(Path.Combine(outDir, "map.png"));
WriteSection(Path.Combine(outDir, "section.png"));
Console.WriteLine($"Wrote {Path.GetFullPath(outDir)}/map.png and section.png");
return 0;

// Top-down: land shaded by height and greener where its top cell is wetter, open water blue by depth, void dark.
void WriteMap(string path)
{
    var rgb = new byte[land.Columns * 3];
    var water = settle.Water;
    for (int c = 0; c < land.Columns; c++)
    {
        int o = c * 3;
        if (land.Count(c) == 0) { rgb[o] = 18; rgb[o + 1] = 22; rgb[o + 2] = 34; continue; }
        int top = land.Index(c, land.Top[c]);
        float open = 0f;
        for (int y = land.Top[c]; y <= land.Hi[c]; y++)
        {
            int i = land.Index(c, y);
            open += water[i] * (1f - land.Solid[i]) / settle.Capacity(i);
        }
        float t = Math.Clamp((land.Top[c] * CoarseLand.Size - 300f) / 900f, 0f, 1f);
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
}

// A slice across x through the row with the most land: ground grey (bluer where wet), water blue, sky dark.
void WriteSection(string path)
{
    int j = 0, most = -1, lo = int.MaxValue, hi = int.MinValue;
    for (int r = 0; r < land.Nz; r++)
    {
        int count = 0;
        for (int i = 0; i < land.Nx; i++) if (land.Count(i + land.Nx * r) > 0) count++;
        if (count > most) { most = count; j = r; }
    }
    for (int i = 0; i < land.Nx; i++)
    {
        int c = i + land.Nx * j;
        if (land.Count(c) == 0) continue;
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
        if (k < 0) { rgb[o] = 18; rgb[o + 1] = 22; rgb[o + 2] = 34; continue; }
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
