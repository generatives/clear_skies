using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Rebuilds each dynamic grid's BepuPhysics collision shape (and inertia) from its block occupancy
/// whenever <see cref="DynamicGrid.ShapeDirty"/> is set. Runs before <see cref="PhysicsWorld"/> steps so the
/// body is current before the step. When the centre of mass shifts on rebuild, the body origin is
/// moved to track it so existing geometry stays fixed in world space.
/// </summary>
public sealed class GridShapeSystem : ISystem
{
    private readonly EntitySet         _grids;
    private readonly PhysicsWorld      _physics;
    private readonly VoxelBoxDecomposer _decomposer = new();
    private readonly List<(Vector3 center, Vector3 size, float mass)> _boxes = new();

    public GridShapeSystem(World world, PhysicsWorld physics)
    {
        _physics = physics;
        _grids   = world.GetEntities().With<DynamicGridComponent>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _grids.GetEntities())
        {
            var grid = e.Get<DynamicGridComponent>().Grid;
            if (!grid.ShapeDirty) continue;

            // Gather merged boxes across all chunks, expressed in grid-local space. Each box is
            // homogeneous in BlockId (see VoxelBoxDecomposer), so its mass is volume * that block's
            // Weight — real per-block-type density instead of uniform volume. Also tally Buoyant voxel
            // count here (AirshipControlSystem's feedforward) since we're already walking every box.
            _boxes.Clear();
            int buoyantCount = 0;
            foreach (var (pos, entry) in grid.All)
            {
                if (!entry.Data.HasAnySolid()) continue;
                var o = pos.WorldOrigin;
                foreach (var (c, s, id) in _decomposer.Decompose(entry.Data))
                {
                    float volume = s.X * s.Y * s.Z;
                    if (id == BlockId.Buoyant) buoyantCount += (int)volume;
                    _boxes.Add((new Vector3(o.X + c.X, o.Y + c.Y, o.Z + c.Z), s, volume * BlockRegistry.Get(id).Weight));
                }
            }
            grid.BuoyantBlockCount = buoyantCount;

            if (_boxes.Count == 0)
            {
                grid.ShapeDirty = false; // nothing solid yet; leave any existing body untouched
                continue;
            }

            var (shape, inertia, com) = _physics.BuildDynamicCompound(_boxes);
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
}
