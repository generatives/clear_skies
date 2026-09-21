using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game;
using ClearSkies.Game.Diagnostics;
using ClearSkies.Game.Generation;
using Silk.NET.Input;
using Silk.NET.Maths;
using System.Numerics;

// Headless perf harness (see GenerationBenchmark) — no GPU/window needed, so this runs before EngineHost.
if (args.Contains("--benchmark-stream"))
{
    StreamingBenchmark.Run();
    return;
}
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

// View distance: xzRadius=4/yRadius=2 (was 3/2). Verified crash-free and smooth at this setting; a bigger
// jump (tried 8/3) hit two real problems: the GPU device was silently capped at a 256 MiB max buffer size
// (fixed in GpuContext — see AdapterLimits), and even past that, single-digit FPS from the GPU light flood
// recomputing a much larger dirty region during the load-in burst plus the per-frame full-chunk scans in
// ChunkMeshSystem/GpuResidencySystem/GpuLightSystem/PhysicsBodySystem (see the deferred dirty-queue task).
// Pushing further needs that follow-up work, not just a bigger radius.
const int ViewXz = 8, ViewY = 3;
host.AddSystem(new ChunkLoadSystem(host.World, staticWorld, worldGen, xzRadius: ViewXz, yRadius: ViewY), SystemStage.Logic);

// Shared GPU voxel storage for lighting (world + ships). ChunkLoadSystem unloads past radius + 1, so the loaded
// span never exceeds 2 * (radius + 1) + 1 chunks per axis — the world's toroidal table size.
var gridStore = new GridStore(host.Context, new Vector3D<int>(2 * ViewXz + 3, 2 * ViewY + 3, 2 * ViewXz + 3));
host.Renderer.AttachGridStore(gridStore);
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
host.AddSystem(new GpuResidencySystem(host.World, staticWorld, gridStore), SystemStage.PreRender);
host.AddSystem(new GpuLightSystem(host.World, staticWorld, host.Context, host.Physics, gridStore), SystemStage.PreRender);
host.AddSystem(meshSystem, SystemStage.PreRender);
host.AddSystem(new RenderSystem(host.World, host.Renderer, host.Gui, host.Time), SystemStage.Render);

var camSpawn = TestScene.Build(host, worldGen.Seed);

// Ray-traced lighting prototype test ship (plan doc, task 4): a small solid hull with a Lamp exposed on
// top, placed near the camera's spawn so its shadow should visibly fall on the terrain below once the
// ray-traced toggle is on and ships are wired into GpuLightSystem's volume slots. Offset from camera
// spawn rather than re-deriving island geometry (TryFindNearestIsland is private to TestScene).
{
    var shipVoxels = new List<(int X, int Y, int Z, BlockId Id, Facing Facing)>();
    for (int x = 0; x < 5; x++)
    for (int z = 0; z < 5; z++)
    for (int y = 0; y < 2; y++)
        shipVoxels.Add((x, y, z, BlockId.Wood, Facing.Up));
    shipVoxels.Add((2, 2, 2, BlockId.Lamp, Facing.Up)); // exposed on the hull's roof, open air on 5 sides

    var shipSpawn = new Vector3(camSpawn.X + 10f, camSpawn.Y - 5f, camSpawn.Z + 45f);
    DynamicGridFactory.SpawnFromVoxels(host.World, meshSystem, gridSelection, shipSpawn, shipVoxels);
    Console.WriteLine($"[test-ship] spawned 5x2x5 hull + lamp at {shipSpawn}");
}

host.Run();

staticWorld.SaveAllDirty(); // graceful-exit flush; unload/autosave already cover the running game
gridStore.Dispose();
