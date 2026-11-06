using System.Collections.Concurrent;
using System.Diagnostics;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;

namespace ClearSkies.Game.Ruins;

/// <summary>What --ruins asks for: off, or which layout model and how many people a big island's ruin is for.</summary>
public sealed class RuinWorldSettings
{
    public bool Enabled = true;
    public string Model = "arterial";
    public RuinSettings Ruin = new();
    /// <summary>Islands with less land than this (blocks²) get no ruin.</summary>
    public long MinIslandLand = 1_000_000;
    /// <summary>An island with this much land (blocks²) or more gets the full population; smaller ones get their share.</summary>
    public long FullIslandLand = 4_000_000;

    /// <summary>Comma-separated name=value pairs: off, model=grid|arterial|hub|spine, population, area-per-person,
    /// aspect, gap-cost, min-island, full-island.</summary>
    public static RuinWorldSettings Parse(string? text)
    {
        var s = new RuinWorldSettings();
        if (string.IsNullOrWhiteSpace(text)) return s;
        var c = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            string name = (eq < 0 ? part : part[..eq]).ToLowerInvariant(), value = eq < 0 ? "true" : part[(eq + 1)..];
            switch (name)
            {
                case "off": s.Enabled = false; break;
                case "model": s.Model = value; break;
                case "population": s.Ruin.Population = int.Parse(value, c); break;
                case "area-per-person": s.Ruin.AreaPerPerson = float.Parse(value, c); break;
                case "aspect": s.Ruin.Aspect = float.Parse(value, c); break;
                case "gap-cost": s.Ruin.GapCost = float.Parse(value, c); break;
                case "min-island": s.MinIslandLand = long.Parse(value, c); break;
                case "full-island": s.FullIslandLand = long.Parse(value, c); break;
                default: throw new ArgumentException($"--ruins: no setting called {name}");
            }
        }
        return s;
    }
}

/// <summary>
/// Ruins in the world, landmass by landmass: the first time a chunk of a landmass is generated, the landmass is
/// sampled (see <see cref="LandmassSample"/>) and every island big enough gets a ruin (see <see cref="RuinSite"/>,
/// <see cref="RuinLayout"/>), laid out and indexed by chunk (see <see cref="RuinCarving"/>) and kept, so every chunk
/// of it agrees; then each chunk is carved as it's generated. All from the seed, so every machine makes the same ruins.
/// </summary>
public sealed class RuinWorld(ulong seed, RuinWorldSettings settings)
{
    private readonly ConcurrentDictionary<(int X, int Z), Lazy<RuinCarving[]>> _landmasses = new();
    [ThreadStatic] private static BlockId[]? _scratch;

    public RuinWorldSettings Settings => settings;

    /// <summary>The ruins of the landmass block (x, z) is on, worked out if they haven't been.</summary>
    public RuinCarving[] At(float x, float z)
    {
        var key = HeartGrid.Landmass(seed, x, z);
        return _landmasses.GetOrAdd(key, k => new Lazy<RuinCarving[]>(() => Build(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private RuinCarving[] Build((int X, int Z) landmass)
    {
        var sw = Stopwatch.StartNew();
        var sample = new LandmassSample(seed, landmass);
        var model = RuinLayout.Models.FirstOrDefault(m => m.Name == settings.Model) ?? RuinLayout.Models[0];
        var ruins = new List<RuinCarving>();
        const long CellArea = RuinSite.Cell * RuinSite.Cell;
        for (int island = 0; island < sample.Islands; island++)
        {
            long land = sample.IslandLand[island] * CellArea;
            if (land < settings.MinIslandLand) break; // (biggest first)
            var ruin = new RuinSettings
            {
                Population = (int)(settings.Ruin.Population * Math.Min(1.0, land / (double)settings.FullIslandLand)),
                AreaPerPerson = settings.Ruin.AreaPerPerson, MaxFloors = settings.Ruin.MaxFloors, Aspect = settings.Ruin.Aspect,
                GapCost = settings.Ruin.GapCost, PieceEntranceArea = settings.Ruin.PieceEntranceArea, Roughness = settings.Ruin.Roughness,
            };
            var site = RuinSite.Build(sample, island, ruin);
            if (site.Floors == 0) continue;
            ruins.Add(new RuinCarving(RuinLayout.Generate(site, model)));
        }
        Console.WriteLine($"Ruins: landmass {landmass}, {ruins.Count} ruins laid out in {sw.Elapsed.TotalSeconds:0.0} s");
        return ruins.ToArray();
    }

    /// <summary>Carves whatever ruins reach into chunk <paramref name="pos"/>, freshly generated.</summary>
    public void Carve(ChunkData data, ChunkPosition pos)
    {
        const int S = ChunkData.Size;
        var ruins = At(pos.X * S + S / 2, pos.Z * S + S / 2);
        long key = RuinCarving.Key(pos.X, pos.Y, pos.Z);
        BlockId[]? blocks = null;
        foreach (var ruin in ruins)
        {
            if (!ruin.ByChunk.ContainsKey(key)) continue;
            if (blocks == null)
            {
                blocks = _scratch ??= new BlockId[ChunkData.Volume];
                for (int z = 0; z < S; z++)
                for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++) blocks[ChunkData.Index(x, y, z)] = data.Get(x, y, z);
            }
            ruin.Carve(pos.X, pos.Y, pos.Z, blocks);
        }
        if (blocks == null) return;
        for (int z = 0; z < S; z++)
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            var b = blocks[ChunkData.Index(x, y, z)];
            if (data.Get(x, y, z) != b) data.Set(x, y, z, b);
        }
    }
}
