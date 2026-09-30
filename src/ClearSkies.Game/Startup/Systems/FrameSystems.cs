using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Game.Diagnostics;
using ClearSkies.Game.Hud;
using Silk.NET.Input;

namespace ClearSkies.Game.Startup.Systems;

/// <summary>Once each frame, after the ticks: where the camera is, what's drawn between the last two ticks (children
/// follow), terrain streamed around the view, and what the player points at and uses; the HUD and the debug panels.</summary>
public static class FrameSystems
{
    public static void Add(GameWorld w)
    {
        var host = w.Host;
        var sim = w.Simulation;
        if (!w.Headless) AddFlying(w);
        host.AddSystem(new EyeSystem(host.World), SystemStage.Frame); // the camera at the local player's eye, if not piloting
        host.AddSystem(sim.Interpolation, SystemStage.Frame);
        host.AddSystem(sim.Hierarchy, SystemStage.Frame);
        host.AddSystem(w.ChunkLoad, SystemStage.Frame);
        if (!w.Headless) AddInteraction(w);
    }

    /// <summary>Moving the camera once a frame (not per tick) while flying, before the interpolation, which then draws it
    /// there: --flight-test (flies once the world has loaded, then quits), and the pilot, which puts the camera under a
    /// piloted grid. Not headless.</summary>
    private static void AddFlying(GameWorld w)
    {
        var host = w.Host;
        host.AddSystem(new StreamingFlightTest(host, w.Options.FlightTest, w.Options.FlightTest ? () => host.Quit() : null),
                       SystemStage.Frame);
        host.AddSystem(w.Simulation.Pilot!, SystemStage.Frame);
    }

    /// <summary>What the player points at and uses, the HUD, and the debug panels. Not headless.</summary>
    private static void AddInteraction(GameWorld w)
    {
        var host = w.Host;
        var sim = w.Simulation;
        var input = host.Input!;
        var renderer = host.Renderer!;
        var pilot = sim.Pilot!;
        host.AddSystem(new BlockTargetSystem(host.World, input, renderer, sim.BlockActions, w.EditLimits), SystemStage.Frame);
        host.AddSystem(new HudUi(w.Ui!, input, sim.BlockActions, pilot, renderer.Atlas,
                                 Path.Combine(AppContext.BaseDirectory, "Resources", "Icons")), SystemStage.Frame); // crosshair, hotbar
        var gridPersistence = new GridPersistenceSystem(host.World, w.Meshes!, host.Physics, w.Selection, w.Commands);
        host.AddSystem(gridPersistence, SystemStage.Frame);
        // The airship's debug panels (pilot, flight, save/load) in one "Airship" window.
        host.AddSystem(new AirshipDebugPanel(pilot, sim.Flight, gridPersistence), SystemStage.Frame);
        host.AddSystem(new LambdaSystem(() =>
        {
            if (!input.WasKeyPressed(Key.Tab)) return;
            renderer.WireframeMode = !renderer.WireframeMode;
            Console.WriteLine($"[debug] wireframe: {renderer.WireframeMode}");
        }), SystemStage.Frame);
    }
}
