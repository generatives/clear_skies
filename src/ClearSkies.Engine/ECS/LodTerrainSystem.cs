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
///
/// Levels chain: level k has cells 2^k blocks a side, and is drawn past level k-1 (<see cref="Finer"/>) out to its own
/// <see cref="OuterRadius"/>, swapping with it as level 1 does with full detail: a column of it covers a 2x2 group of
/// the finer level's, which keeps whole groups (see InRange), and it's drawn until all four are loaded and lit. Each
/// level is its own grid (scale 2^k); a level's rays see it and the coarser levels, passing through their covered
/// columns. Above, "full detail" and "full columns" mean the finer level for any level but the first.
/// </summary>
public sealed class LodTerrainSystem : ISystem, IRenderSystem, IFarTerrain
{
    private const int S = ChunkData.Size;
    /// <summary>Which level of detail this is: 1 for half resolution, 2 for a quarter, and so on.</summary>
    public int Level { get; }
    private readonly int Cell;          // blocks per coarse cell: 2 to the level
    private readonly int ColumnBlocks;  // a coarse column's width (and a coarse chunk's)

    /// <summary>The level drawn nearer the view, which this one swaps with: null for full detail (level 1).</summary>
    public LodTerrainSystem? Finer { get; }

    /// <summary>The level drawn past this one, if any (see <see cref="Finer"/>).</summary>
    public LodTerrainSystem? Coarser { get; private set; }

    /// <summary>Whether this level is on and drawing: it and every finer level are enabled.</summary>
    public bool Live => Enabled && (Finer?.Live ?? true);

    /// <summary>Where this level's terrain starts (blocks): where the finer level stops.</summary>
    public float InnerRadius => Finer?.OuterRadius ?? FullDetailRadius;
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

    /// <summary>How far the view's terrain loads in full detail (blocks): level 1's (the others start where the finer
    /// level stops).</summary>
    public float FullDetailRadius = 512f;

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
        public bool Hurried;                         // picked this frame (HurryFirstLight)
        public List<int>? ToPut;                    // the layers still to put in the grid (null: not started)
        public bool InGrid => ToPut is { Count: 0 };
        public bool Settled;   // in the grid and lit since it went in: its light maps follow the grid
        public bool Baked;     // lit from the grid: it settled there at least once
        public bool Covered;   // its full columns are ready, so the world's rays pass through it (GridStore.SetCovered)
        public bool Leaving;   // its full columns are ready: still drawn until they're lit
        public int Waiting, LeaveWait; // frames spent settling, and leaving
        public int LitSoFar, ChildrenLitSoFar; // how many of the chunks it waits on (settling, leaving) were found lit
        public float FadeInAt = float.NegativeInfinity, FadeOutAt = float.NegativeInfinity; // when it last swapped in, out
        public bool WasReady;  // its full columns were ready when last looked at, so they were drawn
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

    /// <summary>Lights a chunk of a grid ahead of the light queues (GpuLightSystem.Hurry), for the nearest columns held
    /// back for their first light (see <see cref="HurryFirstLight"/>).</summary>
    public Action<GridHandle, ChunkPosition>? Hurry;
    private const int HurriedColumns = 2;
    private readonly List<(float Distance, (int x, int z) Key, CoarseColumn Column)> _toPlace = new();
    private readonly CoarseColumn?[] _hurriedNow = new CoarseColumn?[HurriedColumns];

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
    private bool _grouped; // the range is in whole 2x2 groups (see InRange)
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
    private readonly StepTimer _steps;

    /// <param name="finer">The level under this one (its level is one less), or null for level 1, over full detail.</param>
    public LodTerrainSystem(World world, Renderer renderer, ChunkLoadSystem load, ChunkVolume staticVolume,
                            Func<IWorldGenerator> generatorFactory, IChunkStore store, GridStore? gridStore = null,
                            LodTerrainSystem? finer = null)
    {
        Finer = finer;
        Level = finer == null ? 1 : finer.Level + 1;
        Cell = 1 << Level;
        ColumnBlocks = S * Cell;
        if (finer != null)
        {
            if (finer.Coarser != null) throw new InvalidOperationException("That level already has a coarser one.");
            finer.Coarser = this;
        }
        _steps = new StepTimer("Adding finished columns", "Queueing and dispatching columns",
                               "Swaps: the rest", "Render: listing draws",
                               "Render: light maps", "Render: recording draws",
                               "Swaps: putting columns in the light grid",
                               "Swaps: finer level readiness", "Swaps: edit checks",
                               "Swaps: lighting checks") { Owner = Name };
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

        // Whether a coarse column's full detail is ready is found again only when something it depends on changes (a
        // coarser level's columns look at the finer level's every frame: there are few of them).
        if (finer != null) return;
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

    public bool Active => Live || Finer is { Active: true };

    /// <summary>How much full column (x, z) is hidden by the coarse column over it (see ChunkRenderSystem.Cover): 1
    /// while it's drawn, rising from 0 to 1 over <see cref="FadeSeconds"/> as it comes in, 0 when it isn't; while the
    /// coarse column dissolves away, -t: the full column draws only the share t of pixels the coarse one has left.
    /// Where this level hides nothing there, a coarser level may (it's drawn where this one isn't ready). (x, z) are in
    /// full columns, for every level.</summary>
    public float CoverOf(int x, int z)
    {
        float own = OwnCoverOf(x, z);
        return own == 0f && Coarser is { Live: true } coarser ? coarser.CoverOf(x, z) : own;
    }

    /// <summary>Whether full column (x, z), no longer wanted in full detail, stays loaded for now (ChunkLoadSystem.HeldBy):
    /// until distant terrain has faded in over it, so it doesn't vanish first.</summary>
    public bool HoldsFullColumn(int x, int z) => Live && Wanted((x >> Level, z >> Level)) && CoverOf(x, z) < 1f;

    private float OwnCoverOf(int x, int z)
    {
        if (!Live || !_columns.TryGetValue((x >> Level, z >> Level), out var c) || WaitsForLight(c)) return 0f;
        if (c.Drawn) return CoarseShare(c);
        float t = Faded(c.FadeOutAt);
        return t >= 1f || c.Shown.Count == 0 ? 0f : t <= 0f ? 1f : -t;
    }

    /// <summary>Whether this level draws column <paramref name="key"/> (its own columns) now, lit: what's under it
    /// doesn't show.</summary>
    private bool Shows((int x, int z) key)
        => _columns.TryGetValue(key, out var c) && c.Drawn && !WaitsForLight(c);

    /// <summary>Whether this level's column <paramref name="key"/> can stand in for it in a coarser level's swap: it's
    /// loaded and lit (or all air), or not drawn because what's under it is ready.</summary>
    private bool Covers((int x, int z) key)
        => Wanted(key) && _columns.TryGetValue(key, out var c) && !WaitsForLight(c);

    /// <summary>How much of a drawn coarse column shows, 0-1: rising over <see cref="FadeSeconds"/> as it swaps in for
    /// the full detail that was drawn there; all of it when it comes in over nothing, which happens behind the fog (see
    /// <see cref="ReadyRadius"/>), as dithering terrain in from nothing looks out of place.</summary>
    private float CoarseShare(CoarseColumn c) => Faded(c.FadeInAt);

    /// <summary>How far through its fade a swap that started at <paramref name="at"/> is, 0-1.</summary>
    private float Faded(float at) => FadeSeconds <= 0f ? 1f : System.Math.Clamp((_time - at) / FadeSeconds, 0f, 1f);

    public void Update(float dt)
    {
        _steps.Start();
        _time += dt;
        _frame++;
        if (!Live)
        {
            if (_wasEnabled)
            {
                if (Finer == null) _load.FullDetailRadius = null;
                UnloadAll();
                _wasEnabled = false;
            }
            return;
        }
        _wasEnabled = true;
        FullDetailRadius = MathF.Max(FullDetailRadius, 64f);
        OuterRadius = MathF.Max(OuterRadius, InnerRadius + ColumnBlocks);
        if (Finer == null) _load.FullDetailRadius = FullDetailRadius;
        if (!TryView(out _view)) return;

        var centre = ((int)MathF.Floor(_view.X / ColumnBlocks), (int)MathF.Floor(_view.Z / ColumnBlocks));
        int outer = (int)MathF.Ceiling(OuterRadius / ColumnBlocks);
        bool grouped = Coarser is { Live: true };
        if (centre != _centre || outer != _outerColumns || grouped != _grouped) Rebuild(centre, outer, grouped);
        FreeSome();
        _steps.Lap(DispatchStep);

        ApplyResults();
        _steps.Lap(ApplyStep);
        Dispatch();
        _steps.Lap(DispatchStep);
        UpdateDrawn();
        HurryFirstLight();
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

    private bool Wanted((int x, int z) c) => InRange(c, _centre, _outerColumns, _grouped);

    /// <summary>Whether a loaded column is kept: those wanted, and with a coarser level past this one a ring more, so
    /// one that's just gone out of range is still drawn while the coarser level fades in over it (the coarser level
    /// counts only wanted columns as ready, see <see cref="Covers"/>).</summary>
    private bool Kept((int x, int z) c) => InRange(c, _centre, _outerColumns + (_grouped ? 2 : 0), _grouped);

    /// <summary>Whether column <paramref name="c"/> is within <paramref name="outer"/> columns of
    /// <paramref name="centre"/>. With a coarser level drawn past this one (<paramref name="grouped"/>), by the middle
    /// of the 2x2 group it's in, so this level ends in whole coarser columns, each swapping for its four in one go (as
    /// ChunkLoadSystem.FullDetailRadius does for full detail).</summary>
    private static bool InRange((int x, int z) c, (int x, int z) centre, int outer, bool grouped)
    {
        if (!grouped) return Sq(c.x - centre.x) + Sq(c.z - centre.z) <= Sq(outer);
        double dx = (c.x >> 1) * 2 + 1 - (centre.x + 0.5), dz = (c.z >> 1) * 2 + 1 - (centre.z + 0.5);
        return dx * dx + dz * dz <= (double)outer * outer;
    }

    private static long Sq(int v) => (long)v * v;

    /// <summary>The view moved to another coarse column (or the radius changed): drops what's out of range and queues
    /// what's missing, nearest first.</summary>
    private void Rebuild((int x, int z) centre, int outer, bool grouped)
    {
        _centre = centre;
        _outerColumns = outer;
        _grouped = grouped;
        // Out of range: not drawn from now on, and freed (out of the grid, meshes released) a few a frame.
        foreach (var key in _columns.Keys.Where(k => !Kept(k)).ToList())
        {
            _columns.Remove(key, out var gone);
            if (_toFree.Remove(key, out var older)) Free(key, older);
            _toFree[key] = gone!;
        }

        _queue.Clear();
        _queueHead = 0;
        for (int dz = -outer - 2; dz <= outer + 2; dz++)
        for (int dx = -outer - 2; dx <= outer + 2; dx++)
        {
            var c = (centre.x + dx, centre.z + dz);
            if (!Wanted(c) || _columns.ContainsKey(c) || _inFlight.Contains(c)) continue;
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
        for (int j = 0; j < Cell * Cell; j++)
        {
            int x = col.x * Cell + j % Cell, z = col.z * Cell + j / Cell;
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
                // Each full chunk is shrunk into level 1's chunk over it as it's made; for a coarser level those are
                // shrunk again in turn, a level at a time.
                var coarse = new Dictionary<ChunkPosition, ChunkData>();
                bool majority = Majority, plants = Plants;
                foreach (var (pos, edited) in work)
                {
                    var full = new ChunkData();
                    if (!(edited && _store.TryLoad(pos, full))) _generator.Value!.Generate(full, pos);
                    if (!full.IsUniform(out var only) || only != BlockId.Air) ShrinkInto(coarse, pos, full, majority, plants);
                    full.Release();
                }
                for (int level = 2; level <= Level; level++)
                {
                    var finer = coarse;
                    coarse = new Dictionary<ChunkPosition, ChunkData>();
                    foreach (var (pos, chunk) in finer)
                    {
                        if (chunk.HasAnyNonAir()) ShrinkInto(coarse, pos, chunk, majority, plants);
                        chunk.Release();
                    }
                }
                foreach (var (y, chunk) in coarse.Select(kv => (kv.Key.Y, kv.Value)).OrderBy(c => c.Y))
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

    /// <summary>Shrinks <paramref name="chunk"/> (at <paramref name="pos"/>, in its level's chunks) into the chunk of
    /// the next level over it, in <paramref name="into"/>.</summary>
    private static void ShrinkInto(Dictionary<ChunkPosition, ChunkData> into, ChunkPosition pos, ChunkData chunk,
                                   bool majority, bool plants)
    {
        var at = new ChunkPosition(pos.X >> 1, pos.Y >> 1, pos.Z >> 1);
        if (!into.TryGetValue(at, out var coarse)) into[at] = coarse = new ChunkData();
        Shrink(chunk, coarse, (pos.X & 1) * 16, (pos.Y & 1) * 16, (pos.Z & 1) * 16, majority, plants);
    }

    /// <summary>Shrinks full chunk <paramref name="full"/> into the 16³ octant of <paramref name="coarse"/> at
    /// (ox, oy, oz). Model blocks are left out, and plants unless <paramref name="plants"/> (then an open cell keeps one, see below). By <paramref name="majority"/>: a cell is filled when at least
    /// 4 of its 8 blocks are cubes, with the more common kind (opaque or see-through) and its most common block, so coarse
    /// surfaces round to the nearest cell and meet full detail flush on average; thin things (a fence of cubes, a
    /// one-block ledge) may go, except leaves (<see cref="BlockDef.ShowsInnerFaces"/>), which fill a cell if any of its
    /// blocks is one, so trees stay. Otherwise any cube fills the cell: the most common opaque one, else the most common
    /// see-through one. Either way blocks count by how many of their faces show (aren't against an opaque cube; past
    /// the full chunk counts as showing), and only buried blocks count when none of that kind shows, so the cell takes
    /// what's seen: grass on dirt stays grass, a cliff face or an overhang's underside the stone it shows.</summary>
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
        Span<int> faces = stackalloc int[8]; // how many faces each of the cell's blocks shows
        Span<BlockId> plantOn = stackalloc BlockId[16]; // per x: a plant on top of the cell below (for the cell above)
        for (int z = 0; z < 16; z++)
        {
            plantOn.Clear();
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                for (int k = 0; k < 8; k++)
                    cell[k] = full.Get(2 * x + (k & 1), 2 * y + ((k >> 1) & 1), 2 * z + (k >> 2));
                for (int k = 0; k < 8; k++)
                    faces[k] = IsCube(cell[k]) ? ShownFaces(full, 2 * x + (k & 1), 2 * y + ((k >> 1) & 1), 2 * z + (k >> 2)) : 0;
                var pick = majority ? ByMajority(cell, faces) : ByAny(cell, faces);
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

    private static BlockId ByAny(ReadOnlySpan<BlockId> cell, ReadOnlySpan<int> faces)
    {
        var pick = Seen(cell, faces, opaque: true);
        return pick != BlockId.Air ? pick : Seen(cell, faces, opaque: false);
    }

    /// <summary>The cube of a kind with the most faces showing in a cell (a tie going to the commoner there), or failing
    /// any showing, the most common of all its blocks.</summary>
    private static BlockId Seen(ReadOnlySpan<BlockId> cell, ReadOnlySpan<int> faces, bool opaque)
    {
        BlockId best = BlockId.Air;
        int bestScore = 0;
        for (int i = 0; i < cell.Length; i++)
        {
            var id = cell[i];
            if (faces[i] == 0 || BlockRegistry.Get(id).Transparent == opaque) continue;
            int score = 0;
            for (int j = 0; j < cell.Length; j++) if (cell[j] == id) score += 8 * faces[j] + 1;
            if (score > bestScore) { best = id; bestScore = score; }
        }
        return best != BlockId.Air ? best : MostCommon(cell, opaque);
    }

    /// <summary>How many faces of full chunk block (x, y, z) aren't against an opaque cube (past the chunk: showing).</summary>
    private static int ShownFaces(ChunkData full, int x, int y, int z)
    {
        return Shows(full, x - 1, y, z) + Shows(full, x + 1, y, z) + Shows(full, x, y - 1, z)
             + Shows(full, x, y + 1, z) + Shows(full, x, y, z - 1) + Shows(full, x, y, z + 1);

        static int Shows(ChunkData full, int x, int y, int z)
        {
            if ((uint)x >= S || (uint)y >= S || (uint)z >= S) return 1;
            var id = full.Get(x, y, z);
            return IsCube(id) && !BlockRegistry.Get(id).Transparent ? 0 : 1;
        }
    }

    private static BlockId ByMajority(ReadOnlySpan<BlockId> cell, ReadOnlySpan<int> faces)
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
        if (opaque + see >= 4) return Seen(cell, faces, opaque: opaque >= see);
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
                if (!Live || !Wanted(r.Column)) continue;
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
        long nearSq = Sq((int)MathF.Ceiling(InnerRadius / ColumnBlocks) + 2);
        // Moving on changes which full columns are wanted, but that alone changes no column's readiness until its
        // chunks load or unload (which mark it), so it's not looked at all again. A few near columns a frame are
        // looked at anyway, round them in turn, in case a change came without a mark.
        int nearIndex = 0, recheckFrom = _recheck, recheckTo = _recheck + RecheckPerFrame;
        _litChecksLeft = LitChecksPerFrame;
        foreach (var (key, column) in _columns)
        {
            bool near = Sq(key.x - _centre.x) + Sq(key.z - _centre.z) <= nearSq;
            if (!near) column.ReadyKnown = false; // found again when it's next near (changes meanwhile aren't kept)
            if (!near && column.Drawn && column.Settled && !column.Covered && !column.Leaving) continue;
            bool due = false;
            if (near) { due = nearIndex >= recheckFrom && nearIndex < recheckTo; nearIndex++; }
            if (near && (due || !column.ReadyKnown || _readyDirty.Contains(key) || Finer != null))
            {
                _steps.Lap(SwapStep);
                bool all = true;
                for (int j = 0; j < 4 && all; j++)
                {
                    int x = key.x * 2 + (j & 1), z = key.z * 2 + (j >> 1);
                    all = Finer?.Covers((x, z)) ?? _load.IsColumnReady(x, z);
                }
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
                _toPlace.Add((Distance(_view, key), key, column)); // put in the grid after this loop, nearest first
                column.Settled = false;
                column.Waiting = 0;
                column.LitSoFar = 0;
            }
            else if (!column.Settled)
            {
                column.Settled = ++column.Waiting >= MinSwapFrames && CheckNow(key)
                                 && (column.Waiting > (column.Baked ? MaxSwapFrames : MaxFirstLightFrames)
                                     || ResidentLit(key, column));
                // One held back for its light swaps in from when it's first shown (the full detail is held till then).
                if (column.Settled && !column.Baked && column.Drawn && float.IsFinite(column.FadeInAt)) column.FadeInAt = _time;
            }
            if (!ready)
            {
                column.Leaving = false;
                if (!column.Drawn) column.FadeInAt = column.WasReady ? _time : float.NegativeInfinity;
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
            // Covered while its full detail is ready, and while that's still drawn as this swaps in over it: the world's
            // rays then see only full-detail blocks there. (Uncovered at once, the full detail fading out was shadowed
            // by the coarse blocks in it, and darkened.)
            bool covered = ready || column.Drawn && (WaitsForLight(column) || CoarseShare(column) < 1f);
            if (column.Covered != covered) SetCovered(key, column, covered);
            column.WasReady = ready;
        }
        _editScan = readyIndex == 0 ? 0 : (scan + 1) % readyIndex;
        _readyDirty.Clear();

        // Putting a column in the grid writes each chunk's occupancy and its surface bricks: as many a frame as fit the
        // budget, the rest on later frames (held back, or lit as before, meanwhile). Nearest first: the nearest held
        // back holds the fog, and in the dictionary's order, while the view moved, columns arriving far away took the
        // budget and the fog stopped until it caught up.
        if (_toPlace.Count > 0)
        {
            _toPlace.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            double residentMs = 0;
            _steps.Lap(SwapStep);
            foreach (var (_, key, column) in _toPlace)
            {
                if (residentMs >= ResidentBudgetMs) break;
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                MakeResident(key, column, start + (long)((ResidentBudgetMs - residentMs) * System.Diagnostics.Stopwatch.Frequency / 1000));
                residentMs += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            _steps.Lap(ResidentStep);
            _toPlace.Clear();
        }

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
        if (IsLit == null || Finer != null) return true; // a finer level's columns are lit before they're ready (Covers)
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
        for (int j = 0; j < Cell * Cell; j++)
        for (int layer = 0; layer < 64; layer++)
        {
            var data = _volume.GetData(new ChunkPosition(key.x * Cell + j % Cell, minY + layer, key.z * Cell + j / Cell));
            if (data is { IsDirty: true }) return true;
        }
        return false;
    }

    /// <summary>For the fog, which follows the coarsest level (see <see cref="IFarTerrain"/>): the nearest terrain not
    /// ready to be seen in any level, out to the coarsest live one's outer radius.</summary>
    public float ReadyRadius(Vector3D<float> centre)
    {
        if (!Live) return Finer?.ReadyRadius(centre) ?? OuterRadius;
        // Columns in the outermost band don't count: the view moving on a column brings a ring of new ones in there
        // at once, and the fog pulled in for each and went out again (they come in behind nearly total fog anyway).
        float best = OuterRadius, ignoreFrom = OuterRadius - 2 * ColumnBlocks;
        _fogHeld = default;
        for (var level = this; level != null; level = level.Finer)
        {
            float at = level.NotReadyAt(centre, out var why, out int frames);
            if (at < best && at < ignoreFrom) { best = at; _fogHeld = (level.Level, why, frames, at); }
        }
        return best;
    }

    private enum FogReason { None, Making, Queued, Placing, Lighting }
    private (int Level, FogReason Why, int Frames, float At) _fogHeld;

    /// <summary>What the fog last stopped at (see <see cref="ReadyRadius"/>), for the panel.</summary>
    public string FogHeldBy => _fogHeld.Why switch
    {
        FogReason.Making   => $"level {_fogHeld.Level} column being made, {_fogHeld.At:F0} blocks out",
        FogReason.Queued   => $"level {_fogHeld.Level} column queued, {_fogHeld.At:F0} blocks out",
        FogReason.Placing  => $"level {_fogHeld.Level} column waiting to go in the light grid, {_fogHeld.At:F0} blocks out",
        FogReason.Lighting => $"level {_fogHeld.Level} column waiting {_fogHeld.Frames} frames for its light, {_fogHeld.At:F0} blocks out",
        _ => "nothing (out to the full range)",
    };

    /// <summary>The nearest of this level's columns not ready to be seen: being made, or held back for its light, where
    /// nothing else shows instead (the finer level's ready, or the coarser one drawn).</summary>
    private float NotReadyAt(Vector3D<float> centre, out FogReason why, out int frames)
    {
        float best = float.PositiveInfinity;
        why = FogReason.None;
        frames = 0;
        var coarser = Coarser is { Live: true } up ? up : null;
        foreach (var c in _inFlight)
            if (!_columns.ContainsKey(c) && !Hidden(c) && Distance(centre, c) is var d && d < best) // one being made again is drawn meanwhile
                (best, why) = (d, FogReason.Making);
        for (int i = _queueHead; i < _queue.Count; i++)
            if (!_columns.ContainsKey(_queue[i]) && !Hidden(_queue[i]))
            {
                if (Distance(centre, _queue[i]) is var d && d < best) (best, why) = (d, FogReason.Queued);
                break;
            }
        foreach (var (k, column) in _columns)
            if (WaitsForLight(column) && !(column.ReadyKnown && column.Ready) && !Hidden(k) // what's under it shows instead
                && Distance(centre, k) is var d && d < best)
                (best, why, frames) = (d, column.InGrid ? FogReason.Lighting : FogReason.Placing, column.Waiting);
        return best;

        bool Hidden((int x, int z) k) => coarser != null && coarser.Shows((k.x >> 1, k.z >> 1));
    }

    /// <summary>Whether a column is held back until it's lit: it's never been lit from the grid, and it can be (the grid
    /// isn't full). Drawn meanwhile it would show as open air, bright where it should be shadowed.</summary>
    private bool WaitsForLight(CoarseColumn column)
        => Grid.Index >= 0 && !_gridFull && !column.Baked && !column.Settled && column.Shown.Count > 0;

    /// <summary>The nearest columns held back for their first light hold the fog where they are (see NotReadyAt). The
    /// light queues go nearest the camera first, so while the view moves the full detail loading nearer keeps them
    /// waiting, and the fog stuck until it stopped. Their chunks are lit ahead of the queues instead.</summary>
    private void HurryFirstLight()
    {
        if (Hurry == null) return;
        int picked = 0;
        for (; picked < HurriedColumns; picked++)
        {
            float best = float.PositiveInfinity;
            (int x, int z) bestKey = default;
            CoarseColumn? bestColumn = null;
            foreach (var (k, column) in _columns)
                if (WaitsForLight(column) && !column.Hurried && Distance(_view, k) is var d && d < best)
                    (best, bestKey, bestColumn) = (d, k, column);
            if (bestColumn == null) break;
            bestColumn.Hurried = true;
            _hurriedNow[picked] = bestColumn;
            for (int i = bestColumn.LitSoFar; i < bestColumn.Resident.Count; i++)
                Hurry(Grid, new ChunkPosition(bestKey.x, bestColumn.Resident[i], bestKey.z));
        }
        for (int n = 0; n < picked; n++) _hurriedNow[n]!.Hurried = false;
    }

    /// <summary>Horizontal distance from <paramref name="centre"/> to the nearest point of coarse column <paramref name="c"/>.</summary>
    private float Distance(Vector3D<float> centre, (int x, int z) c)
    {
        float dx = MathF.Max(0f, MathF.Max(c.x * ColumnBlocks - centre.X, centre.X - (c.x + 1) * ColumnBlocks));
        float dz = MathF.Max(0f, MathF.Max(c.z * ColumnBlocks - centre.Z, centre.Z - (c.z + 1) * ColumnBlocks));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public void Render(SystemStage stage, in RenderContext frame)
    {
        if (!Live) return;
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
        var coarser = Coarser is { Live: true } c0 ? c0 : null;
        foreach (var ((cx, cz), column) in _columns)
        {
            // Coming in over the finer level it replaces (drawing the pixels that leaves out), drawn, or going away
            // (leaving out the pixels the finer level coming back draws): a pixel shows one level, never both.
            float share = column.Drawn ? CoarseShare(column) : 1f - Faded(column.FadeOutAt);
            if (share <= 0f) continue;
            float dissolve = column.Drawn ? (share < 1f ? -share : 0f) : 1f - share;
            // A coarser level over it hides it the same way.
            float over = coarser?.OwnCoverOf(cx << Level, cz << Level) ?? 0f;
            if (over >= 1f) continue;
            if (over != 0f) dissolve = over;
            var shown = column.Shown;
            if (shown.Count == 0 || WaitsForLight(column)) continue;
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

    // ── debug UI (in DistantTerrainPanel, one window for every level) ─────────
    public string Name => Level == 1 ? "Distant Terrain" : $"Distant Terrain (level {Level})";

    /// <summary>Remakes every column (a shrink setting changed, see <see cref="Majority"/>, <see cref="Plants"/>).</summary>
    public void RemakeAll() => Remake();

    /// <summary>This level's settings, counts and step timings.</summary>
    public void DrawLevelUi()
    {
        if (Finer != null)
        {
            ImGui.Checkbox($"Draw level {Level} past level {Level - 1}##lod{Level}", ref Enabled);
            if (Finer is { Live: false }) ImGui.TextDisabled($"  (off while level {Level - 1} is)");
        }
        ImGui.SliderFloat($"Out to (blocks)##lod{Level}", ref OuterRadius, 128f, 4096f, "%.0f");
        int drawnColumns = _columns.Values.Count(c => c.Drawn);
        int chunks = _columns.Values.Sum(c => c.Chunks.Count);
        ImGui.Text($"Columns: {_columns.Count:N0} loaded ({drawnColumns:N0} drawn), {_inFlight.Count} being made, " +
                   $"{_queue.Count - _queueHead:N0} queued");
        ImGui.Text($"Chunks: {chunks:N0} ({_chunksDrawn:N0} drawn this frame), {_quads:N0} quads " +
                   $"(~{_quads * ChunkQuad.SizeBytes / (1024.0 * 1024.0):F1} MB); made so far: {_built:N0}");
        if (Grid.Index >= 0)
            ImGui.Text($"Lit as a grid: {Grid.Chunks.Count:N0} chunks, {Grid.Slots.Count:N0} light bricks " +
                       $"({(long)Grid.Slots.Count * GridStore.SlotBytes / (1024 * 1024)} MB)");
        else ImGui.TextDisabled("Lit as open air (no light store)");
        _steps.Draw();
    }

    /// <summary>Full-detail chunks loaded (for the panel).</summary>
    public int FullDetailChunks => _volume.LoadedCount;
}
