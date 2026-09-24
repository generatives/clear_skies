using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering;
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

// The static world is a volume like any other, with an identity Transform (set by ChunkVolume) and zero pivot.
var staticVolumeEntity = host.World.CreateEntity();
var staticVolume = new ChunkVolume(staticVolumeEntity, host.World);
staticVolumeEntity.Set(new ChunkGrid() { Volume = staticVolume });

ulong seed = 1337;
// Model blocks' glTF models (BlockDef.Model paths are relative to Resources/Models — see the csproj's link of
// the blockbench folder), loaded on first use.
using var blockModels = new BlockModelLibrary(host.Renderer, Path.Combine(AppContext.BaseDirectory, "Resources", "Models"));
var meshSystem    = new ChunkMeshSystem(host.World, host.Renderer, blockModels);
var gridSelection = new GridSelection(host.World);

host.AddSystem(host.Gui, SystemStage.Input); // opens ImGui's frame before Logic/PreRender systems run

var physicsBody = new PhysicsBodySystem(host.World, host.Physics);

// View distance: xzRadius=16/yRadius=3 (was 8/3). Verified crash-free and smooth at this setting; a bigger
// jump (tried 16/3) hit two real problems: the GPU device was silently capped at a 256 MiB max buffer size
// (fixed in GpuContext — see AdapterLimits), and even past that, single-digit FPS from the GPU light flood
// recomputing a much larger dirty region during the load-in burst plus the per-frame full-chunk scans in
// ChunkMeshSystem/GpuResidencySystem/GpuLightSystem/PhysicsBodySystem (see the deferred dirty-queue task).
// Pushing further needs that follow-up work, not just a bigger radius.
const int ViewXz = 16, ViewY = 5;
var chunkLoadSystem = new ChunkLoadSystem(host.World, staticVolume, () => new SkyWorldGenerator(seed), xzRadius: ViewXz, yRadius: ViewY);
host.AddSystem(chunkLoadSystem, SystemStage.Logic);

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
var gridPilot = new GridPilotSystem(host.World, host.Input, host.Physics, staticVolume, physicsBody);
var airshipFlight = new AirshipFlightSystem(host.World, host.Physics, host.Input);
host.AddSystem(airshipFlight, SystemStage.Logic);

host.AddSystem(host.Physics, SystemStage.Logic); // steps the simulation once bodies/impulses for this frame are in
host.AddSystem(new PhysicsTransformSyncSystem(host.World, host.Physics), SystemStage.Logic); // body poses -> Transform
host.AddSystem(new HierarchyTransformSystem(host.World), SystemStage.Logic); // e.g. volume Transforms -> chunk Transforms
host.AddSystem(new CharacterCameraSyncSystem(host.World), SystemStage.Logic); // reads the capsule's post-physics pose into Transform
host.AddSystem(gridPilot, SystemStage.Logic);
host.AddSystem(new PlayerInputSystem(host.World, host.Input, meshSystem, host.Renderer, gridSelection), SystemStage.Logic);
var gridPersistence = new GridPersistenceSystem(host.World, meshSystem, host.Physics, gridSelection);
host.AddSystem(gridPersistence, SystemStage.Logic);
// The airship-related debug panels above (Pilot/Flight/Save-Load) drew into their own separate "Systems"
// menu windows; combined here into one "Airship" window so they read as one feature.
host.AddSystem(new AirshipDebugPanel(gridPilot, airshipFlight, gridPersistence), SystemStage.Logic);
host.AddSystem(new LeverTestAnimationSystem(host.World), SystemStage.Logic); // test: rocks lever arms
host.AddSystem(new LambdaSystem(() =>
{
    if (host.Input.WasKeyPressed(Key.Tab))
    {
        host.Renderer.WireframeMode = !host.Renderer.WireframeMode;
        Console.WriteLine($"[debug] wireframe: {host.Renderer.WireframeMode}");
    }
}), SystemStage.Logic);

host.AddSystem(new GpuResidencySystem(host.World, staticVolume, gridStore), SystemStage.PreRender);
host.AddSystem(new GpuLightSystem(host.World, staticVolume, host.Context, gridStore), SystemStage.PreRender);
host.AddSystem(meshSystem, SystemStage.PreRender);
host.AddSystem(new BlockModelSystem(host.World, blockModels), SystemStage.PreRender); // block entities -> RenderedModel
// Rendering: the host opens the frame, runs the render stages (systems in the order added within a stage), then
// closes it with ImGui and presents. Each render system is handed this frame's camera and time.
using var clouds = new CloudRenderSystem(host.Renderer);
host.AddSystem(new ChunkRenderSystem(host.World, host.Renderer), SystemStage.RenderWorld);
host.AddSystem(new ModelRenderSystem(host.World, host.Renderer), SystemStage.RenderWorld);
host.AddSystem(clouds, SystemStage.RenderWorld);
host.AddSystem(new SkyRenderSystem(host.Renderer), SystemStage.RenderSky);
host.AddSystem(new WireframeRenderSystem(host.World, host.Renderer), SystemStage.RenderOverlay);
host.AddSystem(new HudRenderSystem(host.World, host.Renderer), SystemStage.RenderHud);

var camSpawn = TestScene.Build(host, seed);

// Ray-traced lighting prototype test ship (plan doc, task 4): a small solid hull with a Lamp exposed on
// top, placed near the camera's spawn so its shadow should visibly fall on the terrain below once the
// ray-traced toggle is on and ships are wired into GpuLightSystem's volume slots. Offset from camera
// spawn rather than re-deriving island geometry (TryFindNearestIsland is private to TestScene).
{
    var shipVoxels = new List<(int X, int Y, int Z, BlockId Id, BlockOrientation Orientation)>();
    for (int x = 0; x < 5; x++)
    for (int z = 0; z < 5; z++)
    for (int y = 0; y < 2; y++)
        shipVoxels.Add((x, y, z, BlockId.Wood, BlockOrientation.Upright));
    shipVoxels.Add((2, 2, 2, BlockId.Lamp, BlockOrientation.Upright)); // exposed on the hull's roof, open air on 5 sides
    // Model blocks: a lever standing on the roof and one sticking out of the east wall.
    shipVoxels.Add((0, 2, 0, BlockId.Lever, BlockOrientation.Upright));
    shipVoxels.Add((5, 1, 2, BlockId.Lever, BlockOrientation.From(Direction.East, Direction.North)));

    var shipSpawn = new Vector3(camSpawn.X + 10f, camSpawn.Y - 5f, camSpawn.Z + 45f);
    DynamicGridFactory.SpawnFromVoxels(host.World, gridSelection, shipSpawn, shipVoxels);
    Console.WriteLine($"[test-ship] spawned 5x2x5 hull + lamp at {shipSpawn}");
}

// glTF model rendering test: the Blockbench lever, floating a couple of blocks in front of the spawn camera (which
// starts facing -Z, away from the test ship above), just below eye level. No scaling needed: Blockbench's glTF exporter already divides
// its 16-pixels-per-block grid by 16, so 1 exported unit = 1 block.
{
    var lever = blockModels.Get(BlockId.Lever)!;
    var leverEntity = host.World.CreateEntity();
    var leverTransform = Transform.Identity;
    leverTransform.Position = camSpawn + new Vector3D<float>(0f, -0.75f, -2.5f);
    leverEntity.Set(leverTransform);
    leverEntity.Set(new RenderedModel(lever));
    Console.WriteLine($"[model] lever ({lever.Parts.Count} part(s)) at {leverTransform.Position}");
}

host.Run();

chunkLoadSystem.SaveAllDirty(); // graceful-exit flush; unload/autosave already cover the running game
gridStore.Dispose();
