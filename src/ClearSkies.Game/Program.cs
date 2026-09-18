using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game;
using ClearSkies.Game.Generation;
using Silk.NET.Input;

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

var staticColliders = new StaticColliderSystem(staticWorld, host.Physics);

host.AddSystem(new ChunkLoadSystem(host.World, staticWorld, worldGen, xzRadius: 3, yRadius: 2), SystemStage.Logic);
host.AddSystem(staticColliders, SystemStage.Logic);
host.AddSystem(new GridShapeSystem(host.World, host.Physics), SystemStage.Logic);
host.AddSystem(new PlayerGridControlSystem(host.World, host.Physics, host.Input), SystemStage.Logic);

// Milestone 5: airship control (desired-velocity law) + propulsion (Fan/Buoyant impulses), both before
// the physics step so their impulses are integrated this same tick. gridPilot/airshipPropulsion are
// constructed here (they need to exist for AirshipControlSystem's constructor) but registered later —
// gridPilot after the grid pose for this frame is fresh (no one-frame camera-follow lag), and
// airshipPropulsion after AirshipControlSystem so its Fan-block allocation sees this tick's fresh
// DesiredForce/Torque, not last tick's.
var gridPilot = new GridPilotSystem(host.World, host.Input, host.Physics, staticWorld, staticColliders);
var airshipPropulsion = new AirshipPropulsionSystem(host.World, host.Physics);
host.AddSystem(new AirshipControlSystem(host.World, host.Physics, host.Input, gridPilot, airshipPropulsion), SystemStage.Logic);
host.AddSystem(airshipPropulsion, SystemStage.Logic);

host.AddSystem(host.Physics, SystemStage.Logic); // steps the simulation once bodies/impulses for this frame are in
host.AddSystem(new GridTransformSystem(host.World, host.Physics), SystemStage.Logic);
host.AddSystem(gridPilot, SystemStage.Logic);
host.AddSystem(new PlayerInputSystem(host.World, staticWorld, host.Physics, host.Input, meshSystem, host.Renderer, gridSelection), SystemStage.Logic);
host.AddSystem(new GridPersistenceSystem(host.World, meshSystem, host.Physics, gridSelection), SystemStage.Logic);
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

TestScene.Build(host);

host.Run();

staticWorld.SaveAllDirty(); // graceful-exit flush; unload/autosave already cover the running game
