using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Fills the local player's <see cref="PlayerInput"/> once per tick. Runs twice over: <see cref="Collect"/> once per
/// frame in the Input stage (after the UI has claimed the mouse or keyboard), latching that frame's presses and mouse
/// movement; then this system at the start of each tick, handing everything latched since the previous tick to the
/// player's <see cref="PlayerInput"/>. The first tick of a frame gets the frame's presses; any further ticks that frame
/// see only what's held.
/// </summary>
public sealed class InputSampleSystem : ISystem
{
    private readonly EntitySet _players;
    private readonly InputManager _input;
    private readonly InputLatch _latch = new();

    public InputSampleSystem(World world, InputManager input)
    {
        _players = world.GetEntities().With<PlayerInput>().AsSet();
        _input = input;
        Collect = new LambdaSystem(() => _latch.AddFrame(PlayerInputBindings.Pressed(_input),
            new System.Numerics.Vector2(_input.MouseDelta.X, _input.MouseDelta.Y)));
    }

    /// <summary>The per-frame half; schedule it in the Input stage.</summary>
    public ISystem Collect { get; }

    public void Update(float dt)
    {
        var held = PlayerInputBindings.Held(_input);
        foreach (ref readonly Entity e in _players.GetEntities())
        {
            float yaw = 0f, pitch = 0f;
            if (e.Has<MouseLookComponent>()) (yaw, pitch) = (e.Get<MouseLookComponent>().Yaw, e.Get<MouseLookComponent>().Pitch);
            e.Get<PlayerInput>() = _latch.Take(held, yaw, pitch);
        }
    }
}
