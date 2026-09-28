using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using DefaultEcs;
using Silk.NET.Input;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Every frame, before the ticks: Esc frees the cursor and a click on the window captures it again, and while it's
/// captured the mouse turns the view. Looking runs per frame rather than per tick so turning stays smooth at any frame
/// rate; the latest yaw and pitch go into the next tick's <see cref="PlayerInput"/>. Split from PlayerMovementSystem.
/// </summary>
public sealed class LookInputSystem : ISystem
{
    private readonly EntitySet _lookers;
    private readonly InputManager _input;

    public LookInputSystem(World world, InputManager input)
    {
        _lookers = world.GetEntities().With<Transform>().With<MouseLookComponent>().AsSet();
        _input = input;
    }

    public void Update(float dt)
    {
        // Esc unlocks the cursor; clicking the window re-locks it.
        if (_input.WasKeyPressed(Key.Escape) && _input.CursorCaptured)
            _input.CursorCaptured = false;
        else if (_input.WasMouseButtonPressed(MouseButton.Left) && !_input.CursorCaptured)
        {
            _input.CursorCaptured = true;
            // Swallow this click so the same press that recaptures the cursor doesn't also place a block.
            _input.ConsumeMouseButtonPress(MouseButton.Left);
        }

        if (!_input.CursorCaptured) return;
        var delta = _input.MouseDelta;
        foreach (ref readonly Entity e in _lookers.GetEntities())
        {
            // GridPilotSystem turns its own camera while the player pilots a grid; while using an Interactive block the
            // mouse moves the control, not the view (see BlockInteraction).
            if (e.Has<Piloting>() || e.Has<LookLockedComponent>()) continue;

            ref var look = ref e.Get<MouseLookComponent>();
            look.Yaw -= delta.X * look.LookSensitivity;
            look.Pitch -= delta.Y * look.LookSensitivity;
            float limit = MathF.PI / 2f - 0.01f;
            look.Pitch = System.Math.Clamp(look.Pitch, -limit, limit);
            e.Get<Transform>().Rotation = look.BodyRotation; // the head's pitch is the eye's (EyeSystem)
        }
    }
}
