using ClearSkies.Engine.Core;
using ClearSkies.Engine.Physics;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Advances the physics simulation on a fixed timestep, decoupled from the variable frame rate.
/// Accumulates frame delta and steps in fixed increments, capping the number of catch-up substeps
/// per frame to avoid a "spiral of death" after a long stall.
/// </summary>
public sealed class PhysicsSystem : ISystem
{
    private const int MaxStepsPerFrame = 5;

    private readonly PhysicsWorld _physics;
    private readonly float        _fixedStep;
    private float _accumulator;

    public PhysicsSystem(PhysicsWorld physics, float fixedStep)
    {
        _physics   = physics;
        _fixedStep = fixedStep;
    }

    public void Update(float dt)
    {
        _accumulator += dt;

        int steps = 0;
        while (_accumulator >= _fixedStep && steps < MaxStepsPerFrame)
        {
            _physics.Step(_fixedStep);
            _accumulator -= _fixedStep;
            steps++;
        }

        // If we hit the cap and still have a large backlog, drop it rather than chase forever.
        if (_accumulator > _fixedStep) _accumulator = 0f;
    }
}
