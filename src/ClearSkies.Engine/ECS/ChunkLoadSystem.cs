using System.Linq;
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
/// Tracks the active camera's chunk position each frame and queues chunk load/unload
/// operations so only a small batch is processed per frame (throttled by LoadsPerFrame).
/// </summary>
public sealed class ChunkLoadSystem : ISystem, IDebugUiSystem
{
    private static readonly int MaxInFlight = System.Math.Max(2, Environment.ProcessorCount / 2);

    // Generate() now averages ~100us (p99 ~1.1ms) per chunk after the meshing/generation perf pass —
    // see GenerationBenchmark. 16/frame budgets ~1.6ms typical, leaving headroom for meshing/physics/GPU
    // work in the same frame; was 4 when the pipeline was slower.
    private const int LoadsPerFrame = 16;

    /// <summary>Seconds between periodic flushes of any currently-loaded dirty chunks — crash/power-loss
    /// safety for edits to chunks that stay loaded (never unload) for a long time.</summary>
    private const float AutosaveInterval = 30f;

    private readonly string _savesDir;

    private readonly EntitySet      _cameras;
    private readonly ChunkVolume    _staticVolume;
    private readonly ThreadLocal<IWorldGenerator> _generator;
    private readonly int            _xzRadius;
    private readonly int            _yRadius;

    /// <summary>Every (dx,dy,dz) offset within the load radii, precomputed once and sorted closest-first
    /// (same weighting as before: y counts 4x). The shape never changes at runtime, so RebuildLoadQueue
    /// just filters this instead of re-collecting and re-sorting the whole radius on every chunk-boundary
    /// crossing — that sort was O(volume log volume) with a fresh allocation each time, paid every time the
    /// camera crosses into a new chunk, and volume grows as (2*xzRadius+1)^2*(2*yRadius+1).</summary>
    private readonly (int dx, int dy, int dz)[] _offsetsByDistance;

    private readonly Queue<ChunkPosition> _loadQueue = new();
    private int _inFlight = 0;
    private readonly ConcurrentQueue<(ChunkData, ChunkPosition)> _results = new();
    private ChunkPosition _lastCamChunk = new(int.MinValue, int.MinValue, int.MinValue);
    private float _autosaveTimer;

    public ChunkLoadSystem(World world, ChunkVolume staticVolume, Func<IWorldGenerator> generatorFactory,
                           int xzRadius = 5, int yRadius = 2)
    {
        _savesDir = Path.Combine(AppContext.BaseDirectory, "Saves", "World");
        Directory.CreateDirectory(_savesDir);

        _cameras   = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _staticVolume   = staticVolume;
        _generator = new ThreadLocal<IWorldGenerator>(generatorFactory);
        _xzRadius  = xzRadius;
        _yRadius   = yRadius;
        _offsetsByDistance = BuildOffsetsByDistance(xzRadius, yRadius);

        // The camera is somewhere inside the centre chunk, so the load region's nearest edge is at least
        // radius * chunk size away; the distance fog is fitted to fade out by there.
        SkySettings.SetLoadedExtent(xzRadius * ChunkData.Size, yRadius * ChunkData.Size);
    }

    private static (int dx, int dy, int dz)[] BuildOffsetsByDistance(int xzRadius, int yRadius)
    {
        var offsets = new List<(int dx, int dy, int dz, int dist)>();
        for (int dy = -yRadius; dy <= yRadius; dy++)
        for (int dx = -xzRadius; dx <= xzRadius; dx++)
        for (int dz = -xzRadius; dz <= xzRadius; dz++)
            offsets.Add((dx, dy, dz, dx * dx + dy * dy * 4 + dz * dz)); // y weighted less

        offsets.Sort((a, b) => a.dist.CompareTo(b.dist));
        return offsets.Select(o => (o.dx, o.dy, o.dz)).ToArray();
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Chunk Loading";

    public void DrawDebugUi()
    {
        ImGui.Text($"Queued: {_loadQueue.Count}");
        ImGui.Text($"Loaded: {_staticVolume.LoadedCount}");
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

        while (_results.TryDequeue(out var t))
        {
            _inFlight -= 1;
            var (data, pos) = t;
            _staticVolume.AddChunk(pos, data);
        }

        if (!TryGetCameraPos(out var camPos)) return;

        var camChunk = WorldToChunk(camPos);

        // Rebuild the load queue only when the camera crosses a chunk boundary.
        if (camChunk != _lastCamChunk)
        {
            _lastCamChunk = camChunk;
            RebuildLoadQueue(camChunk);
            UnloadDistant(camChunk);
        }

        // Keep the max number in flight as long as there are chunks to load
        while (_inFlight < MaxInFlight && _loadQueue.Count > 0)
        {
            var pos = _loadQueue.Dequeue();
            if (!_staticVolume.IsLoaded(pos))
            {
                _inFlight += 1;
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    var data = new ChunkData();
                    if (!StaticWorldSerializer.TryLoad(SavePath(pos), data))
                        _generator.Value!.Generate(data, pos);
                    data.IsDirty = false;

                    _results.Enqueue((data, pos));
                }, null);
            }
        }

        if (_inFlight > 0)
            Console.WriteLine($"[load] queued={_loadQueue.Count} loaded={_staticVolume.LoadedCount} in flight={_inFlight}");
    }

    private void RebuildLoadQueue(ChunkPosition center)
    {
        _loadQueue.Clear();

        // Spiral outward from the camera for better perceived load-in — offsets are already sorted
        // closest-first (see _offsetsByDistance), so this is just a filter, no per-crossing sort/alloc.
        foreach (var (dx, dy, dz) in _offsetsByDistance)
        {
            var p = center.Offset(dx, dy, dz);
            if (!_staticVolume.IsLoaded(p))
                _loadQueue.Enqueue(p);
        }
    }

    private void UnloadDistant(ChunkPosition center)
    {
        // Collect positions outside the view volume.
        var toUnload = new List<ChunkPosition>();

        foreach (var (pos, _) in _staticVolume.All)
        {
            int dx = System.Math.Abs(pos.X - center.X);
            int dy = System.Math.Abs(pos.Y - center.Y);
            int dz = System.Math.Abs(pos.Z - center.Z);

            if (dx > _xzRadius + 1 || dy > _yRadius + 1 || dz > _xzRadius + 1)
                toUnload.Add(pos);
        }

        foreach (var pos in toUnload)
            Unload(pos);
    }

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
    }

    private string SavePath(ChunkPosition pos) => Path.Combine(_savesDir, $"{pos.X}_{pos.Y}_{pos.Z}.chunk");

    private static ChunkPosition WorldToChunk(Vector3D<float> world) =>
        new((int)System.Math.Floor(world.X / ChunkData.Size),
            (int)System.Math.Floor(world.Y / ChunkData.Size),
            (int)System.Math.Floor(world.Z / ChunkData.Size));
}
