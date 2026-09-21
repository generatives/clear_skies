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
    // Box decomposition averages ~120us per non-empty chunk (see GenerationBenchmark) — much cheaper
    // than meshing, so this can run well ahead of MeshesPerFrame without becoming the new bottleneck.
    private const int CollidersPerFrame = 8;

    private readonly EntitySet         _grids;
    private readonly StaticWorld       _world;
    private readonly PhysicsWorld      _physics;
    private readonly VoxelBoxDecomposer _decomposer = new();
    private readonly List<(Vector3 center, Vector3 size, float mass)> _dynamicBoxes = new();

    // One BigCompound static per non-empty chunk; box count kept only for the debug panel.
    private readonly Dictionary<ChunkPosition, (StaticHandle handle, int boxes)> _colliders = new();
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

            // Drop any existing collider for this chunk before rebuilding.
            if (_colliders.Remove(pos, out var old)) _physics.RemoveStaticCompound(old.handle);

            if (entry.Data.HasAnySolid())
            {
                _sw.Restart();
                var boxes = _decomposer.Decompose(entry.Data);
                if (boxes.Count > 0)
                {
                    var o = pos.WorldOrigin;
                    _colliders[pos] = (_physics.AddStaticCompound(boxes, new PhysVec(o.X, o.Y, o.Z)), boxes.Count);
                }
                long ms = _sw.ElapsedMilliseconds;

                _totalBuilt++;
                built++;

                if (ms > 2)
                    Console.WriteLine($"[collide] chunk {pos} | {boxes.Count} boxes | {ms}ms | total={_totalBuilt}");
            }

            entry.NeedsRecollide = false;
            if (built >= CollidersPerFrame) break;
        }

        // Reconcile: release colliders for chunks that have been unloaded.
        foreach (var pos in _colliders.Keys)
            if (!_world.IsLoaded(pos)) _stale.Add(pos);

        foreach (var pos in _stale)
        {
            if (_colliders.Remove(pos, out var c))
                _physics.RemoveStaticCompound(c.handle);
        }
        _stale.Clear();
    }

    /// <summary>True if <paramref name="pos"/> currently has a static collider registered. Used by
    /// GridPilotSystem's diagnostics to check whether the ground under a falling grid is actually
    /// collidable, as opposed to just loaded/rendered.</summary>
    public bool HasCollider(ChunkPosition pos) => _colliders.ContainsKey(pos);

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
        foreach (var c in _colliders.Values) totalBoxes += c.boxes;

        ImGui.Text($"Chunks with colliders (one BigCompound static each): {_colliders.Count}");
        ImGui.Text($"Total compound child boxes: {totalBoxes}");
        ImGui.Text($"Chunks built (lifetime): {_totalBuilt}");
    }
}
