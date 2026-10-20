using System.Collections.Concurrent;
using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>Terrain drawn beyond what's loaded in full detail, for the fog (see <see cref="FogSystem.Far"/>).</summary>
public interface IFarTerrain
{
    /// <summary>Whether it's drawing: the fog then follows it instead of the full-detail terrain.</summary>
    bool Active { get; }

    /// <summary>How far from <paramref name="centre"/> (horizontally) the terrain is ready to be seen.</summary>
    float ReadyRadius(Vector3D<float> centre);
}

/// <summary>
/// Distant terrain at half resolution (level 1 of the LOD plan): past <see cref="FullDetailRadius"/> the world is drawn
/// from coarse 32³ chunks whose cells are 2 blocks a side, out to <see cref="OuterRadius"/>. The drawn coarse columns
/// are a grid of their own in the GridStore (<see cref="Grid"/>, voxels 2 blocks a side), which GpuLightSystem lights
/// like the world: sun shadows, bounce and AO, with rays crossing between it and the world. Their meshes bake light maps
/// from it as full-detail chunks do from the world. Every loaded column is in the grid, lit by its own blocks: distant
/// terrain's rays don't see the world, and the world's pass through a column whose full detail is ready (covered), so
/// both levels are lit where they overlap and a swap shows settled light either way. A swap to full detail waits until
/// the full-detail chunks are lit. A coarse column covers a 2x2 group of full columns, and ChunkLoadSystem loads full detail
/// in whole groups (<see cref="ChunkLoadSystem.FullDetailRadius"/>), so each coarse column swaps for its four full ones
/// in one frame: it's drawn (and they're hidden, see <see cref="CoverOf"/>) until all four are loaded and meshed.
/// A swap dissolves the level going away over the one coming in, in a screen dither, over <see cref="FadeSeconds"/>.
///
/// A coarse chunk is made by generating the eight full chunks under it (or loading the edited ones from the store) on a
/// worker, shrinking them (<see cref="Shrink"/>) and meshing the result; only its meshes are kept. Coarse columns are
/// kept within the outer radius, including under the full-detail terrain, so moving back out needs nothing loaded.
/// One under full detail whose chunks were edited meanwhile is made again when it's next drawn.
/// A panel toggle (Distant Terrain) turns it off, for comparing against full detail everywhere; the game starts it on.
/// </summary>
public sealed class LodTerrainSystem : ISystem, IRenderSystem, IDebugUiSystem, IFarTerrain
{
    private const int S = ChunkData.Size;
    private const int Cell = 2;                  // blocks per coarse cell
    private const int ColumnBlocks = S * Cell;   // a coarse column's width
    private const int MaxInFlight = 16;
    private const double ResidentBudgetMs = 1.0;
    private const double UploadBudgetMs = 2.0;

    private readonly World _world;
    private readonly Renderer _renderer;
    private readonly ChunkLoadSystem _load;
    private readonly ChunkVolume _volume;
    private readonly IChunkStore _store;
    private readonly GridStore? _gridStore;
    private readonly ThreadLocal<IWorldGenerator> _generator;
    private readonly ThreadLocal<GreedyMesher> _meshers;
    private readonly EntitySet _interests;

    /// <summary>Whether distant terrain is drawn coarse (and full detail only loads to <see cref="FullDetailRadius"/>).</summary>
    public bool Enabled;

    /// <summary>How far the view's terrain loads in full detail (blocks).</summary>
    public float FullDetailRadius = 1024f;

    /// <summary>How far coarse terrain is drawn (blocks).</summary>
    public float OuterRadius = 2048f;

    /// <summary>How long a swap between detail levels takes to dissolve one into the other (seconds; 0: at once).</summary>
    public float FadeSeconds = 2f;
    private float _time;

    /// <summary>How a coarse cell is made from its 8 blocks (see <see cref="Shrink"/>): by majority, or solid if any
    /// block is (the first rule tried; it raises coarse ground half a block on average). Changing it applies to columns
    /// made afterwards.</summary>
    public bool Majority = true;

    /// <summary>Whether coarse cells keep plants (grass, mushrooms) at their own scale, 2 blocks, so distant ground is as
    /// busy as full detail where it swaps (see <see cref="Shrink"/>). Changing it applies to columns made afterwards.</summary>
    public bool Plants = true;

    /// <summary>The loaded coarse columns as a grid of 2-block voxels, lit like the world (see
    /// GpuLightSystem.AddStaticGrid). Unregistered (index -1) without a GridStore: then they're lit as open air.</summary>
    public GridHandle Grid { get; } = new();

    private sealed class CoarseChunk
    {
        public int Y;
        public GpuMesh? Opaque, Cutout, Transparent;
        public PackedOpacity? Occupancy; // for the grid (shared for all-air and all-solid chunks)
        public ChunkPosition Pos;        // in coarse chunks
        public Mat4 Model;               // coarse cells to world
        public Vector3D<float> Min, Centre;
        public bool EverBaked;           // its light maps have been baked from the grid (it can be drawn lit from it)
    }

    private sealed class CoarseColumn
    {
        public readonly List<CoarseChunk> Chunks = new();
        public readonly List<CoarseChunk> Shown = new(); // the chunks with faces (not buried)
        public int MinY, MaxY;
        public bool Drawn;       // this frame: not all four full columns are ready
        public bool Ready, ReadyKnown; // whether all four are ready, as last found (see _readyDirty)
        public bool ChildEdited; // a full chunk under it was edited while it was hidden
        public readonly List<int> Resident = new(); // the chunk layers it has in the grid so far
        public List<int>? ToPut;                    // the layers still to put in the grid (null: not started)
        public bool InGrid => ToPut is { Count: 0 };
        public bool Settled;   // in the grid and lit since it went in: its light maps follow the grid
        public bool Baked;     // lit from the grid: it settled there at least once
        public bool Covered;   // its full columns are ready, so the world's rays pass through it (GridStore.SetCovered)
        public bool Leaving;   // its full columns are ready: still drawn until they're lit
        public int Waiting, LeaveWait; // frames spent settling, and leaving
        public int LitSoFar, ChildrenLitSoFar; // how many of the chunks it waits on (settling, leaving) were found lit
        public float FadeInAt = float.NegativeInfinity, FadeOutAt = float.NegativeInfinity; // when it was last shown, hidden
        public float SettledAt = float.NegativeInfinity; // when it settled (a column held back for light shows from then)
    }

    // A swap waits for the incoming terrain's light (see UpdateDrawn): at least this many frames, since a change is
    // marked for lighting the frame after it's made, and at most the other, in case lighting never settles there.
    private const int MinSwapFrames = 2, MaxSwapFrames = 240;

    // A column that's never been shown waits for its light longer: it's hidden (behind the fog) meanwhile, rather than
    // showing light that's still coming in. The limit is for lighting that never settles there.
    private const int MaxFirstLightFrames = 1800;

    /// <summary>Whether a chunk of a grid is lit (GpuLightSystem.IsLit); without it, everything counts as lit.</summary>
    public Func<GridHandle, ChunkPosition, bool>? IsLit;

    /// <summary>Whether a chunk of a grid is lit and its light done changing (GpuLightSystem.IsSettled), for a column
    /// shown for the first time; without it, <see cref="IsLit"/> is used.</summary>
    public Func<GridHandle, ChunkPosition, bool>? IsSettled;

    // Shared occupancy for the coarse chunks that are all one thing, and for the air right above and below a column's
    // chunks (so the faces there have air with light in it).
    private static readonly PackedOpacity AllAir = new(new uint[GridStore.WordsPerChunk], 0UL, ulong.MaxValue, new(), null, 0UL);
    private static readonly PackedOpacity AllSolid = new(new uint[GridStore.WordsPerChunk], ulong.MaxValue, 0UL, new(), null, 0UL);

    private readonly record struct Built(int Y, ChunkMeshSystem.Packed Opaque, ChunkMeshSystem.Packed Cutout,
                                         ChunkMeshSystem.Packed Transparent, PackedOpacity Occupancy);
    private readonly record struct Result((int x, int z) Column, List<Built> Chunks, Exception? Error);

    private readonly Dictionary<(int x, int z), CoarseColumn> _columns = new();
    private readonly HashSet<(int x, int z)> _inFlight = new();
    private readonly List<(int x, int z)> _queue = new();
    private int _queueHead;
    private readonly ConcurrentQueue<Result> _results = new();
    private (int x, int z) _centre = (int.MinValue, int.MinValue);
    private int _outerColumns = -1;
    private bool _wasEnabled;
    private Vector3D<float> _view;

    private readonly record struct Draw(float DistSq, CoarseChunk Chunk, int Grid, bool Bake, float Dissolve);

    // Light map baking a frame for coarse chunks, in quads (nearest first; one that's been drawn before is drawn with its
    // last light meanwhile, one never baked waits). Columns settling in bulk (loading, flying) bake over a few frames.
    private const int BakeQuadBudget = 65536;
    private int _bakedQuads;
    private readonly List<Draw> _draws = new();
    private static readonly Comparison<Draw> NearestFirst = (a, b) => a.DistSq.CompareTo(b.DistSq);
    private int _built, _chunksDrawn;
    private long _quads;
    private int _frame;
    private int _editScan; // which ready column UpdateDrawn checks for edits this frame

    // CPU time of each step, for the debug panel.
    private const int ApplyStep = 0, DispatchStep = 1, SwapStep = 2, ListStep = 3, LightmapStep = 4, DrawStep = 5, ResidentStep = 6,
                      ReadyStep = 7, EditStep = 8, LitStep = 9;
    private readonly StepTimer _steps = new("Adding finished columns", "Queueing and dispatching columns",
                                            "Swaps: the rest", "Render: listing draws",
                                            "Render: light maps", "Render: recording draws",
                                            "Swaps: putting columns in the light grid",
                                            "Swaps: full detail readiness", "Swaps: edit checks",
                                            "Swaps: lighting checks") { Owner = "Distant Terrain" };

    public LodTerrainSystem(World world, Renderer renderer, ChunkLoadSystem load, ChunkVolume staticVolume,
                            Func<IWorldGenerator> generatorFactory, IChunkStore store, GridStore? gridStore = null)
    {
        _world = world;
        _renderer = renderer;
        _load = load;
        _volume = staticVolume;
        _store = store;
        _gridStore = gridStore;
        gridStore?.RegisterStatic(Grid, Cell);
        _generator = new ThreadLocal<IWorldGenerator>(generatorFactory);
        var atlas = renderer.Atlas;
        _meshers = new ThreadLocal<GreedyMesher>(() => new GreedyMesher(atlas));
        _interests = world.GetEntities().With<TerrainInterest>().With<Transform>().AsSet();

        // Whether a coarse column's full detail is ready is found again only when something it depends on changes.
        load.ColumnChanged += (x, z) => _readyDirty.Add((x >> 1, z >> 1));
        world.SubscribeComponentAdded((in Entity e, in ChunkMeshedFlag _) => ChunkChanged(e));
        world.SubscribeComponentRemoved((in Entity e, in ChunkMeshedFlag _) => ChunkChanged(e));
        world.SubscribeEntityDisposed((in Entity e) => ChunkChanged(e));
    }

    private void ChunkChanged(in Entity e)
    {
        if (!e.Has<Chunk>()) return;
        var entry = e.Get<Chunk>().Entry;
        if (entry.Volume == _volume) _readyDirty.Add((entry.Position.X >> 1, entry.Position.Z >> 1));
    }

    private readonly HashSet<(int x, int z)> _readyDirty = new();
    private int _recheck; // where the round of near columns looked at anyway is up to
    private readonly HashSet<(int x, int z)> _editChecks = new();
    private readonly Queue<(int x, int z)> _editCheckOrder = new();
    private const int EditChecksPerFrame = 2;
    private const int RecheckPerFrame = 16;

    public bool Active => Enabled;

    /// <summary>How much full column (x, z) is hidden by the coarse column over it (see ChunkRenderSystem.Cover): 1
    /// while it's drawn, rising from 0 to 1 over <see cref="FadeSeconds"/> as it comes in, 0 when it isn't.</summary>
    public float CoverOf(int x, int z)
    {
        if (!Enabled || !_columns.TryGetValue((x >> 1, z >> 1), out var c) || !c.Drawn || WaitsForLight(c)) return 0f;
        return Faded(MathF.Max(c.FadeInAt, c.SettledAt));
    }

    /// <summary>How far through its fade a swap that started at <paramref name="at"/> is, 0-1.</summary>
    private float Faded(float at) => FadeSeconds <= 0f ? 1f : System.Math.Clamp((_time - at) / FadeSeconds, 0f, 1f);

    public void Update(float dt)
    {
        _steps.Start();
        _time += dt;
        _frame++;
        if (!Enabled)
        {
            if (_wasEnabled) { _load.FullDetailRadius = null; UnloadAll(); _wasEnabled = false; }
            return;
        }
        _wasEnabled = true;
        FullDetailRadius = MathF.Max(FullDetailRadius, 64f);
        OuterRadius = MathF.Max(OuterRadius, FullDetailRadius + ColumnBlocks);
        _load.FullDetailRadius = FullDetailRadius;
        if (!TryView(out _view)) return;

        var centre = ((int)MathF.Floor(_view.X / ColumnBlocks), (int)MathF.Floor(_view.Z / ColumnBlocks));
        int outer = (int)MathF.Ceiling(OuterRadius / ColumnBlocks);
        if (centre != _centre || outer != _outerColumns) Rebuild(centre, outer);
        FreeSome();
        _steps.Lap(DispatchStep);

        ApplyResults();
        _steps.Lap(ApplyStep);
        Dispatch();
        _steps.Lap(DispatchStep);
        UpdateDrawn();
        _steps.Lap(SwapStep);
    }

    private bool TryView(out Vector3D<float> centre)
    {
        foreach (ref readonly Entity e in _interests.GetEntities())
        {
            if (e.Get<TerrainInterest>().DrawRadius <= 0) continue;
            centre = e.Get<Transform>().Position;
            return true;
        }
        centre = default;
        return false;
    }

    private bool Wanted((int x, int z) c)
        => Sq(c.x - _centre.x) + Sq(c.z - _centre.z) <= Sq(_outerColumns);

    private static long Sq(int v) => (long)v * v;

    /// <summary>The view moved to another coarse column (or the radius changed): drops what's out of range and queues
    /// what's missing, nearest first.</summary>
    private void Rebuild((int x, int z) centre, int outer)
    {
        _centre = centre;
        _outerColumns = outer;
        // Out of range: not drawn from now on, and freed (out of the grid, meshes released) a few a frame.
        foreach (var key in _columns.Keys.Where(k => !Wanted(k)).ToList())
        {
            _columns.Remove(key, out var gone);
            if (_toFree.Remove(key, out var older)) Free(key, older);
            _toFree[key] = gone!;
        }

        _queue.Clear();
        _queueHead = 0;
        for (int dz = -outer; dz <= outer; dz++)
        for (int dx = -outer; dx <= outer; dx++)
        {
            var c = (centre.x + dx, centre.z + dz);
            if (dx * dx + dz * dz > outer * outer || _columns.ContainsKey(c) || _inFlight.Contains(c)) continue;
            _queue.Add(c);
        }
        _queue.Sort((a, b) => (Sq(a.Item1 - centre.x) + Sq(a.Item2 - centre.z))
                              .CompareTo(Sq(b.Item1 - centre.x) + Sq(b.Item2 - centre.z)));
    }

    /// <summary>Hands queued coarse columns to workers; one whose edited chunks haven't arrived from the store waits
    /// (they're asked for) while the ones after it go.</summary>
    private void Dispatch()
    {
        int i = _queueHead, waiting = 0;
        while (_inFlight.Count < MaxInFlight && i < _queue.Count && waiting < MaxInFlight)
        {
            var col = _queue[i];
            if (_inFlight.Contains(col) || !Wanted(col)) { Dequeue(ref i); continue; }
            var work = Work(col);
            if (!Arrived(work)) { i++; waiting++; continue; }
            Dequeue(ref i);
            DispatchColumn(col, work);
        }
    }

    private void Dequeue(ref int i)
    {
        if (i == _queueHead) i = ++_queueHead;
        else _queue.RemoveAt(i);
    }

    /// <summary>The full chunks under coarse column <paramref name="col"/> that may hold something, with whether each
    /// is edited (loaded from the store).</summary>
    private List<(ChunkPosition Pos, bool Edited)> Work((int x, int z) col)
    {
        var work = new List<(ChunkPosition, bool)>();
        int minY = _load.MinLayer;
        for (int j = 0; j < 4; j++)
        {
            int x = col.x * 2 + (j & 1), z = col.z * 2 + (j >> 1);
            ulong bits = _generator.Value!.ColumnLayers(x, z, minY) | _load.BuiltLayers(x, z);
            for (; bits != 0; bits &= bits - 1)
            {
                var p = new ChunkPosition(x, minY + BitOperations.TrailingZeroCount(bits), z);
                work.Add((p, _load.IsEdited(p)));
            }
        }
        return work;
    }

    private bool Arrived(List<(ChunkPosition Pos, bool Edited)> work)
    {
        bool all = true;
        foreach (var (pos, edited) in work)
        {
            if (!edited || _store.IsReady(pos)) continue;
            _store.Request(pos);
            all = _store.IsReady(pos) && all;
        }
        return all;
    }

    private void DispatchColumn((int x, int z) col, List<(ChunkPosition Pos, bool Edited)> work)
    {
        _inFlight.Add(col);
        BackgroundWork.Queue(() =>
        {
            var built = new List<Built>();
            try
            {
                // One full column at a time, bottom to top: the generator profiles a column once for all its chunks,
                // so going layer by layer across the four (each coarse chunk in turn) made it profile again for every
                // chunk, about ten times the work.
                var coarse = new SortedDictionary<int, ChunkData>();
                bool majority = Majority, plants = Plants;
                foreach (var (pos, edited) in work)
                {
                    var full = new ChunkData();
                    if (!(edited && _store.TryLoad(pos, full))) _generator.Value!.Generate(full, pos);
                    if (!full.IsUniform(out var only) || only != BlockId.Air)
                    {
                        if (!coarse.TryGetValue(pos.Y >> 1, out var into)) coarse[pos.Y >> 1] = into = new ChunkData();
                        Shrink(full, into, (pos.X & 1) * 16, (pos.Y & 1) * 16, (pos.Z & 1) * 16, majority, plants);
                    }
                    full.Release();
                }
                foreach (var (y, chunk) in coarse)
                {
                    if (chunk.HasAnyNonAir())
                    {
                        var mesh = _meshers.Value!.Mesh(chunk, null, null, null, null, null, null,
                                                        neighboursForTransparentOnly: true,
                                                        position: new ChunkPosition(col.x, y, col.z));
                        // Buried chunks have no faces but still shadow and reflect, so every chunk with blocks is kept.
                        built.Add(new Built(y, ChunkMeshSystem.PackQuads(mesh.Opaque), ChunkMeshSystem.PackQuads(mesh.Cutout),
                                            ChunkMeshSystem.PackQuads(mesh.Translucent, twoSided: true), Occupancy(chunk)));
                    }
                    chunk.Release();
                }
                _results.Enqueue(new Result(col, built, null));
            }
            catch (Exception e)
            {
                _results.Enqueue(new Result(col, built, e));
            }
        });
    }

    /// <summary>Shrinks full chunk <paramref name="full"/> into the 16³ octant of <paramref name="coarse"/> at
    /// (ox, oy, oz). Model blocks are left out, and plants unless <paramref name="plants"/> (then an open cell keeps one, see below). By <paramref name="majority"/>: a cell is filled when at least
    /// 4 of its 8 blocks are cubes, with the more common kind (opaque or see-through) and its most common block, so coarse
    /// surfaces round to the nearest cell and meet full detail flush on average; thin things (a fence of cubes, a
    /// one-block ledge) may go, except leaves (<see cref="BlockDef.ShowsInnerFaces"/>), which fill a cell if any of its
    /// blocks is one, so trees stay. Otherwise any cube fills the cell: the most common opaque one, else the most common
    /// see-through one.</summary>
    internal static void Shrink(ChunkData full, ChunkData coarse, int ox, int oy, int oz, bool majority = true,
                                bool plants = false)
    {
        if (full.IsUniform(out var only))
        {
            if (!IsCube(only)) return;
            for (int z = 0; z < 16; z++) for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
                coarse.Set(ox + x, oy + y, oz + z, only);
            return;
        }
        Span<BlockId> cell = stackalloc BlockId[8];
        Span<BlockId> plantOn = stackalloc BlockId[16]; // per x: a plant on top of the cell below (for the cell above)
        for (int z = 0; z < 16; z++)
        {
            plantOn.Clear();
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                for (int k = 0; k < 8; k++)
                    cell[k] = full.Get(2 * x + (k & 1), 2 * y + ((k >> 1) & 1), 2 * z + (k >> 2));
                var pick = majority ? ByMajority(cell) : ByAny(cell);
                if (pick != BlockId.Air) coarse.Set(ox + x, oy + y, oz + z, pick);
                if (!plants) continue;
                // Plants (grass, mushrooms): a cell left open keeps the commonest one in it, or failing that one that
                // grew on top of the cell below, whose ground filled it; so coarse ground stays as grassy as full detail.
                if (pick == BlockId.Air)
                {
                    var plant = MostCommonPlant(cell, upperOnly: false);
                    if (plant == BlockId.Air) plant = plantOn[x];
                    if (plant != BlockId.Air) coarse.Set(ox + x, oy + y, oz + z, plant);
                    plantOn[x] = BlockId.Air;
                }
                else plantOn[x] = MostCommonPlant(cell, upperOnly: true);
            }
        }
    }

    private static BlockId MostCommonPlant(ReadOnlySpan<BlockId> cell, bool upperOnly)
    {
        BlockId best = BlockId.Air;
        int bestCount = 0;
        for (int i = 0; i < cell.Length; i++)
        {
            if (upperOnly && ((i >> 1) & 1) == 0) continue;
            var id = cell[i];
            if (id == BlockId.Air || !BlockRegistry.Get(id).IsCross) continue;
            int n = 0;
            for (int j = 0; j < cell.Length; j++) if (cell[j] == id) n++;
            if (n > bestCount) { best = id; bestCount = n; }
        }
        return best;
    }

    private static BlockId ByAny(ReadOnlySpan<BlockId> cell)
    {
        var pick = MostCommon(cell, opaque: true);
        return pick != BlockId.Air ? pick : MostCommon(cell, opaque: false);
    }

    private static BlockId ByMajority(ReadOnlySpan<BlockId> cell)
    {
        int opaque = 0, see = 0;
        bool leaves = false;
        foreach (var id in cell)
        {
            if (!IsCube(id)) continue;
            var def = BlockRegistry.Get(id);
            if (def.Transparent) { see++; leaves |= def.ShowsInnerFaces; }
            else opaque++;
        }
        if (opaque + see >= 4) return MostCommon(cell, opaque: opaque >= see);
        return leaves ? MostCommon(cell, opaque: false, leavesOnly: true) : BlockId.Air;
    }

    /// <summary>A coarse chunk's occupancy for the grid; the shared one for a chunk that's all air or all solid.</summary>
    private static PackedOpacity Occupancy(ChunkData coarse)
    {
        var p = GridStore.Pack(coarse);
        if (p.SeeThrough != null) return p;
        return p.Solid == 0 ? AllAir : p.Air == 0 ? AllSolid : p;
    }

    private static bool IsCube(BlockId id) => id != BlockId.Air && BlockRegistry.Get(id).IsFullCube;

    private static BlockId MostCommon(ReadOnlySpan<BlockId> cell, bool opaque, bool leavesOnly = false)
    {
        BlockId best = BlockId.Air;
        int bestCount = 0;
        for (int i = 0; i < cell.Length; i++)
        {
            var id = cell[i];
            if (!IsCube(id) || BlockRegistry.Get(id).Transparent == opaque) continue;
            if (leavesOnly && !BlockRegistry.Get(id).ShowsInnerFaces) continue;
            int n = 0;
            for (int j = 0; j < cell.Length; j++) if (cell[j] == id) n++;
            if (n > bestCount) { best = id; bestCount = n; }
        }
        return best;
    }

    private void ApplyResults()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < UploadBudgetMs
               && _results.TryDequeue(out var r))
        {
            _inFlight.Remove(r.Column);
            try
            {
                if (r.Error != null) { Console.WriteLine($"[lod] coarse column failed: {r.Error}"); continue; }
                if (!Enabled || !Wanted(r.Column)) continue;
                var column = new CoarseColumn { MinY = int.MaxValue, MaxY = int.MinValue };
                foreach (var b in r.Chunks)
                {
                    var chunk = new CoarseChunk
                    {
                        Y = b.Y, Opaque = Upload(b.Opaque, LightmapKind.Opaque), Cutout = Upload(b.Cutout, LightmapKind.SeeThrough),
                        Transparent = Upload(b.Transparent, LightmapKind.SeeThrough | LightmapKind.TwoSided),
                        Occupancy = b.Occupancy, Pos = new ChunkPosition(r.Column.x, b.Y, r.Column.z),
                        Min = new Vector3D<float>(r.Column.x * ColumnBlocks, b.Y * ColumnBlocks, r.Column.z * ColumnBlocks),
                    };
                    chunk.Centre = chunk.Min + new Vector3D<float>(ColumnBlocks * 0.5f);
                    chunk.Model = Mat4.Multiply(Mat4.Translation(chunk.Min), Mat4.Scale(new Vector3D<float>(Cell)));
                    column.Chunks.Add(chunk);
                    if (chunk.Opaque != null || chunk.Cutout != null || chunk.Transparent != null) column.Shown.Add(chunk);
                    column.MinY = System.Math.Min(column.MinY, b.Y);
                    column.MaxY = System.Math.Max(column.MaxY, b.Y);
                }
                column.Shown.Sort((a, b) => a.Pos.Y.CompareTo(b.Pos.Y)); // so the first and last bound it
                Unload(r.Column); // a column made again replaces the one drawn meanwhile
                _columns[r.Column] = column;
                _built++;
            }
            finally
            {
                foreach (var b in r.Chunks) { Return(b.Opaque); Return(b.Cutout); Return(b.Transparent); }
            }
        }
    }

    private static void Return(in ChunkMeshSystem.Packed p)
    {
        if (p.Data.Length > 0) System.Buffers.ArrayPool<byte>.Shared.Return(p.Data);
    }

    /// <summary>Uploads a coarse mesh, with room for a light map (baked from <see cref="Grid"/> while it's in it).</summary>
    private GpuMesh? Upload(in ChunkMeshSystem.Packed p, LightmapKind kind)
    {
        if (p.QuadCount == 0) return null;
        _quads += p.QuadCount;
        return _renderer.UploadChunkQuads(p.Data.AsSpan(0, p.Bytes), (uint)p.QuadCount, _gridStore != null ? p.LightWords : 0, kind);
    }

    private readonly SortedSet<int> _layers = new();
    private bool _gridFull; // a column didn't fit in the grid: none is tried again until one is evicted

    /// <summary>Puts a column's chunks in <see cref="Grid"/>, with the air right above and below each run of them, for
    /// the lighting to light.</summary>
    /// <summary>Puts more of a column's chunks in the grid, until <paramref name="until"/> (a timestamp): each writes
    /// its occupancy and surface bricks, which for a tall column adds up to more than a frame should spend.</summary>
    private void MakeResident((int x, int z) key, CoarseColumn column, long until)
    {
        if (_gridStore == null || Grid.Index < 0 || column.Chunks.Count == 0) return;
        if (column.ToPut == null)
        {
            if (_gridFull) return;
            _layers.Clear();
            foreach (var c in column.Chunks) { _layers.Add(c.Y - 1); _layers.Add(c.Y); _layers.Add(c.Y + 1); }
            if (!_gridStore.CanHold(Grid, _layers.Count)) { _gridFull = true; return; } // drawn as open air until one goes
            column.ToPut = new List<int>(_layers);
        }
        while (column.ToPut.Count > 0 && System.Diagnostics.Stopwatch.GetTimestamp() < until)
        {
            int y = column.ToPut[^1];
            column.ToPut.RemoveAt(column.ToPut.Count - 1);
            var occupancy = AllAir;
            foreach (var c in column.Chunks) if (c.Y == y) { occupancy = c.Occupancy ?? AllAir; break; }
            _gridStore.UploadPacked(Grid, new ChunkPosition(key.x, y, key.z), occupancy);
            column.Resident.Add(y);
            column.Covered = false; // fresh records aren't: set again for all of them below if it should be
        }
    }

    /// <summary>Takes a column's chunks out of <see cref="Grid"/> (it's unloaded).</summary>
    private void Evict((int x, int z) key, CoarseColumn column)
    {
        if (_gridStore == null) return;
        foreach (int y in column.Resident) _gridStore.RemoveChunk(Grid, new ChunkPosition(key.x, y, key.z));
        if (column.Resident.Count > 0) _gridFull = false;
        column.Resident.Clear();
        column.ToPut = null;
        column.Settled = false;
    }

    private readonly Dictionary<(int x, int z), CoarseColumn> _toFree = new();
    private const double FreeBudgetMs = 0.5;

    /// <summary>Frees out-of-range columns (see Rebuild) while the budget lasts.</summary>
    private void FreeSome()
    {
        if (_toFree.Count == 0) return;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (_toFree.Count > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < FreeBudgetMs)
        {
            var (key, column) = _toFree.First();
            _toFree.Remove(key);
            Free(key, column);
        }
    }

    private void Unload((int x, int z) key)
    {
        // One out of range still to be freed goes first: its chunks in the grid are at the same places.
        if (_toFree.Remove(key, out var older)) Free(key, older);
        if (_columns.Remove(key, out var column)) Free(key, column);
    }

    private void Free((int x, int z) key, CoarseColumn column)
    {
        Evict(key, column);
        foreach (var c in column.Chunks)
        {
            _quads -= (c.Opaque?.QuadCount ?? 0) + (c.Cutout?.QuadCount ?? 0) + (c.Transparent?.QuadCount ?? 0);
            c.Opaque?.Dispose(); c.Cutout?.Dispose(); c.Transparent?.Dispose();
        }
    }

    /// <summary>Makes every coarse column again (the shrink rule changed); each is drawn as it was until then.</summary>
    private void Remake()
    {
        foreach (var key in _columns.Keys)
            if (!_inFlight.Contains(key) && !_queue.Contains(key)) _queue.Add(key);
        _queue.Sort(_queueHead, _queue.Count - _queueHead, Comparer<(int x, int z)>.Create(
            (a, b) => (Sq(a.x - _centre.x) + Sq(a.z - _centre.z)).CompareTo(Sq(b.x - _centre.x) + Sq(b.z - _centre.z))));
    }

    private void UnloadAll()
    {
        foreach (var key in _columns.Keys.ToList()) Unload(key);
        foreach (var (key, column) in _toFree) Free(key, column);
        _toFree.Clear();
        _queue.Clear();
        _queueHead = 0;
        _centre = (int.MinValue, int.MinValue);
        _outerColumns = -1;
    }

    /// <summary>Which coarse columns are drawn this frame: those whose four full columns aren't all ready. One hidden
    /// under full detail notes any edit to the chunks there, and is made again when it's next drawn.</summary>
    private void UpdateDrawn()
    {
        // Edits under hidden columns are looked for in one of them a frame (round the ready ones in turn), and in each
        // as it comes back into view, rather than in all of them every frame (256 chunks each).
        // Only columns near the view can have full detail ready (it loads to FullDetailRadius, in the 2x2 groups that
        // coarse columns are); a far one that's drawn, settled and uncovered has nothing to do.
        int readyIndex = 0, scan = _editScan;
        long nearSq = Sq((int)MathF.Ceiling(FullDetailRadius / ColumnBlocks) + 2);
        // Moving on changes which full columns are wanted, but that alone changes no column's readiness until its
        // chunks load or unload (which mark it), so it's not looked at all again. A few near columns a frame are
        // looked at anyway, round them in turn, in case a change came without a mark.
        int nearIndex = 0, recheckFrom = _recheck, recheckTo = _recheck + RecheckPerFrame;
        double residentMs = 0; // spent putting columns in the grid this frame (not the rest of this loop)
        _litChecksLeft = LitChecksPerFrame;
        foreach (var (key, column) in _columns)
        {
            bool near = Sq(key.x - _centre.x) + Sq(key.z - _centre.z) <= nearSq;
            if (!near) column.ReadyKnown = false; // found again when it's next near (changes meanwhile aren't kept)
            if (!near && column.Drawn && column.Settled && !column.Covered && !column.Leaving) continue;
            bool due = false;
            if (near) { due = nearIndex >= recheckFrom && nearIndex < recheckTo; nearIndex++; }
            if (near && (due || !column.ReadyKnown || _readyDirty.Contains(key)))
            {
                _steps.Lap(SwapStep);
                bool all = true;
                for (int j = 0; j < 4 && all; j++) all = _load.IsColumnReady(key.x * 2 + (j & 1), key.z * 2 + (j >> 1));
                _steps.Lap(ReadyStep);
                column.Ready = all;
                column.ReadyKnown = true;
            }
            bool ready = near && column.Ready;
            if (ready)
            {
                if (readyIndex++ == scan && !column.ChildEdited) column.ChildEdited = AnyEdited(key);
            }
            else if (!column.Drawn && !_inFlight.Contains(key) && column.ChildEdited)
            {
                column.ChildEdited = false;
                _queue.Insert(_queueHead, key); // drawn as it was until it's made again
            }
            else if (!column.Drawn && column.Covered && _editChecks.Add(key)) _editCheckOrder.Enqueue(key); // see below
            // In the grid while it's loaded, so it's lit and ready whenever full detail goes; until it's lit, a drawn
            // column keeps the light maps it baked before, or is lit as open air if it never had any.
            if (!column.InGrid)
            {
                // Putting a column in the grid writes each chunk's occupancy and its surface bricks: as many a frame as
                // fit the budget, the rest on later frames (held back, or lit as before, meanwhile).
                if (residentMs < ResidentBudgetMs)
                {
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    _steps.Lap(SwapStep);
                    MakeResident(key, column, start + (long)((ResidentBudgetMs - residentMs) * System.Diagnostics.Stopwatch.Frequency / 1000));
                    _steps.Lap(ResidentStep);
                    residentMs += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                column.Settled = false;
                column.Waiting = 0;
                column.LitSoFar = 0;
            }
            else if (!column.Settled)
            {
                column.Settled = ++column.Waiting >= MinSwapFrames && CheckNow(key)
                                 && (column.Waiting > (column.Baked ? MaxSwapFrames : MaxFirstLightFrames)
                                     || ResidentLit(key, column));
                if (column.Settled && !column.Baked) column.SettledAt = _time; // shown from now
            }
            // Covered while its full detail is ready: the world's rays then see only full-detail blocks there.
            if (column.Covered != ready) SetCovered(key, column, ready);

            if (!ready)
            {
                column.Leaving = false;
                if (!column.Drawn) column.FadeInAt = _time;
                column.Drawn = true;
            }
            else if (column.Drawn)
            {
                // Swapping to full detail: hidden once that terrain is lit by its own blocks (not the coarse ones over
                // it), so it comes in showing its light.
                if (!column.Leaving) { column.Leaving = true; column.LeaveWait = 0; column.ChildrenLitSoFar = 0; }
                if (++column.LeaveWait >= MinSwapFrames && CheckNow(key) && (column.LeaveWait > MaxSwapFrames || ChildrenLit(key, column)))
                {
                    column.Drawn = false;
                    column.Leaving = false;
                    column.FadeOutAt = _time;
                }
            }
        }
        _editScan = readyIndex == 0 ? 0 : (scan + 1) % readyIndex;
        _readyDirty.Clear();

        // Columns coming back into view are checked for edits under them a few a frame (256 chunks each): many come
        // back at once when the view moves on. Each is drawn as it was meanwhile, and made again if edited.
        for (int n = 0; n < EditChecksPerFrame && _editCheckOrder.TryDequeue(out var k); n++)
        {
            _editChecks.Remove(k);
            if (_columns.ContainsKey(k) && !_inFlight.Contains(k) && AnyEdited(k)) _queue.Insert(_queueHead, k);
        }
        _recheck = recheckTo < nearIndex ? recheckTo : 0;
    }

    /// <summary>Whether a column waiting on lighting looks again this frame: every <see cref="LitCheckEvery"/> frames,
    /// staggered across columns, as a check can read hundreds of chunks' bricks.</summary>
    private bool CheckNow((int x, int z) key) => ((_frame + key.x * 3 + key.z * 5) & (LitCheckEvery - 1)) == 0;
    private const int LitCheckEvery = 4;

    private void SetCovered((int x, int z) key, CoarseColumn column, bool covered)
    {
        column.Covered = covered;
        if (_gridStore == null) return;
        foreach (int y in column.Resident) _gridStore.SetCovered(Grid, new ChunkPosition(key.x, y, key.z), covered);
    }

    // Both go on from the first chunk not lit last time (LitSoFar, ChildrenLitSoFar), rather than reading every chunk's
    // bricks again on each look, and read them all once more when they get to the end, for any that went unlit since.
    private bool ResidentLit((int x, int z) key, CoarseColumn column)
    {
        _steps.Lap(SwapStep);
        bool result = ResidentLitUntimed(key, column);
        _steps.Lap(LitStep);
        return result;
    }

    private bool ResidentLitUntimed((int x, int z) key, CoarseColumn column)
    {
        var lit = column.Baked ? IsLit : IsSettled ?? IsLit; // shown for the first time: once its light is done
        if (lit == null) return true;
        while (column.LitSoFar < column.Resident.Count)
        {
            for (; column.LitSoFar < column.Resident.Count; column.LitSoFar++)
                if (!Budgeted(lit, Grid, new ChunkPosition(key.x, column.Resident[column.LitSoFar], key.z))) return false;
            column.LitSoFar = FirstUnlit(lit, column.Resident.Count, i => new ChunkPosition(key.x, column.Resident[i], key.z), Grid);
        }
        return true;
    }

    private bool ChildrenLit((int x, int z) key, CoarseColumn column)
    {
        _steps.Lap(SwapStep);
        bool result = ChildrenLitUntimed(key, column);
        _steps.Lap(LitStep);
        return result;
    }

    private bool ChildrenLitUntimed((int x, int z) key, CoarseColumn column)
    {
        if (IsLit == null) return true;
        int minY = _load.MinLayer;
        ChunkPosition At(int i) => new(key.x * 2 + ((i >> 6) & 1), minY + (i & 63), key.z * 2 + (i >> 7));
        while (column.ChildrenLitSoFar < 4 * 64)
        {
            for (; column.ChildrenLitSoFar < 4 * 64; column.ChildrenLitSoFar++)
                if (!Budgeted(IsLit, _volume.Gpu, At(column.ChildrenLitSoFar))) return false;
            column.ChildrenLitSoFar = FirstUnlit(IsLit, 4 * 64, At, _volume.Gpu);
        }
        return true;
    }

    /// <summary>A chunk's light check, while the frame's budget of them lasts (each reads up to 64 bricks); past it
    /// every chunk counts as not lit yet, to be looked at again from there on a later frame.</summary>
    private bool Budgeted(Func<GridHandle, ChunkPosition, bool> lit, GridHandle grid, ChunkPosition pos)
        => _litChecksLeft-- > 0 && lit(grid, pos);
    private int _litChecksLeft;
    private const int LitChecksPerFrame = 2048;

    /// <summary>The first of <paramref name="count"/> chunks not lit, or <paramref name="count"/> past the end if all
    /// are.</summary>
    private int FirstUnlit(Func<GridHandle, ChunkPosition, bool> lit, int count, Func<int, ChunkPosition> at,
                           GridHandle grid)
    {
        for (int i = 0; i < count; i++)
            if (!Budgeted(lit, grid, at(i))) return i;
        return int.MaxValue;
    }

    private bool AnyEdited((int x, int z) key)
    {
        _steps.Lap(SwapStep);
        bool result = AnyEditedUntimed(key);
        _steps.Lap(EditStep);
        return result;
    }

    private bool AnyEditedUntimed((int x, int z) key)
    {
        int minY = _load.MinLayer;
        for (int j = 0; j < 4; j++)
        for (int layer = 0; layer < 64; layer++)
        {
            var data = _volume.GetData(new ChunkPosition(key.x * 2 + (j & 1), minY + layer, key.z * 2 + (j >> 1)));
            if (data is { IsDirty: true }) return true;
        }
        return false;
    }

    public float ReadyRadius(Vector3D<float> centre)
    {
        float best = OuterRadius;
        foreach (var c in _inFlight)
            if (!_columns.ContainsKey(c)) best = MathF.Min(best, Distance(centre, c)); // one being made again is drawn meanwhile
        for (int i = _queueHead; i < _queue.Count; i++)
            if (!_columns.ContainsKey(_queue[i])) { best = MathF.Min(best, Distance(centre, _queue[i])); break; }
        foreach (var (c, column) in _columns)
            if (WaitsForLight(column) && !(column.ReadyKnown && column.Ready)) // full detail shows there instead
                best = MathF.Min(best, Distance(centre, c));
        return best;
    }

    /// <summary>Whether a column is held back until it's lit: it's never been lit from the grid, and it can be (the grid
    /// isn't full). Drawn meanwhile it would show as open air, bright where it should be shadowed.</summary>
    private bool WaitsForLight(CoarseColumn column)
        => Grid.Index >= 0 && !_gridFull && !column.Baked && !column.Settled && column.Shown.Count > 0;

    /// <summary>Horizontal distance from <paramref name="centre"/> to the nearest point of coarse column <paramref name="c"/>.</summary>
    private static float Distance(Vector3D<float> centre, (int x, int z) c)
    {
        float dx = MathF.Max(0f, MathF.Max(c.x * ColumnBlocks - centre.X, centre.X - (c.x + 1) * ColumnBlocks));
        float dz = MathF.Max(0f, MathF.Max(c.z * ColumnBlocks - centre.Z, centre.Z - (c.z + 1) * ColumnBlocks));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public void Render(SystemStage stage, in RenderContext frame)
    {
        if (!Enabled) return;
        _steps.Resume();
        if (stage == SystemStage.RenderTransparent)
        {
            foreach (var d in _draws)
                if (d.Chunk.Transparent != null) _renderer.DrawTransparentChunkDepth(d.Chunk.Transparent, d.Chunk.Model, d.Grid, d.Chunk.Pos, d.Dissolve);
            foreach (var d in _draws)
                if (d.Chunk.Transparent != null) _renderer.DrawTransparentChunkMesh(d.Chunk.Transparent, d.Chunk.Model, d.Grid, d.Chunk.Pos, d.Dissolve);
            _steps.Lap(DrawStep);
            return;
        }

        _draws.Clear();
        var size = new Vector3D<float>(ColumnBlocks);
        var camera = frame.CameraPosition;
        foreach (var ((cx, cz), column) in _columns)
        {
            // Drawn, or dissolving over the full detail that replaced it.
            float dissolve = column.Drawn ? 0f : Faded(column.FadeOutAt);
            var shown = column.Shown;
            if (dissolve >= 1f || shown.Count == 0 || WaitsForLight(column)) continue;
            if (!frame.Frustum.Intersects(shown[0].Min, shown[^1].Min + size)) continue; // shown is in Y order
            // Lit from the grid once it's settled there; until then (or leaving it) with the light maps it last baked;
            // one never lit isn't drawn (above) unless the grid is full, when it's lit as open air.
            bool bake = column.Settled && Grid.Index >= 0;
            if (bake) column.Baked = true;
            int grid = column.Baked && Grid.Index >= 0 ? Grid.Index : Renderer.LodGrid;
            foreach (var c in shown)
            {
                if (shown.Count > 1 && !frame.Frustum.Intersects(c.Min, c.Min + size)) continue;
                _draws.Add(new Draw(Vector3D.DistanceSquared(c.Centre, camera), c, grid, bake, dissolve));
            }
        }
        _draws.Sort(NearestFirst);
        _steps.Lap(ListStep);

        // Light maps whose light changed since they were baked, as ChunkRenderSystem does for full-detail chunks, nearest
        // first up to the budget; a chunk never baked isn't drawn until it is.
        _bakedQuads = 0;
        int kept = 0;
        for (int i = 0; i < _draws.Count; i++)
        {
            var d = _draws[i];
            if (d.Bake && !Bake(d.Chunk)) continue;
            _draws[kept++] = d;
        }
        _draws.RemoveRange(kept, _draws.Count - kept);
        _renderer.BakeLightmaps();
        _steps.Lap(LightmapStep);

        _chunksDrawn = _draws.Count;
        foreach (var d in _draws)
            if (d.Chunk.Opaque != null) _renderer.DrawChunkMesh(d.Chunk.Opaque, d.Chunk.Model, d.Grid, d.Chunk.Pos, d.Dissolve);
        foreach (var d in _draws)
            if (d.Chunk.Cutout != null) _renderer.DrawCutoutChunkDepth(d.Chunk.Cutout, d.Chunk.Model, d.Grid, d.Chunk.Pos, d.Dissolve);
        foreach (var d in _draws)
            if (d.Chunk.Cutout != null) _renderer.DrawCutoutChunkMesh(d.Chunk.Cutout, d.Chunk.Model, d.Grid, d.Chunk.Pos, d.Dissolve);
        _steps.Lap(DrawStep);
    }

    /// <summary>Lists a chunk's stale light maps for baking if the frame's budget has room (always the first). Returns
    /// whether it can be drawn: its maps are baked, or were once.</summary>
    private bool Bake(CoarseChunk c)
    {
        int stamp = _renderer.LightStampOf(Grid, c.Pos);
        int quads = Stale(c.Opaque) + Stale(c.Cutout) + Stale(c.Transparent);
        if (quads == 0) return true;
        if (_bakedQuads > 0 && _bakedQuads + quads > BakeQuadBudget) return c.EverBaked;
        _bakedQuads += quads;
        _renderer.PrepareLightmap(c.Opaque, Grid, c.Pos, stamp);
        _renderer.PrepareLightmap(c.Cutout, Grid, c.Pos, stamp);
        _renderer.PrepareLightmap(c.Transparent, Grid, c.Pos, stamp);
        c.EverBaked = true;
        return true;

        int Stale(GpuMesh? mesh) => _renderer.LightmapStale(mesh, Grid, stamp) ? (int)mesh!.QuadCount : 0;
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Distant Terrain";

    public void DrawDebugUi()
    {
        ImGui.Checkbox("Draw distant terrain coarse (LOD)", ref Enabled);
        ImGui.SliderFloat("Full detail out to (blocks)", ref FullDetailRadius, 64f, 1024f, "%.0f");
        ImGui.SliderFloat("Coarse terrain out to (blocks)", ref OuterRadius, 128f, 4096f, "%.0f");
        ImGui.SliderFloat("Swap fade (seconds)", ref FadeSeconds, 0f, 2f, "%.2f");
        if (ImGui.Checkbox("Coarse cells by majority (else any block)", ref Majority)) Remake();
        if (ImGui.Checkbox("Coarse plants (grass)", ref Plants)) Remake();
        ImGui.TextDisabled("  majority: 4 of 8 blocks fill a cell (leaves: any), so coarse ground meets full detail flush");
        int drawnColumns = _columns.Values.Count(c => c.Drawn);
        int chunks = _columns.Values.Sum(c => c.Chunks.Count);
        ImGui.Text($"Coarse columns: {_columns.Count:N0} loaded ({drawnColumns:N0} drawn), {_inFlight.Count} being made, " +
                   $"{_queue.Count - _queueHead:N0} queued");
        ImGui.Text($"Coarse chunks: {chunks:N0} ({_chunksDrawn:N0} drawn this frame), {_quads:N0} quads " +
                   $"(~{_quads * ChunkQuad.SizeBytes / (1024.0 * 1024.0):F1} MB); made so far: {_built:N0}");
        ImGui.Text($"Full-detail chunks loaded: {_volume.LoadedCount:N0}");
        if (Grid.Index >= 0)
            ImGui.Text($"Lit as a grid: {Grid.Chunks.Count:N0} coarse chunks, {Grid.Slots.Count:N0} light bricks " +
                       $"({(long)Grid.Slots.Count * GridStore.SlotBytes / (1024 * 1024)} MB)");
        else ImGui.TextDisabled("Lit as open air (no light store)");
        ImGui.Separator();
        _steps.Draw();
    }
}
