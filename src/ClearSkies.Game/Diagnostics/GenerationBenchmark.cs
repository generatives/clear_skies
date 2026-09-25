using System.Diagnostics;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;

namespace ClearSkies.Game.Diagnostics;

/// <summary>
/// Headless, single-threaded timing harness for the chunk-generation and meshing pipeline. Run with
/// <c>--benchmark</c> (see Program.cs) so it can be measured without spinning up a GPU window. Generates
/// a chunk cube centred on the nearest island to the origin (so the sample mixes island interior with
/// the surrounding empty sky, matching what a real loaded view volume actually contains) and reports
/// per-stage timing distributions.
/// </summary>
public static class GenerationBenchmark
{
    /// <summary>Generates a box of chunks around the nearest island (the old fixed view box ChunkLoadSystem used to
    /// load); pass larger radii to stress-test.</summary>
    public static void Run(ulong seed = 1337, int xzRadius = 8, int yRadius = 3)
    {
        Console.WriteLine($"=== Generation benchmark (seed={seed}, xzRadius={xzRadius}, yRadius={yRadius}) ===");

        var generator = new SkyWorldGenerator(seed);
        var focus      = FindFocusChunk(seed);

        Console.WriteLine($"Focus chunk: {focus} (nearest island to origin)");

        var positions = new List<ChunkPosition>();
        for (int dy = -yRadius; dy <= yRadius; dy++)
        for (int dx = -xzRadius; dx <= xzRadius; dx++)
        for (int dz = -xzRadius; dz <= xzRadius; dz++)
            positions.Add(focus.Offset(dx, dy, dz));

        // ── Stage 1: generation ─────────────────────────────────────────────
        var dataByPos  = new Dictionary<ChunkPosition, ChunkData>(positions.Count);
        var genTimesUs = new List<double>(positions.Count);
        int emptyCount = 0;

        var sw = new Stopwatch();
        var overall = Stopwatch.StartNew();
        foreach (var pos in positions)
        {
            var data = new ChunkData();
            sw.Restart();
            generator.Generate(data, pos);
            sw.Stop();
            genTimesUs.Add(sw.Elapsed.TotalMicroseconds);

            dataByPos[pos] = data;
            if (!data.HasAnySolid()) emptyCount++;
        }
        double genWallMs = overall.Elapsed.TotalMilliseconds;

        // ── Stage 2: meshing (only non-empty chunks reach the mesher in the real pipeline) ──
        var mesher      = new GreedyMesher(atlas: null);
        var meshTimesUs = new List<double>();
        long totalVerts = 0, totalIdx = 0;

        overall.Restart();
        foreach (var pos in positions)
        {
            var data = dataByPos[pos];
            if (!data.HasAnySolid()) continue;

            dataByPos.TryGetValue(pos.Offset(-1, 0, 0), out var nX);
            dataByPos.TryGetValue(pos.Offset(1, 0, 0), out var pX);
            dataByPos.TryGetValue(pos.Offset(0, -1, 0), out var nY);
            dataByPos.TryGetValue(pos.Offset(0, 1, 0), out var pY);
            dataByPos.TryGetValue(pos.Offset(0, 0, -1), out var nZ);
            dataByPos.TryGetValue(pos.Offset(0, 0, 1), out var pZ);

            sw.Restart();
            var (verts, idxs) = mesher.Mesh(data, nX, pX, nY, pY, nZ, pZ);
            sw.Stop();

            meshTimesUs.Add(sw.Elapsed.TotalMicroseconds);
            totalVerts += verts.Count;
            totalIdx   += idxs.Count;
        }
        double meshWallMs = overall.Elapsed.TotalMilliseconds;

        // ── Stage 3: static-collider box decomposition (PhysicsBodySystem's per-loaded-chunk cost) ──
        var decomposer  = new VoxelBoxDecomposer();
        var collideTimesUs = new List<double>();

        overall.Restart();
        foreach (var pos in positions)
        {
            var data = dataByPos[pos];
            if (!data.HasAnySolid()) continue;

            sw.Restart();
            decomposer.Decompose(data);
            sw.Stop();
            collideTimesUs.Add(sw.Elapsed.TotalMicroseconds);
        }
        double collideWallMs = overall.Elapsed.TotalMilliseconds;

        // ── Report ──────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"Chunks sampled: {positions.Count}  (empty: {emptyCount}, non-empty: {positions.Count - emptyCount})");
        Console.WriteLine();
        ReportStage("Generate()", genTimesUs, genWallMs);
        Console.WriteLine();
        ReportStage("Mesh()", meshTimesUs, meshWallMs);
        Console.WriteLine();
        Console.WriteLine($"Mesh output: {totalVerts:N0} verts / {totalIdx:N0} indices across {meshTimesUs.Count} non-empty chunks");
        Console.WriteLine();
        ReportStage("Decompose() [static collider]", collideTimesUs, collideWallMs);

        double combinedPerChunkUs = genTimesUs.Sum() / positions.Count
                                   + (meshTimesUs.Count > 0 ? meshTimesUs.Sum() / positions.Count : 0);
        double chunksPerSecSingleThread = 1_000_000.0 / combinedPerChunkUs;
        Console.WriteLine();
        Console.WriteLine($"Combined avg cost per loaded chunk (gen + mesh, empty chunks skip meshing): {combinedPerChunkUs:F2} us");
        Console.WriteLine($"Projected single-threaded throughput: {chunksPerSecSingleThread:N0} chunks/sec");

        int viewVolume = (2 * xzRadius + 1) * (2 * xzRadius + 1) * (2 * yRadius + 1);
        double fillSeconds = viewVolume / chunksPerSecSingleThread;
        Console.WriteLine($"Time to fully populate a {2 * xzRadius + 1}x{2 * yRadius + 1}x{2 * xzRadius + 1} chunk view volume " +
                           $"({viewVolume:N0} chunks) from cold, single-threaded, no per-frame throttle: {fillSeconds:F2} s");
    }

    private static void ReportStage(string name, List<double> samplesUs, double wallMs)
    {
        if (samplesUs.Count == 0)
        {
            Console.WriteLine($"[{name}] no samples");
            return;
        }

        var sorted = samplesUs.OrderBy(x => x).ToList();
        double Percentile(double p)
        {
            int idx = (int)System.Math.Clamp(p * (sorted.Count - 1), 0, sorted.Count - 1);
            return sorted[idx];
        }

        Console.WriteLine($"[{name}] n={sorted.Count}  wall={wallMs:F1}ms");
        Console.WriteLine($"  avg={sorted.Average():F2}us  min={sorted[0]:F2}us  p50={Percentile(0.50):F2}us  " +
                           $"p95={Percentile(0.95):F2}us  p99={Percentile(0.99):F2}us  max={sorted[^1]:F2}us");
    }

    /// <summary>Spirals outward over region cells from the origin looking for the first cell that holds an
    /// island cluster, returning the chunk position of that island's centre. Mirrors TestScene's spawn-finding
    /// search but only needs "an island exists nearby", not "the closest one".</summary>
    internal static ChunkPosition FindFocusChunk(ulong seed)
    {
        Span<IslandDef> islands = stackalloc IslandDef[4];
        for (int ring = 0; ring <= 32; ring++)
        {
            for (int dx = -ring; dx <= ring; dx++)
            for (int dz = -ring; dz <= ring; dz++)
            {
                if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dz)) != ring) continue;

                int n = RegionGrid.ResolveIslandsForCell(seed, dx, dz, islands);
                if (n == 0) continue;

                ref readonly var island = ref islands[0];
                int cx = (int)MathF.Floor(island.CenterX / ChunkData.Size);
                int cy = (int)MathF.Floor(island.BaseY / ChunkData.Size);
                int cz = (int)MathF.Floor(island.CenterZ / ChunkData.Size);
                return new ChunkPosition(cx, cy, cz);
            }
        }

        return new ChunkPosition(0, 0, 0); // astronomically unlikely fallback
    }
}
