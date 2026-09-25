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
/// Streams the static world around the active camera: a fixed budget of chunks, chosen closest-first by horizontal
/// distance out to the view distance, a whole chunk column at a time — but spent only on chunks that may hold
/// something: the layers the generator says it may fill (<see cref="IWorldGenerator.ColumnLayers"/>) and chunks with a
/// save file (builds). Chunks that turn out to be air are remembered while they stay in range, and are neither loaded
/// nor counted again. The budget therefore goes to islands rather than sky, and reaches the islands out to the view
/// distance.
///
/// Candidates are the columns within the view distance. Where the budget runs out (or else the view
/// distance), and the nearest column still being generated or loaded, set the fog distance (see
/// <see cref="FogDistance"/>): an island only partly loaded fades out at the cut instead of ending in a hard edge.
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

    /// <summary>Chunks found to be air before the budget is re-picked to spend what they freed.</summary>
    private const int AirRebuildBatch = 256;

    /// <summary>Streamed layers below the lowest generated one, for building under the islands. Streaming covers 64
    /// layers (a column's bits); the rest are above.</summary>
    private const int LayersBelow = 8;

    private const int S = ChunkData.Size;

    private readonly string _savesDir;

    private readonly EntitySet      _cameras;
    private readonly ChunkVolume    _staticVolume;
    private readonly ThreadLocal<IWorldGenerator> _generator;
    private readonly ThreadLocal<ChunkData> _scratch = new(() => new ChunkData());
    private readonly int            _budget;
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

    /// <summary>The chunks the budget currently covers, loaded or not: layer bits per column.</summary>
    private readonly Dictionary<(int x, int z), ulong> _wanted = new();

    // Columns with wanted chunks still to generate or load, closest first; and the columns workers have now.
    private readonly Queue<(int x, int z)> _loadQueue = new();
    private readonly HashSet<(int x, int z)> _inFlight = new();
    private readonly ConcurrentQueue<((int x, int z) Column, List<(ChunkPosition Pos, ChunkData? Data)> Chunks)> _results = new();

    private (int x, int z) _lastCamColumn = (int.MinValue, int.MinValue);
    private int _airSinceRebuild;
    private bool _skippedInFlight;
    private float _autosaveTimer;

    // Results of the last rebuild: chunks counted, and the first column that didn't fit (the cut).
    private int _budgetUsed;
    private bool _hasCut;
    private (int x, int z) _cut;
    private int _columnsWalked;

    private float _fogDistance;
    private float _fogTarget;

    private readonly List<ChunkPosition> _toUnload = new();

    /// <summary>Horizontal distance from the camera at which the loaded world stops: the nearest chunk column that the
    /// budget cut off or that is still loading, or else the view distance, eased over time. Fog should be total by here.</summary>
    public float FogDistance => _fogDistance;

    /// <param name="viewDistance">How far out chunks are streamed, in blocks (horizontally), as far as the budget
    /// reaches. The GridStore's world index must fit it: see <see cref="WorldIndexDim"/>.</param>
    /// <param name="minChunkY">Lowest chunk layer the generator fills. Streaming covers 64 layers from
    /// <see cref="LayersBelow"/> under it; the generator's layers must fall inside them. Edits to the static world
    /// outside them are refused (see <see cref="ChunkVolume.EditableLayers"/>).</param>
    public ChunkLoadSystem(World world, ChunkVolume staticVolume, Func<IWorldGenerator> generatorFactory,
                           float viewDistance, int chunkBudget, int minChunkY)
    {
        _savesDir  = Path.Combine(AppContext.BaseDirectory, "Saves", "World");
        Directory.CreateDirectory(_savesDir);

        _cameras      = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _staticVolume = staticVolume;
        _generator    = new ThreadLocal<IWorldGenerator>(generatorFactory);
        _budget       = chunkBudget;
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
        ImGui.Text($"Budget: {_budgetUsed} / {_budget} chunks ({_columnsWalked} columns walked)");
        ImGui.Text($"Loaded: {_staticVolume.LoadedCount}   Queued columns: {_loadQueue.Count}   In flight: {_inFlight.Count}");
        ImGui.Text($"Fog distance: {_fogDistance:F0} (target {_fogTarget:F0})   Cut: {(_hasCut ? $"column {_cut.x},{_cut.z}" : "none")}");
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
            _inFlight.Remove(job.Column);
            foreach (var (pos, data) in job.Chunks)
            {
                if (data == null)
                {
                    if (_saved.Contains(pos)) RecordBuild(pos, hasBlocks: false); // a build that was emptied out
                    else if (_columns.TryGetValue((pos.X, pos.Z), out var col))
                        _columns[(pos.X, pos.Z)] = (col.Generated, col.Air | Bit(pos));
                    _airSinceRebuild++;
                    continue;
                }
                // Dropped if the budget moved on while it generated, or an edit created the chunk meanwhile.
                if (IsWanted(pos) && !_staticVolume.IsLoaded(pos)) _staticVolume.AddChunk(pos, data);
            }
        }

        if (!CameraUtil.TryGetActive(_cameras, out var cam)) return;
        var camPos = cam.Position;

        // Air found frees budget for columns further out, so re-pick once enough has turned up (or loading ran dry).
        var camColumn = ((int)MathF.Floor(camPos.X / S), (int)MathF.Floor(camPos.Z / S));
        bool idle = _loadQueue.Count == 0 && _inFlight.Count == 0;
        if (camColumn != _lastCamColumn || _airSinceRebuild >= AirRebuildBatch ||
            (idle && (_airSinceRebuild > 0 || _skippedInFlight)))
        {
            _lastCamColumn = camColumn;
            _airSinceRebuild = 0;
            _skippedInFlight = false;
            Rebuild(camPos);
        }

        Dispatch();
        UpdateFog(camPos, dt);
    }

    /// <summary>Re-picks the budgeted chunks: walks columns outward from the camera, counting every chunk that may
    /// hold something (see <see cref="MaybeContent"/>) until the next column doesn't fit; queues the columns with chunks
    /// still to fetch, and unloads what's no longer covered.</summary>
    private void Rebuild(Vector3D<float> camPos)
    {
        // An edit may have put blocks where there were none: it counts as a build from now on.
        foreach (var (p, entry) in _staticVolume.All)
            if (entry.Data.IsDirty) RecordBuild(p, hasBlocks: true);

        _wanted.Clear();
        _loadQueue.Clear();
        _budgetUsed = 0;
        _hasCut = false;
        _columnsWalked = 0;
        foreach (var (dx, dz) in _offsetsByDistance)
        {
            int x = _lastCamColumn.x + dx, z = _lastCamColumn.z + dz;
            _columnsWalked++;

            ulong bits = MaybeContent(x, z);
            int count = BitOperations.PopCount(bits);
            if (count == 0) continue;
            if (_budgetUsed + count > _budget)
            {
                _hasCut = true;
                _cut = (x, z);
                break;
            }
            _budgetUsed += count;
            _wanted[(x, z)] = bits;
            if (Missing(x, z).Any()) _loadQueue.Enqueue((x, z));
        }

        _toUnload.Clear();
        foreach (var (p, _) in _staticVolume.All)
            if (!IsWanted(p)) _toUnload.Add(p);
        foreach (var p in _toUnload) Unload(p);

        // Forget the columns out of view: their air is re-learned on return.
        long r2 = (long)_viewColumns * _viewColumns;
        foreach (var key in _columns.Keys.Where(k => Sq(k.x - _lastCamColumn.x) + Sq(k.z - _lastCamColumn.z) > r2).ToList())
            _columns.Remove(key);

        Console.WriteLine($"[load] rebuild: {_columnsWalked} columns walked, budget {_budgetUsed}/{_budget}, " +
                          $"cut {(_hasCut ? $"at {ColumnDistance(camPos, _cut.x, _cut.z):F0} blocks" : "none")}, " +
                          $"queued {_loadQueue.Count} columns, loaded {_staticVolume.LoadedCount}, unloaded {_toUnload.Count}");
    }

    private static long Sq(int v) => (long)v * v;

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

    /// <summary>Hands queued columns to workers, one job per column.</summary>
    private void Dispatch()
    {
        while (_inFlight.Count < MaxInFlight && _loadQueue.TryDequeue(out var col))
        {
            if (_inFlight.Contains(col)) { _skippedInFlight = true; continue; } // re-queued by the next rebuild
            var work = Missing(col.x, col.z).Select(p => (Pos: p, FromSave: _saved.Contains(p))).ToList();
            if (work.Count == 0) continue;

            _inFlight.Add(col);
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                var chunks = new List<(ChunkPosition, ChunkData?)>(work.Count);
                foreach (var (pos, fromSave) in work)
                {
                    var data = _scratch.Value!;
                    if (!(fromSave && StaticWorldSerializer.TryLoad(SavePath(pos), data)))
                        _generator.Value!.Generate(data, pos);
                    if (data.HasAnySolid())
                    {
                        data.IsDirty = false;
                        chunks.Add((pos, data));
                        _scratch.Value = new ChunkData();
                    }
                    else
                    {
                        chunks.Add((pos, null));
                        // Generation only ever writes blocks, so an empty result leaves the buffer all air and ready to
                        // reuse; a loaded save may have overwritten more than blocks, so start that one afresh.
                        if (fromSave) _scratch.Value = new ChunkData();
                    }
                }
                _results.Enqueue((col, chunks));
            }, null);
        }
    }

    /// <summary>Column (x, z)'s budgeted chunks that aren't loaded yet.</summary>
    private IEnumerable<ChunkPosition> Missing(int x, int z)
    {
        for (ulong bits = _wanted.GetValueOrDefault((x, z)); bits != 0; bits &= bits - 1)
        {
            var p = new ChunkPosition(x, _minY + BitOperations.TrailingZeroCount(bits), z);
            if (!_staticVolume.IsLoaded(p)) yield return p;
        }
    }

    private bool IsWanted(ChunkPosition p)
    {
        int bit = p.Y - _minY;
        return bit is >= 0 and < 64 && (_wanted.GetValueOrDefault((p.X, p.Z)) >> bit & 1) != 0;
    }

    /// <summary>Eases <see cref="FogDistance"/> toward the nearest column that is cut off or still missing, or else
    /// the view distance (everything nearer is loaded): in fast,
    /// so a gap is covered before it shows, out slowly, so the view opens up gently as loading catches up.</summary>
    private void UpdateFog(Vector3D<float> camPos, float dt)
    {
        float target = _hasCut  ? ColumnDistance(camPos, _cut.x, _cut.z)
                     : _viewDistance;
        if (_loadQueue.TryPeek(out var next)) target = MathF.Min(target, ColumnDistance(camPos, next.x, next.z));
        foreach (var (x, z) in _inFlight) target = MathF.Min(target, ColumnDistance(camPos, x, z));
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
