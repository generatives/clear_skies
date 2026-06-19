using ClearSkies.Engine.Core;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Generation;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Tracks the active camera's chunk position each frame and queues chunk load/unload
/// operations so only a small batch is processed per frame (throttled by LoadsPerFrame).
/// </summary>
public sealed class ChunkLoadSystem : ISystem
{
    private const int LoadsPerFrame = 4;

    private readonly EntitySet      _cameras;
    private readonly StaticWorld    _manager;
    private readonly IWorldGenerator _generator;
    private readonly int            _xzRadius;
    private readonly int            _yRadius;

    private readonly Queue<ChunkPosition> _loadQueue = new();
    private ChunkPosition _lastCamChunk = new(int.MinValue, int.MinValue, int.MinValue);

    public ChunkLoadSystem(World world, StaticWorld manager, IWorldGenerator generator,
                           int xzRadius = 5, int yRadius = 2)
    {
        _cameras   = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _manager   = manager;
        _generator = generator;
        _xzRadius  = xzRadius;
        _yRadius   = yRadius;
    }

    public void Update(float dt)
    {
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

        // Spiral outward from the camera for better perceived load-in.
        var candidates = new List<(ChunkPosition pos, int dist)>();

        for (int dy = -_yRadius; dy <= _yRadius; dy++)
        for (int dx = -_xzRadius; dx <= _xzRadius; dx++)
        for (int dz = -_xzRadius; dz <= _xzRadius; dz++)
        {
            var p = center.Offset(dx, dy, dz);
            if (!_manager.IsLoaded(p))
                candidates.Add((p, dx * dx + dy * dy * 4 + dz * dz)); // y weighted less
        }

        // Sort closest-first so the camera's immediate surroundings load first.
        candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

        foreach (var (pos, _) in candidates)
            _loadQueue.Enqueue(pos);
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
