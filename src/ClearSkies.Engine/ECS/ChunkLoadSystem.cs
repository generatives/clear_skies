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
/// distance, a whole chunk column (within a fixed layer range) at a time, with no distance limit — but spent only on
/// chunks that hold something. Every chunk that is generated or loaded is recorded in its region's
/// <see cref="RegionSurvey"/> (saved to disk), and chunks known to be air are neither loaded nor counted. A chunk not
/// surveyed yet counts as if it held something until it has been generated, so the first visit to a region fills
/// its survey in closest-first as you travel, and later visits spend the budget exactly. The budget therefore goes to
/// islands rather than sky, and the island ahead stays visible however far off it is.
///
/// Candidates come from the camera's region and the ring of regions around it. Where the budget runs out, and the
/// nearest column still being generated or loaded, set the fog distance (see <see cref="FogDistance"/>): an island
/// only partly loaded fades out at the cut instead of ending in a hard edge.
/// </summary>
public sealed class ChunkLoadSystem : ISystem, IDebugUiSystem
{
    private static readonly int MaxInFlight = System.Math.Max(2, Environment.ProcessorCount / 2);

    /// <summary>Seconds between periodic flushes of dirty chunks and surveys — crash/power-loss safety for edits to
    /// chunks that stay loaded (never unload) for a long time.</summary>
    private const float AutosaveInterval = 30f;

    /// <summary>Candidate regions: the camera's, and this many rings of regions around it. Must stay below the
    /// GridStore's region directory size, so two loaded regions never share a directory entry.</summary>
    private const int RegionRings = 1;

    /// <summary>Chunks found to be air before the budget is re-picked to spend what they freed.</summary>
    private const int AirRebuildBatch = 256;

    /// <summary>One worker job takes up to this many columns (mostly unsurveyed sky, which is quick to rule out)...</summary>
    private const int ColumnsPerJob = 32;
    /// <summary>...or stops once it holds this many chunks known to have content (a full generation or load each).</summary>
    private const int ContentChunksPerJob = 8;

    /// <summary>Fog distance when nothing is cut off or missing: everything the budget reached is loaded.</summary>
    public const float MaxFogDistance = 3800f;

    private const int S = ChunkData.Size;

    private readonly string _savesDir;
    private readonly string _surveyDir;
    private readonly string _surveyKey;

    private readonly EntitySet      _cameras;
    private readonly ChunkVolume    _staticVolume;
    private readonly ThreadLocal<IWorldGenerator> _generator;
    private readonly ThreadLocal<ChunkData> _scratch = new(() => new ChunkData());
    private readonly GridStore?     _store;
    private readonly int            _budget;
    private readonly int            _regionShift;
    private readonly int            _minY, _layers;

    /// <summary>Column offsets from the camera's column, closest first, out to a region and a half — past which the
    /// ring of candidate regions can't reach anyway. Computed once; a rebuild just walks it.</summary>
    private readonly (short dx, short dz)[] _offsetsByDistance;

    private readonly Dictionary<(int x, int z), RegionSurvey> _regions = new();

    /// <summary>Every chunk with a save file (scanned once at startup, kept up to date by saves).</summary>
    private readonly HashSet<ChunkPosition> _saved = new();

    /// <summary>The chunks the budget currently covers, loaded or not.</summary>
    private readonly HashSet<ChunkPosition> _wanted = new();

    // Columns with wanted chunks still to generate or load, closest first; and the columns workers have now.
    private readonly Queue<(int x, int z)> _loadQueue = new();
    private readonly HashSet<(int x, int z)> _inFlight = new();
    private int _jobsInFlight;

    private sealed class JobResult
    {
        public readonly List<(ChunkPosition pos, ChunkData? data)> Chunks = new();
        public readonly List<(int x, int z)> Columns = new();
    }
    private readonly ConcurrentQueue<JobResult> _results = new();

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

    // Scratch.
    private readonly Dictionary<(int x, int z), List<int>> _extraY = new();
    private readonly HashSet<ChunkPosition> _extraAir = new(); // out-of-range saved chunks found empty this session
    private readonly HashSet<ChunkPosition> _toUnload = new();
    private readonly List<ChunkPosition> _columnWork = new();

    /// <summary>Horizontal distance from the camera at which the loaded world stops: the nearest chunk column that the
    /// budget cut off or that is still loading, eased over time. Fog should be total by here.</summary>
    public float FogDistance => _fogDistance;

    /// <param name="surveyKey">Identifies what the generator produces (seed and version): survey files made under a
    /// different key are ignored, since they'd describe different terrain.</param>
    /// <param name="regionChunkShift">log2 of a region's width in chunks; must match the GridStore's.</param>
    /// <param name="minChunkY">Lowest chunk layer streamed.</param>
    /// <param name="maxChunkY">Highest chunk layer streamed (at most 64 layers in all). Chunks outside the range load
    /// only if they have a save file (something was built there).</param>
    public ChunkLoadSystem(World world, ChunkVolume staticVolume, Func<IWorldGenerator> generatorFactory, string surveyKey,
                           int regionChunkShift, int chunkBudget, int minChunkY, int maxChunkY, GridStore? store = null)
    {
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
        _minY         = minChunkY;
        _layers       = maxChunkY - minChunkY + 1;
        _store        = store;
        _offsetsByDistance = BuildOffsetsByDistance((3 << regionChunkShift) / 2);
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
        ImGui.Text($"Budget: {_budgetUsed} / {_budget} chunks ({_columnsWalked} columns walked)");
        ImGui.Text($"Loaded: {_staticVolume.LoadedCount}   Queued columns: {_loadQueue.Count}   In flight: {_inFlight.Count}");
        ImGui.Text($"Fog distance: {_fogDistance:F0} (target {_fogTarget:F0})   Cut: {(_hasCut ? $"column {_cut.x},{_cut.z}" : "none")}");
        ImGui.Text($"Saved chunks: {_saved.Count}   Layers: {_minY}..{_minY + _layers - 1}");

        ImGui.Separator();
        ImGui.Text("Candidate regions:");
        foreach (var ((x, z), r) in _regions)
            ImGui.BulletText($"({x},{z}): {r.KnownAirCount} known air{(r.Dirty ? ", unsaved" : "")}");
        if (_store != null)
        {
            ImGui.Text("GPU region sections:");
            foreach (var (x, z, dx, dy, dz, chunks) in _store.WorldRegions(_staticVolume.Gpu))
                ImGui.BulletText($"({x},{z}): {dx}x{dy}x{dz} entries ({(long)dx * dy * dz * 288 / 1024} KB), {chunks} chunks");
        }

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
            _jobsInFlight--;
            foreach (var c in job.Columns) _inFlight.Remove(c);
            foreach (var (pos, data) in job.Chunks)
            {
                if (_regions.TryGetValue(RegionOf(pos), out var survey)) survey.Record(pos, data != null);
                if (data == null)
                {
                    if (pos.Y < _minY || pos.Y >= _minY + _layers) _extraAir.Add(pos);
                    _airSinceRebuild++;
                    continue;
                }
                // Dropped if the budget moved on while it generated, or an edit created the chunk meanwhile.
                if (_wanted.Contains(pos) && !_staticVolume.IsLoaded(pos)) _staticVolume.AddChunk(pos, data);
            }
        }

        if (!TryGetCameraPos(out var camPos)) return;

        // Air found frees budget for columns further out, so re-pick once enough has turned up (or loading ran dry).
        var camColumn = ((int)MathF.Floor(camPos.X / S), (int)MathF.Floor(camPos.Z / S));
        bool idle = _loadQueue.Count == 0 && _jobsInFlight == 0;
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
    /// hold something (not surveyed yet, known to, has a save file, or is loaded) until the next column doesn't fit;
    /// queues the columns with chunks still to fetch and unloads what's no longer covered.</summary>
    private void Rebuild(Vector3D<float> camPos)
    {
        var camRegion = RegionOf(_lastCamColumn.x, _lastCamColumn.z);

        // Chunks in regions that left range go first, while their surveys are still open to record their saves.
        _toUnload.Clear();
        foreach (var (p, _) in _staticVolume.All)
            if (!InRange(RegionOf(p), camRegion)) _toUnload.Add(p);
        foreach (var p in _toUnload) Unload(p);
        int unloaded = _toUnload.Count;
        UpdateRegions(camRegion);

        // Loaded chunks always count: an edit may have put blocks where the survey found air. In the layer range that
        // goes into the survey (saves record there too); above or below it they're kept per column, along with chunks
        // saved out there, unless one turned out empty this session.
        _extraY.Clear();
        foreach (var (p, _) in _staticVolume.All) NoteContent(p);
        foreach (var p in _saved)
            if ((p.Y < _minY || p.Y >= _minY + _layers) && InRange(RegionOf(p), camRegion) && !_extraAir.Contains(p))
                NoteContent(p);

        _wanted.Clear();
        _loadQueue.Clear();
        _budgetUsed = 0;
        _hasCut = false;
        _columnsWalked = 0;
        foreach (var (dx, dz) in _offsetsByDistance)
        {
            int x = _lastCamColumn.x + dx, z = _lastCamColumn.z + dz;
            if (!_regions.TryGetValue(RegionOf(x, z), out var survey)) continue; // outside the candidate regions
            _columnsWalked++;

            ulong bits = survey.MaybeContent(x, z);
            _extraY.TryGetValue((x, z), out var extra);
            int count = BitOperations.PopCount(bits) + (extra?.Count ?? 0);
            if (count == 0) continue;
            if (_budgetUsed + count > _budget)
            {
                _hasCut = true;
                _cut = (x, z);
                break;
            }
            _budgetUsed += count;

            bool pending = false;
            while (bits != 0)
            {
                int b = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var p = new ChunkPosition(x, _minY + b, z);
                _wanted.Add(p);
                pending |= !_staticVolume.IsLoaded(p);
            }
            if (extra != null)
                foreach (int y in extra)
                {
                    var p = new ChunkPosition(x, y, z);
                    _wanted.Add(p);
                    pending |= !_staticVolume.IsLoaded(p);
                }
            if (pending) _loadQueue.Enqueue((x, z));
        }

        _toUnload.Clear();
        foreach (var (p, _) in _staticVolume.All)
            if (!_wanted.Contains(p)) _toUnload.Add(p);
        foreach (var p in _toUnload) Unload(p);
        unloaded += _toUnload.Count;

        Console.WriteLine($"[load] rebuild: {_columnsWalked} columns walked, budget {_budgetUsed}/{_budget}, " +
                          $"cut {(_hasCut ? $"at {ColumnDistance(camPos, _cut.x, _cut.z):F0} blocks" : "none")}, " +
                          $"queued {_loadQueue.Count} columns, loaded {_staticVolume.LoadedCount}, unloaded {unloaded}");
    }

    /// <summary>Opens the surveys of candidate regions that came into range (from disk, or new) and saves and drops
    /// the ones that left.</summary>
    private void UpdateRegions((int x, int z) camRegion)
    {
        List<(int, int)>? drop = null;
        foreach (var key in _regions.Keys)
            if (!InRange(key, camRegion)) (drop ??= new()).Add(key);
        if (drop != null)
            foreach (var key in drop)
            {
                SaveSurvey(_regions[key]);
                _regions.Remove(key);
            }

        for (int rz = camRegion.z - RegionRings; rz <= camRegion.z + RegionRings; rz++)
        for (int rx = camRegion.x - RegionRings; rx <= camRegion.x + RegionRings; rx++)
        {
            if (_regions.ContainsKey((rx, rz))) continue;
            var survey = RegionSurvey.TryLoad(SurveyPath(rx, rz), _surveyKey, rx, rz, _regionShift, _minY, _layers)
                      ?? new RegionSurvey(rx, rz, _regionShift, _minY, _layers);
            if (survey.SectionSize is { } size) _store?.SetWorldRegionSize(rx, rz, size);
            _regions[(rx, rz)] = survey;
        }
    }

    /// <summary>Marks chunk <paramref name="p"/> as holding something: in its survey if it's in the layer range, else
    /// as an extra chunk of its column.</summary>
    private void NoteContent(ChunkPosition p)
    {
        if (p.Y >= _minY && p.Y < _minY + _layers)
        {
            if (_regions.TryGetValue(RegionOf(p), out var survey)) survey.Record(p, true);
            return;
        }
        if (!_extraY.TryGetValue((p.X, p.Z), out var list)) _extraY[(p.X, p.Z)] = list = new List<int>();
        if (!list.Contains(p.Y)) list.Add(p.Y);
    }

    /// <summary>Hands queued columns to workers, several per job: most of a first visit is sky that generation rules
    /// out in microseconds, so one chunk per job would leave the workers idle waiting on the frame rate.</summary>
    private void Dispatch()
    {
        while (_jobsInFlight < MaxInFlight && _loadQueue.Count > 0)
        {
            var work = new List<(ChunkPosition pos, bool fromSave)>();
            var columns = new List<(int x, int z)>();
            int contentChunks = 0;
            while (_loadQueue.Count > 0 && columns.Count < ColumnsPerJob && contentChunks < ContentChunksPerJob)
            {
                var col = _loadQueue.Dequeue();
                if (_inFlight.Contains(col)) { _skippedInFlight = true; continue; } // re-queued by the next rebuild
                ColumnWork(col.x, col.z);
                if (_columnWork.Count == 0) continue;
                columns.Add(col);
                _inFlight.Add(col);
                var survey = _regions[RegionOf(col.x, col.z)];
                ulong content = survey.Content(col.x, col.z);
                foreach (var p in _columnWork)
                {
                    int bit = p.Y - _minY;
                    bool fromSave = _saved.Contains(p);
                    if (fromSave || bit < 0 || bit >= _layers || (content >> bit & 1) != 0) contentChunks++;
                    work.Add((p, fromSave));
                }
            }
            if (columns.Count == 0) break;

            _jobsInFlight++;
            var result = new JobResult();
            result.Columns.AddRange(columns);
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                foreach (var (pos, fromSave) in work)
                {
                    var data = _scratch.Value!;
                    if (!(fromSave && StaticWorldSerializer.TryLoad(SavePath(pos), data)))
                        _generator.Value!.Generate(data, pos);
                    if (data.HasAnySolid())
                    {
                        data.IsDirty = false;
                        result.Chunks.Add((pos, data));
                        _scratch.Value = new ChunkData();
                    }
                    else
                    {
                        result.Chunks.Add((pos, null));
                        // Generation only ever writes blocks, so an empty result leaves the buffer all air and ready to
                        // reuse; a loaded save may have overwritten more than blocks, so start that one afresh.
                        if (fromSave) _scratch.Value = new ChunkData();
                    }
                }
                _results.Enqueue(result);
            }, null);
        }
    }

    /// <summary>Fills _columnWork with column (x, z)'s wanted chunks that aren't loaded yet.</summary>
    private void ColumnWork(int x, int z)
    {
        _columnWork.Clear();
        if (!_regions.TryGetValue(RegionOf(x, z), out var survey)) return;
        ulong bits = survey.MaybeContent(x, z);
        while (bits != 0)
        {
            int b = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            var p = new ChunkPosition(x, _minY + b, z);
            if (_wanted.Contains(p) && !_staticVolume.IsLoaded(p)) _columnWork.Add(p);
        }
        if (_extraY.TryGetValue((x, z), out var extra))
            foreach (int y in extra)
            {
                var p = new ChunkPosition(x, y, z);
                if (_wanted.Contains(p) && !_staticVolume.IsLoaded(p)) _columnWork.Add(p);
            }
    }

    /// <summary>Eases <see cref="FogDistance"/> toward the nearest column that is cut off or still missing: in fast,
    /// so a gap is covered before it shows, out slowly, so the view opens up gently as loading catches up.</summary>
    private void UpdateFog(Vector3D<float> camPos, float dt)
    {
        float target = _hasCut ? ColumnDistance(camPos, _cut.x, _cut.z) : MaxFogDistance;
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

    private static bool InRange((int x, int z) region, (int x, int z) center)
        => System.Math.Abs(region.x - center.x) <= RegionRings && System.Math.Abs(region.z - center.z) <= RegionRings;

    private bool TryGetCameraPos(out Vector3D<float> pos)
    {
        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            ref readonly var cc = ref e.Get<CameraComponent>();
            if (cc.Active)
            {
                pos = e.Get<Transform>().Position;
                return true;
            }
        }
        pos = default;
        return false;
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
        bool hasContent = entry.Data.HasAnySolid();
        _saved.Add(pos);
        _extraAir.Remove(pos);
        if (_regions.TryGetValue(RegionOf(pos), out var survey)) survey.Record(pos, hasContent);
    }

    private void SaveSurvey(RegionSurvey survey)
    {
        // The table section size it has now (or had when its last chunk went), so a revisit starts at that size.
        if (_store != null && _store.TryGetWorldRegionSize(_staticVolume.Gpu, survey.X, survey.Z, out var size) &&
            survey.SectionSize != size)
        {
            survey.SectionSize = size;
            survey.Save(SurveyPath(survey.X, survey.Z), _surveyKey);
            return;
        }
        if (survey.Dirty) survey.Save(SurveyPath(survey.X, survey.Z), _surveyKey);
    }

    private string SavePath(ChunkPosition pos) => Path.Combine(_savesDir, $"{pos.X}_{pos.Y}_{pos.Z}.chunk");
    private string SurveyPath(int rx, int rz) => Path.Combine(_surveyDir, $"{rx}_{rz}.survey");
}
