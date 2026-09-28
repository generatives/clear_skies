using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using BepuPhysics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Physics.Characters;
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
    /// in bursts of hundreds, so this is well above the core count: the background workers (see BackgroundWork) queue the excess.</summary>
    private const int MaxInFlight = 64;

    private readonly EntitySet         _dirtyChunks;
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

    // Which terrain chunks get colliders (those near something that can touch the terrain) is decided by
    // EntityPresenceSystem, as a Static PhysicsPresence; this system builds and drops them to match.
    private const int S = ChunkData.Size;
    private readonly List<Entity> _near = new(), _far = new();
    private ChunkVolume? _staticVolume;
    private readonly EntitySet _terrainGainedPresence;
    private readonly EntitySet _terrainLostPresence;
    private readonly EntitySet _gridsGainedPresence;
    private readonly EntitySet _gridsLostPresence;
    private readonly EntitySet _bodyOverrides;
    private readonly List<PlayerCharacter> _removedCharacters = new();

    public PhysicsBodySystem(World world, PhysicsWorld physics)
    {
        _physics = physics;
        _dirtyChunks = world.GetEntities().With<Chunk>().With<NeedsRecollideFlag>().AsSet();
        _terrainGainedPresence = world.GetEntities().With<Chunk>().With<OwnPresence>().WhenAdded<PhysicsPresence>().AsSet();
        _terrainLostPresence = world.GetEntities().With<Chunk>().With<OwnPresence>().WhenRemoved<PhysicsPresence>().AsSet();
        _gridsGainedPresence = world.GetEntities().With<DynamicGrid>().With<ChunkGrid>().WhenAdded<PhysicsPresence>().WhenChanged<PhysicsPresence>().AsSet();
        _gridsLostPresence = world.GetEntities().With<DynamicGrid>().With<PhysicsBodyComponent>().WhenRemoved<PhysicsPresence>().AsSet();
        _bodyOverrides = world.GetEntities().With<BodyStateOverride>().With<PhysicsBodyComponent>().With<Transform>().AsSet();
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

        if (entity.Has<CharacterControllerComponent>())
            _removedCharacters.Add(entity.Get<CharacterControllerComponent>().Character);
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

        // Terrain chunks that just gained a collider presence need one built; those that lost it drop theirs.
        foreach (ref readonly Entity entity in _terrainGainedPresence.GetEntities()) entity.Set<NeedsRecollideFlag>();
        _terrainGainedPresence.Complete();
        foreach (ref readonly Entity entity in _terrainLostPresence.GetEntities())
            if (_colliders.Remove(entity.Get<Chunk>().Entry.Position, out var old)) _physics.RemoveStaticCompound(old.handle);
        _terrainLostPresence.Complete();

        // Grids: a body while simulated here, none otherwise.
        foreach (ref readonly Entity entity in _gridsGainedPresence.GetEntities()) _grids.Add(entity);
        _gridsGainedPresence.Complete();
        foreach (var entity in _gridsLostPresence.GetEntities().ToArray())
        {
            _removedBodies.Add(entity.Get<PhysicsBodyComponent>().Body);
            entity.Remove<PhysicsBodyComponent>();
        }
        _gridsLostPresence.Complete();

        // Ship chunks are rebuilt with their grid. Terrain chunks with a collider presence get a collider, oldest request
        // first; a chunk keeps its flag until its job is actually dispatched (streaming can add chunks far faster than
        // MaxInFlight jobs a tick). One without a presence just drops the flag: it's built if it gains one.
        _near.Clear();
        _far.Clear();
        foreach (ref readonly Entity entity in _dirtyChunks.GetEntities())
        {
            var entry = entity.Get<Chunk>().Entry;
            if (entry.Volume.Root.Has<DynamicGrid>()) { _grids.Add(entry.Volume.Root); _far.Add(entity); continue; }
            _staticVolume ??= entry.Volume;
            if (entity.Has<PhysicsPresence>()) _near.Add(entity);
            else
            {
                if (_colliders.Remove(entry.Position, out var old)) _physics.RemoveStaticCompound(old.handle);
                _far.Add(entity);
            }
        }
        foreach (var entity in _far) entity.Remove<NeedsRecollideFlag>();

        foreach (var entity in _near)
        {
            if (!UpdateStaticCollider(entity.Get<Chunk>().Entry)) break; // no job slot free; the rest try next tick
            entity.Remove<NeedsRecollideFlag>();
        }

        // A grid's body: simulated here, or a kinematic copy following its owner's snapshots. A grid with neither has no
        // body here: its Transform alone places it.
        foreach (var entity in _grids)
        {
            if (!entity.IsAlive) continue;
            var mode = entity.Has<PhysicsPresence>() ? entity.Get<PhysicsPresence>().Mode : (PhysicsMode?)null;
            UpdateDynamicGrid(entity, withBody: mode is PhysicsMode.Simulated or PhysicsMode.KinematicFollower,
                              kinematic: mode == PhysicsMode.KinematicFollower);
        }

        // Spawned or overwritten from a description: the body takes the described pose and velocities.
        foreach (var entity in _bodyOverrides.GetEntities().ToArray())
        {
            ref readonly var pb = ref entity.Get<PhysicsBodyComponent>();
            var body = pb.Body;
            ref readonly var t = ref entity.Get<Transform>();
            var o = entity.Get<BodyStateOverride>();
            _physics.SetBodyPose(body, pb.BodyPosition(t), PhysicsConv.ToBepu(t.Rotation));
            if (_physics.GetBodyMass(body) > 0)
            {
                _physics.SetBodyLinearVelocity(body, o.LinearVelocity);
                _physics.SetBodyAngularVelocity(body, o.AngularVelocity);
            }
            entity.Remove<BodyStateOverride>();
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
        BackgroundWork.Soon(() =>
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
        });
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
            if (!r.Entry.Entity.Has<PhysicsPresence>()) continue; // out of range meanwhile

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

    /// <summary>Whether every loaded terrain chunk with blocks within <paramref name="radius"/> of
    /// <paramref name="centre"/> has its collider, so a body placed there won't fall through.</summary>
    public bool CollidersReady(ChunkVolume world, PhysVec centre, float radius)
    {
        int r = (int)MathF.Ceiling(radius / S);
        int cx = (int)MathF.Floor(centre.X / S), cy = (int)MathF.Floor(centre.Y / S), cz = (int)MathF.Floor(centre.Z / S);
        for (int dz = -r; dz <= r; dz++) for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++)
        {
            var pos = new ChunkPosition(cx + dx, cy + dy, cz + dz);
            if (world.GetEntry(pos) is { } entry && entry.Data.HasAnySolid() && !_colliders.ContainsKey(pos)) return false;
        }
        return true;
    }

    // ── dynamic grid bodies (moved from GridShapeSystem) ────────────────────────
    private void UpdateDynamicGrid(Entity entity, bool withBody, bool kinematic)
    {
        if (!withBody)
        {
            // No body here; the grid is placed by its Transform alone.
            if (entity.Has<PhysicsBodyComponent>())
            {
                _removedBodies.Add(entity.Get<PhysicsBodyComponent>().Body);
                entity.Remove<PhysicsBodyComponent>();
            }
            return;
        }

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
        bool zeroInertia = grid.Locked || kinematic;

        // Bepu recentres the compound on its centre of mass, so the body origin is the CoM, which sits at the body's
        // Offset inside the grid's block space. The grid's Transform is that block space and must not move as blocks
        // change: only the body moves, to wherever the new CoM is.
        if (!entity.Has<PhysicsBodyComponent>())
        {
            // First solid block: place the body at the CoM within the grid's current Transform (where it was spawned).
            // Grids default to Locked (see DynamicGrid.Locked) so they don't immediately fall under
            // gravity when spawned; the body is created kinematic (zero inertia) in that case, same
            // as the rebuild branch below.
            ref readonly var t = ref entity.Get<Transform>();
            var orient = PhysicsConv.ToBepu(t.Rotation);
            var pos    = PhysicsConv.ToBepu(t.Position) + Vector3.Transform(com, orient);
            var body   = _physics.AddDynamicBody(shape, zeroInertia ? default : inertia, pos, orient,
                                                 new ColliderInfo(ColliderKind.VoxelGrid, entity));
            entity.Set(new PhysicsBodyComponent { Body = body, Offset = PhysicsConv.ToSilk(com) });
        }
        else
        {
            // The body pose, not the Transform: something may have set it since the last sync. The block space stays
            // put, so the body moves by however far the CoM moved within it.
            ref var pb = ref entity.Get<PhysicsBodyComponent>();
            var body = pb.Body;
            var (pos, orient) = _physics.GetBodyPose(body);
            var worldShift = Vector3.Transform(com - PhysicsConv.ToBepu(pb.Offset), orient);
            pb.Offset = PhysicsConv.ToSilk(com);
            var oldShape = _physics.GetBodyShape(body);

            // While locked, keep the body's actual physics inertia zeroed (kinematic) even though
            // the shape/geometry updates — grid.Inertia (above) still tracks the real value for
            // GridPilotSystem to restore on unlock.
            _physics.SetBodyShape(body, shape, zeroInertia ? default : inertia);
            _physics.SetBodyPose(body, pos + worldShift, orient);
            _physics.RemoveCompound(oldShape);
        }
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

        foreach (var character in _removedCharacters) character.Dispose();
        _removedCharacters.Clear();

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
        ImGui.Text($"Jobs in flight: {_inFlight} / {MaxInFlight}");
        ImGui.Text($"Main-thread add (smoothed): {_applyMs:F3} ms per chunk");
    }
}
