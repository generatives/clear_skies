using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Physics.Characters;
using DefaultEcs;
using Silk.NET.Maths;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Each tick, before physics: the FreeFly/Walking mode toggle (V) and per-mode movement, all from the tick's
/// <see cref="PlayerInput"/>. FreeFly moves <see cref="Transform.Position"/> directly; E and Q raise and lower its speed
/// by <see cref="FlySpeedStep"/> (Ctrl triples it while held). Walking instead feeds WASD/Shift/Space into the
/// character's motion goals (<see cref="PlayerCharacter.UpdateCharacterGoals"/>) — actual movement happens inside the
/// physics step via the ported BepuPhysics2 character-controller constraint (see Physics/Characters/);
/// <see cref="CharacterCameraSyncSystem"/> reads the resulting body pose back into <see cref="Transform"/> after it.
/// Mouse-look is per frame, in <see cref="LookInputSystem"/>.
///
/// Must run before <c>host.Physics</c> in the tick so this tick's motion goals are set before Simulation.Timestep's
/// CollisionsDetected analysis runs (same precedent as AirshipFlightSystem).
/// </summary>
public sealed class PlayerMovementSystem : ISystem
{
    private readonly EntitySet _players;

    public PlayerMovementSystem(World world)
    {
        _players = world.GetEntities()
            .With<Transform>().With<PlayerInput>()
            .With<FreeFlyController>().With<CharacterControllerComponent>().With<CharacterModeComponent>()
            .AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _players.GetEntities())
        {
            // Skip entirely while GridPilotSystem is flying the camera along a piloted grid — it
            // handles its own look input and overwrites Transform each frame.
            if (e.Has<CameraGridFollowComponent>()) continue;

            ref var t = ref e.Get<Transform>();
            ref readonly var input = ref e.Get<PlayerInput>();

            ref var mode = ref e.Get<CharacterModeComponent>();
            if (input.WasPressed(PlayerButtons.ToggleFly))
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
                if (!frozen) UpdateFreeFly(ref t, ref e.Get<FreeFlyController>(), input, dt);
            }
            else
            {
                var forward = Vec.Rotate(t.Rotation, new Vector3D<float>(0, 0, -1));
                cc.Character.UpdateCharacterGoals(CharacterKeys(input), new PhysVec(forward.X, forward.Y, forward.Z), dt, frozen);
            }
        }
    }

    /// <summary>The walking character's keys from a tick's input.</summary>
    public static PlayerCharacter.CharacterInput CharacterKeys(in PlayerInput input) => new()
    {
        Move = input.Move,
        Sprint = input.IsHeld(PlayerButtons.Down),
        Crouch = input.IsHeld(PlayerButtons.Crouch),
        JumpPressed = input.WasPressed(PlayerButtons.Up),
    };

    /// <summary>Blocks per second each E/Q press adds or removes from the free-fly speed; also its minimum.</summary>
    public const float FlySpeedStep = 5f;

    private static void UpdateFreeFly(ref Transform t, ref FreeFlyController c, in PlayerInput input, float dt)
    {
        var forward = Vec.Rotate(t.Rotation, new Vector3D<float>(0, 0, -1));
        var right   = Vec.Rotate(t.Rotation, new Vector3D<float>(1, 0, 0));
        var up      = new Vector3D<float>(0, 1, 0);

        var move = forward * input.Axis(PlayerButtons.Forward, PlayerButtons.Back)
                 + right * input.Axis(PlayerButtons.Right, PlayerButtons.Left)
                 + up * input.Axis(PlayerButtons.Up, PlayerButtons.Down);

        if (input.WasPressed(PlayerButtons.Next) || input.WasPressed(PlayerButtons.Previous))
        {
            float step = input.WasPressed(PlayerButtons.Next) ? FlySpeedStep : -FlySpeedStep;
            c.MoveSpeed = MathF.Max(FlySpeedStep, c.MoveSpeed + step);
            Console.WriteLine($"[fly] speed: {c.MoveSpeed:0} blocks/s");
        }

        float speed = input.IsHeld(PlayerButtons.Crouch) ? c.MoveSpeed * 3f : c.MoveSpeed;

        if (move.LengthSquared > 1e-6f)
            t.Position += Vector3D.Normalize(move) * speed * dt;
    }
}
