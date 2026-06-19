using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Rebuilds each dynamic grid's BepuPhysics collision shape (and inertia) from its block occupancy
/// whenever <see cref="DynamicGrid.ShapeDirty"/> is set. Runs before <see cref="PhysicsSystem"/> so the
/// body is current before the step. When the centre of mass shifts on rebuild, the body origin is
/// moved to track it so existing geometry stays fixed in world space.
/// </summary>
public sealed class GridShapeSystem : ISystem
{
    private readonly EntitySet         _grids;
    private readonly PhysicsWorld      _physics;
    private readonly VoxelBoxDecomposer _decomposer = new();
    private readonly List<(Vector3 center, Vector3 size)> _boxes = new();

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

            // Gather merged boxes across all chunks, expressed in grid-local space.
            _boxes.Clear();
            foreach (var (pos, entry) in grid.All)
            {
                if (!entry.Data.HasAnySolid()) continue;
                var o = pos.WorldOrigin;
                foreach (var (c, s) in _decomposer.Decompose(entry.Data))
                    _boxes.Add((new Vector3(o.X + c.X, o.Y + c.Y, o.Z + c.Z), s));
            }

            if (_boxes.Count == 0)
            {
                grid.ShapeDirty = false; // nothing solid yet; leave any existing body untouched
                continue;
            }

            var (shape, inertia, com) = _physics.BuildDynamicCompound(_boxes);

            if (!grid.BodyCreated)
            {
                grid.Body        = _physics.AddDynamicBody(shape, inertia, grid.SpawnPosition);
                grid.CenterOfMass = com;
                grid.BodyCreated  = true;
            }
            else
            {
                // Preserve world geometry as the local CoM moves: shift the body origin by the rotated delta.
                var (pos, orient) = _physics.GetBodyPose(grid.Body);
                var worldShift = Vector3.Transform(com - grid.CenterOfMass, orient);
                var oldShape = _physics.GetBodyShape(grid.Body);

                _physics.SetBodyShape(grid.Body, shape, inertia);
                _physics.SetBodyPose(grid.Body, pos + worldShift, orient);
                _physics.RemoveCompound(oldShape);

                grid.CenterOfMass = com;
            }

            grid.ShapeDirty = false;
        }
    }
}
