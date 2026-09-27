using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using DefaultEcs;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Fills the local player's <see cref="PlayerInput"/> once per tick. Registered in two stages: in
/// <see cref="SystemStage.Input"/> (after the UI has claimed the mouse or keyboard) it latches the frame's presses and
/// mouse movement, and first in <see cref="SystemStage.Simulation"/> it hands everything latched since the previous
/// tick to the player's <see cref="PlayerInput"/>. The first tick of a frame gets the frame's presses; any further ticks that frame
/// see only what's held.
/// </summary>
public sealed class InputSampleSystem : IStagedSystem
{
    private readonly EntitySet _players;
    private readonly InputManager _input;
    private readonly InputLatch _latch = new();

    public InputSampleSystem(World world, InputManager input)
    {
        _players = world.GetEntities().With<PlayerInput>().AsSet();
        _input = input;
    }

    public void Update(SystemStage stage, float dt)
    {
        if (stage == SystemStage.Input)
            _latch.AddFrame(PlayerInputBindings.Pressed(_input), new System.Numerics.Vector2(_input.MouseDelta.X, _input.MouseDelta.Y));
        else
            Tick();
    }

    private void Tick()
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
