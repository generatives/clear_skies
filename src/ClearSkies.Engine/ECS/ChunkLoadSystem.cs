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
/// Streams the static world around terrain interests (see <see cref="TerrainInterest"/>), like a typical block game: a
/// queue of the chunk columns within each interest's load radius, closest to any interest first by horizontal distance,
/// rebuilt whenever an interest crosses into another column (or one comes or goes), and loaded a whole column at a time.
/// Every interest is streamed the same way: the local player's view is one with a large draw radius, and a body
/// simulated here away from the view (another player's character on the host, a ship) one with only a collider radius. A rebuild is spread over frames (within <see cref="StreamBudgetMs"/>, for
/// unloading what's no longer wanted and for scanning the wanted columns), so crossing a column doesn't cost one long frame.
/// Only chunks that may hold something are queued: the layers the generator says
/// it may fill (<see cref="IWorldGenerator.ColumnLayers"/>) and chunks with a save file (builds). The generator's
/// layers are a loose bound, so chunks that turn out to be air are remembered (a bit per column) until their column
/// is no longer wanted, and aren't queued again.
///
/// What's loaded is limited by a budget (<see cref="IChunkBudget"/>: GPU light storage for a drawn world). When the
/// budget is full, the queue stops; and if the next queued column is nearer (to its nearest interest) than the farthest
/// loaded one, the farthest is unloaded to make room. So the loaded world is always what's nearest an interest that fits.
///
/// What's on its way is in the ECS for other systems (the fog, see <see cref="FogSystem"/>): each column queued or
/// loading is an entity with <see cref="TerrainColumnLoading"/> until its chunks are added, and each interest gets
/// <see cref="TerrainScanned"/>, how far around it every wanted column is known (queued, loading or loaded).
/// </summary>
public sealed class ChunkLoadSystem : ISystem, IDebugUiSystem
{
    /// <summary>Column jobs (loading or generating a column's missing chunks) in flight at once. Most of a first visit is
    /// sky that generation rules out in microseconds, so this is well above the core count: the background workers queue
    /// the excess.</summary>
    private const int MaxInFlight = 64;

    /// <summary>Columns queued at once. Far more than load before the centre next moves a column; when the queue runs
    /// out short of the view distance it is rebuilt from there.</summary>
    private const int MaxQueued = 4096;

    /// <summary>Streamed layers below the lowest generated one, for building under the islands. Streaming covers 64
    /// layers (a column's bits); the rest are above.</summary>
    private const int LayersBelow = 8;

    /// <summary>Chunks unloaded at most per frame to make room: enough to keep up with flying, few enough that
    /// unloading doesn't stall a frame.</summary>
    private const int MaxEvictChunksPerFrame = 512;

    /// <summary>Main-thread time eviction may take per frame (ms): unloading a chunk (saving it, disposing its entity,
    /// releasing its storage) isn't free, and a full batch of chunks in one frame took ~14 ms. It stops after the column
    /// that runs over, and carries on next frame; loading waits for the room meanwhile, which the fog covers.</summary>
    private const double MaxEvictMsPerFrame = 2.0;

    private const int S = ChunkData.Size;

    private readonly IChunkStore _chunkStore;

    private readonly EntitySet      _interests;
    private readonly ChunkVolume    _staticVolume;
    private readonly IChunkBudget   _loadBudget;
    private readonly IChunkPreparer? _preparer;
    private readonly ThreadLocal<IWorldGenerator> _generator;
    private readonly ThreadLocal<ChunkData> _scratch = new(() => new ChunkData());
    private readonly int            _minY;      // the lowest streamed layer: bit 0 of a column's bits

    /// <summary>Column offsets from an interest's column, closest first, with their distance squared, by radius in
    /// columns. Computed once per radius; a rebuild just walks them.</summary>
    private readonly Dictionary<int, (short dx, short dz, int d)[]> _offsetsByDistance = new();
    private readonly float _viewDistance; // the farthest anything is drawn

    /// <summary>An interest as streaming sees it: its entity, the column it's streamed around, and its load radius in
    /// columns.</summary>
    private readonly record struct Ring(Entity Interest, (int x, int z) Column, int LoadColumns);

    private readonly List<Ring> _rings = new();   // as of the last rebuild
    private readonly List<Ring> _current = new(); // this frame's
    private readonly Dictionary<Entity, (int x, int z)> _interestColumns = new();
    private readonly HashSet<Entity> _seen = new();
    private readonly World _world;

    /// <summary>The entity standing for each column queued or loading (see <see cref="TerrainColumnLoading"/>).</summary>
    private readonly Dictionary<(int x, int z), Entity> _loading = new();

    /// <summary>Per wanted column (layer bits): what the generator may fill, and what turned out to be air. Dropped
    /// once the column is no longer wanted, so returning re-learns its air.</summary>
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

    // The rebuild's scan of the wanted columns: per ring, its offsets and the next one to look at; and whether it has
    // looked at them all (or filled the queue). Columns nearer an interest than the scan's position are all queued,
    // loading or loaded.
    private (short dx, short dz, int d)[][] _scanOffsets = Array.Empty<(short, short, int)[]>();
    private int[] _scanAt = Array.Empty<int>();
    private bool _scanDone = true;

    // Chunks no longer wanted at the last rebuild, unloaded from _unloadAt on a few per frame.
    private int _unloadAt;
    private readonly List<(int x, int z)> _columnsUnwanted = new();
    private readonly Dictionary<(int x, int z), int> _inFlight = new();
    private int _inFlightChunks;
    private readonly ConcurrentQueue<((int x, int z) Column, List<(ChunkPosition Pos, ChunkData? Data, ChunkPreparation? Prepared)> Chunks)> _results = new();

    private bool _skippedInFlight;
    private bool _nothingToEvict; // the last search found nothing farther than the queue's head; cleared by a rebuild

    /// <summary>Loaded columns, farthest first, as of the first eviction since the last rebuild; <see cref="_evictNext"/>
    /// is the next to consider. Columns loaded since then are nearer (the queue is closest first), so the order holds.</summary>
    private readonly List<(int x, int z)> _evictOrder = new();
    private readonly HashSet<(int x, int z)> _evictColumns = new();
    private long[] _evictKeys = Array.Empty<long>();
    private int _evictNext = -1; // -1: not built since the last rebuild
    private bool _full;
    private int _evictions;


    private readonly List<ChunkPosition> _toUnload = new();

    /// <param name="budget">What limits what's loaded.</param>
    /// <param name="viewDistance">The farthest out chunks are streamed to be drawn, in blocks (horizontally): an interest's
    /// draw radius is capped at it (the GPU store's world index is sized for it).</param>
    /// <param name="minChunkY">Lowest chunk layer the generator fills. Streaming covers 64 layers from
    /// <see cref="LayersBelow"/> under it; the generator's layers must fall inside them. Edits to the static world
    /// outside them are refused (see <see cref="ChunkVolume.EditableLayers"/>).</param>
    /// <param name="chunkStore">Where edited chunks are kept (the world's save database on the host).</param>
    /// <param name="preparer">Work on each loaded chunk's data on the worker that loaded it (packing it for the GPU, for
    /// a drawn world).</param>
    public ChunkLoadSystem(World world, ChunkVolume staticVolume, IChunkBudget budget, Func<IWorldGenerator> generatorFactory,
                           float viewDistance, int minChunkY, IChunkStore chunkStore, IChunkPreparer? preparer = null)
    {
        _chunkStore = chunkStore;
        _world = world;

        _interests    = world.GetEntities().With<Transform>().With<TerrainInterest>().AsSet();
        _staticVolume = staticVolume;
        _loadBudget   = budget;
        _preparer     = preparer;
        _generator    = new ThreadLocal<IWorldGenerator>(generatorFactory);
        _minY         = minChunkY - LayersBelow;
        _staticVolume.EditableLayers = (_minY, _minY + 63); // only what streaming can load back
        _viewDistance = viewDistance;
        ScanSaves();
    }

    /// <summary>Column offsets within <paramref name="radius"/> columns, closest first.</summary>
    private (short dx, short dz, int d)[] OffsetsByDistance(int radius)
    {
        if (_offsetsByDistance.TryGetValue(radius, out var cached)) return cached;
        var offsets = new List<(short dx, short dz, int d)>();
        for (int dz = -radius; dz <= radius; dz++)
        for (int dx = -radius; dx <= radius; dx++)
            if (dx * dx + dz * dz <= radius * radius) offsets.Add(((short)dx, (short)dz, dx * dx + dz * dz));
        offsets.Sort((a, b) => a.d.CompareTo(b.d));
        return _offsetsByDistance[radius] = offsets.ToArray();
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
        ImGui.Text($"Interests: {_rings.Count}   Draw limit: {_viewDistance:F0} blocks ({_columns.Count} columns known)");
        ImGui.Text($"Budget: {_loadBudget.Describe(_staticVolume)}" + (_full ? " (full)" : ""));
        ImGui.Text($"Loaded: {_staticVolume.LoadedCount:N0} chunks   Queued columns: {_queue.Count - _queueHead}" +
                   $"{(_queueTruncated ? "+" : "")}   In flight: {_inFlight.Count}");
        ImGui.Text($"Columns evicted: {_evictions}");
        ImGui.Text($"Saved chunks: {_saved.Count}   Layers: {_minY}..{_minY + 63}");

        ImGui.Separator();
        _steps.Draw();
    }

    // CPU time of each step, for the debug panel: which one a hitch while streaming came from.
    private const int ApplyStep = 0, UnloadStep = 1, QueueStep = 2, DispatchStep = 3, EvictStep = 4, EvictOrderStep = 5;
    private readonly StepTimer _steps = new("Adding finished columns", "Rebuild: unloading what's unwanted",
                                            "Rebuild: queueing columns", "Dispatching jobs", "Evicting far columns",
                                            "Evicting: ordering columns") { Owner = "Chunk Loading" };

    public void Update(float dt)
    {
        _steps.Start();

        _budget.Restart();
        GatherRings();
        bool idle = _scanDone && _queueHead == _queue.Count && _inFlight.Count == 0;
        if (!_current.SequenceEqual(_rings) || (idle && (_skippedInFlight || _queueTruncated)))
        {
            _rings.Clear();
            _rings.AddRange(_current);
            _skippedInFlight = false;
            Rebuild();
        }
        UnloadSome(BudgetLeft);
        _steps.Lap(UnloadStep);

        double applyUntil = _steps.SinceLap() + BudgetLeft;
        while (_steps.SinceLap() < applyUntil && _results.TryDequeue(out var job))
        {
            _inFlightChunks -= _inFlight[job.Column];
            _inFlight.Remove(job.Column);
            foreach (var (pos, data, prepared) in job.Chunks)
            {
                if (data == null)
                {
                    if (_saved.Contains(pos)) RecordBuild(pos, hasBlocks: false); // a build that was emptied out
                    else if (_columns.TryGetValue((pos.X, pos.Z), out var col))
                        _columns[(pos.X, pos.Z)] = (col.Generated, col.Air | Bit(pos));
                    continue;
                }
                // Dropped if the interests moved on while it generated, or an edit created the chunk meanwhile.
                if (Wanted(pos.X, pos.Z) && !_staticVolume.IsLoaded(pos)) _staticVolume.AddChunk(pos, data, prepared);
            }
            Loaded(job.Column);
        }
        _steps.Lap(ApplyStep);

        ScanSome(BudgetLeft);
        _steps.Lap(QueueStep);

        Dispatch();
        _steps.Lap(DispatchStep);
    }

    /// <summary>This frame's interests as rings (<see cref="_current"/>, in a fixed order so a frame where none moved
    /// compares equal to the last rebuild's).</summary>
    private void GatherRings()
    {
        _current.Clear();
        _seen.Clear();
        foreach (ref readonly Entity e in _interests.GetEntities())
        {
            var interest = e.Get<TerrainInterest>();
            var position = e.Get<Transform>().Position;
            float draw = MathF.Min(interest.DrawRadius, _viewDistance);
            int loadColumns = (int)MathF.Ceiling(MathF.Max(interest.ColliderRadius, draw) / S);
            _current.Add(new Ring(e, InterestColumn(e, position), loadColumns));
            _seen.Add(e);
        }
        if (_interestColumns.Count > _seen.Count)
            foreach (var gone in _interestColumns.Keys.Where(k => !_seen.Contains(k)).ToList()) _interestColumns.Remove(gone);
        _current.Sort((a, b) => (a.Column.x, a.Column.z, a.LoadColumns, a.Interest.GetHashCode())
                                .CompareTo((b.Column.x, b.Column.z, b.LoadColumns, b.Interest.GetHashCode())));
    }

    /// <summary>How many times the queue has been rebuilt (the view moved, or loading caught up).</summary>
    public int Rebuilds { get; private set; }

    /// <summary>Chunks loaded, and column jobs still loading.</summary>
    public int LoadedChunks => _staticVolume.LoadedCount;
    public int ColumnsInFlight => _inFlight.Count;

    /// <summary>Starts over from the interests' new columns: lists what's no longer wanted to unload, and restarts the
    /// scan for wanted columns with chunks still to fetch, closest first. Both then go on a little each frame
    /// (<see cref="UnloadSome"/>, <see cref="ScanSome"/>).</summary>
    private void Rebuild()
    {
        Rebuilds++;
        // Whatever the last rebuild didn't get to unload goes now: a chunk that stays loaded out of view could share a
        // cell of the GPU store's world index with one coming into view on the other side (see LightBudget.WorldIndexDim).
        UnloadSome(double.PositiveInfinity);

        _toUnload.Clear();
        _unloadAt = 0;
        // (An edit that put blocks where there were none counts as a build once its chunk is saved: by the autosave,
        // or as it unloads. Until then the chunk is loaded, so the queue doesn't need to know.)
        foreach (var (p, _) in _staticVolume.All)
            if (!Wanted(p.X, p.Z)) _toUnload.Add(p);

        // Forget the columns no longer wanted: their air is re-learned on return.
        _columnsUnwanted.Clear();
        foreach (var key in _columns.Keys)
            if (!Wanted(key.x, key.z)) _columnsUnwanted.Add(key);
        foreach (var key in _columnsUnwanted) _columns.Remove(key);
        _columnsUnwanted.Clear();
        foreach (var key in _loading.Keys)
            if (!Wanted(key.x, key.z)) _columnsUnwanted.Add(key);
        foreach (var key in _columnsUnwanted) Loaded(key);
        _steps.Lap(UnloadStep);

        _queue.Clear();
        _queueHead = 0;
        _queueTruncated = false;
        _nothingToEvict = false;
        _evictNext = -1;
        _scanOffsets = _rings.Select(r => OffsetsByDistance(r.LoadColumns)).ToArray();
        _scanAt = new int[_rings.Count];
        _scanDone = false;
        PublishScanned();
        _steps.Lap(QueueStep);
    }

    /// <summary>Unloads chunks no longer wanted at the last rebuild, for up to <paramref name="budgetMs"/>.</summary>
    private void UnloadSome(double budgetMs)
    {
        double start = _steps.SinceLap();
        while (_unloadAt < _toUnload.Count && _steps.SinceLap() - start < budgetMs)
            Unload(_toUnload[_unloadAt++]); // a no-op if eviction already unloaded it
    }

    /// <summary>Carries on the rebuild's scan of the wanted columns, closest to an interest first, for up to
    /// <paramref name="budgetMs"/>, queueing each with chunks still to fetch. The rings are walked together: each step
    /// takes the ring whose next column is nearest its centre, and where rings overlap, a column is the nearest ring's.</summary>
    private void ScanSome(double budgetMs)
    {
        if (_scanDone) return;
        double start = _steps.SinceLap();
        while (true)
        {
            // Checked every column: one seen for the first time asks the generator for its layers, which isn't cheap.
            if (_steps.SinceLap() - start >= budgetMs) { PublishScanned(); return; }
            int next = 0;
            for (int i = 1; i < _scanOffsets.Length; i++)
                if (_scanAt[i] < _scanOffsets[i].Length &&
                    (_scanAt[next] >= _scanOffsets[next].Length || _scanOffsets[i][_scanAt[i]].d < _scanOffsets[next][_scanAt[next]].d))
                    next = i;
            if (next >= _scanOffsets.Length || _scanAt[next] >= _scanOffsets[next].Length) break;
            var (dx, dz, d) = _scanOffsets[next][_scanAt[next]++];
            int x = _rings[next].Column.x + dx, z = _rings[next].Column.z + dz;
            if (_rings.Count > 1 && !NearestRing(next, x, z, d)) continue; // another ring's
            if (HasMissing(x, z))
            {
                if (_queue.Count == MaxQueued) { _queueTruncated = true; _scanAt[next]--; break; }
                _queue.Add((x, z));
                Loading((x, z));
            }
        }
        _scanDone = true;
        PublishScanned();
        Console.WriteLine($"[load] rebuild: queued {_queue.Count}{(_queueTruncated ? "+" : "")} columns around {_rings.Count} " +
                          $"interests, loaded {_staticVolume.LoadedCount} chunks ({_loadBudget.Describe(_staticVolume)}), " +
                          $"unloaded {_toUnload.Count}, evicted {_evictions} columns so far");
    }

    /// <summary>Blocks an interest must be past its column's edge before it's streamed around the next, so standing on
    /// a column boundary doesn't unload and reload the edge of the view every wobble.</summary>
    private const float ViewHysteresis = 2f;

    /// <summary>The column an interest is streamed around: the one it's in, once it's clearly left the last one.</summary>
    private (int x, int z) InterestColumn(Entity e, Vector3D<float> position)
    {
        var column = ((int)MathF.Floor(position.X / S), (int)MathF.Floor(position.Z / S));
        if (_interestColumns.TryGetValue(e, out var last) && column != last)
        {
            float x0 = last.x * S, z0 = last.z * S;
            bool near = position.X >= x0 - ViewHysteresis && position.X < x0 + S + ViewHysteresis &&
                        position.Z >= z0 - ViewHysteresis && position.Z < z0 + S + ViewHysteresis;
            if (near) column = last;
        }
        return _interestColumns[e] = column;
    }

    /// <summary>Gives each interest <see cref="TerrainScanned"/>: how far around it the scan has looked, short of the
    /// first column it hasn't (or all of its load radius once it's looked at everything).</summary>
    private void PublishScanned()
    {
        for (int i = 0; i < _rings.Count; i++)
        {
            var e = _rings[i].Interest;
            if (!e.IsAlive) continue;
            var offsets = _scanOffsets[i];
            float radius = _scanAt[i] >= offsets.Length ? float.PositiveInfinity
                         : MathF.Max(0f, (MathF.Sqrt(offsets[_scanAt[i]].d) - 1f) * S);
            if (!e.Has<TerrainScanned>() || e.Get<TerrainScanned>().Radius != radius) e.Set(new TerrainScanned { Radius = radius });
        }
    }

    /// <summary>Column (x, z) is queued: it has an entity saying so (<see cref="TerrainColumnLoading"/>).</summary>
    private void Loading((int x, int z) col)
    {
        if (_loading.ContainsKey(col)) return;
        var e = _world.CreateEntity();
        e.Set(new TerrainColumnLoading { X = col.x, Z = col.z });
        _loading[col] = e;
    }

    /// <summary>Column (x, z) has loaded, or is no longer wanted, or had nothing to load: its entity goes.</summary>
    private void Loaded((int x, int z) col)
    {
        if (_loading.Remove(col, out var e) && e.IsAlive) e.Dispose();
    }

    /// <summary>Whether ring <paramref name="ring"/>, <paramref name="distSq"/> from column (x, z), is the nearest ring
    /// that wants it (the first, of rings as near), so it's scanned once.</summary>
    private bool NearestRing(int ring, int x, int z, long distSq)
    {
        for (int i = 0; i < _rings.Count; i++)
        {
            if (i == ring) continue;
            long d = Sq(x - _rings[i].Column.x) + Sq(z - _rings[i].Column.z);
            if (d <= Sq(_rings[i].LoadColumns) && (d < distSq || (d == distSq && i < ring))) return false;
        }
        return true;
    }

    /// <summary>Within an interest's load radius.</summary>
    private bool Wanted(int x, int z)
    {
        foreach (var ring in _rings)
            if (Sq(x - ring.Column.x) + Sq(z - ring.Column.z) <= Sq(ring.LoadColumns)) return true;
        return false;
    }

    private static long Sq(int v) => (long)v * v;

    /// <summary>Distance squared, in columns, from column <paramref name="c"/> to the nearest interest's.</summary>
    private long ColumnDistSq((int x, int z) c)
    {
        long best = long.MaxValue;
        foreach (var ring in _rings) best = System.Math.Min(best, Sq(c.x - ring.Column.x) + Sq(c.z - ring.Column.z));
        return best;
    }

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
            if (work.Count == 0) { _queueHead++; Loaded(col); continue; }

            int adding = _inFlightChunks + work.Count;
            if (!_loadBudget.HasRoomFor(_staticVolume, adding))
            {
                _full = true;
                var shortfall = _loadBudget.ShortfallFor(_staticVolume, adding);
                if (!shortfall.IsNone)
                {
                    _steps.Lap(DispatchStep);
                    EvictFartherThan(ColumnDistSq(col), shortfall.Chunks + work.Count, shortfall.Units);
                    _steps.Lap(EvictStep);
                }
                break;
            }

            _queueHead++;
            DispatchColumn(col, work);
        }
    }

    /// <summary>Hands a column's missing chunks to a worker: each loaded from the store if saved, else generated.</summary>
    private void DispatchColumn((int x, int z) col, List<(ChunkPosition Pos, bool FromSave)> work)
    {
        _inFlight.Add(col, work.Count);
        _inFlightChunks += work.Count;
        BackgroundWork.Queue(() =>
        {
            var loaded = new List<(ChunkPosition, ChunkData?, ChunkPreparation?)>(work.Count);
            foreach (var (pos, fromSave) in work)
            {
                var data = _scratch.Value!;
                if (!(fromSave && _chunkStore.TryLoad(pos, data)))
                    _generator.Value!.Generate(data, pos);
                data.Compact(); // stone inside an island, or sky, keeps one block instead of 64 KB
                if (data.HasAnyNonAir())
                {
                    data.IsDirty = false;
                    loaded.Add((pos, data, _preparer?.Prepare(data))); // off the main thread
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

    /// <summary>Unloads the farthest loaded columns (not being loaded) that are more than a column farther than
    /// <paramref name="distSq"/> (so two columns at about the same distance don't keep swapping), until
    /// <paramref name="chunks"/> chunks and <paramref name="units"/> of the budget are freed, or
    /// <see cref="MaxEvictChunksPerFrame"/> chunks, or <see cref="MaxEvictMsPerFrame"/> of time.</summary>
    private void EvictFartherThan(long distSq, int chunks, int units)
    {
        if (_nothingToEvict) return;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_evictNext < 0) BuildEvictOrder();
        _steps.Lap(EvictOrderStep);

        float margin = MathF.Sqrt(distSq) + 1f;
        int freedChunks = 0, freedUnits = 0;
        while (freedChunks < MaxEvictChunksPerFrame && (freedChunks < chunks || freedUnits < units))
        {
            if (_evictNext >= _evictOrder.Count || ColumnDistSq(_evictOrder[_evictNext]) <= margin * margin)
            {
                _nothingToEvict = true;
                return;
            }
            var far = _evictOrder[_evictNext];
            if (_inFlight.ContainsKey(far)) { _evictNext++; continue; }
            // The time budget is checked per chunk: a column cut short stays next, and its unloaded chunks are
            // skipped when it comes up again.
            int unloaded = 0;
            for (int layer = 0; layer < 64; layer++)
            {
                var p = new ChunkPosition(far.x, _minY + layer, far.z);
                if (!_staticVolume.IsLoaded(p)) continue;
                if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds >= MaxEvictMsPerFrame)
                {
                    if (unloaded > 0) _evictions++;
                    return;
                }
                freedUnits += _loadBudget.CostOf(_staticVolume, p);
                Unload(p);
                unloaded++;
                freedChunks++;
            }
            _evictNext++;
            if (unloaded > 0) _evictions++;
        }
    }

    private void BuildEvictOrder()
    {
        _evictColumns.Clear();
        foreach (var (p, _) in _staticVolume.All) _evictColumns.Add((p.X, p.Z));
        _evictOrder.Clear();
        _evictOrder.AddRange(_evictColumns);
        // Each column's distance once, farthest first (the comparison sort would compute it twice per comparison).
        if (_evictKeys.Length < _evictOrder.Count) _evictKeys = new long[_evictOrder.Count * 2];
        for (int i = 0; i < _evictOrder.Count; i++) _evictKeys[i] = -ColumnDistSq(_evictOrder[i]);
        _evictKeys.AsSpan(0, _evictOrder.Count).Sort(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_evictOrder));
        _evictNext = 0;
    }

    /// <summary>Whether column (x, z) has chunks that may hold something and aren't loaded yet.</summary>
    private bool HasMissing(int x, int z)
    {
        for (ulong bits = MaybeContent(x, z); bits != 0; bits &= bits - 1)
            if (!_staticVolume.IsLoaded(new ChunkPosition(x, _minY + BitOperations.TrailingZeroCount(bits), z))) return true;
        return false;
    }

    /// <summary>Whether the terrain within <paramref name="radius"/> (horizontally) of <paramref name="centre"/> has
    /// loaded: every column there is wanted, with nothing still to load or loading.</summary>
    public bool IsTerrainLoaded(Vector3D<float> centre, float radius)
    {
        int cx = (int)MathF.Floor(centre.X / S), cz = (int)MathF.Floor(centre.Z / S), r = (int)MathF.Ceiling(radius / S);
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            int x = cx + dx, z = cz + dz;
            if (!Wanted(x, z) || _inFlight.ContainsKey((x, z)) || HasMissing(x, z)) return false;
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

    public void Unload(ChunkPosition pos)
    {
        var entry = _staticVolume.GetEntry(pos);
        if (entry is not null)
        {
            SaveIfDirty(pos, entry);
        }
        _staticVolume.RemoveChunk(pos);
        entry?.Data.Release(); // its arrays go to the next chunk to load
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
        RecordSave(pos, entry.Data.HasAnyNonAir());
    }

}
