using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Input;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Test-harness controls that apply only to the <see cref="SelectedGridComponent">Selected Grid</see>.
/// Arrow keys push horizontally (world X/Z), Page Up / Page Down push vertically, and End halts all
/// motion. Impulses are scaled by the grid's mass so the applied acceleration is consistent regardless
/// of grid size. Runs just before <see cref="PhysicsWorld"/> steps so the impulses are integrated by
/// the following step.
/// </summary>
public sealed class PlayerGridControlSystem : ISystem
{
    private const float Acceleration = 15f; // units/s² applied while a direction key is held

    private readonly EntitySet    _selectedGrid;
    private readonly PhysicsWorld _physics;
    private readonly InputManager _input;

    public PlayerGridControlSystem(World world, PhysicsWorld physics, InputManager input)
    {
        _physics      = physics;
        _input        = input;
        _selectedGrid = world.GetEntities().With<DynamicGrid>().With<SelectedGridComponent>().AsSet();
    }

    public void Update(float dt)
    {
        var dir = Vector3.Zero;
        if (_input.IsKeyDown(Key.Up))       dir.Z -= 1f; // forward (−Z)
        if (_input.IsKeyDown(Key.Down))     dir.Z += 1f;
        if (_input.IsKeyDown(Key.Left))     dir.X -= 1f;
        if (_input.IsKeyDown(Key.Right))    dir.X += 1f;
        if (_input.IsKeyDown(Key.I))        dir.Y += 1f;
        if (_input.IsKeyDown(Key.K))        dir.Y -= 1f;

        bool stop  = _input.WasKeyPressed(Key.Semicolon);
        bool moving = dir != Vector3.Zero;
        if (!moving && !stop) return;
        if (moving) dir = Vector3.Normalize(dir);

        foreach (ref readonly Entity e in _selectedGrid.GetEntities())
        {
            var grid = e.Get<DynamicGrid>();
            if (!grid.BodyCreated) continue;

            if (stop)
            {
                _physics.StopBody(grid.Body);
                continue;
            }

            float mass = _physics.GetBodyMass(grid.Body);
            if (mass <= 0f) continue;
            _physics.ApplyLinearImpulse(grid.Body, dir * (Acceleration * mass * dt));
        }
    }
}
