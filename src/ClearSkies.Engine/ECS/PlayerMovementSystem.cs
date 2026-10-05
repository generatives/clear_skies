using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Physics.Characters;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Entities;
using DefaultEcs;
using Silk.NET.Maths;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Each tick, before physics: the local player's free-fly/walking toggle (V, sent as a SetMoveMode command, which applies
/// later this same tick; see <see cref="Players.SetFreeFlying"/>) and every simulated player's movement, all from the tick's
/// <see cref="PlayerInput"/> (a player played elsewhere toggles with their own SetMoveMode, so only their movement is run here). Free-flying moves <see cref="Transform.Position"/> directly
/// the way the player looks; E and Q raise and lower its speed by <see cref="FlySpeedStep"/> (Ctrl triples it while
/// held). Walking instead feeds WASD/Shift/Space into the character's motion goals
/// (<see cref="PlayerCharacter.UpdateCharacterGoals"/>) — actual movement happens inside the physics step via the
/// ported BepuPhysics2 character-controller constraint (see Physics/Characters/);
/// <see cref="PhysicsTransformSyncSystem"/> reads the resulting body pose back into <see cref="Transform"/> after it.
/// Mouse-look is per frame, in <see cref="LookInputSystem"/>. A player using an Interactive block
/// (<see cref="LookLockedComponent"/>) or piloting a grid (<see cref="Piloting"/>) stands still where they are.
///
/// Must run before <c>host.Physics</c> in the tick so this tick's motion goals are set before Simulation.Timestep's
/// CollisionsDetected analysis runs (same precedent as AirshipFlightSystem).
/// </summary>
public sealed class PlayerMovementSystem : ISystem
{
    private readonly EntitySet _players;
    private readonly CommandSystem? _commands;
    private readonly EntitySet _walkers;
    private readonly EntitySet _flyers;
    private readonly List<Entity> _toggled = new();

    /// <param name="commands">Where the walk/fly toggle is sent as SetMoveMode; without one it's set directly.</param>
    public PlayerMovementSystem(World world, CommandSystem? commands = null)
    {
        _commands = commands;
        _players = world.GetEntities().With<LocalPlayer>().With<PlayerInput>().With<CharacterControllerComponent>().AsSet();
        _walkers = world.GetEntities()
            .With<Transform>().With<PlayerInput>().With<CharacterControllerComponent>()
            .Without<FreeFlying>().AsSet();
        _flyers = world.GetEntities()
            .With<Transform>().With<PlayerInput>().With<FreeFlyController>().With<FreeFlying>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _players.GetEntities())
            if (e.Get<PlayerInput>().WasPressed(PlayerButtons.ToggleFly) && !e.Has<Piloting>()) _toggled.Add(e);
        foreach (var e in _toggled)
        {
            if (_commands != null && e.Has<EntityId>())
                _commands.Send(new SetMoveMode { Player = e.Get<EntityId>(), FreeFly = !e.Has<FreeFlying>() });
            else
                Players.SetFreeFlying(e, !e.Has<FreeFlying>()); // changes which set it's in
        }
        _toggled.Clear();

        foreach (ref readonly Entity e in _walkers.GetEntities())
        {
            ref readonly var input = ref e.Get<PlayerInput>();
            var forward = LookForward(input);
            e.Get<CharacterControllerComponent>().Character.UpdateCharacterGoals(
                CharacterKeys(input), new PhysVec(forward.X, forward.Y, forward.Z), dt, Frozen(e));
        }

        foreach (ref readonly Entity e in _flyers.GetEntities())
            if (!Frozen(e)) UpdateFreeFly(ref e.Get<Transform>(), ref e.Get<FreeFlyController>(), e.Get<PlayerInput>(), dt);
    }

    /// <summary>The way the player looks: the tick's look angles (the Transform only turns with the yaw).</summary>
    private static Vector3D<float> LookForward(in PlayerInput input) =>
        Vec.Rotate(Quaternion<float>.CreateFromYawPitchRoll(input.Yaw, input.Pitch, 0f), new Vector3D<float>(0, 0, -1));

    /// <summary>Using an Interactive block or piloting a grid holds the player still: no walking, jumping or flying.</summary>
    private static bool Frozen(Entity e) => e.Has<LookLockedComponent>() || e.Has<Piloting>();

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
        // The way the player looks (the tick's look angles; the Transform only turns with the yaw).
        var look    = Quaternion<float>.CreateFromYawPitchRoll(input.Yaw, input.Pitch, 0f);
        var forward = Vec.Rotate(look, new Vector3D<float>(0, 0, -1));
        var right   = Vec.Rotate(look, new Vector3D<float>(1, 0, 0));
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
