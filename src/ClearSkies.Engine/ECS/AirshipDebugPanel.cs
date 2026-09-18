using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Combines the airship-related debug panels — piloting (<see cref="GridPilotSystem"/>), flight
/// (<see cref="AirshipFlightSystem"/>: the velocity control law and Fan/Buoyant propulsion tuning
/// together), and grid save/load (<see cref="GridPersistenceSystem"/>) — into a single "Airship" window
/// instead of separate ones. Purely a UI aggregator: each wrapped system still updates itself
/// independently at its own place in the schedule; this only owns the combined layout, calling each
/// system's DrawDebugUi() (a plain method, not its own IDebugUiSystem registration) inside a collapsing
/// section.
/// </summary>
public sealed class AirshipDebugPanel : ISystem, IDebugUiSystem
{
    private readonly GridPilotSystem _pilot;
    private readonly AirshipFlightSystem _flight;
    private readonly GridPersistenceSystem _persistence;

    public AirshipDebugPanel(GridPilotSystem pilot, AirshipFlightSystem flight, GridPersistenceSystem persistence)
    {
        _pilot       = pilot;
        _flight      = flight;
        _persistence = persistence;
    }

    public void Update(float dt) { } // pure UI aggregator; wrapped systems update themselves elsewhere.

    public string DebugName => "Airship";

    public void DrawDebugUi()
    {
        if (ImGui.CollapsingHeader("Save / Load"))
            _persistence.DrawDebugUi();
        if (ImGui.CollapsingHeader("Pilot", ImGuiTreeNodeFlags.DefaultOpen))
            _pilot.DrawDebugUi();
        if (ImGui.CollapsingHeader("Flight", ImGuiTreeNodeFlags.DefaultOpen))
            _flight.DrawDebugUi();
    }
}
