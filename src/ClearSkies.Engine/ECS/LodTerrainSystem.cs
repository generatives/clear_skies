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

    /// <summary>How far from <paramref name="centre"/> the terrain is ready to be seen.</summary>
    float ReadyRadius(Vector3D<float> centre);
}

/// <summary>
/// Distant terrain at half resolution (level 1 of the LOD plan): past <see cref="FullDetailRadius"/> the world is drawn
/// from coarse 32³ chunks whose cells are 2 blocks a side, out to <see cref="OuterRadius"/>. Both are balls around the
/// view: full detail loads in a ball of 2x2x2 groups of chunks (<see cref="ChunkLoadSystem.FullDetailRadius"/>), each
/// group under one coarse chunk, and a coarse chunk is drawn (and the eight full chunks under it hidden, see
/// <see cref="CoverOf"/>) until all eight are loaded and meshed, then swaps for them in one go. A swap dissolves the
/// level going away over the one coming in, in a screen dither, over <see cref="FadeSeconds"/>.
///
/// The coarse chunks in range are a grid of their own in the GridStore (<see cref="Grid"/>, voxels 2 blocks a side),
/// which GpuLightSystem lights like the world: sun shadows, bounce and AO, with rays crossing between it and the world.
/// Their meshes bake light maps from it as full-detail chunks do from the world. Every coarse chunk in range is in the
/// grid, lit by its own blocks: distant terrain's rays don't see the world, and the world's pass through a coarse chunk
/// whose full detail is ready (covered), so both levels are lit where they overlap and a swap shows settled light either
/// way. A swap to full detail waits until the full-detail chunks are lit.
///
/// Coarse terrain is made a column at a time (the generator works out a column's shape once for all its chunks): the
/// full chunks under it are generated (or the edited ones loaded from the store) on a worker, shrunk
/// (<see cref="Shrink"/>) and meshed; only the meshes are kept, for every layer of the column, so the view moving up or
/// down needs nothing made. Columns are kept within the outer radius (across), including under the full-detail terrain,
/// so moving back out needs nothing made either. One under full detail whose chunks were edited meanwhile is made again
/// when it's next drawn. A panel toggle (Distant Terrain) turns it off, for comparing against full detail everywhere;
/// the game starts it on.
///
/// Levels chain: level k has cells 2^k blocks a side, and is drawn past level k-1 (<see cref="Finer"/>) out to its own
/// <see cref="OuterRadius"/>, swapping with it as level 1 does with full detail: a chunk of it covers a 2x2x2 group of
/// the finer level's, which keeps whole groups (see InRange), and it's drawn until all eight are loaded and lit. Each
/// level is its own grid (scale 2^k); a level's rays see it and the coarser levels, passing through their covered
/// chunks. Above, "full detail" and "full chunks" mean the finer level for any level but the first.
/// </summary>
public sealed class LodTerrainSystem : ISystem, IRenderSystem, IFarTerrain
{
    private const int S = ChunkData.Size;
    /// <summary>Which level of detail this is: 1 for half resolution, 2 for a quarter, and so on.</summary>
    public int Level { get; }
    private readonly int Cell;          // blocks per coarse cell: 2 to the level
    private readonly int ColumnBlocks;  // a coarse chunk's size (and a coarse column's width)

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

    /// <summary>The coarse chunks in range as a grid of 2-block voxels, lit like the world (see
    /// GpuLightSystem.AddStaticGrid). Unregistered (index -1) without a GridStore: then they're lit as open air.</summary>
    public GridHandle Grid { get; } = new();

    /// <summary>A coarse chunk: what's drawn of it, and where it is in swapping with the finer level and in the grid.
    /// Every layer of a made column has one, air included, so every place has a state to hide full detail by.</summary>
    private sealed class CoarseChunk
    {
        public GpuMesh? Opaque, Cutout, Transparent;
        public PackedOpacity Occupancy = AllAir; // for the grid (shared for all-air and all-solid chunks)
        public ChunkPosition Pos;        // in coarse chunks
        public Mat4 Model;               // coarse cells to world
        public Vector3D<float> Min, Centre;
        public bool EverBaked;           // its light maps have been baked from the grid (it can be drawn lit from it)
        public bool HasFaces => Opaque != null || Cutout != null || Transparent != null;
        public bool Gridded;             // goes in the grid while kept: it has blocks, or is the air next to some

        public bool Drawn;       // this frame: not all eight finer chunks under it are ready
        public bool Ready, ReadyKnown; // whether all eight are ready, as last found (see _readyDirty)
        public bool InGrid;      // its occupancy is in the grid
        public bool Settled;     // in the grid and lit since it went in: its light maps follow the grid
        public bool Baked;       // lit from the grid: it settled there at least once
        public bool Covered;     // its full chunks are ready, so the world's rays pass through it (GridStore.SetCovered)
        public bool Leaving;     // its full chunks are ready: still drawn until they're lit
        public bool Evicting;    // out of range, in _toEvict: taken out of the grid a few a frame
        public int Waiting, LeaveWait; // frames spent settling, and leaving
        public float FadeInAt = float.NegativeInfinity, FadeOutAt = float.NegativeInfinity; // when it last swapped in, out
        public bool WasReady;    // its full chunks were ready when last looked at, so they were drawn
    }

    /// <summary>A made coarse column: a chunk for every layer from <see cref="MinY"/> up, bottom first.</summary>
    private sealed class CoarseColumn
    {
        public readonly List<CoarseChunk> Chunks = new();
        // The chunks with faces, bottom first: most of a column is air above the land, or rock under it, and the
        // per-frame walks that only care about what's drawn (listing draws, the fog) skip reading those.
        public readonly List<CoarseChunk> Faced = new();
        public int MinY;
        public bool ChildEdited; // a full chunk under it was edited while it was hidden
        public bool Quiet;       // far from full detail, and every chunk kept is drawn, settled and uncovered: nothing to do
        public CoarseChunk? At(int y) => y >= MinY && y < MinY + Chunks.Count ? Chunks[y - MinY] : null;
    }

    // A swap waits for the incoming terrain's light (see UpdateDrawn): at least this many frames, since a change is
    // marked for lighting the frame after it's made, and at most the other, in case lighting never settles there.
    private const int MinSwapFrames = 2, MaxSwapFrames = 240;

    // A chunk that's never been shown waits for its light longer: it's hidden (behind the fog) meanwhile, rather than
    // showing open-air light. The limit is for lighting that never comes there.
    private const int MaxFirstLightFrames = 1800;

    /// <summary>Whether a chunk of a grid is lit (GpuLightSystem.IsLit); without it, everything counts as lit.</summary>
    public Func<GridHandle, ChunkPosition, bool>? IsLit;

    /// <summary>Puts a chunk of a grid ahead of the light queues (GpuLightSystem.ShowFirst), asked every frame for terrain
    /// waiting to be shown: a chunk held back for its first light, and the full detail waiting to swap in for one.</summary>
    public Action<GridHandle, ChunkPosition>? ShowFirst;
    private readonly List<(float Distance, CoarseChunk Chunk)> _toPlace = new();

    // Shared occupancy for the coarse chunks that are all one thing.
    private static readonly PackedOpacity AllAir = new(new uint[GridStore.WordsPerChunk], 0UL, ulong.MaxValue, new(), null, 0UL);
    private static readonly PackedOpacity AllSolid = new(new uint[GridStore.WordsPerChunk], ulong.MaxValue, 0UL, new(), null, 0UL);

    private readonly record struct Built(int Y, ChunkMeshSystem.Packed Opaque, ChunkMeshSystem.Packed Cutout,
                                         ChunkMeshSystem.Packed Transparent, PackedOpacity Occupancy);
    private readonly record struct Result((int x, int z) Column, int MinY, int MaxY, List<Built> Chunks, Exception? Error);

    private readonly Dictionary<(int x, int z), CoarseColumn> _columns = new();
    private readonly HashSet<(int x, int z)> _inFlight = new();
    private readonly List<(int x, int z)> _queue = new();
    private int _queueHead;
    private readonly ConcurrentQueue<Result> _results = new();
    private (int x, int y, int z) _centre = (int.MinValue, int.MinValue, int.MinValue);
    private int _outerChunks = -1;
    private bool _grouped; // the range is in whole 2x2x2 groups (see InRange)
    private bool _wasEnabled;
    private Vector3D<float> _view;

    private readonly record struct Draw(float DistSq, CoarseChunk Chunk, int Grid, bool Bake, float Dissolve);

    // Light map baking a frame for coarse chunks, in quads (nearest first; one that's been drawn before is drawn with its
    // last light meanwhile, one never baked waits). Chunks settling in bulk (loading, flying) bake over a few frames.
    private const int BakeQuadBudget = 65536;
    private int _bakedQuads;
    private readonly List<Draw> _draws = new();
    private static readonly Comparison<Draw> NearestFirst = (a, b) => a.DistSq.CompareTo(b.DistSq);
    private int _built, _chunksDrawn;
    private long _quads;
    private int _frame;
    private int _editScan; // which column with ready chunks UpdateDrawn checks for edits this frame

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
                               "Swaps: putting chunks in the light grid",
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

        // Whether a coarse chunk's full detail is ready is found again only when something it depends on changes (a
        // coarser level's chunks look at the finer level's every frame: there are few of them).
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

    private readonly HashSet<(int x, int z)> _readyDirty = new(); // columns whose chunks' readiness may have changed
    private int _recheck; // where the round of near chunks looked at anyway is up to
    private readonly HashSet<(int x, int z)> _editChecks = new();
    private readonly Queue<(int x, int z)> _editCheckOrder = new();
    private const int EditChecksPerFrame = 2;
    private const int RecheckPerFrame = 32;

    public bool Active => Live || Finer is { Active: true };

    private CoarseChunk? ChunkAt(int x, int y, int z) => _columns.TryGetValue((x, z), out var c) ? c.At(y) : null;

    /// <summary>How much full chunk (x, y, z) is hidden by the coarse chunk over it (see ChunkRenderSystem.Cover): 1
    /// while it's drawn, rising from 0 to 1 over <see cref="FadeSeconds"/> as it comes in, 0 when it isn't; while the
    /// coarse chunk dissolves away, -t: the full chunk draws only the share t of pixels the coarse one has left.
    /// Where this level hides nothing there, a coarser level may (it's drawn where this one isn't ready). (x, y, z) are
    /// in full chunks, for every level.</summary>
    public float CoverOf(int x, int y, int z)
    {
        float own = OwnCoverOf(x, y, z);
        return own == 0f && Coarser is { Live: true } coarser ? coarser.CoverOf(x, y, z) : own;
    }

    /// <summary>Whether full chunk (x, y, z), no longer wanted in full detail, stays loaded for now
    /// (ChunkLoadSystem.HeldBy): until distant terrain has faded in over it, so it doesn't vanish first.</summary>
    public bool HoldsFullChunk(int x, int y, int z)
        => Live && Wanted((x >> Level, y >> Level, z >> Level)) && CoverOf(x, y, z) < 1f;

    private float OwnCoverOf(int x, int y, int z)
    {
        if (!Live || ChunkAt(x >> Level, y >> Level, z >> Level) is not { } c || WaitsForLight(c)) return 0f;
        if (c.Drawn) return CoarseShare(c);
        float t = Faded(c.FadeOutAt);
        return t >= 1f ? 0f : t <= 0f ? 1f : -t;
    }

    /// <summary>Whether this level draws chunk <paramref name="key"/> (its own chunks) now, lit: what's under it doesn't
    /// show.</summary>
    private bool Shows((int x, int y, int z) key) => ChunkAt(key.x, key.y, key.z) is { Drawn: true } c && !WaitsForLight(c);

    /// <summary>Whether this level's chunk <paramref name="key"/> can stand in for it in a coarser level's swap: it's
    /// in range, made and lit (or air), or not drawn because what's under it is ready.</summary>
    private bool Covers((int x, int y, int z) key)
        => Wanted(key) && _columns.TryGetValue((key.x, key.z), out var column)
           && (column.At(key.y) is not { } c || !WaitsForLight(c));

    /// <summary>How much of a drawn coarse chunk shows, 0-1: rising over <see cref="FadeSeconds"/> as it swaps in for
    /// the full detail that was drawn there; all of it when it comes in over nothing, which happens behind the fog (see
    /// <see cref="ReadyRadius"/>), as dithering terrain in from nothing looks out of place.</summary>
    private float CoarseShare(CoarseChunk c) => Faded(c.FadeInAt);

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

        var centre = ((int)MathF.Floor(_view.X / ColumnBlocks), (int)MathF.Floor(_view.Y / ColumnBlocks),
                      (int)MathF.Floor(_view.Z / ColumnBlocks));
        int outer = (int)MathF.Ceiling(OuterRadius / ColumnBlocks);
        bool grouped = Coarser is { Live: true };
        if (centre != _centre || outer != _outerChunks || grouped != _grouped) Rebuild(centre, outer, grouped);
        _gridStore?.BeginRemovals(); // chunks next to those going are looked at once (see GridStore.BeginRemovals)
        FreeSome();
        EvictSome();
        _gridStore?.EndRemovals();
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

    /// <summary>Whether chunk <paramref name="c"/> is in range: drawn here (where the finer level isn't ready).</summary>
    private bool Wanted((int x, int y, int z) c) => InRange(c, _centre, _outerChunks, _grouped);

    /// <summary>Whether a chunk is kept (in the grid, and drawn while not ready): those wanted, and with a coarser level
    /// past this one a shell more, so one that's just gone out of range is still drawn while the coarser level fades in
    /// over it (the coarser level counts only wanted chunks as ready, see <see cref="Covers"/>).</summary>
    private bool Kept((int x, int y, int z) c) => InRange(c, _centre, _outerChunks + (_grouped ? 2 : 0), _grouped);

    /// <summary>Whether some of column <paramref name="c"/> is wanted (it's made and queued by this), and kept.</summary>
    private bool WantedColumn((int x, int z) c) => InRange((c.x, _centre.y, c.z), _centre, _outerChunks, _grouped);
    private bool KeptColumn((int x, int z) c) => InRange((c.x, _centre.y, c.z), _centre, _outerChunks + (_grouped ? 2 : 0), _grouped);

    /// <summary>Whether chunk <paramref name="c"/> is within <paramref name="outer"/> chunks of
    /// <paramref name="centre"/>. With a coarser level drawn past this one (<paramref name="grouped"/>), by the middle
    /// of the 2x2x2 group it's in, so this level ends in whole coarser chunks, each swapping for its eight in one go (as
    /// ChunkLoadSystem.FullDetailRadius does for full detail).</summary>
    private static bool InRange((int x, int y, int z) c, (int x, int y, int z) centre, int outer, bool grouped)
    {
        if (!grouped) return Sq(c.x - centre.x) + Sq(c.y - centre.y) + Sq(c.z - centre.z) <= Sq(outer);
        double dx = (c.x >> 1) * 2 + 1 - (centre.x + 0.5), dy = (c.y >> 1) * 2 + 1 - (centre.y + 0.5),
               dz = (c.z >> 1) * 2 + 1 - (centre.z + 0.5);
        return dx * dx + dy * dy + dz * dz <= (double)outer * outer;
    }

    private static long Sq(int v) => (long)v * v;

    /// <summary>The view moved to another coarse chunk (or the radius changed): drops the columns out of range and
    /// queues those missing, nearest first. Which chunks of the rest are in range is looked at again (UpdateDrawn).</summary>
    private void Rebuild((int x, int y, int z) centre, int outer, bool grouped)
    {
        _centre = centre;
        _outerChunks = outer;
        _grouped = grouped;
        // Out of range: not drawn from now on, and freed (out of the grid, meshes released) a few a frame.
        foreach (var key in _columns.Keys.Where(k => !KeptColumn(k)).ToList())
        {
            _columns.Remove(key, out var gone);
            if (_toFree.Remove(key, out var older)) Free(older);
            _toFree[key] = gone!;
        }
        foreach (var column in _columns.Values) column.Quiet = false;

        _queue.Clear();
        _queueHead = 0;
        for (int dz = -outer - 2; dz <= outer + 2; dz++)
        for (int dx = -outer - 2; dx <= outer + 2; dx++)
        {
            var c = (centre.x + dx, centre.z + dz);
            if (!WantedColumn(c) || _columns.ContainsKey(c) || _inFlight.Contains(c)) continue;
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
            if (_inFlight.Contains(col) || !WantedColumn(col)) { Dequeue(ref i); continue; }
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
        // The column's layers in this level's chunks: every one gets a chunk, air included (see CoarseColumn).
        int minY = 0, maxY = -1;
        if (work.Count > 0)
        {
            minY = work.Min(w => w.Pos.Y) >> Level;
            maxY = work.Max(w => w.Pos.Y) >> Level;
        }
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
                _results.Enqueue(new Result(col, minY, maxY, built, null));
            }
            catch (Exception e)
            {
                _results.Enqueue(new Result(col, minY, maxY, built, e));
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
        var cube = CubeTable;
        Span<BlockId> cell = stackalloc BlockId[8];
        Span<int> faces = stackalloc int[8]; // how many faces each of the cell's blocks shows
        Span<BlockId> plantOn = stackalloc BlockId[16]; // per x: a plant on top of the cell below (for the cell above)
        for (int z = 0; z < 16; z++)
        {
            plantOn.Clear();
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                bool anyCube = false, same = true;
                for (int k = 0; k < 8; k++)
                {
                    var id = cell[k] = full.Get(2 * x + (k & 1), 2 * y + ((k >> 1) & 1), 2 * z + (k >> 2));
                    anyCube |= cube[(byte)id];
                    same &= id == cell[0];
                }
                // Most cells are all one block (air, or rock under the ground): without a cube that's air, and all one
                // cube that cube, whichever rule; neither needs the faces counted.
                BlockId pick;
                if (!anyCube) pick = BlockId.Air;
                else if (same) pick = cell[0];
                else
                {
                    for (int k = 0; k < 8; k++)
                        faces[k] = cube[(byte)cell[k]] ? ShownFaces(full, 2 * x + (k & 1), 2 * y + ((k >> 1) & 1), 2 * z + (k >> 2)) : 0;
                    pick = majority ? ByMajority(cell, faces) : ByAny(cell, faces);
                }
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
                else plantOn[x] = same ? BlockId.Air : MostCommonPlant(cell, upperOnly: true); // one cube: no plant in it
            }
        }
    }

    // Per block id: a full cube; an opaque one (hides what's against it).
    private static readonly bool[] CubeTable = BuildTable(id => IsCube(id));
    private static readonly bool[] OpaqueCubeTable = BuildTable(id => IsCube(id) && !BlockRegistry.Get(id).Transparent);
    private static bool[] BuildTable(Func<BlockId, bool> of)
    {
        var t = new bool[256];
        for (int i = 0; i < t.Length; i++) t[i] = of((BlockId)i);
        return t;
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
        var opaque = OpaqueCubeTable;
        return Shows(x - 1, y, z) + Shows(x + 1, y, z) + Shows(x, y - 1, z) + Shows(x, y + 1, z) + Shows(x, y, z - 1)
             + Shows(x, y, z + 1);

        int Shows(int x, int y, int z)
            => (uint)x >= S || (uint)y >= S || (uint)z >= S || !opaque[(byte)full.Get(x, y, z)] ? 1 : 0;
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
                if (!Live || !WantedColumn(r.Column)) continue;
                // A chunk for every layer, and one of air above and below them all, so their faces there have air with
                // light in it.
                var column = new CoarseColumn { MinY = r.MinY - 1 };
                var made = new Dictionary<int, Built>();
                foreach (var b in r.Chunks) made[b.Y] = b;
                for (int y = r.MinY - 1; y <= r.MaxY + 1; y++)
                {
                    var chunk = new CoarseChunk
                    {
                        Pos = new ChunkPosition(r.Column.x, y, r.Column.z),
                        Min = new Vector3D<float>(r.Column.x * ColumnBlocks, y * ColumnBlocks, r.Column.z * ColumnBlocks),
                        // In the grid if it has blocks or is next to some: air far from any has nothing to light.
                        Gridded = made.ContainsKey(y - 1) || made.ContainsKey(y) || made.ContainsKey(y + 1),
                    };
                    chunk.Centre = chunk.Min + new Vector3D<float>(ColumnBlocks * 0.5f);
                    chunk.Model = Mat4.Multiply(Mat4.Translation(chunk.Min), Mat4.Scale(new Vector3D<float>(Cell)));
                    if (made.TryGetValue(y, out var b))
                    {
                        chunk.Opaque = Upload(b.Opaque, LightmapKind.Opaque);
                        chunk.Cutout = Upload(b.Cutout, LightmapKind.SeeThrough);
                        chunk.Transparent = Upload(b.Transparent, LightmapKind.SeeThrough | LightmapKind.TwoSided);
                        chunk.Occupancy = b.Occupancy;
                    }
                    column.Chunks.Add(chunk);
                    if (chunk.HasFaces) column.Faced.Add(chunk);
                }
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

    private bool _gridFull; // a chunk didn't fit in the grid: none is tried again until one is evicted

    /// <summary>Puts a chunk in <see cref="Grid"/> for the lighting to light: its occupancy and surface bricks.</summary>
    private void MakeResident(CoarseChunk c)
    {
        if (_gridStore == null || Grid.Index < 0 || _gridFull || c.InGrid) return;
        if (!_gridStore.CanHold(Grid, 1)) { _gridFull = true; return; } // drawn as open air until one goes
        _gridStore.UploadPacked(Grid, c.Pos, c.Occupancy);
        c.InGrid = true;
        c.Covered = false; // a fresh record isn't: set again (UpdateDrawn) if it should be
    }

    /// <summary>Takes a chunk out of <see cref="Grid"/> (it's out of range, or unloaded).</summary>
    private void Evict(CoarseChunk c)
    {
        c.Settled = false;
        if (!c.InGrid) return;
        c.InGrid = false;
        _gridStore?.RemoveChunk(Grid, c.Pos);
        _gridFull = false;
    }

    // Chunks gone out of range, to take out of the grid: a step of the view takes a whole shell of them out of range at
    // once, and taking them all out in that frame (each refreshes its neighbours' surfaces) made a long one.
    private readonly List<CoarseChunk> _toEvict = new();
    private int _evictAt;
    private const double EvictBudgetMs = 0.5;

    /// <summary>Takes chunks gone out of range out of the grid while the budget lasts; one back in range meanwhile
    /// stays.</summary>
    private void EvictSome()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (_evictAt < _toEvict.Count && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < EvictBudgetMs)
        {
            var c = _toEvict[_evictAt++];
            c.Evicting = false;
            if (!Kept((c.Pos.X, c.Pos.Y, c.Pos.Z))) Evict(c);
        }
        if (_evictAt == _toEvict.Count) { _toEvict.Clear(); _evictAt = 0; }
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
            Free(column);
        }
    }

    private void Unload((int x, int z) key)
    {
        // One out of range still to be freed goes first: its chunks in the grid are at the same places.
        if (_toFree.Remove(key, out var older)) Free(older);
        if (_columns.Remove(key, out var column)) Free(column);
    }

    private void Free(CoarseColumn column)
    {
        foreach (var c in column.Chunks)
        {
            Evict(c);
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
        foreach (var (_, column) in _toFree) Free(column);
        _toFree.Clear();
        foreach (var c in _toEvict) c.Evicting = false;
        _toEvict.Clear();
        _evictAt = 0;
        _queue.Clear();
        _queueHead = 0;
        _centre = (int.MinValue, int.MinValue, int.MinValue);
        _outerChunks = -1;
    }

    /// <summary>Which coarse chunks are drawn this frame: those in range whose eight full chunks aren't all ready. A
    /// column with chunks hidden under full detail notes any edit to the chunks there, and is made again when one of
    /// them is next drawn.</summary>
    private void UpdateDrawn()
    {
        // Edits under hidden chunks are looked for in one column a frame (round those with ready chunks in turn), and in
        // each as its chunks come back into view, rather than in all of them every frame (256 chunks each).
        // Only chunks near the view can have full detail ready (it loads to FullDetailRadius, in the 2x2x2 groups that
        // coarse chunks are); a far column whose chunks are drawn, settled and uncovered has nothing to do.
        int readyIndex = 0, scan = _editScan;
        long nearSq = Sq((int)MathF.Ceiling(InnerRadius / ColumnBlocks) + 2);
        // Moving on changes which full chunks are wanted, which marks their columns (ChunkLoadSystem.ColumnChanged); a few
        // near chunks a frame are looked at anyway, round them in turn, in case a change came without a mark.
        int nearIndex = 0, recheckFrom = _recheck, recheckTo = _recheck + RecheckPerFrame;
        _litChecksLeft = LitChecksPerFrame;
        foreach (var (key, column) in _columns)
        {
            long across = Sq(key.x - _centre.x) + Sq(key.z - _centre.z);
            bool nearColumn = across <= nearSq;
            if (!nearColumn && column.Quiet) continue;
            bool dirty = _readyDirty.Contains(key), quiet = !nearColumn, anyReady = false;
            foreach (var c in column.Chunks)
            {
                var k = (key.x, c.Pos.Y, key.z);
                if (!Kept(k)) { Drop(c); continue; }
                bool near = nearColumn && across + Sq(c.Pos.Y - _centre.y) <= nearSq;
                if (!near) c.ReadyKnown = false; // found again when it's next near (changes meanwhile aren't kept)
                bool due = false;
                if (near) { due = nearIndex >= recheckFrom && nearIndex < recheckTo; nearIndex++; }
                if (near && (due || !c.ReadyKnown || dirty || Finer != null))
                {
                    _steps.Lap(SwapStep);
                    c.Ready = ChildrenReady(c.Pos);
                    _steps.Lap(ReadyStep);
                    c.ReadyKnown = true;
                }
                bool ready = near && c.Ready;
                if (ready) anyReady = true;
                else if (!c.Drawn && !_inFlight.Contains(key) && column.ChildEdited)
                {
                    column.ChildEdited = false;
                    _queue.Insert(_queueHead, key); // drawn as it was until it's made again
                }
                else if (!c.Drawn && c.Covered && _editChecks.Add(key)) _editCheckOrder.Enqueue(key); // see below
                // In the grid while it's kept, so it's lit and ready whenever full detail goes; until it's lit, a drawn
                // chunk keeps the light maps it baked before, or is held back if it never had any.
                if (!c.Gridded) c.Settled = true; // air with no blocks near: nothing to light
                else if (!c.InGrid)
                {
                    _toPlace.Add((Distance(_view, c.Pos), c)); // put in the grid after this loop, nearest first
                    c.Settled = false;
                    c.Waiting = 0;
                }
                else if (!c.Settled)
                {
                    c.Settled = ++c.Waiting >= MinSwapFrames && CheckNow(c.Pos)
                                && (c.Waiting > (c.Baked ? MaxSwapFrames : MaxFirstLightFrames) || ChunkLit(c));
                    // One held back for its light swaps in from when it's first shown (the full detail is held till then).
                    if (c.Settled && !c.Baked && c.Drawn && float.IsFinite(c.FadeInAt)) c.FadeInAt = _time;
                }
                // Terrain waiting to be shown is lit ahead of light changes on terrain already shown (nearest first, see
                // ShowFirst): this chunk while it's held back for its first light (it holds the fog there), with the chunks
                // above and below it that ChunkLit looks at too.
                if (c.InGrid && WaitsForLight(c) && ShowFirst != null)
                {
                    ShowFirst(Grid, c.Pos);
                    ShowFirst(Grid, Above(c.Pos, 1));
                    ShowFirst(Grid, Above(c.Pos, -1));
                }
                if (!ready)
                {
                    c.Leaving = false;
                    if (!c.Drawn) c.FadeInAt = c.WasReady ? _time : float.NegativeInfinity;
                    c.Drawn = true;
                }
                else if (c.Drawn)
                {
                    // Swapping to full detail: hidden once that terrain is lit by its own blocks (not the coarse ones over
                    // it), so it comes in showing its light.
                    if (!c.Leaving) { c.Leaving = true; c.LeaveWait = 0; }
                    // And the full detail swapping in for it, which waits for its own light (ChildrenLit).
                    if (Finer == null && ShowFirst != null)
                        for (int j = 0; j < 8; j++)
                            ShowFirst(_volume.Gpu, new ChunkPosition(c.Pos.X * 2 + (j & 1), c.Pos.Y * 2 + ((j >> 1) & 1), c.Pos.Z * 2 + (j >> 2)));
                    if (++c.LeaveWait >= MinSwapFrames && CheckNow(c.Pos) && (c.LeaveWait > MaxSwapFrames || ChildrenLit(c.Pos)))
                    {
                        c.Drawn = false;
                        c.Leaving = false;
                        c.FadeOutAt = _time;
                    }
                }
                // Covered while its full detail is ready, and while that's still drawn as this swaps in over it: the world's
                // rays then see only full-detail blocks there. (Uncovered at once, the full detail fading out was shadowed
                // by the coarse blocks in it, and darkened.)
                bool covered = ready || c.Drawn && (WaitsForLight(c) || CoarseShare(c) < 1f);
                if (c.Covered != covered) SetCovered(c, covered);
                c.WasReady = ready;
                quiet &= c.Drawn && c.Settled && !c.Covered && !c.Leaving && (c.InGrid || !c.Gridded);
            }
            column.Quiet = quiet;
            if (anyReady && readyIndex++ == scan && !column.ChildEdited) column.ChildEdited = AnyEdited(key);
        }
        _editScan = readyIndex == 0 ? 0 : (scan + 1) % readyIndex;
        _readyDirty.Clear();

        // Putting a chunk in the grid writes its occupancy and its surface bricks: as many a frame as fit the budget, the
        // rest on later frames (held back, or lit as before, meanwhile). Nearest first: the nearest held back holds the
        // fog, and in the dictionary's order, while the view moved, chunks arriving far away took the budget and the fog
        // stopped until it caught up.
        if (_toPlace.Count > 0)
        {
            _toPlace.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            _steps.Lap(SwapStep);
            long until = System.Diagnostics.Stopwatch.GetTimestamp()
                         + (long)(ResidentBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000);
            foreach (var (_, c) in _toPlace)
            {
                if (_gridFull || System.Diagnostics.Stopwatch.GetTimestamp() >= until) break;
                MakeResident(c);
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

    /// <summary>A chunk out of range (past the kept shell): not drawn, and out of the grid a few a frame (see
    /// <see cref="EvictSome"/>).</summary>
    private void Drop(CoarseChunk c)
    {
        if (c.InGrid && !c.Evicting) { c.Evicting = true; _toEvict.Add(c); }
        c.Drawn = c.Leaving = c.ReadyKnown = c.WasReady = c.Covered = false;
        c.FadeOutAt = float.NegativeInfinity; // gone at once: it's at the fog, or under full detail
        c.Waiting = 0;
    }

    /// <summary>Whether the eight finer chunks under coarse chunk <paramref name="p"/> are ready to be seen.</summary>
    private bool ChildrenReady(ChunkPosition p)
    {
        for (int j = 0; j < 8; j++)
        {
            int x = p.X * 2 + (j & 1), y = p.Y * 2 + ((j >> 1) & 1), z = p.Z * 2 + (j >> 2);
            if (!(Finer?.Covers((x, y, z)) ?? _load.IsChunkReady(new ChunkPosition(x, y, z)))) return false;
        }
        return true;
    }

    /// <summary>Whether a chunk waiting on lighting looks again this frame: every <see cref="LitCheckEvery"/> frames,
    /// staggered across chunks, as a check reads up to a few hundred bricks.</summary>
    private bool CheckNow(ChunkPosition p) => ((_frame + p.X * 3 + p.Y * 7 + p.Z * 5) & (LitCheckEvery - 1)) == 0;
    private const int LitCheckEvery = 4;

    private void SetCovered(CoarseChunk c, bool covered)
    {
        c.Covered = covered;
        if (_gridStore != null && c.InGrid) _gridStore.SetCovered(Grid, c.Pos, covered);
    }

    /// <summary>Whether a chunk in the grid shows its light: its bricks, and the air bricks above and below it that its
    /// top and bottom faces read, are lit.</summary>
    private bool ChunkLit(CoarseChunk c)
    {
        // Shown for the first time once lit too, not once its light is done changing (three bounce updates, far ones
        // eight frames apart): it comes in behind the fog, where the rest of its light settling barely shows.
        var lit = IsLit;
        if (lit == null) return true;
        _steps.Lap(SwapStep);
        bool result = Budgeted(lit, Grid, c.Pos) && Budgeted(lit, Grid, Above(c.Pos, 1)) && Budgeted(lit, Grid, Above(c.Pos, -1));
        _steps.Lap(LitStep);
        return result;
    }

    private static ChunkPosition Above(ChunkPosition p, int dy) => new(p.X, p.Y + dy, p.Z);

    /// <summary>Whether the full-detail chunks under coarse chunk <paramref name="p"/> are lit by their own blocks.</summary>
    private bool ChildrenLit(ChunkPosition p)
    {
        if (IsLit == null || Finer != null) return true; // a finer level's chunks are lit before they're ready (Covers)
        _steps.Lap(SwapStep);
        bool result = true;
        for (int j = 0; j < 8 && result; j++)
            result = Budgeted(IsLit, _volume.Gpu, new ChunkPosition(p.X * 2 + (j & 1), p.Y * 2 + ((j >> 1) & 1), p.Z * 2 + (j >> 2)));
        _steps.Lap(LitStep);
        return result;
    }

    /// <summary>A chunk's light check, while the frame's budget of them lasts (each reads up to 64 bricks); past it
    /// every chunk counts as not lit yet, to be looked at again on a later frame.</summary>
    private bool Budgeted(Func<GridHandle, ChunkPosition, bool> lit, GridHandle grid, ChunkPosition pos)
        => _litChecksLeft-- > 0 && lit(grid, pos);
    private int _litChecksLeft;
    private const int LitChecksPerFrame = 2048;

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
        // Chunks in the outermost band don't count: the view moving on a chunk brings a shell of new ones in there at
        // once, and the fog pulled in for each and went out again (they come in behind nearly total fog anyway).
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
        FogReason.Placing  => $"level {_fogHeld.Level} chunk waiting to go in the light grid, {_fogHeld.At:F0} blocks out",
        FogReason.Lighting => $"level {_fogHeld.Level} chunk waiting {_fogHeld.Frames} frames for its light, {_fogHeld.At:F0} blocks out",
        _ => "nothing (out to the full range)",
    };

    /// <summary>The nearest of this level's terrain not ready to be seen: a column being made or queued (by its distance
    /// across, as which of its chunks are in range isn't known yet), or a chunk in range held back for its light, where
    /// nothing else shows instead (the finer level's ready, or the coarser one drawn).</summary>
    private float NotReadyAt(Vector3D<float> centre, out FogReason why, out int frames)
    {
        float best = float.PositiveInfinity;
        why = FogReason.None;
        frames = 0;
        var coarser = Coarser is { Live: true } up ? up : null;
        foreach (var c in _inFlight)
            if (!_columns.ContainsKey(c) && !HiddenColumn(c) && Across(centre, c) is var d && d < best) // one being made again is drawn meanwhile
                (best, why) = (d, FogReason.Making);
        for (int i = _queueHead; i < _queue.Count; i++)
            if (!_columns.ContainsKey(_queue[i]) && !HiddenColumn(_queue[i]))
            {
                if (Across(centre, _queue[i]) is var d && d < best) (best, why) = (d, FogReason.Queued);
                break;
            }
        foreach (var (k, column) in _columns)
        {
            if (Across(centre, k) >= best) continue;
            foreach (var c in column.Faced) // only one with faces waits for light
                if (WaitsForLight(c) && !(c.ReadyKnown && c.Ready) && Kept((k.x, c.Pos.Y, k.z))
                    && !(coarser != null && coarser.Shows((k.x >> 1, c.Pos.Y >> 1, k.z >> 1))) // what's under it shows instead
                    && Distance(centre, c.Pos) is var d && d < best)
                    (best, why, frames) = (d, c.InGrid ? FogReason.Lighting : FogReason.Placing, c.Waiting);
        }
        return best;

        bool HiddenColumn((int x, int z) k) => coarser != null && coarser.Shows((k.x >> 1, _centre.y >> 1, k.z >> 1));
    }

    /// <summary>Whether a chunk is held back until it's lit: it's never been lit from the grid, and it can be (the grid
    /// isn't full). Drawn meanwhile it would show as open air, bright where it should be shadowed.</summary>
    private bool WaitsForLight(CoarseChunk c)
        => Grid.Index >= 0 && !_gridFull && !c.Baked && !c.Settled && c.HasFaces;

    /// <summary>Horizontal distance from <paramref name="centre"/> to the nearest point of coarse column <paramref name="c"/>.</summary>
    private float Across(Vector3D<float> centre, (int x, int z) c)
    {
        float dx = MathF.Max(0f, MathF.Max(c.x * ColumnBlocks - centre.X, centre.X - (c.x + 1) * ColumnBlocks));
        float dz = MathF.Max(0f, MathF.Max(c.z * ColumnBlocks - centre.Z, centre.Z - (c.z + 1) * ColumnBlocks));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Distance from <paramref name="centre"/> to the nearest point of coarse chunk <paramref name="p"/>.</summary>
    private float Distance(Vector3D<float> centre, ChunkPosition p)
    {
        float dx = MathF.Max(0f, MathF.Max(p.X * ColumnBlocks - centre.X, centre.X - (p.X + 1) * ColumnBlocks));
        float dy = MathF.Max(0f, MathF.Max(p.Y * ColumnBlocks - centre.Y, centre.Y - (p.Y + 1) * ColumnBlocks));
        float dz = MathF.Max(0f, MathF.Max(p.Z * ColumnBlocks - centre.Z, centre.Z - (p.Z + 1) * ColumnBlocks));
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
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
            var faced = column.Faced;
            if (faced.Count == 0) continue;
            if (!frame.Frustum.Intersects(faced[0].Min, faced[^1].Min + size)) continue; // in Y order
            foreach (var c in faced)
            {
                // Coming in over the finer level it replaces (drawing the pixels that leaves out), drawn, or going away
                // (leaving out the pixels the finer level coming back draws): a pixel shows one level, never both.
                float share = c.Drawn ? CoarseShare(c) : 1f - Faded(c.FadeOutAt);
                if (share <= 0f) continue;
                float dissolve = c.Drawn ? (share < 1f ? -share : 0f) : 1f - share;
                // A coarser level over it hides it the same way.
                float over = coarser?.OwnCoverOf(cx << Level, c.Pos.Y << Level, cz << Level) ?? 0f;
                if (over >= 1f) continue;
                if (over != 0f) dissolve = over;
                if (WaitsForLight(c)) continue;
                if (!frame.Frustum.Intersects(c.Min, c.Min + size)) continue;
                // Lit from the grid once it's settled there; until then (or leaving it) with the light maps it last
                // baked; one never lit isn't drawn (above) unless the grid is full, when it's lit as open air.
                bool bake = c.Settled && c.InGrid;
                if (bake) c.Baked = true;
                int grid = c.Baked && Grid.Index >= 0 ? Grid.Index : Renderer.LodGrid;
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
        int withFaces = 0, drawn = 0, inGrid = 0;
        foreach (var column in _columns.Values)
            foreach (var c in column.Chunks)
            {
                if (c.HasFaces) withFaces++;
                if (c.Drawn && c.HasFaces) drawn++;
                if (c.InGrid) inGrid++;
            }
        ImGui.Text($"Columns: {_columns.Count:N0} made, {_inFlight.Count} being made, {_queue.Count - _queueHead:N0} queued");
        ImGui.Text($"Chunks with faces: {withFaces:N0} ({drawn:N0} drawn, {_chunksDrawn:N0} in view), {_quads:N0} quads " +
                   $"(~{_quads * ChunkQuad.SizeBytes / (1024.0 * 1024.0):F1} MB); columns made so far: {_built:N0}");
        if (Grid.Index >= 0)
            ImGui.Text($"Lit as a grid: {inGrid:N0} chunks, {Grid.Slots.Count:N0} light bricks " +
                       $"({(long)Grid.Slots.Count * GridStore.SlotBytes / (1024 * 1024)} MB)");
        else ImGui.TextDisabled("Lit as open air (no light store)");
        _steps.Draw();
    }

    /// <summary>Full-detail chunks loaded (for the panel).</summary>
    public int FullDetailChunks => _volume.LoadedCount;
}
