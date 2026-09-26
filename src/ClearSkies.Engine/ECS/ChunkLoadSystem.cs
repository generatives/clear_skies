using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Generation;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;
using System.Collections.Concurrent;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Streams the static world around the active camera, like a typical block game: a queue of the chunk columns within
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

    /// <summary>Seconds between periodic flushes of dirty chunks — crash/power-loss safety for edits to
    /// chunks that stay loaded (never unload) for a long time.</summary>
    private const float AutosaveInterval = 30f;

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

    private const int S = ChunkData.Size;

    private readonly string _savesDir;

    private readonly EntitySet      _cameras;
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
    private bool _full;
    private int _evictions;
    private float _autosaveTimer;

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
    /// <param name="worldName">The saves folder: one per generator, since edits to one's terrain don't belong in
    /// another's.</param>
    public ChunkLoadSystem(World world, ChunkVolume staticVolume, GridStore store, Func<IWorldGenerator> generatorFactory,
                           float viewDistance, int minChunkY, string worldName)
    {
        _savesDir  = Path.Combine(AppContext.BaseDirectory, "Saves", worldName);
        Directory.CreateDirectory(_savesDir);

        _cameras      = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
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
        foreach (var path in Directory.EnumerateFiles(_savesDir, "*.chunk"))
        {
            var parts = Path.GetFileNameWithoutExtension(path).Split('_');
            if (parts.Length == 3 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y) &&
                int.TryParse(parts[2], out int z))
                RecordSave(new ChunkPosition(x, y, z), hasBlocks: true);
        }
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
        ImGui.Text($"Autosave in: {System.Math.Max(0f, AutosaveInterval - _autosaveTimer):F0}s");
    }

    public void Update(float dt)
    {
        // Crash/power-loss safety: flush dirty chunks on a fixed cadence regardless of camera/streaming
        // state, so edits to a chunk that never unloads aren't only ever saved on graceful exit.
        _autosaveTimer += dt;
        if (_autosaveTimer >= AutosaveInterval)
        {
            _autosaveTimer = 0f;
            SaveAllDirty();
        }

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
                // Dropped if the camera moved on while it generated, or an edit created the chunk meanwhile.
                if (InView(pos.X, pos.Z) && !_staticVolume.IsLoaded(pos)) _staticVolume.AddChunk(pos, data);
            }
        }

        if (!CameraUtil.TryGetActive(_cameras, out var cam)) return;
        var camPos = cam.Position;

        var camColumn = ((int)MathF.Floor(camPos.X / S), (int)MathF.Floor(camPos.Z / S));
        bool idle = _queueHead == _queue.Count && _inFlight.Count == 0;
        if (camColumn != _lastCamColumn || (idle && (_skippedInFlight || _queueTruncated)))
        {
            _lastCamColumn = camColumn;
            _skippedInFlight = false;
            Rebuild();
        }

        Dispatch();
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
            if (!InView(p.X, p.Z)) _toUnload.Add(p);
        foreach (var p in _toUnload) Unload(p);

        // Forget the columns out of view: their air is re-learned on return.
        foreach (var key in _columns.Keys.Where(k => !InView(k.x, k.z)).ToList())
            _columns.Remove(key);

        _queue.Clear();
        _queueHead = 0;
        _queueTruncated = false;
        _nothingToEvict = false;
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
    private int PendingChunks() => System.Math.Max(0, _staticVolume.LoadedCount - _store.WorldChunkCount);

    /// <summary>Hands queued columns to workers, one job per column, while the budget has room. When it doesn't, and
    /// the next column is nearer than the farthest loaded one, unloads that to make room.</summary>
    private void Dispatch()
    {
        _full = false;
        while (_inFlight.Count < MaxInFlight && _queueHead < _queue.Count)
        {
            var col = _queue[_queueHead];
            if (_inFlight.ContainsKey(col)) { _skippedInFlight = true; _queueHead++; continue; } // re-queued by the next rebuild
            var work = Missing(col.x, col.z).Select(p => (Pos: p, FromSave: _saved.Contains(p))).ToList();
            if (work.Count == 0) { _queueHead++; continue; }

            int chunks = _staticVolume.LoadedCount + _inFlightChunks + work.Count;
            int bricks = WorldBricks() + (PendingChunks() + _inFlightChunks + work.Count) * PendingBricks;
            if (chunks > MaxChunks || bricks > _store.WorldLightBudget)
            {
                _full = true;
                // Only once the store really is full: until uploads catch up, pending chunks are just estimates.
                if (_staticVolume.LoadedCount + _inFlightChunks + work.Count > MaxChunks ||
                    WorldBricks() + (_inFlightChunks + work.Count) * PendingBricks >= _store.WorldLightBudget)
                    EvictFartherThan(ColumnDistSq(col));
                break;
            }

            _queueHead++;
            _inFlight.Add(col, work.Count);
            _inFlightChunks += work.Count;
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                var loaded = new List<(ChunkPosition, ChunkData?)>(work.Count);
                foreach (var (pos, fromSave) in work)
                {
                    var data = _scratch.Value!;
                    if (!(fromSave && StaticWorldSerializer.TryLoad(SavePath(pos), data)))
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
    }

    /// <summary>Unloads the farthest loaded column (not being loaded), if it is more than a column farther than
    /// <paramref name="distSq"/> (so two columns at about the same distance don't keep swapping). One per frame: the
    /// store frees its bricks when it next runs.</summary>
    private void EvictFartherThan(long distSq)
    {
        if (_nothingToEvict) return;
        (int x, int z) far = default;
        long farDistSq = -1;
        foreach (var (p, _) in _staticVolume.All)
        {
            var c = (p.X, p.Z);
            long d = ColumnDistSq(c);
            if (d > farDistSq && !_inFlight.ContainsKey(c)) { farDistSq = d; far = c; }
        }
        float margin = MathF.Sqrt(distSq) + 1f;
        if (farDistSq < 0 || farDistSq <= margin * margin)
        {
            _nothingToEvict = true;
            return;
        }
        for (int layer = 0; layer < 64; layer++)
        {
            var p = new ChunkPosition(far.x, _minY + layer, far.z);
            if (_staticVolume.IsLoaded(p)) Unload(p);
        }
        _evictions++;
    }

    /// <summary>Column (x, z)'s chunks that may hold something and aren't loaded yet.</summary>
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

    /// <summary>Writes every currently loaded chunk with unsaved edits to disk. Called by the periodic autosave and
    /// once on graceful shutdown.</summary>
    public void SaveAllDirty()
    {
        foreach (var (pos, entry) in _staticVolume.All)
            SaveIfDirty(pos, entry);
    }

    private void SaveIfDirty(ChunkPosition pos, ChunkEntry entry)
    {
        if (!entry.Data.IsDirty) return;
        StaticWorldSerializer.Save(entry.Data, SavePath(pos));
        entry.Data.IsDirty = false;
        // What's there now is what a reload finds, so an edit off the island's terrain (a bridge, a tower) comes back,
        // and one that cleared a chunk out stops costing budget.
        RecordSave(pos, entry.Data.HasAnySolid());
    }

    private string SavePath(ChunkPosition pos) => Path.Combine(_savesDir, $"{pos.X}_{pos.Y}_{pos.Z}.chunk");
}
