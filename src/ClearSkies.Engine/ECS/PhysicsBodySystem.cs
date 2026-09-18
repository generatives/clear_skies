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
/// Owns every BepuPhysics body/collider create-or-rebuild call in the game: static terrain colliders
/// (reacting to <see cref="ChunkEntry.NeedsRecollide"/>) and dynamic grid bodies (reacting to
/// <see cref="DynamicGrid.ShapeDirty"/>). Both halves decompose their own voxel occupancy into boxes
/// internally — merged from two previously separate systems (GridShapeSystem, StaticColliderSystem)
/// because nothing outside each pipeline ever read the intermediate box list, so splitting "decompose"
/// and "call physics" across two systems communicating via a component would only have added
/// indirection (see AirshipFlightSystem's doc comment for the same judgment made elsewhere in this
/// codebase).
/// </summary>
public sealed class PhysicsBodySystem : ISystem, IDebugUiSystem
{
    private const int CollidersPerFrame = 4;

    private readonly EntitySet         _grids;
    private readonly StaticWorld       _world;
    private readonly PhysicsWorld      _physics;
    private readonly VoxelBoxDecomposer _decomposer = new();
    private readonly List<(Vector3 center, Vector3 size, float mass)> _dynamicBoxes = new();

    private readonly Dictionary<ChunkPosition, List<StaticHandle>> _colliders = new();
    private readonly List<ChunkPosition> _stale = new();

    private readonly Stopwatch _sw = new();
    private int _totalBuilt;

    public PhysicsBodySystem(World world, StaticWorld staticWorld, PhysicsWorld physics)
    {
        _world   = staticWorld;
        _physics = physics;
        _grids   = world.GetEntities().With<DynamicGridComponent>().AsSet();
    }

    public void Update(float dt)
    {
        UpdateStaticColliders();
        UpdateDynamicGrids();
    }

    // ── static terrain colliders (moved from StaticColliderSystem) ─────────────
    private void UpdateStaticColliders()
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
                _physics.AddStaticBoxes(boxes.ConvertAll(b => (b.center, b.size)), new PhysVec(o.X, o.Y, o.Z), handles);
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

    /// <summary>True if <paramref name="pos"/> currently has at least one static collider box
    /// registered. Used by GridPilotSystem's diagnostics to check whether the ground under a falling
    /// grid is actually collidable, as opposed to just loaded/rendered.</summary>
    public bool HasCollider(ChunkPosition pos) => _colliders.TryGetValue(pos, out var h) && h.Count > 0;

    // ── dynamic grid bodies (moved from GridShapeSystem) ────────────────────────
    private void UpdateDynamicGrids()
    {
        foreach (ref readonly Entity e in _grids.GetEntities())
        {
            var grid = e.Get<DynamicGridComponent>().Grid;
            if (!grid.ShapeDirty) continue;

            // Gather merged boxes across all chunks, expressed in grid-local space. Each box is
            // homogeneous in BlockId (see VoxelBoxDecomposer), so its mass is volume * that block's
            // Weight — real per-block-type density instead of uniform volume. Also tally Buoyant voxel
            // count here (AirshipFlightSystem's feedforward) since we're already walking every box.
            _dynamicBoxes.Clear();
            int buoyantCount = 0;
            foreach (var (pos, entry) in grid.All)
            {
                if (!entry.Data.HasAnySolid()) continue;
                var o = pos.WorldOrigin;
                foreach (var (c, s, id) in _decomposer.Decompose(entry.Data))
                {
                    float volume = s.X * s.Y * s.Z;
                    if (id == BlockId.Buoyant) buoyantCount += (int)volume;
                    _dynamicBoxes.Add((new Vector3(o.X + c.X, o.Y + c.Y, o.Z + c.Z), s, volume * BlockRegistry.Get(id).Weight));
                }
            }
            grid.BuoyantBlockCount = buoyantCount;

            if (_dynamicBoxes.Count == 0)
            {
                grid.ShapeDirty = false; // nothing solid yet; leave any existing body untouched
                continue;
            }

            var (shape, inertia, com) = _physics.BuildDynamicCompound(_dynamicBoxes);
            grid.Inertia = inertia;

            if (!grid.BodyCreated)
            {
                // Grids default to Locked (see DynamicGrid.Locked) so they don't immediately fall under
                // gravity when spawned; the body is created kinematic (zero inertia) in that case, same
                // as the rebuild branch below.
                grid.Body        = _physics.AddDynamicBody(shape, grid.Locked ? default : inertia, grid.SpawnPosition);
                grid.CenterOfMass = com;
                grid.BodyCreated  = true;
            }
            else
            {
                // Preserve world geometry as the local CoM moves: shift the body origin by the rotated delta.
                var (pos, orient) = _physics.GetBodyPose(grid.Body);
                var worldShift = Vector3.Transform(com - grid.CenterOfMass, orient);
                var oldShape = _physics.GetBodyShape(grid.Body);

                // While locked, keep the body's actual physics inertia zeroed (kinematic) even though
                // the shape/geometry updates — grid.Inertia (above) still tracks the real value for
                // GridPilotSystem to restore on unlock.
                _physics.SetBodyShape(grid.Body, shape, grid.Locked ? default : inertia);
                _physics.SetBodyPose(grid.Body, pos + worldShift, orient);
                _physics.RemoveCompound(oldShape);

                grid.CenterOfMass = com;
            }

            grid.ShapeDirty = false;
        }
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Static Colliders";

    public void DrawDebugUi()
    {
        int totalBoxes = 0;
        foreach (var handles in _colliders.Values) totalBoxes += handles.Count;

        ImGui.Text($"Chunks with colliders: {_colliders.Count}");
        ImGui.Text($"Total static collider boxes: {totalBoxes}");
        ImGui.Text($"Chunks built (lifetime): {_totalBuilt}");
    }
}
