using System.Linq;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Generation;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Tracks the active camera's chunk position each frame and queues chunk load/unload
/// operations so only a small batch is processed per frame (throttled by LoadsPerFrame).
/// </summary>
public sealed class ChunkLoadSystem : ISystem, IDebugUiSystem
{
    // Generate() now averages ~100us (p99 ~1.1ms) per chunk after the meshing/generation perf pass —
    // see GenerationBenchmark. 16/frame budgets ~1.6ms typical, leaving headroom for meshing/physics/GPU
    // work in the same frame; was 4 when the pipeline was slower.
    private const int LoadsPerFrame = 16;

    /// <summary>Seconds between periodic flushes of any currently-loaded dirty chunks — crash/power-loss
    /// safety for edits to chunks that stay loaded (never unload) for a long time.</summary>
    private const float AutosaveInterval = 30f;

    private readonly EntitySet      _cameras;
    private readonly StaticWorld    _manager;
    private readonly IWorldGenerator _generator;
    private readonly int            _xzRadius;
    private readonly int            _yRadius;

    /// <summary>Every (dx,dy,dz) offset within the load radii, precomputed once and sorted closest-first
    /// (same weighting as before: y counts 4x). The shape never changes at runtime, so RebuildLoadQueue
    /// just filters this instead of re-collecting and re-sorting the whole radius on every chunk-boundary
    /// crossing — that sort was O(volume log volume) with a fresh allocation each time, paid every time the
    /// camera crosses into a new chunk, and volume grows as (2*xzRadius+1)^2*(2*yRadius+1).</summary>
    private readonly (int dx, int dy, int dz)[] _offsetsByDistance;

    private readonly Queue<ChunkPosition> _loadQueue = new();
    private ChunkPosition _lastCamChunk = new(int.MinValue, int.MinValue, int.MinValue);
    private float _autosaveTimer;

    public ChunkLoadSystem(World world, StaticWorld manager, IWorldGenerator generator,
                           int xzRadius = 5, int yRadius = 2)
    {
        _cameras   = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _manager   = manager;
        _generator = generator;
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
        ImGui.Text($"Loaded: {_manager.LoadedCount}");
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
            _manager.SaveAllDirty();
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

        // Process a small batch of the load queue each frame.
        int processed = 0;
        while (processed < LoadsPerFrame && _loadQueue.Count > 0)
        {
            var pos = _loadQueue.Dequeue();
            if (!_manager.IsLoaded(pos))
            {
                _manager.Load(pos, _generator);
                processed++;
            }
        }

        if (processed > 0)
            Console.WriteLine($"[load] queued={_loadQueue.Count} loaded={_manager.LoadedCount} loaded_this_frame={processed}");
    }

    private void RebuildLoadQueue(ChunkPosition center)
    {
        _loadQueue.Clear();

        // Spiral outward from the camera for better perceived load-in — offsets are already sorted
        // closest-first (see _offsetsByDistance), so this is just a filter, no per-crossing sort/alloc.
        foreach (var (dx, dy, dz) in _offsetsByDistance)
        {
            var p = center.Offset(dx, dy, dz);
            if (!_manager.IsLoaded(p))
                _loadQueue.Enqueue(p);
        }
    }

    private void UnloadDistant(ChunkPosition center)
    {
        // Collect positions outside the view volume.
        var toUnload = new List<ChunkPosition>();

        foreach (var (pos, _) in _manager.All)
        {
            int dx = System.Math.Abs(pos.X - center.X);
            int dy = System.Math.Abs(pos.Y - center.Y);
            int dz = System.Math.Abs(pos.Z - center.Z);

            if (dx > _xzRadius + 1 || dy > _yRadius + 1 || dz > _xzRadius + 1)
                toUnload.Add(pos);
        }

        foreach (var pos in toUnload)
            _manager.Unload(pos);
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

    private static ChunkPosition WorldToChunk(Vector3D<float> world) =>
        new((int)System.Math.Floor(world.X / ChunkData.Size),
            (int)System.Math.Floor(world.Y / ChunkData.Size),
            (int)System.Math.Floor(world.Z / ChunkData.Size));
}
