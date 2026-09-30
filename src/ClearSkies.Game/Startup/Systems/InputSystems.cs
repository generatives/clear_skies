using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;

namespace ClearSkies.Game.Startup.Systems;

/// <summary>First each frame, before any ticks: the debug UI's and the game UI's frames, mouse-look, and collecting the
/// frame's input for the ticks.</summary>
public static class InputSystems
{
    public static void Add(GameWorld w)
    {
        var host = w.Host;
        if (host.Gui is null || host.Input is null) return; // headless
        host.AddSystem(host.Gui, SystemStage.Input); // opens ImGui's frame before any other system draws into it
        // The game UI opens its layout after ImGui's frame, since ImGui resets whether the UI has the mouse; later systems
        // declare elements, and UiRenderSystem draws them in the HUD stage.
        host.AddSystem(w.Ui!, SystemStage.Input);
        host.AddSystem(new LookInputSystem(host.World, host.Input), SystemStage.Input);
        host.AddSystem(w.Simulation.InputSample!, SystemStage.Input); // latches the frame's input
    }
}
