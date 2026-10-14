using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Entities;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Lets the player flick a toggle: clicking one switches its ship's anchors on or off (a SetShipAnchored command; see
/// <see cref="ShipControls.Anchored"/>). Unlike a lever there's nothing to drag: the click is the whole use.
///
/// A toggle is a view of whether its ship is anchored, as a lever is of its thrust: every tick, after the commands,
/// this poses each toggle's arm from its ship's setting, so every toggle on a ship flips together, and one flips back
/// by itself when <see cref="AnchorSystem"/> finds nothing in reach and lets the ship go.
///
/// The model is the lever's, painted red, so its arm swings the same way (see <see cref="LeverControlSystem"/>): leaning
/// <see cref="LeverControlSystem.MaxAngle"/> to the block's north face when on, to its south face when off.
/// </summary>
public sealed class ToggleControlSystem : ISystem, IDisposable, IDebugUiSystem
{
    private const string ArmNode = "arm_group";

    // The arm node's own axis it pivots about, as the lever's: turning +Y about -X leans it towards -Z (north).
    private static readonly Vector3D<float> PivotAxis = -Vector3D<float>.UnitX;

    private readonly CommandSystem _commands;
    private readonly EntitySet     _toggles;
    private readonly IDisposable   _subscription;
    private Entity _lastUsed;

    public ToggleControlSystem(World world, CommandSystem commands)
    {
        _commands     = commands;
        _toggles      = world.GetEntities().With<Toggle>().With<BlockRef>().With<RenderedModel>().AsSet();
        _subscription = world.Subscribe<BlockInteraction>(OnInteraction);
    }

    /// <summary>Where a toggle's arm leans from upright (radians, towards its north face) for its ship's setting.</summary>
    public static float ArmAngle(in BlockRef toggle) =>
        ShipControls.Of(toggle.Volume).Anchored ? LeverControlSystem.MaxAngle : -LeverControlSystem.MaxAngle;

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _toggles.GetEntities())
            e.Get<RenderedModel>().SetRotationFromRest(ArmNode,
                Quaternion<float>.CreateFromAxisAngle(PivotAxis, ArmAngle(e.Get<BlockRef>())));
    }

    private void OnInteraction(in BlockInteraction interaction)
    {
        if (interaction.Phase != InteractionPhase.Began) return;
        var e = interaction.Block;
        if (!e.IsAlive || !e.Has<Toggle>() || !e.Has<BlockRef>()) return;
        _lastUsed = e;
        Send(e, !ShipControls.Of(e.Get<BlockRef>().Volume).Anchored);
    }

    /// <summary>Asks for the toggle's ship to be anchored or let go.</summary>
    private void Send(Entity toggle, bool anchored)
    {
        ref readonly var block = ref toggle.Get<BlockRef>();
        if (!block.Volume.Root.IsAlive || !block.Volume.Root.Has<EntityId>()) return;
        _commands.Send(new SetShipAnchored { Ship = block.Volume.Root.Get<EntityId>(), Anchored = anchored });
    }

    public void Dispose() => _subscription.Dispose();

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Toggles";

    public void DrawDebugUi()
    {
        ImGui.Text($"Toggles: {_toggles.Count:N0}");
        if (_lastUsed.IsAlive && _lastUsed.Has<Toggle>() && _lastUsed.Has<BlockRef>())
        {
            bool anchored = ShipControls.Of(_lastUsed.Get<BlockRef>().Volume).Anchored;
            if (ImGui.Checkbox("Last used: anchored", ref anchored)) Send(_lastUsed, anchored);
        }
        else
        {
            ImGui.TextDisabled("Click a toggle to flick it.");
        }
    }
}
