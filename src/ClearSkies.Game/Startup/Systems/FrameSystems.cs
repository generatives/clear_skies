using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Game.Diagnostics;
using ClearSkies.Game.Hud;
using ClearSkies.Net.Sync;
using Silk.NET.Input;

namespace ClearSkies.Game.Startup.Systems;

/// <summary>Once each frame, after the ticks: where the camera is, what's drawn between the last two ticks (children
/// follow), terrain streamed around the view, and what the player points at and uses; the HUD and the debug panels.</summary>
public static class FrameSystems
{
    public static void Add(GameWorld w, RemoteBodySystem remoteBodies)
    {
        var host = w.Host;
        var sim = w.Simulation;
        // Moves the camera once a frame (not per tick) while flying; before the interpolation, which then draws it there.
        // --flight-test flies once the world has loaded, then quits.
        host.AddSystem(new StreamingFlightTest(host, w.Options.FlightTest, w.Options.FlightTest ? () => host.Window.Native.Close() : null),
                       SystemStage.Frame);
        host.AddSystem(sim.Pilot, SystemStage.Frame); // puts the camera under a piloted grid...
        host.AddSystem(new EyeSystem(host.World), SystemStage.Frame); // ...or at the local player's eye
        host.AddSystem(sim.Interpolation, SystemStage.Frame);
        host.AddSystem(remoteBodies, SystemStage.Frame); // bodies owned elsewhere, about 100 ms behind
        host.AddSystem(sim.Hierarchy, SystemStage.Frame);
        host.AddSystem(w.ChunkLoad, SystemStage.Frame);
        host.AddSystem(new BlockTargetSystem(host.World, host.Input, host.Renderer, sim.BlockActions, w.EditLimits), SystemStage.Frame);
        host.AddSystem(new HudUi(w.Ui, host.Input, sim.BlockActions, sim.Pilot, host.Renderer.Atlas,
                                 Path.Combine(AppContext.BaseDirectory, "Resources", "Icons")), SystemStage.Frame); // crosshair, hotbar
        var gridPersistence = new GridPersistenceSystem(host.World, w.Meshes, host.Physics, w.Selection, w.Commands);
        host.AddSystem(gridPersistence, SystemStage.Frame);
        // The airship's debug panels (pilot, flight, save/load) in one "Airship" window.
        host.AddSystem(new AirshipDebugPanel(sim.Pilot, sim.Flight, gridPersistence), SystemStage.Frame);
        host.AddSystem(new LambdaSystem(() =>
        {
            if (!host.Input.WasKeyPressed(Key.Tab)) return;
            host.Renderer.WireframeMode = !host.Renderer.WireframeMode;
            Console.WriteLine($"[debug] wireframe: {host.Renderer.WireframeMode}");
        }), SystemStage.Frame);
    }
}
