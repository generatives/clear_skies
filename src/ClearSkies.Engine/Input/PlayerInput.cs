using System.Numerics;
using Silk.NET.Input;

namespace ClearSkies.Engine.Input;

/// <summary>The game's buttons, named by what they do by default. One key can drive several (see
/// <see cref="PlayerInputBindings"/>), and a system reads whichever fits its mode: in free-fly <see cref="Up"/> rises,
/// walking it jumps.</summary>
[Flags]
public enum PlayerButtons : uint
{
    None = 0,
    Forward = 1 << 0,
    Back = 1 << 1,
    Left = 1 << 2,
    Right = 1 << 3,
    /// <summary>Space: jump, or rise while flying.</summary>
    Up = 1 << 4,
    /// <summary>Shift: sprint, or sink while flying.</summary>
    Down = 1 << 5,
    /// <summary>Ctrl: crouch, or fly faster while held.</summary>
    Crouch = 1 << 6,
    /// <summary>V: switch between walking and free-fly.</summary>
    ToggleFly = 1 << 7,
    /// <summary>E: raise the fly speed, or turn right while piloting.</summary>
    Next = 1 << 8,
    /// <summary>Q: lower the fly speed, or turn left while piloting.</summary>
    Previous = 1 << 9,
    /// <summary>Left mouse button: place a block, or use a control.</summary>
    Primary = 1 << 10,
    /// <summary>Right mouse button: break a block.</summary>
    Secondary = 1 << 11,
    /// <summary>G: spawn a one-block grid in front of the player.</summary>
    SpawnGrid = 1 << 12,
    /// <summary>L: cycle the block to place.</summary>
    CycleBlock = 1 << 13,
}

/// <summary>
/// One tick's input for the local player: raw input, not gameplay commands. Simulation systems in the Simulation stage read
/// only this, never <see cref="InputManager"/>, so what they see doesn't depend on how many frames or ticks there were:
/// <see cref="Pressed"/> holds every press since the previous tick, even from frames that ran no tick.
/// </summary>
public struct PlayerInput
{
    /// <summary>Buttons down when the tick was sampled.</summary>
    public PlayerButtons Held;

    /// <summary>Buttons pressed since the previous tick, even if already released.</summary>
    public PlayerButtons Pressed;

    /// <summary>The view direction when the tick was sampled (see <c>MouseLookComponent</c>).</summary>
    public float Yaw, Pitch;

    /// <summary>Mouse movement in pixels since the previous tick, for dragging controls.</summary>
    public Vector2 MouseDelta;

    /// <summary>Whether the cursor is captured for playing (not freed for the UI): the mouse aims and clicks act.</summary>
    public bool Aiming;

    public readonly bool IsHeld(PlayerButtons button) => (Held & button) != 0;
    public readonly bool WasPressed(PlayerButtons button) => (Pressed & button) != 0;

    /// <summary>Walking direction from the held keys: (right, forward), each -1, 0 or 1.</summary>
    public readonly Vector2 Move => new(Axis(PlayerButtons.Right, PlayerButtons.Left), Axis(PlayerButtons.Forward, PlayerButtons.Back));

    /// <summary>+1 if <paramref name="positive"/> is held, −1 if <paramref name="negative"/> is, 0 for both or neither.</summary>
    public readonly float Axis(PlayerButtons positive, PlayerButtons negative)
        => (IsHeld(positive) ? 1f : 0f) - (IsHeld(negative) ? 1f : 0f);
}

/// <summary>Collects presses and mouse movement over frames and hands them to the next tick. A frame can run no tick
/// (at high frame rates most don't), while <see cref="InputManager"/> forgets a press after its frame, so without this a
/// click or jump in such a frame would be lost.</summary>
public sealed class InputLatch
{
    private PlayerButtons _pressed;
    private Vector2 _mouseDelta;

    /// <summary>Adds one frame's presses and mouse movement.</summary>
    public void AddFrame(PlayerButtons pressed, Vector2 mouseDelta)
    {
        _pressed |= pressed;
        _mouseDelta += mouseDelta;
    }

    /// <summary>Builds a tick's input from what's been collected since the last call, and starts collecting afresh.</summary>
    public PlayerInput Take(PlayerButtons held, float yaw, float pitch, bool aiming = true)
    {
        var input = new PlayerInput { Held = held, Pressed = _pressed, Yaw = yaw, Pitch = pitch, MouseDelta = _mouseDelta, Aiming = aiming };
        _pressed = PlayerButtons.None;
        _mouseDelta = Vector2.Zero;
        return input;
    }
}

/// <summary>Which keys drive which <see cref="PlayerButtons"/>.</summary>
public static class PlayerInputBindings
{
    public static readonly (Key Key, PlayerButtons Buttons)[] Keys =
    {
        (Key.W, PlayerButtons.Forward),
        (Key.S, PlayerButtons.Back),
        (Key.A, PlayerButtons.Left),
        (Key.D, PlayerButtons.Right),
        (Key.Space, PlayerButtons.Up),
        (Key.ShiftLeft, PlayerButtons.Down),
        (Key.ShiftRight, PlayerButtons.Down),
        (Key.ControlLeft, PlayerButtons.Crouch),
        (Key.ControlRight, PlayerButtons.Crouch),
        (Key.V, PlayerButtons.ToggleFly),
        (Key.E, PlayerButtons.Next),
        (Key.Q, PlayerButtons.Previous),
        (Key.G, PlayerButtons.SpawnGrid),
        (Key.L, PlayerButtons.CycleBlock),
    };

    public static readonly (MouseButton Button, PlayerButtons Buttons)[] MouseButtons =
    {
        (MouseButton.Left, PlayerButtons.Primary),
        (MouseButton.Right, PlayerButtons.Secondary),
    };

    /// <summary>Buttons whose keys are down now.</summary>
    public static PlayerButtons Held(InputManager input)
    {
        var buttons = PlayerButtons.None;
        foreach (var (key, button) in Keys)
            if (input.IsKeyDown(key)) buttons |= button;
        foreach (var (mouse, button) in MouseButtons)
            if (input.IsMouseButtonDown(mouse)) buttons |= button;
        return buttons;
    }

    /// <summary>Buttons whose keys were pressed this frame.</summary>
    public static PlayerButtons Pressed(InputManager input)
    {
        var buttons = PlayerButtons.None;
        foreach (var (key, button) in Keys)
            if (input.WasKeyPressed(key)) buttons |= button;
        foreach (var (mouse, button) in MouseButtons)
            if (input.WasMouseButtonPressed(mouse)) buttons |= button;
        return buttons;
    }
}
