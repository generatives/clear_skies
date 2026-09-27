using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Persistence;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;
using System.Collections.Concurrent;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Streams the static world around the local player's terrain interest (see <see cref="TerrainInterest"/>), like a typical block game: a queue of the chunk columns within
/// the view distance, closest first by horizontal distance, rebuilt whenever the camera crosses into another column,
/// and loaded a whole column at a time. A rebuild is spread over frames (within <see cref="StreamBudgetMs"/>, for
/// unloading what left view and for scanning the columns in view), so crossing a column doesn't cost one long frame.
/// Only chunks that may hold something are queued: the layers the generator says
/// it may fill (<see cref="IWorldGenerator.ColumnLayers"/>) and chunks with a save file (builds). The generator's
/// layers are a loose bound, so chunks that turn out to be air are remembered (a bit per column) until their column
/// leaves view, and aren't queued again.
///
/// What's loaded is limited by GPU light storage (<see cref="GridStore.WorldLightBudget"/>): the world's surface
/// bricks, plus a surface chunk's average for each chunk loaded but not uploaded yet or still loading. When the
/// budget is full, the queue stops; and if the next queued column is nearer than the farthest loaded one (after
/// the camera moved), the farthest is unloaded to make room. So the loaded world is always the nearest that fits.
///
/// The fog (see <see cref="FogDistance"/>) sits at the nearest column still queued or loading, or else at the view
/// distance: an island only partly loaded fades out where loading stopped instead of ending in a hard edge.
/// </summary>
public sealed class ChunkLoadSystem : ISystem, IDebugUiSystem
{
    /// <summary>Column jobs (loading or generating a column's missing chunks) in flight at once. Most of a first visit is
    /// sky that generation rules out in microseconds, so this is well above the core count: the background workers queue
    /// the excess.</summary>
    private const int MaxInFlight = 64;

    /// <summary>The GridStore world index width that fits a view distance: wider than the span of chunks loaded at
    /// once (with a column to spare each side for the frame between a chunk leaving range and its storage being
    /// released), so two loaded chunks never share a cell.</summary>
    public static int WorldIndexDim(float viewDistance) => 2 * (int)MathF.Ceiling(viewDistance / S) + 3;

    /// <summary>Light bricks counted for a chunk whose real cost the GPU store doesn't know yet (loaded but not
    /// uploaded, or loading): a surface chunk's measured average. Buried stone and sky cost less, so this errs
    /// towards waiting for uploads to catch up rather than overshooting.</summary>
    private const int PendingBricks = 26;

    /// <summary>World chunks loaded at most: the GPU's world index limit, less room for chunks leaving range whose
    /// storage is released a frame later.</summary>
    private const int MaxChunks = GridStore.MaxWorldChunks - 4096;

    /// <summary>Columns queued at once. Far more than load before the camera next moves a column; when the queue runs
    /// out short of the view distance it is rebuilt from there.</summary>
    private const int MaxQueued = 4096;

    /// <summary>Streamed layers below the lowest generated one, for building under the islands. Streaming covers 64
    /// layers (a column's bits); the rest are above.</summary>
    private const int LayersBelow = 8;

    /// <summary>Chunks unloaded at most per frame to make room: enough to keep up with flying, few enough that
    /// unloading doesn't stall a frame.</summary>
    private const int MaxEvictChunksPerFrame = 512;

    /// <summary>Light bricks freed beyond what the next column needs, so the columns after it don't each wait a frame
    /// for their own eviction.</summary>
    private const int EvictSlack = 4096;

    private const int S = ChunkData.Size;

    private readonly IChunkStore _chunkStore;

    private readonly EntitySet      _interests;
    private readonly ChunkVolume    _staticVolume;
    private readonly GridStore      _store;
    private readonly ThreadLocal<IWorldGenerator> _generator;
    private readonly ThreadLocal<ChunkData> _scratch = new(() => new ChunkData());
    private readonly int            _minY;      // the lowest streamed layer: bit 0 of a column's bits

    /// <summary>Column offsets from the camera's column, closest first, out to the view distance. Computed once; a
    /// rebuild just walks it.</summary>
    private readonly (short dx, short dz)[] _offsetsByDistance;
    private readonly int _viewColumns; // the view distance in columns
    private readonly float _viewDistance;

    /// <summary>Per column in view (layer bits): what the generator may fill, and what turned out to be air. Dropped
    /// once the column leaves view, so returning re-learns its air.</summary>
    private readonly Dictionary<(int x, int z), (ulong Generated, ulong Air)> _columns = new();

    /// <summary>Every chunk with a save file (scanned once at startup, kept up to date by saves).</summary>
    private readonly HashSet<ChunkPosition> _saved = new();

    /// <summary>Per column (layer bits): chunks holding a build — a save with blocks, or an unsaved edit.</summary>
    private readonly Dictionary<(int x, int z), ulong> _built = new();

    // Columns with chunks still to generate or load, closest first, from _queueHead on; and the columns workers have
    // now, with how many chunks each.
    private readonly List<(int x, int z)> _queue = new();
    private int _queueHead;
    private bool _queueTruncated;

    /// <summary>Main-thread time per frame for the work that comes in bursts, shared: unloading what left view,
    /// adding finished columns, and scanning for columns to queue, in that order. What doesn't fit waits for the next
    /// frame. Each still gets a minimum (<see cref="MinStepMs"/>) so none is starved for long.</summary>
    private const double StreamBudgetMs = 2.5, MinStepMs = 0.3;
    private readonly System.Diagnostics.Stopwatch _budget = new();

    private double BudgetLeft => System.Math.Max(MinStepMs, StreamBudgetMs - _budget.Elapsed.TotalMilliseconds);

    // The rebuild's scan of the columns in view: the next offset to look at, and whether it has looked at them all
    // (or filled the queue). Columns nearer than the scan's position are all queued, loading or loaded.
    private int _scanAt;
    private bool _scanDone = true;

    // Chunks that left view at the last rebuild, unloaded from _unloadAt on a few per frame.
    private int _unloadAt;
    private readonly List<(int x, int z)> _columnsOutOfView = new();
    private readonly Dictionary<(int x, int z), int> _inFlight = new();
    private int _inFlightChunks;
    private readonly ConcurrentQueue<((int x, int z) Column, List<(ChunkPosition Pos, ChunkData? Data, PackedOpacity? Packed)> Chunks)> _results = new();

    private (int x, int z) _lastCamColumn = (int.MinValue, int.MinValue);
    private bool _skippedInFlight;
    private bool _nothingToEvict; // the last search found nothing farther than the queue's head; cleared by a rebuild

    /// <summary>Loaded columns, farthest first, as of the first eviction since the last rebuild; <see cref="_evictNext"/>
    /// is the next to consider. Columns loaded since then are nearer (the queue is closest first), so the order holds.</summary>
    private readonly List<(int x, int z)> _evictOrder = new();
    private int _evictNext = -1; // -1: not built since the last rebuild
    private bool _full;
    private int _evictions;

    private float _fogDistance;
    private float _fogTarget;

    private readonly List<ChunkPosition> _toUnload = new();

    /// <summary>Horizontal distance from the camera at which the loaded world stops: the nearest chunk column still
    /// queued or loading, or else the view distance, eased over time. Fog should be total by here.</summary>
    public float FogDistance => _fogDistance;

    /// <param name="store">The GPU store the world's chunks go to: its light budget limits what's loaded.</param>
    /// <param name="viewDistance">How far out chunks are streamed, in blocks (horizontally), as far as the budget
    /// reaches. The GridStore's world index must fit it: see <see cref="WorldIndexDim"/>.</param>
    /// <param name="minChunkY">Lowest chunk layer the generator fills. Streaming covers 64 layers from
    /// <see cref="LayersBelow"/> under it; the generator's layers must fall inside them. Edits to the static world
    /// outside them are refused (see <see cref="ChunkVolume.EditableLayers"/>).</param>
    /// <param name="chunkStore">Where edited chunks are kept (the world's save database on the host).</param>
    public ChunkLoadSystem(World world, ChunkVolume staticVolume, GridStore store, Func<IWorldGenerator> generatorFactory,
                           float viewDistance, int minChunkY, IChunkStore chunkStore)
    {
        _chunkStore = chunkStore;

        _interests    = world.GetEntities().With<Transform>().With<TerrainInterest>().AsSet();
        _staticVolume = staticVolume;
        _store        = store;
        _generator    = new ThreadLocal<IWorldGenerator>(generatorFactory);
        _minY         = minChunkY - LayersBelow;
        _staticVolume.EditableLayers = (_minY, _minY + 63); // only what streaming can load back
        _viewDistance = viewDistance;
        _viewColumns  = (int)MathF.Ceiling(viewDistance / S);
        _offsetsByDistance = BuildOffsetsByDistance(_viewColumns);
        ScanSaves();
    }

    /// <summary>Where the drawn world is streamed around: the <see cref="TerrainInterestKind.Full"/> terrain interest
    /// (the local player's, see EntityPresenceSystem). Colliders-only interests get their colliders from what's loaded
    /// here; streaming data around them too comes with multiplayer.</summary>
    private bool TryGetViewCentre(out Vector3D<float> centre)
    {
        foreach (ref readonly Entity e in _interests.GetEntities())
        {
            if (e.Get<TerrainInterest>().Kind != TerrainInterestKind.Full) continue;
            centre = e.Get<Transform>().Position;
            return true;
        }
        centre = default;
        return false;
    }

    private static (short dx, short dz)[] BuildOffsetsByDistance(int radius)
    {
        var offsets = new List<(short dx, short dz, int d)>();
        for (int dz = -radius; dz <= radius; dz++)
        for (int dx = -radius; dx <= radius; dx++)
            if (dx * dx + dz * dz <= radius * radius) offsets.Add(((short)dx, (short)dz, dx * dx + dz * dz));
        offsets.Sort((a, b) => a.d.CompareTo(b.d));
        return offsets.Select(o => (o.dx, o.dz)).ToArray();
    }

    private void ScanSaves()
    {
        foreach (var pos in _chunkStore.SavedChunks())
            RecordSave(pos, hasBlocks: true);
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Chunk Loading";

    public void DrawDebugUi()
    {
        ImGui.Text($"View distance: {_viewDistance:F0} blocks ({_columns.Count} columns known)");
        ImGui.Text($"Light: {WorldBricks():N0} / {_store.WorldLightBudget:N0} bricks, {PendingChunks():N0} chunks not uploaded yet" +
                   (_full ? " (full)" : ""));
        ImGui.Text($"Loaded: {_staticVolume.LoadedCount:N0} / {MaxChunks:N0} chunks   Queued columns: {_queue.Count - _queueHead}" +
                   $"{(_queueTruncated ? "+" : "")}   In flight: {_inFlight.Count}");
        ImGui.Text($"Fog distance: {_fogDistance:F0} (target {_fogTarget:F0})   Columns evicted: {_evictions}");
        ImGui.Text($"Saved chunks: {_saved.Count}   Layers: {_minY}..{_minY + 63}");

        ImGui.Separator();
        _steps.Draw();
    }

    // CPU time of each step, for the debug panel: which one a hitch while streaming came from.
    private const int ApplyStep = 0, UnloadStep = 1, QueueStep = 2, DispatchStep = 3, EvictStep = 4, FogStep = 5;
    private readonly StepTimer _steps = new("Adding finished columns", "Rebuild: unloading out of view",
                                            "Rebuild: queueing columns", "Dispatching jobs", "Evicting far columns", "Fog") { Owner = "Chunk Loading" };

    public void Update(float dt)
    {
        _steps.Start();

        _budget.Restart();
        bool haveCam = TryGetViewCentre(out var camPos);
        if (haveCam)
        {
            var camColumn = ((int)MathF.Floor(camPos.X / S), (int)MathF.Floor(camPos.Z / S));
            bool idle = _scanDone && _queueHead == _queue.Count && _inFlight.Count == 0;
            if (camColumn != _lastCamColumn || (idle && (_skippedInFlight || _queueTruncated)))
            {
                _lastCamColumn = camColumn;
                _skippedInFlight = false;
                Rebuild();
            }
            UnloadSome(BudgetLeft);
            _steps.Lap(UnloadStep);
        }

        double applyUntil = _steps.SinceLap() + BudgetLeft;
        while (_steps.SinceLap() < applyUntil && _results.TryDequeue(out var job))
        {
            _inFlightChunks -= _inFlight[job.Column];
            _inFlight.Remove(job.Column);
            foreach (var (pos, data, packed) in job.Chunks)
            {
                if (data == null)
                {
                    if (_saved.Contains(pos)) RecordBuild(pos, hasBlocks: false); // a build that was emptied out
                    else if (_columns.TryGetValue((pos.X, pos.Z), out var col))
                        _columns[(pos.X, pos.Z)] = (col.Generated, col.Air | Bit(pos));
                    continue;
                }
                // Dropped if the camera moved on while it generated, or an edit created the chunk meanwhile.
                if (InView(pos.X, pos.Z) && !_staticVolume.IsLoaded(pos)) _staticVolume.AddChunk(pos, data, packed);
            }
        }
        _steps.Lap(ApplyStep);
        if (!haveCam) return;

        ScanSome(BudgetLeft);
        _steps.Lap(QueueStep);

        Dispatch();
        _steps.Lap(DispatchStep);
        UpdateFog(camPos, dt);
        _steps.Lap(FogStep);
    }

    /// <summary>Starts over from the camera's new column: lists what left the view distance to unload, and restarts
    /// the scan for columns in view with chunks still to fetch, closest first. Both then go on a little each frame
    /// (<see cref="UnloadSome"/>, <see cref="ScanSome"/>).</summary>
    private void Rebuild()
    {
        // Whatever the last rebuild didn't get to unload goes now: a chunk that stays loaded out of view could share a
        // cell of the GPU store's world index with one coming into view on the other side (see WorldIndexDim).
        UnloadSome(double.PositiveInfinity);

        _toUnload.Clear();
        _unloadAt = 0;
        // (An edit that put blocks where there were none counts as a build once its chunk is saved: by the autosave,
        // or as it unloads. Until then the chunk is loaded, so the queue doesn't need to know.)
        foreach (var (p, _) in _staticVolume.All)
            if (!InView(p.X, p.Z)) _toUnload.Add(p);

        // Forget the columns out of view: their air is re-learned on return.
        _columnsOutOfView.Clear();
        foreach (var key in _columns.Keys)
            if (!InView(key.x, key.z)) _columnsOutOfView.Add(key);
        foreach (var key in _columnsOutOfView) _columns.Remove(key);
        _steps.Lap(UnloadStep);

        _queue.Clear();
        _queueHead = 0;
        _queueTruncated = false;
        _nothingToEvict = false;
        _evictNext = -1;
        _scanAt = 0;
        _scanDone = false;
        _steps.Lap(QueueStep);
    }

    /// <summary>Unloads chunks that left view at the last rebuild, for up to <paramref name="budgetMs"/>.</summary>
    private void UnloadSome(double budgetMs)
    {
        double start = _steps.SinceLap();
        while (_unloadAt < _toUnload.Count && _steps.SinceLap() - start < budgetMs)
            Unload(_toUnload[_unloadAt++]); // a no-op if eviction already unloaded it
    }

    /// <summary>Carries on the rebuild's scan of the columns in view, closest first, for up to
    /// <paramref name="budgetMs"/>, queueing each with chunks still to fetch.</summary>
    private void ScanSome(double budgetMs)
    {
        if (_scanDone) return;
        double start = _steps.SinceLap();
        while (_scanAt < _offsetsByDistance.Length)
        {
            // Checked every column: one seen for the first time asks the generator for its layers, which isn't cheap.
            if (_steps.SinceLap() - start >= budgetMs) return;
            var (dx, dz) = _offsetsByDistance[_scanAt];
            int x = _lastCamColumn.x + dx, z = _lastCamColumn.z + dz;
            if (HasMissing(x, z))
            {
                if (_queue.Count == MaxQueued) { _queueTruncated = true; break; }
                _queue.Add((x, z));
            }
            _scanAt++;
        }
        _scanDone = true;
        Console.WriteLine($"[load] rebuild: queued {_queue.Count}{(_queueTruncated ? "+" : "")} columns, loaded " +
                          $"{_staticVolume.LoadedCount} chunks ({PendingChunks()} not uploaded), light {WorldBricks()}/" +
                          $"{_store.WorldLightBudget} bricks, unloaded {_toUnload.Count}, evicted {_evictions} columns so far");
    }

    private bool InView(int x, int z) => Sq(x - _lastCamColumn.x) + Sq(z - _lastCamColumn.z) <= Sq(_viewColumns);

    private static long Sq(int v) => (long)v * v;

    private long ColumnDistSq((int x, int z) c) => Sq(c.x - _lastCamColumn.x) + Sq(c.z - _lastCamColumn.z);

    /// <summary>Chunks of column (x, z) that may hold something: what the generator may fill, less what turned out to be
    /// air, plus builds.</summary>
    private ulong MaybeContent(int x, int z)
    {
        if (!_columns.TryGetValue((x, z), out var col))
            _columns[(x, z)] = col = (_generator.Value!.ColumnLayers(x, z, _minY), 0UL);
        return (col.Generated & ~col.Air) | _built.GetValueOrDefault((x, z));
    }

    private ulong Bit(ChunkPosition p) => 1UL << (p.Y - _minY);

    /// <summary>Records whether chunk <paramref name="p"/> holds a build; a chunk that was emptied out is air again.</summary>
    private void RecordBuild(ChunkPosition p, bool hasBlocks)
    {
        if (p.Y < _minY || p.Y > _minY + 63) return;
        ulong b = Bit(p);
        ulong built = _built.GetValueOrDefault((p.X, p.Z));
        _built[(p.X, p.Z)] = hasBlocks ? built | b : built & ~b;
        if (_columns.TryGetValue((p.X, p.Z), out var col))
            _columns[(p.X, p.Z)] = (col.Generated, hasBlocks ? col.Air & ~b : col.Air | b);
    }

    private void RecordSave(ChunkPosition p, bool hasBlocks)
    {
        _saved.Add(p);
        RecordBuild(p, hasBlocks);
    }

    /// <summary>Light bricks the world's loaded chunks hold in the GPU store (not counting unloaded ones still
    /// waiting to be released).</summary>
    private int WorldBricks() => _staticVolume.Gpu.Slots.Count - _store.WorldBricksReleasing;

    /// <summary>Chunks loaded but not uploaded to the GPU store yet.</summary>
    private int PendingChunks() =>
        System.Math.Max(0, _staticVolume.LoadedCount - (_store.WorldChunkCount - _store.WorldChunksReleasing));

    /// <summary>Hands queued columns to workers, one job per column, while the budget has room. When it doesn't, and
    /// the next column is nearer than the farthest loaded one, unloads that to make room.</summary>
    private void Dispatch()
    {
        _full = false;
        while (_inFlight.Count < MaxInFlight && _queueHead < _queue.Count)
        {
            var col = _queue[_queueHead];
            if (_inFlight.ContainsKey(col)) { _skippedInFlight = true; _queueHead++; continue; } // re-queued by the next rebuild
            var work = Missing(col.x, col.z);
            if (work.Count == 0) { _queueHead++; continue; }

            int chunks = _staticVolume.LoadedCount + _inFlightChunks + work.Count;
            int bricks = WorldBricks() + (PendingChunks() + _inFlightChunks + work.Count) * PendingBricks;
            if (chunks > MaxChunks || bricks > _store.WorldLightBudget)
            {
                _full = true;
                // Only once the store really is full: until uploads catch up, pending chunks are just estimates.
                int overChunks = _staticVolume.LoadedCount + _inFlightChunks + work.Count - MaxChunks;
                int overBricks = WorldBricks() + (_inFlightChunks + work.Count) * PendingBricks - _store.WorldLightBudget;
                if (overChunks > 0 || overBricks >= 0)
                {
                    _steps.Lap(DispatchStep);
                    EvictFartherThan(ColumnDistSq(col), overChunks + work.Count, overBricks + EvictSlack);
                    _steps.Lap(EvictStep);
                }
                break;
            }

            _queueHead++;
            _inFlight.Add(col, work.Count);
            _inFlightChunks += work.Count;
            BackgroundWork.Queue(() =>
            {
                var loaded = new List<(ChunkPosition, ChunkData?, PackedOpacity?)>(work.Count);
                foreach (var (pos, fromSave) in work)
                {
                    var data = _scratch.Value!;
                    if (!(fromSave && _chunkStore.TryLoad(pos, data)))
                        _generator.Value!.Generate(data, pos);
                    data.Compact(); // stone inside an island, or sky, keeps one block instead of 64 KB
                    if (data.HasAnySolid())
                    {
                        data.IsDirty = false;
                        loaded.Add((pos, data, GridStore.Pack(data))); // the GPU store's packing, off the main thread
                        _scratch.Value = new ChunkData();
                    }
                    else
                    {
                        loaded.Add((pos, null, null));
                        // Generation only ever writes blocks, so an empty result leaves the buffer all air and ready to
                        // reuse; a loaded save may have overwritten more than blocks, so start that one afresh.
                        if (fromSave) _scratch.Value = new ChunkData();
                    }
                }
                _results.Enqueue((col, loaded));
            });
        }
    }

    /// <summary>Unloads the farthest loaded columns (not being loaded) that are more than a column farther than
    /// <paramref name="distSq"/> (so two columns at about the same distance don't keep swapping), until
    /// <paramref name="chunks"/> chunks and <paramref name="bricks"/> light bricks are freed, or
    /// <see cref="MaxEvictChunksPerFrame"/> chunks. Their bricks count as free at once (GridStore.WorldBricksReleasing),
    /// though the store releases them over the next frames.</summary>
    private void EvictFartherThan(long distSq, int chunks, int bricks)
    {
        if (_nothingToEvict) return;
        if (_evictNext < 0) BuildEvictOrder();

        float margin = MathF.Sqrt(distSq) + 1f;
        int freedChunks = 0, freedBricks = 0;
        while (freedChunks < MaxEvictChunksPerFrame && (freedChunks < chunks || freedBricks < bricks))
        {
            if (_evictNext >= _evictOrder.Count || ColumnDistSq(_evictOrder[_evictNext]) <= margin * margin)
            {
                _nothingToEvict = true;
                return;
            }
            var far = _evictOrder[_evictNext++];
            if (_inFlight.ContainsKey(far)) continue;
            int unloaded = 0;
            for (int layer = 0; layer < 64; layer++)
            {
                var p = new ChunkPosition(far.x, _minY + layer, far.z);
                if (!_staticVolume.IsLoaded(p)) continue;
                freedBricks += ChunkBricks(p);
                Unload(p);
                unloaded++;
            }
            if (unloaded == 0) continue;
            freedChunks += unloaded;
            _evictions++;
        }
    }

    private void BuildEvictOrder()
    {
        var columns = new HashSet<(int x, int z)>();
        foreach (var (p, _) in _staticVolume.All) columns.Add((p.X, p.Z));
        _evictOrder.Clear();
        _evictOrder.AddRange(columns);
        _evictOrder.Sort((a, b) => ColumnDistSq(b).CompareTo(ColumnDistSq(a)));
        _evictNext = 0;
    }

    /// <summary>Light bricks chunk <paramref name="p"/> holds in the GPU store (none until it is uploaded).</summary>
    private int ChunkBricks(ChunkPosition p) => _store.BricksOf(_staticVolume.Gpu, p);

    /// <summary>Whether column (x, z) has chunks that may hold something and aren't loaded yet.</summary>
    private bool HasMissing(int x, int z)
    {
        for (ulong bits = MaybeContent(x, z); bits != 0; bits &= bits - 1)
            if (!_staticVolume.IsLoaded(new ChunkPosition(x, _minY + BitOperations.TrailingZeroCount(bits), z))) return true;
        return false;
    }

    /// <summary>Whether the terrain within <paramref name="radius"/> (horizontally) of <paramref name="centre"/> has
    /// loaded: every column there is in view, with nothing still to load or loading.</summary>
    public bool IsTerrainLoaded(Vector3D<float> centre, float radius)
    {
        if (_lastCamColumn.x == int.MinValue) return false;
        int cx = (int)MathF.Floor(centre.X / S), cz = (int)MathF.Floor(centre.Z / S), r = (int)MathF.Ceiling(radius / S);
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            int x = cx + dx, z = cz + dz;
            if (!InView(x, z) || _inFlight.ContainsKey((x, z)) || HasMissing(x, z)) return false;
        }
        return true;
    }

    /// <summary>Column (x, z)'s chunks that may hold something and aren't loaded yet, with whether each has a save.</summary>
    private List<(ChunkPosition Pos, bool FromSave)> Missing(int x, int z)
    {
        var missing = new List<(ChunkPosition, bool)>();
        for (ulong bits = MaybeContent(x, z); bits != 0; bits &= bits - 1)
        {
            var p = new ChunkPosition(x, _minY + BitOperations.TrailingZeroCount(bits), z);
            if (!_staticVolume.IsLoaded(p)) missing.Add((p, _saved.Contains(p)));
        }
        return missing;
    }

    /// <summary>Eases <see cref="FogDistance"/> toward the nearest column still queued or loading, or else the view
    /// distance (everything nearer is loaded): in fast, so a gap is covered before it shows, out slowly, so the view
    /// opens up gently as loading catches up.</summary>
    private void UpdateFog(Vector3D<float> camPos, float dt)
    {
        // While a rebuild's scan is still going, the nearest column it found missing is known only up to where it has
        // looked: until it finds one, or finishes, the target stays where it was (it only moved by a column).
        float target = _queueHead < _queue.Count ? ColumnDistance(camPos, _queue[_queueHead].x, _queue[_queueHead].z)
                     : !_scanDone ? _fogTarget
                     : _viewDistance;
        foreach (var (x, z) in _inFlight.Keys) target = MathF.Min(target, ColumnDistance(camPos, x, z));
        _fogTarget = target;

        float rate = target < _fogDistance ? 8f : 1f;
        _fogDistance += (target - _fogDistance) * (1f - MathF.Exp(-rate * dt));
        SkySettings.SetFogDistance(_fogDistance);
    }

    /// <summary>Horizontal distance from the camera to the nearest point of chunk column (x, z).</summary>
    private static float ColumnDistance(Vector3D<float> cam, int x, int z)
    {
        float dx = MathF.Max(0f, MathF.Abs(cam.X - (x * S + S * 0.5f)) - S * 0.5f);
        float dz = MathF.Max(0f, MathF.Abs(cam.Z - (z * S + S * 0.5f)) - S * 0.5f);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public void Unload(ChunkPosition pos)
    {
        var entry = _staticVolume.GetEntry(pos);
        if (entry is not null)
        {
            SaveIfDirty(pos, entry);
        }
        _staticVolume.RemoveChunk(pos);
    }

    /// <summary>Writes every currently loaded chunk with unsaved edits to the chunk store. Called by autosave (inside its
    /// transaction) and on exit.</summary>
    public void SaveAllDirty()
    {
        foreach (var (pos, entry) in _staticVolume.All)
            SaveIfDirty(pos, entry);
    }

    private void SaveIfDirty(ChunkPosition pos, ChunkEntry entry)
    {
        if (!entry.Data.IsDirty) return;
        _chunkStore.Save(pos, entry.Data);
        entry.Data.IsDirty = false;
        // What's there now is what a reload finds, so an edit off the island's terrain (a bridge, a tower) comes back,
        // and one that cleared a chunk out stops costing budget.
        RecordSave(pos, entry.Data.HasAnySolid());
    }

}
