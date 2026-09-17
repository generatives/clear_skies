namespace ClearSkies.Engine.Gui;

/// <summary>
/// Implement alongside <see cref="Core.ISystem"/> to contribute a panel to the engine's "Systems"
/// menu bar (see <see cref="ImGuiController"/>). No separate registration call is needed —
/// <c>EngineHost.AddSystem</c> detects this interface and wires it up automatically.
/// </summary>
public interface IDebugUiSystem
{
    /// <summary>Label shown in the "Systems" dropdown and as the panel's window title. Must be
    /// unique among registered systems.</summary>
    string DebugName { get; }

    /// <summary>Called once per frame, between <c>ImGui.Begin(DebugName)</c> and <c>ImGui.End()</c>,
    /// while this system's entry is checked in the menu. Issue <c>ImGui.*</c> calls here to
    /// populate the window.</summary>
    void DrawDebugUi();
}
