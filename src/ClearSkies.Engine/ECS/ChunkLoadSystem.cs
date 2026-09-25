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
/// distance but with no distance limit, from only the chunks that can hold anything — terrain the
/// <see cref="ITerrainLayout"/> says may be there, chunks with a save file, and chunks already loaded (e.g. created
/// by an edit). Chunks that generate empty are remembered as known air and never loaded or counted, so the budget is
/// spent on islands instead of sky, and the island ahead stays visible however far off it is.
///
/// Candidates come from the camera's region and the ring of regions around it, a whole chunk column at a time, so
/// the loaded world is everything within some horizontal distance of the camera. Where the budget runs out, that
/// distance is the fog distance (see <see cref="FogDistance"/>): an island only partly inside it fades out at the
/// cut instead of ending in a hard edge.
/// </summary>
public sealed class ChunkLoadSystem : ISystem, IDebugUiSystem
{
    private static readonly int MaxInFlight = System.Math.Max(2, Environment.ProcessorCount / 2);

    /// <summary>Seconds between periodic flushes of any currently-loaded dirty chunks — crash/power-loss
    /// safety for edits to chunks that stay loaded (never unload) for a long time.</summary>
    private const float AutosaveInterval = 30f;

    /// <summary>Candidate regions: the camera's, and this many rings of regions around it. Must stay below the
    /// GridStore's region directory size, so two loaded regions never share a directory entry.</summary>
    private const int RegionRings = 1;

    /// <summary>Chunks found to be air before the budget is re-picked to spend what they freed.</summary>
    private const int AirRebuildBatch = 128;

    /// <summary>Fog distance when nothing is cut off or missing: the whole searched area is loaded.</summary>
    public const float MaxFogDistance = 3800f;

    private const int S = ChunkData.Size;

    private readonly string _savesDir;

    private readonly EntitySet      _cameras;
    private readonly ChunkVolume    _staticVolume;
    private readonly ThreadLocal<IWorldGenerator> _generator;
    private readonly ITerrainLayout _layout;
    private readonly GridStore?     _store;
    private readonly int            _budget;
    private readonly int            _regionShift;

    /// <summary>Per candidate region: where its terrain may be, and which of those chunks turned out empty.</summary>
    private sealed class Region
    {
        public required IReadOnlyList<TerrainColumn> Columns;
        public readonly HashSet<ChunkPosition> KnownAir = new();
    }
    private readonly Dictionary<(int x, int z), Region> _regions = new();

    /// <summary>Every chunk with a save file (scanned once at startup, kept up to date by saves).</summary>
    private readonly HashSet<ChunkPosition> _saved = new();

    /// <summary>The chunks the budget currently covers, loaded or not (known air excluded).</summary>
    private readonly HashSet<ChunkPosition> _wanted = new();

    // Load queue in closest-column-first order; each entry carries its column for the fog's nearest-missing distance.
    private readonly Queue<(ChunkPosition pos, int colX, int colZ)> _loadQueue = new();
    private readonly List<(ChunkPosition pos, int colX, int colZ)> _inFlight = new();
    private readonly ConcurrentQueue<(ChunkData data, ChunkPosition pos, bool empty)> _results = new();

    private (int x, int z) _lastCamColumn = (int.MinValue, int.MinValue);
    private int _airSinceRebuild;
    private float _autosaveTimer;

    // Budget results of the last rebuild: chunks counted, and the first column that didn't fit (the cut).
    private int _budgetUsed;
    private bool _hasCut;
    private (int x, int z) _cut;

    private float _fogDistance;
    private float _fogTarget;

    // Scratch for rebuilds.
    private sealed class Candidate
    {
        public int X, Z, MinY = int.MaxValue, MaxY = int.MinValue;
        public List<int>? ExtraY;
        public float Distance;
    }
    private readonly Dictionary<(int x, int z), Candidate> _candidates = new();
    private readonly List<Candidate> _sorted = new();
    private readonly List<ChunkPosition> _columnChunks = new();
    private readonly HashSet<ChunkPosition> _toUnload = new();

    /// <summary>Horizontal distance from the camera at which the loaded world stops: the nearest chunk column that the
    /// budget cut off or that is still loading, eased over time. Fog should be total by here.</summary>
    public float FogDistance => _fogDistance;

    public ChunkLoadSystem(World world, ChunkVolume staticVolume, Func<IWorldGenerator> generatorFactory,
                           ITerrainLayout layout, int chunkBudget, GridStore? store = null)
    {
        _savesDir = Path.Combine(AppContext.BaseDirectory, "Saves", "World");
        Directory.CreateDirectory(_savesDir);
        ScanSaves();

        _cameras      = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _staticVolume = staticVolume;
        _generator    = new ThreadLocal<IWorldGenerator>(generatorFactory);
        _layout       = layout;
        _regionShift  = layout.RegionChunkShift;
        _budget       = chunkBudget;
        _store        = store;
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
        ImGui.Text($"Budget: {_budgetUsed} / {_budget} chunks");
        ImGui.Text($"Loaded: {_staticVolume.LoadedCount}   Queued: {_loadQueue.Count}   In flight: {_inFlight.Count}");
        int knownAir = 0;
        foreach (var r in _regions.Values) knownAir += r.KnownAir.Count;
        ImGui.Text($"Known air: {knownAir}   Saved chunks: {_saved.Count}");
        ImGui.Text($"Fog distance: {_fogDistance:F0} (target {_fogTarget:F0})   Cut: {(_hasCut ? $"column {_cut.x},{_cut.z}" : "none")}");

        ImGui.Separator();
        ImGui.Text("Candidate regions (terrain columns):");
        foreach (var ((x, z), r) in _regions)
            ImGui.BulletText($"({x},{z}): {r.Columns.Count} columns, {r.KnownAir.Count} known air");
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

        while (_results.TryDequeue(out var r))
        {
            _inFlight.RemoveAll(f => f.pos == r.pos);
            // Dropped if the budget moved on while it generated, or an edit created the chunk meanwhile.
            if (!_wanted.Contains(r.pos) || _staticVolume.IsLoaded(r.pos)) continue;
            if (r.empty)
            {
                // Never loaded, and no longer counted: the next rebuild spends its share of the budget further out.
                if (_regions.TryGetValue(RegionOf(r.pos), out var region)) region.KnownAir.Add(r.pos);
                _wanted.Remove(r.pos);
                _airSinceRebuild++;
                continue;
            }
            _staticVolume.AddChunk(r.pos, r.data);
        }

        if (!TryGetCameraPos(out var camPos)) return;

        var camColumn = ((int)MathF.Floor(camPos.X / S), (int)MathF.Floor(camPos.Z / S));
        // Air found frees budget for columns further out, so re-pick once enough has turned up (or loading ran dry).
        if (camColumn != _lastCamColumn || _airSinceRebuild >= AirRebuildBatch ||
            (_airSinceRebuild > 0 && _loadQueue.Count == 0 && _inFlight.Count == 0))
        {
            _lastCamColumn = camColumn;
            _airSinceRebuild = 0;
            Rebuild(camPos);
        }

        // Keep the max number in flight as long as there are chunks to load
        while (_inFlight.Count < MaxInFlight && _loadQueue.Count > 0)
        {
            var job = _loadQueue.Dequeue();
            var pos = job.pos;
            if (_staticVolume.IsLoaded(pos) || !_wanted.Contains(pos)) continue;
            _inFlight.Add(job);
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                var data = new ChunkData();
                if (!StaticWorldSerializer.TryLoad(SavePath(pos), data))
                    _generator.Value!.Generate(data, pos);
                data.IsDirty = false;

                _results.Enqueue((data, pos, !data.HasAnySolid()));
            }, null);
        }

        UpdateFog(camPos, dt);
    }

    /// <summary>Re-picks the budgeted chunks around the camera: gathers candidate columns, walks them closest first
    /// counting every chunk not known to be air until the budget is spent, queues what's missing and unloads the
    /// rest.</summary>
    private void Rebuild(Vector3D<float> camPos)
    {
        var camRegion = RegionOf(_lastCamColumn.x, _lastCamColumn.z);

        // Candidate regions: add the ones that came into range, drop (with their known air) the ones that left.
        var drop = new List<(int, int)>();
        foreach (var key in _regions.Keys)
            if (!InRange(key, camRegion)) drop.Add(key);
        foreach (var key in drop) _regions.Remove(key);
        for (int rz = camRegion.z - RegionRings; rz <= camRegion.z + RegionRings; rz++)
        for (int rx = camRegion.x - RegionRings; rx <= camRegion.x + RegionRings; rx++)
        {
            if (_regions.ContainsKey((rx, rz))) continue;
            var columns = _layout.TerrainColumns(rx, rz);
            _regions[(rx, rz)] = new Region { Columns = columns };
            if (columns.Count > 0 && _store != null)
            {
                int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
                foreach (var c in columns)
                {
                    x0 = System.Math.Min(x0, c.X); x1 = System.Math.Max(x1, c.X);
                    z0 = System.Math.Min(z0, c.Z); z1 = System.Math.Max(z1, c.Z);
                    y0 = System.Math.Min(y0, c.MinY); y1 = System.Math.Max(y1, c.MaxY);
                }
                _store.SetWorldRegionExtent(rx, rz, new ChunkPosition(x0, y0, z0), new ChunkPosition(x1, y1, z1));
            }
        }

        // Candidate columns: terrain, saved chunks and loaded chunks in range. Loaded chunks out of range unload.
        _candidates.Clear();
        _toUnload.Clear();
        foreach (var region in _regions.Values)
            foreach (var c in region.Columns)
            {
                var cand = CandidateAt(c.X, c.Z);
                cand.MinY = System.Math.Min(cand.MinY, c.MinY);
                cand.MaxY = System.Math.Max(cand.MaxY, c.MaxY);
            }
        foreach (var p in _saved)
            if (InRange(RegionOf(p), camRegion)) AddExtra(p);
        foreach (var (p, _) in _staticVolume.All)
        {
            if (InRange(RegionOf(p), camRegion)) AddExtra(p);
            else _toUnload.Add(p);
        }

        _sorted.Clear();
        foreach (var cand in _candidates.Values)
        {
            cand.Distance = ColumnDistance(camPos, cand.X, cand.Z);
            _sorted.Add(cand);
        }
        _sorted.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        // Walk closest first, a whole column at a time, until the next column doesn't fit.
        _wanted.Clear();
        _loadQueue.Clear();
        _budgetUsed = 0;
        _hasCut = false;
        foreach (var cand in _sorted)
        {
            _columnChunks.Clear();
            for (int y = cand.MinY; y <= cand.MaxY; y++) AddIfCounted(new ChunkPosition(cand.X, y, cand.Z));
            if (cand.ExtraY != null)
                foreach (int y in cand.ExtraY)
                    if (y < cand.MinY || y > cand.MaxY) AddIfCounted(new ChunkPosition(cand.X, y, cand.Z));

            if (_budgetUsed + _columnChunks.Count > _budget)
            {
                _hasCut = true;
                _cut = (cand.X, cand.Z);
                break;
            }
            _budgetUsed += _columnChunks.Count;
            foreach (var p in _columnChunks)
            {
                _wanted.Add(p);
                if (!_staticVolume.IsLoaded(p) && !IsInFlight(p)) _loadQueue.Enqueue((p, cand.X, cand.Z));
            }
        }

        foreach (var (p, _) in _staticVolume.All)
            if (!_wanted.Contains(p)) _toUnload.Add(p);
        foreach (var p in _toUnload) Unload(p);

        Console.WriteLine($"[load] rebuild: {_sorted.Count} candidate columns, budget {_budgetUsed}/{_budget}, " +
                          $"cut {(_hasCut ? $"at {ColumnDistance(camPos, _cut.x, _cut.z):F0} blocks" : "none")}, " +
                          $"queued {_loadQueue.Count}, loaded {_staticVolume.LoadedCount}, unloaded {_toUnload.Count}");
    }

    private Candidate CandidateAt(int x, int z)
    {
        if (!_candidates.TryGetValue((x, z), out var cand))
            _candidates[(x, z)] = cand = new Candidate { X = x, Z = z };
        return cand;
    }

    private void AddExtra(ChunkPosition p)
    {
        var cand = CandidateAt(p.X, p.Z);
        cand.ExtraY ??= new List<int>();
        if (!cand.ExtraY.Contains(p.Y)) cand.ExtraY.Add(p.Y);
    }

    /// <summary>A chunk counts against the budget unless it is known to be air (and not since created by an edit).</summary>
    private void AddIfCounted(ChunkPosition p)
    {
        if (!_staticVolume.IsLoaded(p) && _regions.TryGetValue(RegionOf(p), out var region) && region.KnownAir.Contains(p))
            return;
        _columnChunks.Add(p);
    }

    private bool IsInFlight(ChunkPosition p)
    {
        foreach (var f in _inFlight) if (f.pos == p) return true;
        return false;
    }

    /// <summary>Eases <see cref="FogDistance"/> toward the nearest column that is cut off or still missing: in fast,
    /// so a gap is covered before it shows, out slowly, so the view opens up gently as loading catches up.</summary>
    private void UpdateFog(Vector3D<float> camPos, float dt)
    {
        float target = _hasCut ? ColumnDistance(camPos, _cut.x, _cut.z) : MaxFogDistance;
        if (_loadQueue.TryPeek(out var next)) target = MathF.Min(target, ColumnDistance(camPos, next.colX, next.colZ));
        foreach (var f in _inFlight) target = MathF.Min(target, ColumnDistance(camPos, f.colX, f.colZ));
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

    /// <summary>Writes every currently loaded chunk with unsaved edits to disk, clearing its dirty flag.
    /// Called by the periodic autosave (see ChunkLoadSystem) and once on graceful shutdown.</summary>
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
        // Saved chunks are always candidates, so an edit off the island's terrain (a bridge, a tower) comes back.
        _saved.Add(pos);
        if (_regions.TryGetValue(RegionOf(pos), out var region)) region.KnownAir.Remove(pos);
    }

    private string SavePath(ChunkPosition pos) => Path.Combine(_savesDir, $"{pos.X}_{pos.Y}_{pos.Z}.chunk");
}
