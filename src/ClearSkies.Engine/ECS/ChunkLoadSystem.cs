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
/// and loaded a whole column at a time. Only chunks that may hold something are queued: the layers the generator says
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
    /// sky that generation rules out in microseconds, so this is well above the core count: the thread pool queues
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
    private readonly Dictionary<(int x, int z), int> _inFlight = new();
    private int _inFlightChunks;
    private readonly ConcurrentQueue<((int x, int z) Column, List<(ChunkPosition Pos, ChunkData? Data)> Chunks)> _results = new();

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

    // Columns waiting for saved chunks the store doesn't have yet (a client's, fetched from the host), and chunks
    // edited while their column was loading, whose loaded data is stale.
    private readonly List<(int x, int z)> _waiting = new();
    private readonly HashSet<ChunkPosition> _stale = new();

    // Colliders-only streaming: columns wanted by colliders-only terrain interests (ships this machine simulates, away
    // from its own view) that aren't in view. Their chunks are loaded as data for colliders, never drawn or uploaded,
    // and don't count against the light budget.
    private readonly HashSet<(int x, int z)> _colliderColumns = new();
    private int _colliderRefresh;
    private const int MaxColliderColumnJobs = 8;

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

    }

    public void Update(float dt)
    {

        while (_results.TryDequeue(out var job))
        {
            _inFlightChunks -= _inFlight[job.Column];
            _inFlight.Remove(job.Column);
            foreach (var (pos, data) in job.Chunks)
            {
                if (data == null)
                {
                    if (_saved.Contains(pos)) RecordBuild(pos, hasBlocks: false); // a build that was emptied out
                    else if (_columns.TryGetValue((pos.X, pos.Z), out var col))
                        _columns[(pos.X, pos.Z)] = (col.Generated, col.Air | Bit(pos));
                    continue;
                }
                // Dropped if the camera moved on while it generated, or an edit created the chunk meanwhile, or it was
                // edited while loading (it's loaded again, edit and all).
                if (_stale.Remove(pos)) { if (!_waiting.Contains(job.Column)) _waiting.Add(job.Column); continue; }
                if (Wanted(pos.X, pos.Z) && !_staticVolume.IsLoaded(pos)) _staticVolume.AddChunk(pos, data);
            }
        }

        if (!TryGetViewCentre(out var camPos)) return;

        var camColumn = ((int)MathF.Floor(camPos.X / S), (int)MathF.Floor(camPos.Z / S));
        bool idle = _queueHead == _queue.Count && _inFlight.Count == 0;
        if (camColumn != _lastCamColumn || (idle && (_skippedInFlight || _queueTruncated)))
        {
            _lastCamColumn = camColumn;
            _skippedInFlight = false;
            Rebuild();
        }

        Dispatch();
        StreamColliderColumns();
        UpdateFog(camPos, dt);
    }

    /// <summary>Unloads what left the view distance, and re-queues the columns in view with chunks still to fetch,
    /// closest first.</summary>
    private void Rebuild()
    {
        // An edit may have put blocks where there were none: it counts as a build from now on.
        foreach (var (p, entry) in _staticVolume.All)
            if (entry.Data.IsDirty) RecordBuild(p, hasBlocks: true);

        _toUnload.Clear();
        foreach (var (p, _) in _staticVolume.All)
            if (!Wanted(p.X, p.Z)) _toUnload.Add(p);
        foreach (var p in _toUnload) Unload(p);

        // Forget the columns out of view: their air is re-learned on return.
        foreach (var key in _columns.Keys.Where(k => !Wanted(k.x, k.z)).ToList())
            _columns.Remove(key);

        _queue.Clear();
        _queueHead = 0;
        _queueTruncated = false;
        _nothingToEvict = false;
        _evictNext = -1;
        foreach (var (dx, dz) in _offsetsByDistance)
        {
            int x = _lastCamColumn.x + dx, z = _lastCamColumn.z + dz;
            if (!Missing(x, z).Any()) continue;
            if (_queue.Count == MaxQueued) { _queueTruncated = true; break; }
            _queue.Add((x, z));
        }

        Console.WriteLine($"[load] rebuild: queued {_queue.Count}{(_queueTruncated ? "+" : "")} columns, loaded " +
                          $"{_staticVolume.LoadedCount} chunks ({PendingChunks()} not uploaded), light {WorldBricks()}/" +
                          $"{_store.WorldLightBudget} bricks, unloaded {_toUnload.Count}, evicted {_evictions} columns so far");
    }

    private bool InView(int x, int z) => Sq(x - _lastCamColumn.x) + Sq(z - _lastCamColumn.z) <= Sq(_viewColumns);

    /// <summary>In view, or wanted for colliders.</summary>
    private bool Wanted(int x, int z) => InView(x, z) || _colliderColumns.Contains((x, z));

    /// <summary>Loads (as data only) the columns colliders-only interests want that aren't in view, and unloads the ones
    /// no longer wanted. The wanted set is refreshed every 30 frames.</summary>
    private void StreamColliderColumns()
    {
        if (++_colliderRefresh >= 30)
        {
            _colliderRefresh = 0;
            var wanted = new HashSet<(int x, int z)>();
            foreach (ref readonly Entity e in _interests.GetEntities())
            {
                ref readonly var interest = ref e.Get<TerrainInterest>();
                if (interest.Kind != TerrainInterestKind.CollidersOnly) continue;
                var p = e.Get<Transform>().Position;
                int cx = (int)MathF.Floor(p.X / S), cz = (int)MathF.Floor(p.Z / S), r = (int)MathF.Ceiling(interest.Radius / S);
                for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dz * dz <= r * r && !InView(cx + dx, cz + dz)) wanted.Add((cx + dx, cz + dz));
            }
            var dropped = _colliderColumns.Where(c => !wanted.Contains(c)).ToList();
            _colliderColumns.Clear();
            _colliderColumns.UnionWith(wanted);
            foreach (var (x, z) in dropped)
            {
                if (InView(x, z)) continue;
                for (int layer = 0; layer < 64; layer++)
                {
                    var p = new ChunkPosition(x, _minY + layer, z);
                    if (_staticVolume.IsLoaded(p)) Unload(p);
                }
                _columns.Remove((x, z));
            }
        }

        int jobs = 0;
        foreach (var col in _colliderColumns)
        {
            if (jobs >= MaxColliderColumnJobs || _inFlight.Count >= MaxInFlight) break;
            if (_inFlight.ContainsKey(col)) continue;
            var work = Missing(col.x, col.z).Select(p => (Pos: p, FromSave: _saved.Contains(p))).ToList();
            if (work.Count == 0) continue;
            if (work.Any(w => w.FromSave && !_chunkStore.IsReady(w.Pos)))
            {
                foreach (var w in work) if (w.FromSave && !_chunkStore.IsReady(w.Pos)) _chunkStore.Request(w.Pos);
                continue;
            }
            DispatchColumn(col, work);
            jobs++;
        }
    }

    /// <summary>Chunks loaded for colliders only: out of view, never uploaded.</summary>
    private int ColliderOnlyChunks()
    {
        int n = 0;
        foreach (var (x, z) in _colliderColumns)
            for (int layer = 0; layer < 64; layer++)
                if (_staticVolume.IsLoaded(new ChunkPosition(x, _minY + layer, z))) n++;
        return n;
    }

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

    /// <summary>Light bricks the world's chunks hold in the GPU store.</summary>
    private int WorldBricks() => _staticVolume.Gpu.Slots.Count;

    /// <summary>Chunks loaded but not uploaded to the GPU store yet.</summary>
    private int PendingChunks() => System.Math.Max(0, _staticVolume.LoadedCount - ColliderOnlyChunks() - _store.WorldChunkCount);

    /// <summary>Hands queued columns to workers, one job per column, while the budget has room. When it doesn't, and
    /// the next column is nearer than the farthest loaded one, unloads that to make room.</summary>
    private void Dispatch()
    {
        _full = false;

        // Columns whose saved chunks have arrived go next.
        for (int i = _waiting.Count - 1; i >= 0; i--)
        {
            var col = _waiting[i];
            if (!InView(col.x, col.z)) { _waiting.RemoveAt(i); continue; }
            if (Missing(col.x, col.z).Any(p => _saved.Contains(p) && !_chunkStore.IsReady(p))) continue;
            _waiting.RemoveAt(i);
            _queue.Insert(_queueHead, col);
        }

        while (_inFlight.Count < MaxInFlight && _queueHead < _queue.Count)
        {
            var col = _queue[_queueHead];
            if (_inFlight.ContainsKey(col)) { _skippedInFlight = true; _queueHead++; continue; } // re-queued by the next rebuild
            var work = Missing(col.x, col.z).Select(p => (Pos: p, FromSave: _saved.Contains(p))).ToList();
            if (work.Count == 0) { _queueHead++; continue; }
            if (work.Any(w => w.FromSave && !_chunkStore.IsReady(w.Pos)))
            {
                // Saved chunks not here yet: ask, and come back to the column once they are.
                foreach (var w in work) if (w.FromSave && !_chunkStore.IsReady(w.Pos)) _chunkStore.Request(w.Pos);
                if (!_waiting.Contains(col)) _waiting.Add(col);
                _queueHead++;
                continue;
            }

            int chunks = _staticVolume.LoadedCount + _inFlightChunks + work.Count;
            int bricks = WorldBricks() + (PendingChunks() + _inFlightChunks + work.Count) * PendingBricks;
            if (chunks > MaxChunks || bricks > _store.WorldLightBudget)
            {
                _full = true;
                // Only once the store really is full: until uploads catch up, pending chunks are just estimates.
                int overChunks = _staticVolume.LoadedCount + _inFlightChunks + work.Count - MaxChunks;
                int overBricks = WorldBricks() + (_inFlightChunks + work.Count) * PendingBricks - _store.WorldLightBudget;
                if (overChunks > 0 || overBricks >= 0)
                    EvictFartherThan(ColumnDistSq(col), overChunks + work.Count, overBricks + EvictSlack);
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
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            var loaded = new List<(ChunkPosition, ChunkData?)>(work.Count);
            foreach (var (pos, fromSave) in work)
            {
                var data = _scratch.Value!;
                if (!(fromSave && _chunkStore.TryLoad(pos, data)))
                    _generator.Value!.Generate(data, pos);
                data.Compact(); // stone inside an island, or sky, keeps one block instead of 64 KB
                if (data.HasAnySolid())
                {
                    data.IsDirty = false;
                    loaded.Add((pos, data));
                    _scratch.Value = new ChunkData();
                }
                else
                {
                    loaded.Add((pos, null));
                    // Generation only ever writes blocks, so an empty result leaves the buffer all air and ready to
                    // reuse; a loaded save may have overwritten more than blocks, so start that one afresh.
                    if (fromSave) _scratch.Value = new ChunkData();
                }
            }
            _results.Enqueue((col, loaded));
        }, null);
    }

    /// <summary>Unloads the farthest loaded columns (not being loaded) that are more than a column farther than
    /// <paramref name="distSq"/> (so two columns at about the same distance don't keep swapping), until
    /// <paramref name="chunks"/> chunks and <paramref name="bricks"/> light bricks are freed, or
    /// <see cref="MaxEvictChunksPerFrame"/> chunks. The store frees the bricks when it next runs, so the next frame
    /// sees the room.</summary>
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
        foreach (var (p, _) in _staticVolume.All)
            if (!_colliderColumns.Contains((p.X, p.Z))) columns.Add((p.X, p.Z)); // colliders-only columns use no budget
        _evictOrder.Clear();
        _evictOrder.AddRange(columns);
        _evictOrder.Sort((a, b) => ColumnDistSq(b).CompareTo(ColumnDistSq(a)));
        _evictNext = 0;
    }

    /// <summary>Light bricks chunk <paramref name="p"/> holds in the GPU store (none until it is uploaded).</summary>
    private int ChunkBricks(ChunkPosition p)
    {
        if (!_staticVolume.Gpu.Chunks.TryGetValue(p, out var rec) || rec.BrickSlots == null) return 0;
        int n = 0;
        foreach (int slot in rec.BrickSlots) if (slot >= 0) n++;
        return n;
    }

    /// <summary>Column (x, z)'s chunks that may hold something and aren't loaded yet.</summary>
    /// <summary>Every chunk that has been built on (edited since generation): what a joining client must fetch from the
    /// host rather than generate.</summary>
    public IEnumerable<ChunkPosition> EditedChunks
    {
        get
        {
            var set = new HashSet<ChunkPosition>(_saved);
            foreach (var (p, entry) in _staticVolume.All) if (entry.Data.IsDirty) set.Add(p);
            return set;
        }
    }

    /// <summary>A chunk was edited while not loaded here: it's a build from now on, and if its column is loading, what
    /// loads is stale and is loaded again.</summary>
    public void MarkEdited(ChunkPosition pos)
    {
        RecordSave(pos, hasBlocks: true);
        if (_inFlight.ContainsKey((pos.X, pos.Z))) _stale.Add(pos);
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
            if (!Wanted(x, z) || _inFlight.ContainsKey((x, z)) || Missing(x, z).Any()) return false;
        }
        return true;
    }

    private IEnumerable<ChunkPosition> Missing(int x, int z)
    {
        for (ulong bits = MaybeContent(x, z); bits != 0; bits &= bits - 1)
        {
            var p = new ChunkPosition(x, _minY + BitOperations.TrailingZeroCount(bits), z);
            if (!_staticVolume.IsLoaded(p)) yield return p;
        }
    }

    /// <summary>Eases <see cref="FogDistance"/> toward the nearest column still queued or loading, or else the view
    /// distance (everything nearer is loaded): in fast, so a gap is covered before it shows, out slowly, so the view
    /// opens up gently as loading catches up.</summary>
    private void UpdateFog(Vector3D<float> camPos, float dt)
    {
        float target = _queueHead < _queue.Count ? ColumnDistance(camPos, _queue[_queueHead].x, _queue[_queueHead].z)
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
