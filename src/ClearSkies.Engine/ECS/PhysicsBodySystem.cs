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
    /// <summary>Static-collider jobs (one chunk each) in flight at once (see UpdateStaticCollider). Streaming adds chunks
    /// in bursts of hundreds, so this is well above the core count: the thread pool queues the excess.</summary>
    private const int MaxInFlight = 64;

    private readonly EntitySet         _dirtyChunks;
    private readonly EntitySet         _cameras;
    private readonly List<Entity>      _nearest = new();
    private readonly HashSet<Entity>   _grids = new();
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

    // Terrain colliders only near what can touch the terrain: the camera (the player) and every dynamic grid (ships).
    // Streaming loads tens of thousands of chunks, each 0.2-2 ms of decomposition on the same workers as generation
    // and meshing, and nothing far away ever collides with them. A chunk needing a collider out of range waits in
    // _pendingStatic until something comes near; a collider left out of range (past a margin, so one at the edge
    // isn't rebuilt back and forth) is dropped and its chunk waits again.
    private const float ColliderRange = 192f, ColliderDropRange = 256f;
    private const int S = ChunkData.Size;
    private readonly EntitySet _dynamicGrids;
    private readonly HashSet<ChunkPosition> _pendingStatic = new();
    private readonly List<Vector3> _centres = new();
    private readonly List<Entity> _near = new(), _far = new();
    private ChunkVolume? _staticVolume;
    private int _sweepFrame;
    private static readonly (int dx, int dy, int dz)[] RangeOffsets = BuildRangeOffsets();

    private static (int, int, int)[] BuildRangeOffsets()
    {
        int r = (int)MathF.Ceiling(ColliderRange / S);
        var list = new List<(int, int, int)>();
        for (int dz = -r; dz <= r; dz++) for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++)
            list.Add((dx, dy, dz));
        return list.ToArray();
    }

    /// <summary>Where colliders are wanted this frame: the camera and each dynamic grid.</summary>
    private void GatherCentres()
    {
        _centres.Clear();
        if (CameraUtil.TryGetActive(_cameras, out var cam)) _centres.Add(new Vector3(cam.Position.X, cam.Position.Y, cam.Position.Z));
        foreach (ref readonly Entity e in _dynamicGrids.GetEntities())
        {
            var p = e.Get<Transform>().Position;
            _centres.Add(new Vector3(p.X, p.Y, p.Z));
        }
    }

    /// <summary>Distance squared from chunk <paramref name="pos"/>'s box to the nearest centre.</summary>
    private float NearestCentreSq(ChunkPosition pos)
    {
        var lo = new Vector3(pos.X * S, pos.Y * S, pos.Z * S);
        float best = float.MaxValue;
        foreach (var c in _centres)
        {
            var d = Vector3.Max(Vector3.Max(lo - c, c - (lo + new Vector3(S))), Vector3.Zero);
            best = MathF.Min(best, d.LengthSquared());
        }
        return best;
    }

    /// <summary>Flags the waiting chunks that are now in range of a centre; every 30 frames, drops the colliders that
    /// are out of range of all of them.</summary>
    private void UpdateStaticRange()
    {
        if (_staticVolume != null && _pendingStatic.Count > 0)
            foreach (var c in _centres)
            {
                int cx = (int)MathF.Floor(c.X / S), cy = (int)MathF.Floor(c.Y / S), cz = (int)MathF.Floor(c.Z / S);
                foreach (var (dx, dy, dz) in RangeOffsets)
                {
                    var pos = new ChunkPosition(cx + dx, cy + dy, cz + dz);
                    if (!_pendingStatic.Contains(pos) || NearestCentreSq(pos) > ColliderRange * ColliderRange) continue;
                    _pendingStatic.Remove(pos);
                    if (_staticVolume.GetEntry(pos) is { } entry) entry.Entity.Set<NeedsRecollideFlag>();
                }
            }

        if (++_sweepFrame < 30) return;
        _sweepFrame = 0;
        List<ChunkPosition>? drop = null;
        foreach (var pos in _colliders.Keys)
            if (NearestCentreSq(pos) > ColliderDropRange * ColliderDropRange) (drop ??= new()).Add(pos);
        if (drop == null) return;
        foreach (var pos in drop)
        {
            _physics.RemoveStaticCompound(_colliders[pos].handle);
            _colliders.Remove(pos);
            _pendingStatic.Add(pos);
        }
    }

    public PhysicsBodySystem(World world, PhysicsWorld physics)
    {
        _physics = physics;
        _dirtyChunks = world.GetEntities().With<Chunk>().With<NeedsRecollideFlag>().AsSet();
        _cameras = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _dynamicGrids = world.GetEntities().With<DynamicGrid>().With<Transform>().AsSet();
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

        // Ship chunks are rebuilt with their grid. Terrain chunks in range of a centre get a collider, closest first; a
        // terrain chunk keeps its flag until its job is actually dispatched (streaming adds chunks far faster than
        // MaxInFlight jobs a frame). One out of range waits in _pendingStatic instead (see UpdateStaticRange).
        GatherCentres();
        _near.Clear();
        _far.Clear();
        foreach (ref readonly Entity entity in _dirtyChunks.GetEntities())
        {
            var entry = entity.Get<Chunk>().Entry;
            if (entry.Volume.Root.Has<DynamicGrid>()) { _grids.Add(entry.Volume.Root); _far.Add(entity); continue; }
            _staticVolume ??= entry.Volume;
            if (NearestCentreSq(entry.Position) <= ColliderRange * ColliderRange) _near.Add(entity);
            else
            {
                // Out of range: an outdated collider there goes too (it would only be rebuilt once something comes near).
                if (_colliders.Remove(entry.Position, out var old)) _physics.RemoveStaticCompound(old.handle);
                _pendingStatic.Add(entry.Position);
                _far.Add(entity);
            }
        }
        foreach (var entity in _far) entity.Remove<NeedsRecollideFlag>();

        if (_near.Count > MaxInFlight - _inFlight)
            _near.Sort((a, b) => NearestCentreSq(a.Get<Chunk>().Entry.Position).CompareTo(NearestCentreSq(b.Get<Chunk>().Entry.Position)));
        foreach (var entity in _near)
        {
            if (!UpdateStaticCollider(entity.Get<Chunk>().Entry)) break; // no job slot free; the rest try next frame
            entity.Remove<NeedsRecollideFlag>();
        }
        UpdateStaticRange();

        foreach (var entity in _grids)
        {
            UpdateDynamicGrid(entity);
        }
    }

    // ── static terrain colliders (moved from StaticColliderSystem) ─────────────
    // Box decomposition and the BigCompound tree build (~0.2-0.35ms per non-empty chunk, spiking past 2ms on
    // dense ones — see StreamingBenchmark) run on thread-pool workers; only the cheap Shapes/Statics adds happen
    // here. One job in flight per chunk, same pattern as ChunkMeshSystem: a chunk re-dirtied mid-job is
    // re-dispatched once that job lands, and a result for a chunk unloaded meanwhile is dropped. A chunk's old
    // collider stays in place until its replacement arrives, so an edit never opens a hole for a frame.
    /// <summary>Starts (or, for an empty chunk, completes) <paramref name="entry"/>'s collider rebuild; false if every
    /// job slot is taken.</summary>
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
        var data = entry.Data;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                // Terrain needs no per-box mass, so boxes may span block types — roughly halves the box count.
                var boxes = _decomposers.Value!.Decompose(data, mergeBlockTypes: true);
                _colliderResults.Enqueue((pos, entry, boxes.Count > 0 ? PhysicsWorld.PrepareStaticCompound(boxes) : null, null));
            }
            catch (Exception e)
            {
                _colliderResults.Enqueue((pos, entry, null, e));
            }
        }, null);
        return true;
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
            if (entry.Volume == _staticVolume) _pendingStatic.Remove(entry.Position);
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
        ImGui.Text($"Terrain chunks waiting until something comes within {ColliderRange:F0} blocks: {_pendingStatic.Count:N0}");
        ImGui.Text($"Total compound child boxes: {totalBoxes}");
        ImGui.Text($"Chunks built (lifetime): {_totalBuilt}");
        ImGui.Text($"Jobs in flight: {_inFlight} / {MaxInFlight}");
        ImGui.Text($"Main-thread add (smoothed): {_applyMs:F3} ms per chunk");
    }
}
