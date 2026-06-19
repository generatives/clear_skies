using System.Diagnostics;
using BepuPhysics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Builds and maintains BepuPhysics static colliders for the static world's terrain. Reacts to each
/// chunk's <see cref="ChunkEntry.NeedsRecollide"/> flag (set on block edits), rebuilding that chunk's
/// boxes via <see cref="VoxelBoxDecomposer"/>. Owns the per-chunk <see cref="StaticHandle"/> lists
/// itself and reconciles them against the loaded set, so chunk unloads need no physics coupling.
/// </summary>
public sealed class StaticColliderSystem : ISystem
{
    private const int CollidersPerFrame = 4;

    private readonly StaticWorld        _world;
    private readonly PhysicsWorld       _physics;
    private readonly VoxelBoxDecomposer _decomposer = new();

    private readonly Dictionary<ChunkPosition, List<StaticHandle>> _colliders = new();
    private readonly List<ChunkPosition> _stale = new();

    private readonly Stopwatch _sw = new();
    private int _totalBuilt;

    public StaticColliderSystem(StaticWorld world, PhysicsWorld physics)
    {
        _world   = world;
        _physics = physics;
    }

    public void Update(float dt)
    {
        int built = 0;

        foreach (var (pos, entry) in _world.All)
        {
            if (!entry.NeedsRecollide) continue;

            // Drop any existing colliders for this chunk before rebuilding.
            _colliders.TryGetValue(pos, out var handles);
            if (handles is { Count: > 0 }) _physics.RemoveStatics(handles);

            if (entry.Data.HasAnySolid())
            {
                _sw.Restart();
                var boxes = _decomposer.Decompose(entry.Data);
                handles ??= new List<StaticHandle>();
                var o = pos.WorldOrigin;
                _physics.AddStaticBoxes(boxes, new PhysVec(o.X, o.Y, o.Z), handles);
                long ms = _sw.ElapsedMilliseconds;

                _colliders[pos] = handles;
                _totalBuilt++;
                built++;

                if (ms > 2)
                    Console.WriteLine($"[collide] chunk {pos} | {boxes.Count} boxes | {ms}ms | total={_totalBuilt}");
            }
            else
            {
                _colliders.Remove(pos);
            }

            entry.NeedsRecollide = false;
            if (built >= CollidersPerFrame) break;
        }

        // Reconcile: release colliders for chunks that have been unloaded.
        foreach (var pos in _colliders.Keys)
            if (!_world.IsLoaded(pos)) _stale.Add(pos);

        foreach (var pos in _stale)
        {
            if (_colliders.TryGetValue(pos, out var handles))
                _physics.RemoveStatics(handles);
            _colliders.Remove(pos);
        }
        _stale.Clear();
    }
}
