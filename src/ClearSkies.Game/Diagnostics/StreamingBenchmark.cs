using System.Diagnostics;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Generation;

namespace ClearSkies.Game.Diagnostics;

/// <summary>
/// Headless replay of the chunk-streaming pipeline (run with <c>--benchmark-stream</c>): loads chunks in
/// ChunkLoadSystem's closest-first order at its per-frame budget while the camera flies along +X, marks
/// neighbours dirty (with and without <see cref="ChunkVolume.FaceHasSolid"/>'s filter), and meshes at the old
/// synchronous 4-per-frame budget — counting how many Mesh() calls the load order causes per chunk. Also splits the
/// static-collider cost (PhysicsBodySystem) into decomposition, child Shapes.Add and the BigCompound tree build,
/// compares merged-block-type boxes and SweepBuild, times the shipped worker/main-thread split, and checks the
/// worker-built tree matches BigCompound's own constructor.
/// </summary>
public static class StreamingBenchmark
{
    private const int LoadsPerFrame = 16, MeshesPerFrame = 4;

    public static void Run(ulong seed = 1337, int xzRadius = 8, int yRadius = 3, int flyChunks = 12, int framesPerChunk = 20)
    {
        Console.WriteLine($"=== Streaming benchmark (seed={seed}, radius={xzRadius}/{yRadius}, fly {flyChunks} chunks @ {framesPerChunk} frames/chunk) ===");
        var generator = new HeartWorldGenerator(seed);
        var focus = GenerationBenchmark.FindFocusChunk(seed);

        var offsets = new List<(int dx, int dy, int dz, int d)>();
        for (int dy = -yRadius; dy <= yRadius; dy++)
        for (int dx = -xzRadius; dx <= xzRadius; dx++)
        for (int dz = -xzRadius; dz <= xzRadius; dz++)
            offsets.Add((dx, dy, dz, dx * dx + dy * dy * 4 + dz * dz));
        offsets.Sort((a, b) => a.d.CompareTo(b.d));

        // Generate everything up front (generation is not what's measured here).
        var cache = new Dictionary<ChunkPosition, ChunkData>();
        ChunkData Gen(ChunkPosition p)
        {
            if (!cache.TryGetValue(p, out var d)) { d = new ChunkData(); generator.Generate(d, p); cache[p] = d; }
            return d;
        }

        foreach (bool filtered in new[] { false, true })
        {
            var loaded = new Dictionary<ChunkPosition, bool>(); // value = NeedsRemesh
            var meshCount = new Dictionary<ChunkPosition, int>();
            var queue = new Queue<ChunkPosition>();
            var last = new ChunkPosition(int.MinValue, 0, 0);
            int frame = 0, totalFrames = flyChunks * framesPerChunk + 400;

            void Mark(ChunkPosition p) { if (loaded.ContainsKey(p)) loaded[p] = true; }

            for (; frame < totalFrames; frame++)
            {
                var cam = focus.Offset(System.Math.Min(frame / framesPerChunk, flyChunks), 0, 0);
                if (cam != last)
                {
                    last = cam;
                    queue.Clear();
                    foreach (var (dx, dy, dz, _) in offsets)
                    {
                        var p = cam.Offset(dx, dy, dz);
                        if (!loaded.ContainsKey(p)) queue.Enqueue(p);
                    }
                    foreach (var p in loaded.Keys.ToList())
                        if (System.Math.Abs(p.X - cam.X) > xzRadius + 1) loaded.Remove(p); // unload (neighbour marks ignored)
                }

                for (int n = 0; n < LoadsPerFrame && queue.Count > 0; n++)
                {
                    var p = queue.Dequeue();
                    if (loaded.ContainsKey(p)) { n--; continue; }
                    var d = Gen(p);
                    loaded[p] = true;
                    for (int f = 0; f < 6; f++)
                        if (!filtered || ChunkVolume.FaceHasSolid(d, f)) Mark(p.Offset(Dir(f)));
                }

                int built = 0;
                foreach (var p in loaded.Keys.ToList())
                {
                    if (!loaded[p]) continue;
                    loaded[p] = false;
                    if (!cache[p].HasAnySolid()) continue;
                    meshCount[p] = meshCount.GetValueOrDefault(p) + 1;
                    if (++built >= MeshesPerFrame) break;
                }
            }

            int chunks = meshCount.Count, calls = meshCount.Values.Sum();
            Console.WriteLine($"[remesh, neighbour filter {(filtered ? "ON " : "OFF")}] non-empty chunks meshed: {chunks}, Mesh() calls: {calls} " +
                              $"({(double)calls / System.Math.Max(1, chunks):F2} per chunk)");
        }

        // ── Collider build split ────────────────────────────────────────────
        var pool = new BufferPool();
        var shapes = new Shapes(pool, 16);
        var decomposer = new VoxelBoxDecomposer();
        var mesher = new GreedyMesher(atlas: null);
        var tDec = new List<double>(); var tAdd = new List<double>(); var tTree = new List<double>(); var tMesh = new List<double>();
        var boxCounts = new List<int>();
        var sw = new Stopwatch();
        foreach (var (p, d) in cache)
        {
            if (!d.HasAnySolid()) continue;
            sw.Restart();
            var boxes = decomposer.Decompose(d);
            tDec.Add(sw.Elapsed.TotalMicroseconds);
            boxCounts.Add(boxes.Count);

            sw.Restart();
            pool.Take<CompoundChild>(boxes.Count, out var children);
            for (int i = 0; i < boxes.Count; i++)
            {
                var (c, s, _) = boxes[i];
                children[i] = new CompoundChild { LocalPose = new RigidPose(c), ShapeIndex = shapes.Add(new Box(s.X, s.Y, s.Z)) };
            }
            tAdd.Add(sw.Elapsed.TotalMicroseconds);

            sw.Restart();
            var big = new BigCompound(children, shapes, pool);
            tTree.Add(sw.Elapsed.TotalMicroseconds);

            sw.Restart();
            mesher.Mesh(d, cache.GetValueOrDefault(p.Offset(-1, 0, 0)), cache.GetValueOrDefault(p.Offset(1, 0, 0)),
                           cache.GetValueOrDefault(p.Offset(0, -1, 0)), cache.GetValueOrDefault(p.Offset(0, 1, 0)),
                           cache.GetValueOrDefault(p.Offset(0, 0, -1)), cache.GetValueOrDefault(p.Offset(0, 0, 1)));
            tMesh.Add(sw.Elapsed.TotalMicroseconds);

            big.Dispose(pool); // children buffer + tree; child boxes leak into the throwaway Shapes, fine here
        }
        Console.WriteLine();
        Console.WriteLine($"Non-empty chunks: {boxCounts.Count}, boxes/chunk avg={boxCounts.Average():F0} max={boxCounts.Max()}");
        Report("Decompose()", tDec);
        Report("Shapes.Add children", tAdd);
        Report("new BigCompound (tree build)", tTree);
        Report("Mesh()", tMesh);

        // ── Collider variants: merged block types, SweepBuild ───────────────
        foreach (var (merge, sweep) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var total = new List<double>(); var counts = new List<int>();
            foreach (var d in cache.Values)
            {
                if (!d.HasAnySolid()) continue;
                sw.Restart();
                var boxes = decomposer.Decompose(d, merge);
                pool.Take<CompoundChild>(boxes.Count, out var children);
                for (int i = 0; i < boxes.Count; i++)
                {
                    var (c, s, _) = boxes[i];
                    children[i] = new CompoundChild { LocalPose = new RigidPose(c), ShapeIndex = shapes.Add(new Box(s.X, s.Y, s.Z)) };
                }
                BigCompound big;
                if (sweep)
                {
                    pool.Take<BepuUtilities.BoundingBox>(boxes.Count, out var bounds);
                    for (int i = 0; i < boxes.Count; i++)
                    {
                        var (c, s, _) = boxes[i];
                        bounds[i] = new BepuUtilities.BoundingBox(c - s * 0.5f, c + s * 0.5f);
                    }
                    var tree = new BepuPhysics.Trees.Tree(pool, boxes.Count);
                    tree.SweepBuild(pool, bounds);
                    pool.Return(ref bounds);
                    big = new BigCompound { Children = children, Tree = tree };
                }
                else big = new BigCompound(children, shapes, pool);
                total.Add(sw.Elapsed.TotalMicroseconds);
                counts.Add(boxes.Count);
                big.Dispose(pool);
            }
            Report($"collider total, merge={merge,-5} sweep={sweep,-5} boxes avg={counts.Average():F0} max={counts.Max()}", total);
        }

        // ── Shipped split: worker half vs main-thread half (PhysicsBodySystem) ──
        using var physics = new ClearSkies.Engine.Physics.PhysicsWorld(new Vector3(0, -6, 0), 1f / 60f);
        var worker = new List<double>(); var main = new List<double>();
        foreach (var d in cache.Values)
        {
            if (!d.HasAnySolid()) continue;
            sw.Restart();
            var boxes = decomposer.Decompose(d, mergeBlockTypes: true);
            if (boxes.Count == 0) continue;
            var build = ClearSkies.Engine.Physics.PhysicsWorld.PrepareStaticCompound(boxes);
            worker.Add(sw.Elapsed.TotalMicroseconds);
            sw.Restart();
            var h = physics.AddStaticCompound(build, Vector3.Zero);
            main.Add(sw.Elapsed.TotalMicroseconds);
            physics.RemoveStaticCompound(h);
        }
        // Equivalence: the worker-built tree must be byte-identical to what BigCompound's own constructor builds.
        int same = 0, diff = 0;
        foreach (var d in cache.Values)
        {
            if (!d.HasAnySolid()) continue;
            var boxes = decomposer.Decompose(d, mergeBlockTypes: true);
            if (boxes.Count <= 1) continue; // single box: AddStaticCompound uses BigCompound's own constructor
            var build = ClearSkies.Engine.Physics.PhysicsWorld.PrepareStaticCompound(boxes);
            pool.Take<CompoundChild>(boxes.Count, out var children);
            for (int i = 0; i < boxes.Count; i++)
                children[i] = new CompoundChild { LocalPose = new RigidPose(boxes[i].center), ShapeIndex = shapes.Add(new Box(boxes[i].size.X, boxes[i].size.Y, boxes[i].size.Z)) };
            var big = new BigCompound(children, shapes, pool);
            var bytes = new byte[big.Tree.GetSerializedByteCount()];
            big.Tree.Serialize(bytes);
            if (bytes.AsSpan().SequenceEqual(ClearSkies.Engine.Physics.PhysicsWorld.DebugTreeBytes(build))) same++;
            else
            {
                // Compare the fields queries actually read (metanode scratch/padding may hold stale pool bytes).
                var ours = new BepuPhysics.Trees.Tree(ClearSkies.Engine.Physics.PhysicsWorld.DebugTreeBytes(build).ToArray(), pool);
                ref var t = ref big.Tree;
                bool eq = ours.NodeCount == t.NodeCount && ours.LeafCount == t.LeafCount;
                for (int i = 0; eq && i < t.NodeCount; i++)
                {
                    ref var a = ref ours.Nodes[i]; ref var b = ref t.Nodes[i];
                    eq = a.A.Min == b.A.Min && a.A.Max == b.A.Max && a.A.Index == b.A.Index && a.A.LeafCount == b.A.LeafCount
                      && a.B.Min == b.B.Min && a.B.Max == b.B.Max && a.B.Index == b.B.Index && a.B.LeafCount == b.B.LeafCount
                      && ours.Metanodes[i].Parent == t.Metanodes[i].Parent && ours.Metanodes[i].IndexInParent == t.Metanodes[i].IndexInParent;
                }
                for (int i = 0; eq && i < t.LeafCount; i++)
                    eq = ours.Leaves[i].NodeIndex == t.Leaves[i].NodeIndex && ours.Leaves[i].ChildIndex == t.Leaves[i].ChildIndex;
                if (eq) same++; else diff++;
                ours.Dispose(pool);
            }
            big.Dispose(pool);
        }
        Console.WriteLine($"[tree equivalence vs BigCompound ctor] identical={same} different={diff}");
        Report("collider worker half (Decompose merged + tree build + serialize)", worker);
        Report("collider MAIN-THREAD half (Shapes.Add + tree copy + Statics.Add)", main);
    }

    private static (int, int, int) Dir(int f) => f switch
    {
        0 => (-1, 0, 0), 1 => (1, 0, 0), 2 => (0, -1, 0), 3 => (0, 1, 0), 4 => (0, 0, -1), _ => (0, 0, 1),
    };

    private static ChunkPosition Offset(this ChunkPosition p, (int x, int y, int z) d) => p.Offset(d.x, d.y, d.z);

    private static void Report(string name, List<double> us)
    {
        var s = us.OrderBy(x => x).ToList();
        Console.WriteLine($"[{name}] avg={s.Average():F0}us p50={s[s.Count / 2]:F0}us p95={s[(int)(s.Count * 0.95)]:F0}us max={s[^1]:F0}us total={s.Sum() / 1000:F0}ms");
    }
}
