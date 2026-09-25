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
/// distance out to the view distance, a whole chunk column at a time — but spent only on chunks that hold something.
/// Every chunk that is generated or loaded is recorded in its region's <see cref="RegionSurvey"/> (saved to disk), and
/// chunks known to be air are neither loaded nor counted. A chunk not surveyed yet counts as if it held something
/// until it has been generated, so the first visit to a region fills its survey in closest-first as you travel, and
/// later visits spend the budget exactly. The budget therefore goes to islands rather than sky, and reaches the
/// islands out to the view distance.
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

    /// <summary>Seconds between periodic flushes of dirty chunks and surveys — crash/power-loss safety for edits to
    /// chunks that stay loaded (never unload) for a long time.</summary>
    private const float AutosaveInterval = 30f;

    /// <summary>The GridStore world index width that fits a view distance: wider than the span of chunks loaded at
    /// once (with a column to spare each side for the frame between a chunk leaving range and its storage being
    /// released), so two loaded chunks never share a cell.</summary>
    public static int WorldIndexDim(float viewDistance) => 2 * (int)MathF.Ceiling(viewDistance / S) + 3;

    /// <summary>Chunks found to be air before the budget is re-picked to spend what they freed.</summary>
    private const int AirRebuildBatch = 256;

    /// <summary>Streamed layers below the generated ones, for building under the islands. Streaming covers 64 layers
    /// (a survey column); the rest are above.</summary>
    private const int LayersBelow = 8;

    private const int S = ChunkData.Size;

    private readonly string _savesDir;
    private readonly string _surveyDir;
    private readonly string _surveyKey;

    private readonly EntitySet      _cameras;
    private readonly ChunkVolume    _staticVolume;
    private readonly ThreadLocal<IWorldGenerator> _generator;
    private readonly ThreadLocal<ChunkData> _scratch = new(() => new ChunkData());
    private readonly int            _budget;
    private readonly int            _regionShift;
    private readonly int            _minY;      // the lowest streamed layer: bit 0 of a survey column
    private readonly ulong          _generated; // the layers the generator fills, as survey bits

    /// <summary>Column offsets from the camera's column, closest first, out to the view distance. Computed once; a
    /// rebuild just walks it.</summary>
    private readonly (short dx, short dz)[] _offsetsByDistance;
    private readonly int _viewColumns; // the view distance in columns
    private readonly float _viewDistance;

    private readonly Dictionary<(int x, int z), RegionSurvey> _regions = new();

    /// <summary>Every chunk with a save file (scanned once at startup, kept up to date by saves).</summary>
    private readonly HashSet<ChunkPosition> _saved = new();

    /// <summary>The chunks the budget currently covers, loaded or not: survey bits per column.</summary>
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

    /// <param name="surveyKey">Identifies what the generator produces (seed and version): survey files made under a
    /// different key are ignored, since they'd describe different terrain.</param>
    /// <param name="regionChunkShift">log2 of a region's width in chunks; must match the GridStore's.</param>
    /// <param name="viewDistance">How far out chunks are streamed, in blocks (horizontally), as far as the budget
    /// reaches. The GridStore's world index must fit it: see <see cref="WorldIndexDim"/>.</param>
    /// <param name="minChunkY">Lowest chunk layer the generator fills.</param>
    /// <param name="maxChunkY">Highest chunk layer the generator fills. Streaming reaches <see cref="LayersBelow"/>
    /// layers under <paramref name="minChunkY"/> and the rest of 64 above; outside the generated layers it loads only
    /// chunks that something was built in. Edits to the static world outside those 64 layers are refused (see
    /// <see cref="ChunkVolume.EditableLayers"/>).</param>
    public ChunkLoadSystem(World world, ChunkVolume staticVolume, Func<IWorldGenerator> generatorFactory, string surveyKey,
                           int regionChunkShift, float viewDistance, int chunkBudget, int minChunkY, int maxChunkY)
    {
        int generatedLayers = maxChunkY - minChunkY + 1;
        if (generatedLayers < 1 || generatedLayers > 64 - LayersBelow)
            throw new ArgumentOutOfRangeException(nameof(maxChunkY), $"1-{64 - LayersBelow} generated layers");

        _savesDir  = Path.Combine(AppContext.BaseDirectory, "Saves", "World");
        _surveyDir = Path.Combine(_savesDir, "Regions");
        Directory.CreateDirectory(_surveyDir);
        ScanSaves();

        _cameras      = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _staticVolume = staticVolume;
        _generator    = new ThreadLocal<IWorldGenerator>(generatorFactory);
        _surveyKey    = surveyKey;
        _regionShift  = regionChunkShift;
        _budget       = chunkBudget;
        _minY         = minChunkY - LayersBelow;
        _generated    = ((1UL << generatedLayers) - 1) << LayersBelow;
        _staticVolume.EditableLayers = (_minY, _minY + 63); // only what streaming can load back
        _viewDistance = viewDistance;
        _viewColumns  = (int)MathF.Ceiling(viewDistance / S);
        _offsetsByDistance = BuildOffsetsByDistance(_viewColumns);
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
                _saved.Add(new ChunkPosition(x, y, z));
        }
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Chunk Loading";

    public void DrawDebugUi()
    {
        ImGui.Text($"View distance: {_viewDistance:F0} blocks ({_regions.Count} regions)");
        ImGui.Text($"Budget: {_budgetUsed} / {_budget} chunks ({_columnsWalked} columns walked)");
        ImGui.Text($"Loaded: {_staticVolume.LoadedCount}   Queued columns: {_loadQueue.Count}   In flight: {_inFlight.Count}");
        ImGui.Text($"Fog distance: {_fogDistance:F0} (target {_fogTarget:F0})   Cut: {(_hasCut ? $"column {_cut.x},{_cut.z}" : "none")}");
        ImGui.Text($"Saved chunks: {_saved.Count}   Layers: {_minY}..{_minY + 63}");

        ImGui.Separator();
        ImGui.Text("Candidate regions:");
        foreach (var ((x, z), r) in _regions)
            ImGui.BulletText($"({x},{z}): {r.KnownAirCount} known air{(r.Dirty ? ", unsaved" : "")}");

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
                if (_regions.TryGetValue(RegionOf(pos), out var survey)) survey.Record(pos, data != null);
                if (data == null)
                {
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
    /// hold something (see <see cref="RegionSurvey.MaybeContent"/>) until the next column doesn't fit; queues the
    /// columns with chunks still to fetch, unloads what's no longer covered, and swaps in the surveys of the regions
    /// now in range.</summary>
    private void Rebuild(Vector3D<float> camPos)
    {
        var camRegion = RegionOf(_lastCamColumn.x, _lastCamColumn.z);
        int rings = (_viewColumns >> _regionShift) + 1;
        for (int rz = camRegion.z - rings; rz <= camRegion.z + rings; rz++)
        for (int rx = camRegion.x - rings; rx <= camRegion.x + rings; rx++)
            if (InView((rx, rz)) && !_regions.ContainsKey((rx, rz))) _regions[(rx, rz)] = OpenSurvey(rx, rz);

        // Loaded chunks always count: an edit may have put blocks where the survey found air.
        foreach (var (p, _) in _staticVolume.All)
            if (_regions.TryGetValue(RegionOf(p), out var survey)) survey.Record(p, true);

        _wanted.Clear();
        _loadQueue.Clear();
        _budgetUsed = 0;
        _hasCut = false;
        _columnsWalked = 0;
        foreach (var (dx, dz) in _offsetsByDistance)
        {
            int x = _lastCamColumn.x + dx, z = _lastCamColumn.z + dz;
            _columnsWalked++;

            ulong bits = _regions[RegionOf(x, z)].MaybeContent(x, z); // every column in view has its region open
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

        // Unloading saves edits into their surveys, so the surveys of regions that left range are dropped after it.
        _toUnload.Clear();
        foreach (var (p, _) in _staticVolume.All)
            if (!IsWanted(p)) _toUnload.Add(p);
        foreach (var p in _toUnload) Unload(p);
        foreach (var key in _regions.Keys.Where(k => !InView(k)).ToList())
        {
            SaveSurvey(_regions[key]);
            _regions.Remove(key);
        }

        Console.WriteLine($"[load] rebuild: {_columnsWalked} columns walked, budget {_budgetUsed}/{_budget}, " +
                          $"cut {(_hasCut ? $"at {ColumnDistance(camPos, _cut.x, _cut.z):F0} blocks" : "none")}, " +
                          $"queued {_loadQueue.Count} columns, loaded {_staticVolume.LoadedCount}, unloaded {_toUnload.Count}");
    }

    /// <summary>Region (rx, rz)'s survey from disk, or a new one (a first visit, or the terrain changed).</summary>
    private RegionSurvey OpenSurvey(int rx, int rz)
    {
        var survey = RegionSurvey.TryLoad(SurveyPath(rx, rz), _surveyKey, rx, rz, _regionShift, _minY, _generated);
        if (survey != null) return survey;
        survey = new RegionSurvey(rx, rz, _regionShift, _minY, _generated);
        // A new survey takes the layers the generator doesn't fill for air, but saved chunks there hold a build.
        foreach (var p in _saved)
            if (RegionOf(p) == (rx, rz)) survey.Record(p, true);
        return survey;
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

    private (int x, int z) RegionOf(ChunkPosition p) => RegionOf(p.X, p.Z);
    private (int x, int z) RegionOf(int chunkX, int chunkZ) => (chunkX >> _regionShift, chunkZ >> _regionShift);

    /// <summary>Whether region (x, z) holds any column within the view distance of the camera's column.</summary>
    private bool InView((int x, int z) region)
    {
        int w = 1 << _regionShift;
        int x0 = region.x * w, z0 = region.z * w;
        long dx = System.Math.Max(0, System.Math.Max(x0 - _lastCamColumn.x, _lastCamColumn.x - (x0 + w - 1)));
        long dz = System.Math.Max(0, System.Math.Max(z0 - _lastCamColumn.z, _lastCamColumn.z - (z0 + w - 1)));
        return dx * dx + dz * dz <= (long)_viewColumns * _viewColumns;
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

    /// <summary>Writes every currently loaded chunk with unsaved edits, and every changed region survey, to disk.
    /// Called by the periodic autosave and once on graceful shutdown.</summary>
    public void SaveAllDirty()
    {
        foreach (var (pos, entry) in _staticVolume.All)
            SaveIfDirty(pos, entry);
        foreach (var survey in _regions.Values)
            SaveSurvey(survey);
    }

    private void SaveIfDirty(ChunkPosition pos, ChunkEntry entry)
    {
        if (!entry.Data.IsDirty) return;
        StaticWorldSerializer.Save(entry.Data, SavePath(pos));
        entry.Data.IsDirty = false;
        // What's there now is what a reload finds, so an edit off the island's terrain (a bridge, a tower) comes back,
        // and one that cleared a chunk out stops costing budget.
        _saved.Add(pos);
        if (_regions.TryGetValue(RegionOf(pos), out var survey)) survey.Record(pos, entry.Data.HasAnySolid());
    }

    private void SaveSurvey(RegionSurvey survey)
    {
        if (survey.Dirty) survey.Save(SurveyPath(survey.X, survey.Z), _surveyKey);
    }

    private string SavePath(ChunkPosition pos) => Path.Combine(_savesDir, $"{pos.X}_{pos.Y}_{pos.Z}.chunk");
    private string SurveyPath(int rx, int rz) => Path.Combine(_surveyDir, $"{rx}_{rz}.survey");
}
