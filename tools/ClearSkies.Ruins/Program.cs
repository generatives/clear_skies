using System.Diagnostics;
using System.Globalization;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;
using ClearSkies.Game.Ruins;

// Prototype of ruin layouts (see ClearSkies.Game.Ruins): picks a ruin's region in one landmass (see HeartGrid.Landmass),
// lays it out with each layout model, and reports how long each step takes, how long carving its chunks takes next to
// generating them, and how good each layout is. Writes a map of the region and of every floor of every layout.
//
//   dotnet run -c Release --project tools/ClearSkies.Ruins -- [--seed 1337] [--cell 1 -1] [--aspect 2] [--gap-cost 8] [--roughness 0.7] [--piece-entrance 40000]
//       [--population 150000] [--area-per-person 40] [--max-floors 40] [--island 0] [--model all|grid|arterial|hub|spine] [--gen-sample 200]
//       [--out ruins-out] [--in-world]
//
// --in-world also generates real chunks through the game's generator with ruins on (see RuinWorld) and draws an upright
// slice through the main entrance (world-slice.png). With no --cell it takes the spawn's landmass; --island picks one of its islands (0 is the biggest). RUINS_DEBUG=1 also
// prints how many rooms are reachable on each floor.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
ulong seed = 1337;
(int X, int Z)? cell = null;
var settings = new RuinSettings();
string model = "all", outDir = "ruins-out";
bool inWorld = false;
int genSample = 200, island = 0;
for (int a = 0; a < args.Length; a++)
{
    switch (args[a])
    {
        case "--seed": seed = ulong.Parse(args[++a]); break;
        case "--cell": cell = (int.Parse(args[++a]), int.Parse(args[++a])); break;
        case "--aspect": settings.Aspect = float.Parse(args[++a]); break;
        case "--roughness": settings.Roughness = float.Parse(args[++a]); break;
        case "--gap-cost": settings.GapCost = float.Parse(args[++a]); break;
        case "--piece-entrance": settings.PieceEntranceArea = int.Parse(args[++a]); break;
        case "--population": settings.Population = int.Parse(args[++a]); break;
        case "--area-per-person": settings.AreaPerPerson = float.Parse(args[++a]); break;
        case "--max-floors": settings.MaxFloors = int.Parse(args[++a]); break;
        case "--model": model = args[++a]; break;
        case "--gen-sample": genSample = int.Parse(args[++a]); break;
        case "--out": outDir = args[++a]; break;
        case "--island": island = int.Parse(args[++a]); break;
        case "--in-world": inWorld = true; break;
        default: Console.Error.WriteLine($"Unknown argument {args[a]}"); return 1;
    }
}
var landmass = cell ?? HeartGrid.Landmass(seed, 11250f, -6250f);
Directory.CreateDirectory(outDir);
Console.WriteLine($"Seed {seed}, landmass {landmass}, {Environment.ProcessorCount} cores");

const int C = RuinSite.Cell;
var sample = new LandmassSample(seed, landmass);
Console.WriteLine($"Landmass {sample.Nx * C} x {sample.Nz * C} blocks, {sample.Land.Count(b => b) * (long)C * C / 1e6:0.00} M blocks² of land; " +
                  $"sampled in {sample.SampleTime.TotalSeconds:0.00} s, {sample.Islands} islands found in {sample.IslandTime.TotalSeconds:0.00} s:");
for (int k = 0; k < Math.Min(sample.Islands, 8); k++)
    Console.WriteLine($"  island {k}: {sample.IslandLand[k] * (long)C * C / 1e6:0.00} M blocks² of land");
if (island >= sample.Islands) { Console.Error.WriteLine($"No island {island}"); return 1; }
var site = RuinSite.Build(sample, island, settings);
Console.WriteLine($"Island {island}, region: {site.RegionLandCells * (long)C * C / 1e6:0.00} M blocks² of land ({100.0 * site.RegionLandCells / site.IslandLandCells:0}%), " +
                  $"{(site.RegionCells - site.RegionLandCells) * (long)C * C / 1e6:0.00} M of gaps, {site.Nx * C} x {site.Nz * C} blocks; picked in {site.RegionTime.TotalSeconds:0.00} s");
{
    var tops = Enumerable.Range(0, site.Nx * site.Nz).Where(c => site.Region[c] && site.Surface[c] != short.MinValue)
        .Select(c => (int)site.Surface[c]).Order().ToArray();
    if (tops.Length > 0)
        Console.WriteLine($"Region surface: 10% {tops[tops.Length / 10]}, median {tops[tops.Length / 2]}, 90% {tops[tops.Length * 9 / 10]}");
}
Console.WriteLine($"Needs {site.TargetArea / 1e6:0.00} M blocks² of floor ({settings.Population:N0} people x {settings.AreaPerPerson} blocks²): " +
                  $"{site.Floors} floors from y {site.TopY} down to {site.FloorY(site.Floors - 1)}");
Console.WriteLine($"{site.Entrances.Count} entrances: " + string.Join(", ", site.Entrances.Select(e =>
    $"({site.X0 + e.Cell % site.Nx * C}, {site.Z0 + e.Cell / site.Nx * C}) to floor {e.Floor}")));
{
    int w = 0;
    for (int c = 0; c < site.Nx * site.Nz; c++) if (site.Region[c]) w++;
    double across = 2 * Math.Sqrt(w * (double)C * C / Math.PI);
    Console.WriteLine($"Shape: about {across:0} blocks across (as a disc of its footprint) by {site.Floors * RuinSite.FloorPitch} deep");
}
for (int f = 0; f < site.Floors; f++)
{
    var g = site.Grounds[f];
    int Count(RuinSite.Ground k) => g.Count(x => x == k);
    Console.WriteLine($"  floor {f}: solid {Count(RuinSite.Ground.Solid) * (long)C * C / 1e6:0.00} M, partial {Count(RuinSite.Ground.Partial) * (long)C * C / 1e6:0.00} M, " +
                      $"open {Count(RuinSite.Ground.Open) * (long)C * C / 1e6:0.00} M blocks²");
}
Maps.Site(site, Path.Combine(outDir, "site.png"));

var models = RuinLayout.Models.Where(m => model == "all" || m.Name == model).ToArray();
var rows = new List<string>();
var floor0 = new List<byte[]>();
int scale = Maps.ScaleFor(site);
var blocks = new BlockId[ChunkData.Volume];
HashSet<long>? touched = null;
foreach (var m in models)
{
    var plan = RuinLayout.Generate(site, m);
    var sw = Stopwatch.StartNew();
    var carving = new RuinCarving(plan);
    var indexTime = sw.Elapsed;

    // Carve every chunk it touches, one at a time, into a chunk of stone.
    var times = new List<double>(carving.ByChunk.Count);
    long air = 0, brick = 0;
    foreach (var k in carving.ByChunk.Keys)
    {
        var (cx, cy, cz) = RuinCarving.Unkey(k);
        Array.Fill(blocks, BlockId.Stone);
        long t0 = Stopwatch.GetTimestamp();
        carving.Carve(cx, cy, cz, blocks);
        times.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
        foreach (var b in blocks) { if (b == BlockId.Air) air++; else if (b == BlockId.StoneBrick) brick++; }
    }
    times.Sort();
    touched ??= carving.ByChunk.Keys.ToHashSet();

    var mt = LayoutMetrics.Of(plan);
    Console.WriteLine();
    Console.WriteLine($"== {m.Name} ==");
    Console.WriteLine($"  layout {plan.SkeletonTime.TotalMilliseconds:0} ms skeleton + {plan.RoomsTime.TotalMilliseconds:0} ms rooms; " +
                      $"{plan.Parts.Count:N0} parts, {carving.Boxes.Length:N0} boxes in {carving.ByChunk.Count:N0} chunks, indexed in {indexTime.TotalMilliseconds:0} ms, ~{carving.MemoryBytes / 1e6:0.0} MB");
    Console.WriteLine($"  carving: mean {times.Average():0.0} µs, 99% {times[(int)(0.99 * (times.Count - 1))]:0.0} µs, worst {times[^1]:0.0} µs a chunk; " +
                      $"{air / 1e6:0.0} M blocks of air, {brick / 1e6:0.0} M of brick");
    Console.WriteLine($"  {mt.Rooms:N0} plots ({mt.RoomArea / 1e6:0.00} M blocks², {100 * mt.Coverage:0}% of solid ground), halls {mt.HallArea / 1e6:0.00} M blocks², " +
                      $"{mt.Bridges} bridges ({mt.BridgeCells * C} blocks), {mt.Districts} districts");
    Console.WriteLine($"  reachable from the entrance: {100.0 * mt.Reached / Math.Max(1, mt.Rooms):0.0}% of plots; walk mean {mt.MeanWalk:0} blocks, 90% {mt.P90Walk:0}, worst {mt.MaxWalk:0}");
    rows.Add(string.Join(",", m.Name, plan.SkeletonTime.TotalMilliseconds.ToString("0.0"), plan.RoomsTime.TotalMilliseconds.ToString("0.0"),
        indexTime.TotalMilliseconds.ToString("0.0"), carving.Boxes.Length, carving.ByChunk.Count, (carving.MemoryBytes / 1e6).ToString("0.00"),
        times.Average().ToString("0.0"), times[(int)(0.99 * (times.Count - 1))].ToString("0.0"),
        mt.Rooms, mt.RoomArea, (100 * mt.Coverage).ToString("0.0"), mt.HallArea, mt.Bridges, mt.BridgeCells * C, mt.Districts,
        (100.0 * mt.Reached / Math.Max(1, mt.Rooms)).ToString("0.0"), mt.MeanWalk.ToString("0"), mt.P90Walk.ToString("0"), mt.MaxWalk.ToString("0")));

    // Slices through the carved blocks round the entrance: across the top floor, and down through the floors.
    if (plan.Parts.FirstOrDefault(p => p.Entrance) is { Kind: PartKind.Core } entrance)
    {
        int ex = site.X0 + (entrance.Rect.I0 + entrance.Rect.I1) / 2 * C, ez = site.Z0 + (entrance.Rect.J0 + entrance.Rect.J1) / 2 * C;
        int surface = site.Surface[(entrance.Rect.I0 + 1) + site.Nx * (entrance.Rect.J0 + 1)];
        Maps.Slices(carving, ex - 128, ez - 128, site.TopY + 2, 256, surface + 12, surface + 12 - site.FloorY(site.Floors - 1) + 4, ez,
            Path.Combine(outDir, $"{m.Name}-slices.png"));
    }
    var floorImages = new List<byte[]>();
    for (int f = 0; f < site.Floors; f++)
    {
        var img = Maps.Floor(plan, f, scale);
        Maps.Write(Path.Combine(outDir, $"{m.Name}-floor{f}.png"), img, site.Nx * scale, site.Nz * scale);
        floorImages.Add(img);
        if (f == 0) floor0.Add(img);
    }
    Maps.Sheet(Path.Combine(outDir, $"{m.Name}-floors.png"), floorImages, site.Nx * scale, site.Nz * scale);
}
if (floor0.Count > 1) Maps.Row(Path.Combine(outDir, "compare-floor0.png"), floor0, site.Nx * scale, site.Nz * scale);
File.WriteAllLines(Path.Combine(outDir, "metrics.csv"),
    ["model,skeleton_ms,rooms_ms,index_ms,boxes,chunks,memory_mb,carve_mean_us,carve_p99_us,rooms,room_area,coverage_pct,hall_area,bridges,bridge_blocks,districts,reachable_pct,walk_mean,walk_p90,walk_max", .. rows]);

// What generating those chunks costs without ruins, for scale: a sample of them, one at a time, each with its own
// generator (so nothing is cached from a neighbour).
if (touched is { Count: > 0 } && genSample > 0)
{
    var keys = touched.OrderBy(k => k).Where((_, i) => i % Math.Max(1, touched.Count / genSample) == 0).Take(genSample).ToArray();
    var gen = new HeartWorldGenerator(seed);
    var data = new ChunkData();
    var warm = RuinCarving.Unkey(keys[0]);
    gen.Generate(data, new ChunkPosition(warm.X, warm.Y, warm.Z));
    var times = new List<double>();
    foreach (var k in keys)
    {
        var (cx, cy, cz) = RuinCarving.Unkey(k);
        var fresh = new HeartWorldGenerator(seed);
        var d = new ChunkData();
        long t0 = Stopwatch.GetTimestamp();
        fresh.Generate(d, new ChunkPosition(cx, cy, cz));
        times.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
    }
    times.Sort();
    Console.WriteLine();
    Console.WriteLine($"Generating {keys.Length} of those chunks (no landscape): mean {times.Average() / 1000:0.00} ms, median {times[times.Count / 2] / 1000:0.00} ms a chunk");
}

// Through the game's own generator, ruins and all: an upright slice of real blocks through the main entrance.
if (inWorld)
{
    var world = new RuinWorld(seed, new RuinWorldSettings { Model = model == "all" ? "arterial" : model, Ruin = settings });
    var gen = new HeartWorldGenerator(seed, null, null, world);
    int ex = site.X0 + site.Centre % site.Nx * C, ez = site.Z0 + site.Centre / site.Nx * C;
    var sw = Stopwatch.StartNew();
    world.At(ex, ez);
    Console.WriteLine($"In world: ruins for the landmass in {sw.Elapsed.TotalSeconds:0.0} s");
    const int W = 640, Top = 40;
    int yTop = site.Surface[site.Centre] + Top, yBottom = site.FloorY(site.Floors - 1) - 16, H = yTop - yBottom;
    var chunks = new Dictionary<(int, int, int), ChunkData>();
    long brick = 0;
    var times = new List<double>();
    ChunkData Chunk(int cx, int cy, int cz)
    {
        if (chunks.TryGetValue((cx, cy, cz), out var d)) return d;
        d = new ChunkData();
        long t0 = Stopwatch.GetTimestamp();
        gen.Generate(d, new ChunkPosition(cx, cy, cz));
        times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        return chunks[(cx, cy, cz)] = d;
    }
    var rgb = new byte[W * H * 3];
    for (int py = 0; py < H; py++)
    for (int px = 0; px < W; px++)
    {
        int x = ex - W / 2 + px, y = yTop - py;
        var b = Chunk(x >> 5, y >> 5, ez >> 5).Get(x & 31, y & 31, ez & 31);
        (byte, byte, byte) col = b switch
        {
            BlockId.Air => ((byte)24, (byte)26, (byte)34),
            BlockId.StoneBrick => ((byte)196, (byte)150, (byte)110),
            BlockId.Water => ((byte)50, (byte)90, (byte)200),
            BlockId.Grass or BlockId.Dirt => ((byte)80, (byte)130, (byte)60),
            _ => ((byte)90, (byte)92, (byte)98),
        };
        if (b == BlockId.StoneBrick) brick++;
        int o = (px + W * py) * 3; rgb[o] = col.Item1; rgb[o + 1] = col.Item2; rgb[o + 2] = col.Item3;
    }
    Maps.Write(Path.Combine(outDir, "world-slice.png"), rgb, W, H);
    times.Sort();
    Console.WriteLine($"In world: {chunks.Count} chunks generated with ruins, mean {times.Average():0.00} ms, median {times[times.Count / 2]:0.00} ms; " +
                      $"{brick} brick in the slice (x {ex - W / 2}..{ex + W / 2}, z {ez}, y {yBottom}..{yTop})");
}
return 0;
