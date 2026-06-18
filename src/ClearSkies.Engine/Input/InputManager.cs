using ClearSkies.Engine.Windowing;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace ClearSkies.Engine.Input;

/// <summary>Wraps Silk.NET input: key state, per-frame mouse delta, and cursor capture for mouse-look.</summary>
public sealed class InputManager : IDisposable
{
    private readonly IInputContext _input;
    private readonly IKeyboard? _keyboard;
    private readonly IMouse? _mouse;

    private readonly HashSet<Key> _justPressed = new();
    private readonly HashSet<MouseButton> _justMousePressed = new();
    private System.Numerics.Vector2 _accumDelta;
    private System.Numerics.Vector2 _lastPos;
    private bool _firstMove = true;
    private bool _cursorCaptured;

    public InputManager(GameWindow window)
    {
        _input = window.Native.CreateInput();
        _keyboard = _input.Keyboards.Count > 0 ? _input.Keyboards[0] : null;
        _mouse = _input.Mice.Count > 0 ? _input.Mice[0] : null;

        if (_keyboard != null)
            _keyboard.KeyDown += (_, key, _) => _justPressed.Add(key);

        if (_mouse != null)
        {
            _mouse.MouseMove += (_, pos) =>
            {
                if (_firstMove) { _lastPos = pos; _firstMove = false; return; }
                _accumDelta += pos - _lastPos;
                _lastPos = pos;
            };
            _mouse.MouseDown += (_, btn) => _justMousePressed.Add(btn);
        }
    }

    /// <summary>Latch per-frame state. Call once at the start of each frame before reading deltas/edges.</summary>
    public void NewFrame()
    {
        _justPressed.Clear();
        _justMousePressed.Clear();
        _accumDelta = System.Numerics.Vector2.Zero;
    }

    public bool IsKeyDown(Key key) => _keyboard?.IsKeyPressed(key) ?? false;

    public bool WasKeyPressed(Key key) => _justPressed.Contains(key);
    public bool WasMouseButtonPressed(MouseButton button) => _justMousePressed.Contains(button);

    public Vector2D<float> MouseDelta => new(_accumDelta.X, _accumDelta.Y);

    public bool CursorCaptured
    {
        get => _cursorCaptured;
        set
        {
            _cursorCaptured = value;
            if (_mouse != null)
            {
                _mouse.Cursor.CursorMode = value ? CursorMode.Disabled : CursorMode.Normal;
                _firstMove = true;
            }
        }
    }

    public void Dispose() => _input.Dispose();
}
