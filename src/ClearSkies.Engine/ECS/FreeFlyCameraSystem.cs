using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
using DefaultEcs;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>Reads input and updates the camera entity's <see cref="Transform"/> (WASD/QE + mouse-look).</summary>
public sealed class FreeFlyCameraSystem : ISystem
{
    private readonly EntitySet _cameras;
    private readonly InputManager _input;

    public FreeFlyCameraSystem(World world, InputManager input)
    {
        _input = input;
        _cameras = world.GetEntities().With<Transform>().With<CameraComponent>().With<FreeFlyController>().AsSet();
    }

    public void Update(float dt)
    {
        // Esc unlocks the cursor; clicking the window re-locks it.
        if (_input.WasKeyPressed(Key.Escape) && _input.CursorCaptured)
            _input.CursorCaptured = false;
        else if (_input.WasMouseButtonPressed(MouseButton.Left) && !_input.CursorCaptured)
            _input.CursorCaptured = true;

        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            ref var t = ref e.Get<Transform>();
            ref var c = ref e.Get<FreeFlyController>();

            if (_input.CursorCaptured)
            {
                var delta = _input.MouseDelta;
                c.Yaw -= delta.X * c.LookSensitivity;
                c.Pitch -= delta.Y * c.LookSensitivity;
                float limit = MathF.PI / 2f - 0.01f;
                c.Pitch = System.Math.Clamp(c.Pitch, -limit, limit);
                t.Rotation = Quaternion<float>.CreateFromYawPitchRoll(c.Yaw, c.Pitch, 0f);
            }

            var forward = Vec.Rotate(t.Rotation, new Vector3D<float>(0, 0, -1));
            var right = Vec.Rotate(t.Rotation, new Vector3D<float>(1, 0, 0));
            var up = new Vector3D<float>(0, 1, 0);

            var move = Vector3D<float>.Zero;
            if (_input.IsKeyDown(Key.W)) move += forward;
            if (_input.IsKeyDown(Key.S)) move -= forward;
            if (_input.IsKeyDown(Key.D)) move += right;
            if (_input.IsKeyDown(Key.A)) move -= right;
            if (_input.IsKeyDown(Key.Space)) move += up;
            if (_input.IsKeyDown(Key.ShiftLeft) || _input.IsKeyDown(Key.ShiftRight)) move -= up;
            if (_input.IsKeyDown(Key.E)) c.MoveSpeed += 2;
            if (_input.IsKeyDown(Key.Q)) c.MoveSpeed -= 2;

            c.MoveSpeed = MathF.Max(2f, c.MoveSpeed);

            if (move.LengthSquared > 1e-6f)
                t.Position += Vector3D.Normalize(move) * c.MoveSpeed * dt;
        }
    }
}
