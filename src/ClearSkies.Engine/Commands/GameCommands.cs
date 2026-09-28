using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics;

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
    public static void RegisterAll(CommandSystem commands, BlockEntities blocks, EditLimits limits, EntityRegistry registry,
                                   PhysicsWorld physics)
    {
        commands.Register(new EditVoxelsHandler(blocks, limits));
        commands.Register(new SetLeverHandler(blocks));
        commands.Register(new SetWheelHandler(blocks));
        commands.Register(new SetGridLockedHandler(registry, physics));
        commands.Register(new RightGridHandler(registry, physics));
        commands.Register(new SetMoveModeHandler(registry));
    }
}

/// <summary>Command IDs on the wire (see <see cref="GameCommands"/>).</summary>
public static class CommandIds
{
    public const ushort EditVoxels = 1;
    public const ushort SetLever = 2;
    public const ushort SetWheel = 3;
    public const ushort SetGridLocked = 4;
    public const ushort RightGrid = 5;
    public const ushort SetMoveMode = 6;
}
