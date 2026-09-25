using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
using DefaultEcs;
using Silk.NET.Input;
using Silk.NET.Maths;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Pre-physics: cursor capture, mouse-look, the FreeFly/Walking mode toggle (V), and per-mode
/// movement input. FreeFly teleports <see cref="Transform.Position"/> directly, unchanged from
/// the old free-fly-only camera. Walking instead feeds WASD/Shift/Space into the character's
/// motion goals (<see cref="Physics.Characters.PlayerCharacter.UpdateCharacterGoals"/>) — actual
/// movement happens inside the physics step via the ported BepuPhysics2 character-controller
/// constraint (see Physics/Characters/); <see cref="CharacterCameraSyncSystem"/> reads the
/// resulting body pose back into <see cref="Transform"/> after the physics step.
///
/// Must run before <c>host.Physics</c> in Program.cs so this frame's motion goals are set before
/// Simulation.Timestep's CollisionsDetected analysis runs (same precedent as AirshipFlightSystem).
/// </summary>
public sealed class PlayerMovementSystem : ISystem
{
    private readonly EntitySet    _cameras;
    private readonly InputManager _input;

    public PlayerMovementSystem(World world, InputManager input)
    {
        _cameras = world.GetEntities()
            .With<Transform>().With<CameraComponent>().With<MouseLookComponent>()
            .With<FreeFlyController>().With<CharacterControllerComponent>().With<CharacterModeComponent>()
            .AsSet();
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

        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            // Skip entirely while GridPilotSystem is flying the camera along a piloted grid — it
            // handles its own look input and overwrites Transform each frame post-physics.
            if (e.Has<CameraGridFollowComponent>()) continue;

            ref var t    = ref e.Get<Transform>();
            ref var look = ref e.Get<MouseLookComponent>();

            // While using an Interactive block the mouse moves the control, not the view (see BlockInteraction).
            if (_input.CursorCaptured && !e.Has<LookLockedComponent>())
            {
                var delta = _input.MouseDelta;
                look.Yaw -= delta.X * look.LookSensitivity;
                look.Pitch -= delta.Y * look.LookSensitivity;
                float limit = MathF.PI / 2f - 0.01f;
                look.Pitch = System.Math.Clamp(look.Pitch, -limit, limit);
                t.Rotation = Quaternion<float>.CreateFromYawPitchRoll(look.Yaw, look.Pitch, 0f);
            }

            ref var mode = ref e.Get<CharacterModeComponent>();
            if (_input.WasKeyPressed(Key.V))
                mode.FreeFly = !mode.FreeFly;

            // Using an Interactive block holds the player still: no walking, jumping or flying until they let go.
            bool frozen = e.Has<LookLockedComponent>();

            ref var cc = ref e.Get<CharacterControllerComponent>();
            if (mode.FreeFly)
            {
                // Not actively walking — keep the capsule glued to wherever the camera is, so
                // switching back to Walking always resumes from the visible position instead of
                // falling from a stale one.
                cc.Character.TeleportTo(new PhysVec(t.Position.X, t.Position.Y - cc.EyeHeight, t.Position.Z));
                if (!frozen) UpdateFreeFly(ref t, ref e.Get<FreeFlyController>(), dt);
            }
            else
            {
                var forward = Vec.Rotate(t.Rotation, new Vector3D<float>(0, 0, -1));
                cc.Character.UpdateCharacterGoals(_input, new PhysVec(forward.X, forward.Y, forward.Z), dt, frozen);
            }
        }
    }

    private void UpdateFreeFly(ref Transform t, ref FreeFlyController c, float dt)
    {
        var forward = Vec.Rotate(t.Rotation, new Vector3D<float>(0, 0, -1));
        var right   = Vec.Rotate(t.Rotation, new Vector3D<float>(1, 0, 0));
        var up      = new Vector3D<float>(0, 1, 0);

        bool speedUp = false;

        var move = Vector3D<float>.Zero;
        if (_input.IsKeyDown(Key.W)) move += forward;
        if (_input.IsKeyDown(Key.S)) move -= forward;
        if (_input.IsKeyDown(Key.D)) move += right;
        if (_input.IsKeyDown(Key.A)) move -= right;
        if (_input.IsKeyDown(Key.Space)) move += up;
        if (_input.IsKeyDown(Key.ShiftLeft) || _input.IsKeyDown(Key.ShiftRight)) move -= up;
        if (_input.IsKeyDown(Key.ControlLeft) || _input.IsKeyDown(Key.ControlRight)) speedUp = true;
        
        c.MoveSpeed += _input.ScrollDelta.Y * 0.5f; // scroll wheel adjusts speed up/down
        c.MoveSpeed = MathF.Max(2f, c.MoveSpeed);

        float speed = speedUp ? c.MoveSpeed * 3f : c.MoveSpeed;

        if (move.LengthSquared > 1e-6f)
            t.Position += Vector3D.Normalize(move) * speed * dt;
    }
}
