using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game;
using ClearSkies.Game.Diagnostics;
using ClearSkies.Game.Generation;
using Silk.NET.Input;

// Headless perf harness (see GenerationBenchmark) — no GPU/window needed, so this runs before EngineHost.
if (args.Contains("--benchmark"))
{
    GenerationBenchmark.Run();
    return;
}

using var host = new EngineHost(new EngineOptions("Clear Skies", 1280, 720, LogGpuErrors: true));

host.Renderer.LoadTextureAtlas(
    Path.Combine(AppContext.BaseDirectory, "Resources", "spritesheet_tiles.png"),
    Path.Combine(AppContext.BaseDirectory, "Resources", "spritesheet_tiles.xml"));

// Phase 4.0: prove the GPU compute path (upload → dispatch → readback) before building lighting on it.
GpuComputeSelfTest.Run(host.Context);

var staticWorld   = new StaticWorld(host.World);
var worldGen      = new SkyWorldGenerator();
var meshSystem    = new ChunkMeshSystem(staticWorld, host.Renderer);
var gridSelection = new GridSelection(host.World);

host.AddSystem(host.Gui, SystemStage.Input); // opens ImGui's frame before Logic/PreRender systems run

var physicsBody = new PhysicsBodySystem(host.World, staticWorld, host.Physics);

// View distance: xzRadius=8/yRadius=3 caused WGPU validation errors / crashes (likely the GPU-resident
// light/opacity volume outgrowing a buffer limit at that window size — see GpuResidencySystem). Dialed
// back to 4/2 (up from the original 3/2) until that's root-caused.
host.AddSystem(new ChunkLoadSystem(host.World, staticWorld, worldGen, xzRadius: 4, yRadius: 2), SystemStage.Logic);
host.AddSystem(physicsBody, SystemStage.Logic);
host.AddSystem(new PlayerGridControlSystem(host.World, host.Physics, host.Input), SystemStage.Logic);

// Character controller (ported from BepuPhysics2's own Demos/Demos/Characters — see
// Physics/Characters/): motion goals (WASD/jump/mode toggle) must be set before the physics step
// so Simulation.Timestep's CollisionsDetected analysis sees them this same tick.
host.AddSystem(new PlayerMovementSystem(host.World, host.Input), SystemStage.Logic);

// Milestone 5: airship flight (velocity control law + Fan/Buoyant propulsion, merged into one system —
// see AirshipFlightSystem), before the physics step so its impulses are integrated this same tick.
var gridPilot = new GridPilotSystem(host.World, host.Input, host.Physics, staticWorld, physicsBody);
var airshipFlight = new AirshipFlightSystem(host.World, host.Physics, host.Input);
host.AddSystem(airshipFlight, SystemStage.Logic);

host.AddSystem(host.Physics, SystemStage.Logic); // steps the simulation once bodies/impulses for this frame are in
host.AddSystem(new GridTransformSystem(host.World, host.Physics), SystemStage.Logic);
host.AddSystem(new HierarchyTransformSystem(host.World), SystemStage.Logic);
host.AddSystem(new CharacterCameraSyncSystem(host.World), SystemStage.Logic); // reads the capsule's post-physics pose into Transform
host.AddSystem(gridPilot, SystemStage.Logic);
host.AddSystem(new PlayerInputSystem(host.World, staticWorld, host.Physics, host.Input, meshSystem, host.Renderer, gridSelection), SystemStage.Logic);
var gridPersistence = new GridPersistenceSystem(host.World, meshSystem, host.Physics, gridSelection);
host.AddSystem(gridPersistence, SystemStage.Logic);
// The airship-related debug panels above (Pilot/Flight/Save-Load) drew into their own separate "Systems"
// menu windows; combined here into one "Airship" window so they read as one feature.
host.AddSystem(new AirshipDebugPanel(gridPilot, airshipFlight, gridPersistence), SystemStage.Logic);
host.AddSystem(new LambdaSystem(() =>
{
    if (host.Input.WasKeyPressed(Key.Tab))
    {
        host.Renderer.WireframeMode = !host.Renderer.WireframeMode;
        Console.WriteLine($"[debug] wireframe: {host.Renderer.WireframeMode}");
    }
}), SystemStage.Logic);
host.AddSystem(new GpuResidencySystem(host.World, staticWorld, host.Context, host.Renderer), SystemStage.PreRender);
host.AddSystem(new GpuLightSystem(host.World, staticWorld, host.Context, host.Physics, host.Renderer), SystemStage.PreRender);
host.AddSystem(meshSystem, SystemStage.PreRender);
host.AddSystem(new RenderSystem(host.World, host.Renderer, host.Gui, host.Time), SystemStage.Render);

TestScene.Build(host, worldGen.Seed);

host.Run();

staticWorld.SaveAllDirty(); // graceful-exit flush; unload/autosave already cover the running game
