using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using BepuPhysics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Owns every BepuPhysics body/collider create-or-rebuild call in the game (reacting to <see cref="NeedsRecollideFlag"/>),
/// and removes a disposed entity's <see cref="PhysicsBodyComponent"/> body and shape. Both halves decompose their own voxel occupancy into boxes
/// internally — merged from two previously separate systems (GridShapeSystem, StaticColliderSystem)
/// because nothing outside each pipeline ever read the intermediate box list, so splitting "decompose"
/// and "call physics" across two systems communicating via a component would only have added
/// indirection (see AirshipFlightSystem's doc comment for the same judgment made elsewhere in this
/// codebase).
/// </summary>
public sealed class PhysicsBodySystem : ISystem, IDebugUiSystem
{
    /// <summary>Static-collider jobs in flight at once (see UpdateStaticCollider), and terrain chunks per job: streaming
    /// adds chunks in bursts, and a job per chunk per frame couldn't keep up.</summary>
    private static readonly int MaxJobs = System.Math.Max(2, Environment.ProcessorCount / 2);
    private const int ChunksPerJob = 8;
    private static readonly int MaxInFlight = MaxJobs * ChunksPerJob; // chunks

    private readonly EntitySet         _dirtyChunks;
    private readonly EntitySet         _cameras;
    private readonly List<Entity>      _nearest = new();
    private readonly HashSet<Entity>   _grids = new();
    private readonly List<ChunkEntry>  _batch = new();
    private readonly PhysicsWorld      _physics;
    private readonly VoxelBoxDecomposer _decomposer = new(); // dynamic grids, main thread
    private readonly ThreadLocal<VoxelBoxDecomposer> _decomposers = new(() => new VoxelBoxDecomposer()); // terrain workers
    private int _inFlight = 0;
    private readonly ConcurrentQueue<(ChunkPosition Pos, ChunkEntry Entry, PhysicsWorld.StaticCompoundBuild? Build, Exception? Error)> _colliderResults = new();
    private double _applyMs;
    private readonly List<(Vector3 center, Vector3 size, float mass)> _dynamicBoxes = new();

    // One BigCompound static per non-empty chunk; box count kept only for the debug panel.
    private readonly Dictionary<ChunkPosition, (StaticHandle handle, int boxes)> _colliders = new();
    private readonly List<BodyHandle> _removedBodies = new();
    private readonly List<ChunkEntry> _removedChunks = new();

    private readonly Stopwatch _sw = new();
    private int _totalBuilt;

    public PhysicsBodySystem(World world, PhysicsWorld physics)
    {
        _physics = physics;
        _dirtyChunks = world.GetEntities().With<Chunk>().With<NeedsRecollideFlag>().AsSet();
        _cameras = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        world.SubscribeEntityDisposed(OnEntityDisposed);
    }

    private void OnEntityDisposed(in Entity entity)
    {
        if (entity.Has<PhysicsBodyComponent>())
            _removedBodies.Add(entity.Get<PhysicsBodyComponent>().Body);

        if (entity.Has<Chunk>())
        {
            var chunk = entity.Get<Chunk>();
            _removedChunks.Add(chunk.Entry);
        }
    }

    public void Update(float dt)
    {
        ApplyColliderResults();
        UpdateColliders();
        Cleanup();
    }

    public void UpdateColliders()
    {
        _grids.Clear();

        // Closest to the camera first (ship chunks before any terrain, see NearestChunks), so the ground under the
        // player gets its collider before the far side of the island does. A terrain chunk keeps its flag until its
        // job is actually dispatched: streaming adds chunks far faster than MaxInFlight jobs a frame.
        NearestChunks.Select(_dirtyChunks, _cameras, MaxInFlight - _inFlight + 8, _nearest);
        foreach (var entity in _nearest)
        {
            var entry = entity.Get<Chunk>().Entry;
            var volumeEntity = entry.Volume.Root;

            if (volumeEntity.Has<DynamicGrid>())
                _grids.Add(volumeEntity);
            else if (!UpdateStaticCollider(entry))
                continue; // no job slot free; try again next frame

            entity.Remove<NeedsRecollideFlag>();
        }

        DispatchBatch();

        foreach (var entity in _grids)
        {
            UpdateDynamicGrid(entity);
        }
    }

    // ── static terrain colliders (moved from StaticColliderSystem) ─────────────
    // Box decomposition and the BigCompound tree build (~0.2-0.35ms per non-empty chunk, spiking past 2ms on
    // dense ones — see StreamingBenchmark) run on thread-pool workers; only the cheap Shapes/Statics adds happen
    // here. At most one job holds a given chunk, same pattern as ChunkMeshSystem: a chunk re-dirtied mid-job is
    // re-dispatched once that job lands, and a result for a chunk unloaded meanwhile is dropped. A chunk's old
    // collider stays in place until its replacement arrives, so an edit never opens a hole for a frame.
    /// <summary>Queues (or, for an empty chunk, completes) <paramref name="entry"/>'s collider rebuild for
    /// <see cref="DispatchBatch"/>; false if every job slot is taken.</summary>
    private bool UpdateStaticCollider(ChunkEntry entry)
    {
        var pos = entry.Position;

        if (!entry.Data.HasAnySolid())
        {
            if (_colliders.Remove(pos, out var old)) _physics.RemoveStaticCompound(old.handle);
            return true;
        }

        if (_inFlight >= MaxInFlight) return false;

        _inFlight += 1;
        _batch.Add(entry);
        return true;
    }

    /// <summary>Starts jobs for the chunks <see cref="UpdateStaticCollider"/> queued this frame, up to
    /// <see cref="ChunksPerJob"/> each.</summary>
    private void DispatchBatch()
    {
        for (int start = 0; start < _batch.Count; start += ChunksPerJob)
        {
            var job = _batch.GetRange(start, System.Math.Min(ChunksPerJob, _batch.Count - start));
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                foreach (var entry in job)
                {
                    try
                    {
                        // Terrain needs no per-box mass, so boxes may span block types — roughly halves the box count.
                        var boxes = _decomposers.Value!.Decompose(entry.Data, mergeBlockTypes: true);
                        _colliderResults.Enqueue((entry.Position, entry,
                            boxes.Count > 0 ? PhysicsWorld.PrepareStaticCompound(boxes) : null, null));
                    }
                    catch (Exception e)
                    {
                        _colliderResults.Enqueue((entry.Position, entry, null, e));
                    }
                }
            }, null);
        }
        _batch.Clear();
    }

    private void ApplyColliderResults()
    {
        while (_colliderResults.TryDequeue(out var r))
        {
            _inFlight -= 1;
            if (r.Error is not null)
            {
                Console.WriteLine($"[collide] chunk {r.Pos} failed: {r.Error}");
                continue;
            }

            if (!r.Entry.Entity.IsAlive) continue; // unloaded (or unloaded and reloaded) meanwhile

            _sw.Restart();
            if (_colliders.Remove(r.Pos, out var old)) _physics.RemoveStaticCompound(old.handle);
            if (r.Build is not null)
            {
                var o = r.Pos.WorldOrigin;
                _colliders[r.Pos] = (_physics.AddStaticCompound(r.Build, new PhysVec(o.X, o.Y, o.Z)), r.Build.BoxCount);
            }
            _applyMs += 0.05 * (_sw.Elapsed.TotalMilliseconds - _applyMs);
            _totalBuilt++;
        }
    }

    /// <summary>True if <paramref name="pos"/> currently has a static collider registered. Used by
    /// GridPilotSystem's diagnostics to check whether the ground under a falling grid is actually
    /// collidable, as opposed to just loaded/rendered.</summary>
    public bool HasCollider(ChunkPosition pos) => _colliders.ContainsKey(pos);

    // ── dynamic grid bodies (moved from GridShapeSystem) ────────────────────────
    private void UpdateDynamicGrid(Entity entity)
    {
        // Gather merged boxes across all chunks, expressed in grid-local space. Each box is
        // homogeneous in BlockId (see VoxelBoxDecomposer), so its mass is volume * that block's
        // Weight — real per-block-type density instead of uniform volume.
        _dynamicBoxes.Clear();

        var chunkVolume = entity.Get<ChunkGrid>().Volume;
        ref var grid = ref entity.Get<DynamicGrid>();

        foreach (var (pos, entry) in chunkVolume.All)
        {
            if (!entry.Data.HasAnySolid()) continue;
            var o = pos.WorldOrigin;
            foreach (var (c, s, id) in _decomposer.Decompose(entry.Data))
            {
                float volume = s.X * s.Y * s.Z;
                _dynamicBoxes.Add((new Vector3(o.X + c.X, o.Y + c.Y, o.Z + c.Z), s, volume * BlockRegistry.Get(id).Weight));
            }
        }

        if (_dynamicBoxes.Count == 0)
        {
            return;
        }

        var (shape, inertia, com) = _physics.BuildDynamicCompound(_dynamicBoxes);
        grid.Inertia = inertia;

        // Bepu recentres the compound on its centre of mass, so the body origin — and with it the grid's
        // Transform — is the CoM, and the volume's pivot follows it. Either way the grid must not move in the
        // world as the pivot moves: the body origin goes wherever the new pivot currently is.
        var oldPivot = PhysicsConv.ToBepu(chunkVolume.Pivot);
        if (!entity.Has<PhysicsBodyComponent>())
        {
            // First solid block: place the body under the grid's current Transform (where it was spawned).
            // Grids default to Locked (see DynamicGrid.Locked) so they don't immediately fall under
            // gravity when spawned; the body is created kinematic (zero inertia) in that case, same
            // as the rebuild branch below.
            ref readonly var t = ref entity.Get<Transform>();
            var orient = PhysicsConv.ToBepu(t.Rotation);
            var pos    = PhysicsConv.ToBepu(t.Position) + Vector3.Transform(com - oldPivot, orient);
            var body   = _physics.AddDynamicBody(shape, grid.Locked ? default : inertia, pos, orient);
            entity.Set(new PhysicsBodyComponent { Body = body });
        }
        else
        {
            // The body pose, not the Transform: something may have set it since the last sync.
            var body = entity.Get<PhysicsBodyComponent>().Body;
            var (pos, orient) = _physics.GetBodyPose(body);
            var worldShift = Vector3.Transform(com - oldPivot, orient);
            var oldShape = _physics.GetBodyShape(body);

            // While locked, keep the body's actual physics inertia zeroed (kinematic) even though
            // the shape/geometry updates — grid.Inertia (above) still tracks the real value for
            // GridPilotSystem to restore on unlock.
            _physics.SetBodyShape(body, shape, grid.Locked ? default : inertia);
            _physics.SetBodyPose(body, pos + worldShift, orient);
            _physics.RemoveCompound(oldShape);
        }
        chunkVolume.Pivot = PhysicsConv.ToSilk(com);
    }

    private void Cleanup()
    {
        foreach (var body in _removedBodies)
        {
            var shape = _physics.GetBodyShape(body);
            _physics.RemoveBody(body);
            _physics.RemoveCompound(shape);
        }
        _removedBodies.Clear();

        foreach (var entry in _removedChunks)
        {
            if (_colliders.Remove(entry.Position, out var c))
                _physics.RemoveStaticCompound(c.handle);
        }
        _removedChunks.Clear();
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Static Colliders";

    public void DrawDebugUi()
    {
        int totalBoxes = 0;
        foreach (var c in _colliders.Values) totalBoxes += c.boxes;

        ImGui.Text($"Chunks with colliders (one BigCompound static each): {_colliders.Count}");
        ImGui.Text($"Total compound child boxes: {totalBoxes}");
        ImGui.Text($"Chunks built (lifetime): {_totalBuilt}");
        ImGui.Text($"Chunks in flight: {_inFlight} / {MaxInFlight} ({ChunksPerJob} per job, up to {MaxJobs} jobs)");
        ImGui.Text($"Main-thread add (smoothed): {_applyMs:F3} ms per chunk");
    }
}
