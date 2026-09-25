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
/// Streams the static world around the active camera: chunks chosen closest-first by horizontal distance out to the
/// view distance, a whole chunk column at a time, until a budget of GPU light storage is spent — but only chunks that
/// may hold something: the layers the generator says it may fill (<see cref="IWorldGenerator.ColumnLayers"/>) and
/// chunks with a save file (builds). Chunks that turn out to be air are remembered while they stay in range, and are
/// neither loaded nor counted again.
///
/// A chunk costs the light bricks it will need: its bricks holding air next to solid (see
/// <see cref="GridStore.SurfaceBricks"/>). That's known once it has been generated or loaded (and remembered while it
/// stays in view); until then it is <see cref="EstimatedBricks"/>. So solid stone inside an island costs next to
/// nothing, and surfaces — island tops, undersides, cave walls — cost what they will really use. The number of chunks
/// is capped too, at what the GPU's world index can address.
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

    /// <summary>Light bricks a chunk not yet generated is expected to cost. Most chunks the generator can only rule
    /// out by generating them are air or solid, so this is well under what a surface chunk costs (about 26).</summary>
    private const int EstimatedBricks = 8;

    /// <summary>World chunks loaded at most: the GPU's world index limit, less room for chunks leaving range whose
    /// storage is released a frame later.</summary>
    private const int MaxChunks = GridStore.MaxWorldChunks - 4096;

    /// <summary>Streamed layers below the lowest generated one, for building under the islands. Streaming covers 64
    /// layers (a column's bits); the rest are above.</summary>
    private const int LayersBelow = 8;

    private const int S = ChunkData.Size;

    private readonly string _savesDir;

    private readonly EntitySet      _cameras;
    private readonly ChunkVolume    _staticVolume;
    private readonly ThreadLocal<IWorldGenerator> _generator;
    private readonly ThreadLocal<ChunkData> _scratch = new(() => new ChunkData());
    private readonly int            _budget;    // light bricks
    private readonly int            _rebuildDrift;
    private readonly int            _minY;      // the lowest streamed layer: bit 0 of a column's bits

    /// <summary>Column offsets from the camera's column, closest first, out to the view distance. Computed once; a
    /// rebuild just walks it.</summary>
    private readonly (short dx, short dz)[] _offsetsByDistance;
    private readonly int _viewColumns; // the view distance in columns
    private readonly float _viewDistance;

    /// <summary>What is known about a column in view. Dropped once the column leaves view, so returning re-learns it.</summary>
    private sealed class Column
    {
        public ulong Generated; // layers the generator may fill
        public ulong Air;       // layers that turned out to be air
        public ulong Known;     // layers generated or loaded with blocks: their bricks (below) are known
        public (ulong Solid, ulong Air, int Cost)[]? Bricks; // per layer: brick masks and light cost
    }

    private readonly Dictionary<(int x, int z), Column> _columns = new();

    /// <summary>Every chunk with a save file (scanned once at startup, kept up to date by saves).</summary>
    private readonly HashSet<ChunkPosition> _saved = new();

    /// <summary>Per column (layer bits): chunks holding a build — a save with blocks, or an unsaved edit.</summary>
    private readonly Dictionary<(int x, int z), ulong> _built = new();

    /// <summary>The chunks the budget currently covers, loaded or not: layer bits per column.</summary>
    private readonly Dictionary<(int x, int z), ulong> _wanted = new();

    // Columns with wanted chunks still to generate or load, closest first; and the columns workers have now.
    private readonly Queue<(int x, int z)> _loadQueue = new();
    private readonly HashSet<(int x, int z)> _inFlight = new();
    private readonly ConcurrentQueue<((int x, int z) Column, List<(ChunkPosition Pos, ChunkData? Data, ulong Solid, ulong Air)> Chunks)> _results = new();

    private (int x, int z) _lastCamColumn = (int.MinValue, int.MinValue);
    private int _driftSinceRebuild; // bricks by which newly learned costs differed from their estimates
    private bool _skippedInFlight;
    private float _autosaveTimer;

    // Results of the last rebuild: bricks counted (and how many of them estimated), chunks, and the first column
    // that didn't fit (the cut).
    private int _budgetUsed, _budgetEstimated, _chunksWanted;
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
    /// <param name="lightBudget">Light bricks the loaded world may use: see <see cref="GridStore.WorldLightBudget"/>.</param>
    public ChunkLoadSystem(World world, ChunkVolume staticVolume, Func<IWorldGenerator> generatorFactory,
                           float viewDistance, int lightBudget, int minChunkY)
    {
        // World2: the island grids generate different terrain, so saves of edits to the old terrain (in World) don't
        // belong in it.
        _savesDir  = Path.Combine(AppContext.BaseDirectory, "Saves", "World2");
        Directory.CreateDirectory(_savesDir);

        _cameras      = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _staticVolume = staticVolume;
        _generator    = new ThreadLocal<IWorldGenerator>(generatorFactory);
        _budget       = lightBudget;
        _rebuildDrift = System.Math.Max(lightBudget / 20, 1024);
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
        ImGui.Text($"Budget: {_budgetUsed:N0} / {_budget:N0} light bricks ({_budgetEstimated:N0} estimated), " +
                   $"{_chunksWanted:N0} / {MaxChunks:N0} chunks ({_columnsWalked} columns walked)");
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
            foreach (var (pos, data, solid, air) in job.Chunks)
            {
                if (data == null)
                {
                    if (_saved.Contains(pos)) RecordBuild(pos, hasBlocks: false); // a build that was emptied out
                    else if (_columns.TryGetValue((pos.X, pos.Z), out var col)) col.Air |= Bit(pos);
                    _driftSinceRebuild += EstimatedBricks;
                    continue;
                }
                _driftSinceRebuild += System.Math.Abs(SetBricks(pos, solid, air) - EstimatedBricks);
                // Dropped if the budget moved on while it generated, or an edit created the chunk meanwhile.
                if (IsWanted(pos) && !_staticVolume.IsLoaded(pos)) _staticVolume.AddChunk(pos, data);
            }
        }

        if (!CameraUtil.TryGetActive(_cameras, out var cam)) return;
        var camPos = cam.Position;

        // Chunks cost other than estimated (air or buried stone frees budget for columns further out, surfaces take
        // more), so re-pick once enough has turned up (or loading ran dry).
        var camColumn = ((int)MathF.Floor(camPos.X / S), (int)MathF.Floor(camPos.Z / S));
        bool idle = _loadQueue.Count == 0 && _inFlight.Count == 0;
        if (camColumn != _lastCamColumn || _driftSinceRebuild >= _rebuildDrift ||
            (idle && (_driftSinceRebuild > 0 || _skippedInFlight)))
        {
            _lastCamColumn = camColumn;
            _driftSinceRebuild = 0;
            _skippedInFlight = false;
            Rebuild(camPos);
        }

        Dispatch();
        UpdateFog(camPos, dt);
    }

    /// <summary>Re-picks the budgeted chunks: walks columns outward from the camera, counting the light cost of every
    /// chunk that may hold something (see <see cref="MaybeContent"/>) until the next column doesn't fit; queues the
    /// columns with chunks still to fetch, and unloads what's no longer covered.</summary>
    private void Rebuild(Vector3D<float> camPos)
    {
        // An edit may have put blocks where there were none: it counts as a build from now on, and costs what it
        // costs now.
        foreach (var (p, entry) in _staticVolume.All)
        {
            if (!entry.Data.IsDirty) continue;
            RecordBuild(p, hasBlocks: true);
            var (solid, air) = GridStore.BrickMasksOf(entry.Data);
            SetBricks(p, solid, air);
        }

        _wanted.Clear();
        _loadQueue.Clear();
        _budgetUsed = _budgetEstimated = _chunksWanted = 0;
        _hasCut = false;
        _columnsWalked = 0;
        foreach (var (dx, dz) in _offsetsByDistance)
        {
            int x = _lastCamColumn.x + dx, z = _lastCamColumn.z + dz;
            _columnsWalked++;

            ulong bits = MaybeContent(x, z);
            if (bits == 0) continue;
            var col = _columns[(x, z)];
            int count = BitOperations.PopCount(bits), cost = 0, estimated = 0;
            for (ulong b = bits; b != 0; b &= b - 1)
            {
                int layer = BitOperations.TrailingZeroCount(b);
                if ((col.Known >> layer & 1) != 0) cost += col.Bricks![layer].Cost;
                else estimated += EstimatedBricks;
            }
            if (_budgetUsed + cost + estimated > _budget || _chunksWanted + count > MaxChunks)
            {
                _hasCut = true;
                _cut = (x, z);
                break;
            }
            _budgetUsed += cost + estimated;
            _budgetEstimated += estimated;
            _chunksWanted += count;
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

        Console.WriteLine($"[load] rebuild: {_columnsWalked} columns walked, budget {_budgetUsed}/{_budget} bricks " +
                          $"({_budgetEstimated} estimated) in {_chunksWanted} chunks, " +
                          $"cut {(_hasCut ? $"at {ColumnDistance(camPos, _cut.x, _cut.z):F0} blocks" : "none")}, " +
                          $"queued {_loadQueue.Count} columns, loaded {_staticVolume.LoadedCount}, unloaded {_toUnload.Count}");
    }

    private static long Sq(int v) => (long)v * v;

    /// <summary>Chunks of column (x, z) that may hold something: what the generator may fill, less what turned out to be
    /// air, plus builds.</summary>
    private ulong MaybeContent(int x, int z)
    {
        if (!_columns.TryGetValue((x, z), out var col))
            _columns[(x, z)] = col = new Column { Generated = _generator.Value!.ColumnLayers(x, z, _minY) };
        return (col.Generated & ~col.Air) | _built.GetValueOrDefault((x, z));
    }

    /// <summary>Records chunk <paramref name="p"/>'s brick masks, and so its light cost and its neighbours' (whose
    /// surface depends on its solid); returns its cost.</summary>
    private int SetBricks(ChunkPosition p, ulong solid, ulong air)
    {
        if (p.Y < _minY || p.Y > _minY + 63) return 0;
        if (!_columns.TryGetValue((p.X, p.Z), out var col))
            _columns[(p.X, p.Z)] = col = new Column { Generated = _generator.Value!.ColumnLayers(p.X, p.Z, _minY) };
        int layer = p.Y - _minY;
        col.Bricks ??= new (ulong, ulong, int)[64];
        col.Bricks[layer] = (solid, air, 0);
        col.Known |= 1UL << layer;
        int cost = UpdateCost(p);
        for (int f = 0; f < 6; f++) UpdateCost(Neighbour(p, f));
        return cost;
    }

    /// <summary>Recomputes a known chunk's light cost from its masks and its known neighbours' solid.</summary>
    private int UpdateCost(ChunkPosition p)
    {
        if (!TryGetBricks(p, out var col, out int layer)) return 0;
        Span<ulong> neighbours = stackalloc ulong[6];
        for (int f = 0; f < 6; f++)
            neighbours[f] = TryGetBricks(Neighbour(p, f), out var n, out int nl) ? n.Bricks![nl].Solid : 0UL;
        ref var b = ref col.Bricks![layer];
        b.Cost = BitOperations.PopCount(GridStore.SurfaceBricks(b.Solid, b.Air, neighbours));
        return b.Cost;
    }

    private bool TryGetBricks(ChunkPosition p, out Column col, out int layer)
    {
        layer = p.Y - _minY;
        return _columns.TryGetValue((p.X, p.Z), out col!) && layer is >= 0 and < 64 && (col.Known >> layer & 1) != 0;
    }

    // Face order as GridStore.SurfaceBricks takes the neighbours: +x, -x, +y, -y, +z, -z.
    private static ChunkPosition Neighbour(ChunkPosition p, int face) => face switch
    {
        0 => p.Offset(1, 0, 0), 1 => p.Offset(-1, 0, 0), 2 => p.Offset(0, 1, 0),
        3 => p.Offset(0, -1, 0), 4 => p.Offset(0, 0, 1), _ => p.Offset(0, 0, -1),
    };

    private ulong Bit(ChunkPosition p) => 1UL << (p.Y - _minY);

    /// <summary>Records whether chunk <paramref name="p"/> holds a build; a chunk that was emptied out is air again.</summary>
    private void RecordBuild(ChunkPosition p, bool hasBlocks)
    {
        if (p.Y < _minY || p.Y > _minY + 63) return;
        ulong b = Bit(p);
        ulong built = _built.GetValueOrDefault((p.X, p.Z));
        _built[(p.X, p.Z)] = hasBlocks ? built | b : built & ~b;
        if (_columns.TryGetValue((p.X, p.Z), out var col))
        {
            col.Air = hasBlocks ? col.Air & ~b : col.Air | b;
            if (!hasBlocks) col.Known &= ~b;
        }
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
                var chunks = new List<(ChunkPosition, ChunkData?, ulong, ulong)>(work.Count);
                foreach (var (pos, fromSave) in work)
                {
                    var data = _scratch.Value!;
                    if (!(fromSave && StaticWorldSerializer.TryLoad(SavePath(pos), data)))
                        _generator.Value!.Generate(data, pos);
                    data.Compact(); // stone inside an island, or sky, keeps one block instead of 64 KB
                    if (data.HasAnySolid())
                    {
                        data.IsDirty = false;
                        var (solid, air) = GridStore.BrickMasksOf(data);
                        chunks.Add((pos, data, solid, air));
                        _scratch.Value = new ChunkData();
                    }
                    else
                    {
                        chunks.Add((pos, null, 0UL, 0UL));
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
