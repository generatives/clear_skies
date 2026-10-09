using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics;
using DefaultEcs;

namespace ClearSkies.Engine.Commands;

/// <summary>
/// Every gameplay command: its ID on the wire and its handler. To add one:
/// <list type="number">
/// <item>Write the command, a struct implementing <see cref="ICommand"/> (plain data: whatever applying it on any machine
/// needs), and its handler (a <see cref="CommandHandler{T}"/>, or a <see cref="PredictedCommandHandler{T, TUndo}"/> if
/// the sender should see it straight away), in <c>Commands/Handlers</c>.</item>
/// <item>Give it the next ID in <see cref="CommandIds"/>. IDs go on the wire and into saves, so never reuse or renumber
/// one.</item>
/// <item>Register its handler in <see cref="RegisterAll"/>.</item>
/// </list>
/// The game and the tests both register through <see cref="RegisterAll"/>, so a new command works in both.
/// </summary>
public static class GameCommands
{
    /// <summary>Registers every gameplay command's handler.</summary>
    /// <param name="playerModel">What other players are drawn as; none headless.</param>
    public static void RegisterAll(CommandSystem commands, World world, Session session, BlockEntities blocks,
                                   EditLimits limits, EntityRegistry registry, PhysicsWorld physics, GridSelection selection,
                                   PlayerModel? playerModel = null)
    {
        commands.Register(new EditVoxelsHandler(blocks, limits));
        commands.Register(new SetShipThrustHandler(registry));
        commands.Register(new SetShipTurnHandler(registry));
        commands.Register(new SetShipAnchoredHandler(registry));
        commands.Register(new SetGridLockedHandler(registry, physics));
        commands.Register(new RightGridHandler(registry, physics));
        commands.Register(new SetMoveModeHandler(registry));
        commands.Register(new SpawnGridHandler(world, registry, session, physics, selection));
        commands.Register(new SpawnPlayerHandler(world, registry, session, physics, playerModel));
        commands.Register(new DespawnEntityHandler(registry));
    }
}

/// <summary>Command IDs on the wire (see <see cref="GameCommands"/>).</summary>
public static class CommandIds
{
    public const ushort EditVoxels = 1;
    public const ushort SetShipThrust = 2; // 2 and 3 were SetLever and SetWheel before any release
    public const ushort SetShipTurn = 3;
    public const ushort SetGridLocked = 4;
    public const ushort RightGrid = 5;
    public const ushort SetMoveMode = 6;
    public const ushort SpawnGrid = 7;
    public const ushort SpawnPlayer = 8;
    public const ushort DespawnEntity = 9;
    public const ushort SetShipAnchored = 10;
}
