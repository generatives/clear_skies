using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Ui;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game;
using ClearSkies.Game.Diagnostics;
using ClearSkies.Game.Generation;
using ClearSkies.Game.Hud;
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

// Session: single-player is a host session with nobody connected. Network IDs and owners exist, all local.
var session = Session.SinglePlayer();
var registry = new NetRegistry(host.World);
var idAllocator = new NetIdAllocator();
registry.RequestBlock = idAllocator.NextBlock;
using var gridNetworking = new GridNetworking(host.World, registry, session);

// The static world is a volume like any other, with an identity Transform (set by ChunkVolume) and zero pivot, and a
// reserved network ID. Its chunks each decide their own presence layers (see EntityPresenceSystem).
var staticVolumeEntity = host.World.CreateEntity();
var staticVolume = new ChunkVolume(staticVolumeEntity, host.World) { MeshIgnoresNeighbours = true, ChunksOwnPresence = true };
staticVolumeEntity.Set(new ChunkGrid() { Volume = staticVolume });
staticVolumeEntity.Set(new NetId { Value = NetRegistry.WorldVolume });
staticVolumeEntity.Set(session.LocalOwner());
staticVolumeEntity.Set<Rendered>();

ulong seed = 1337;
// Model blocks' glTF models (BlockDef.Model paths are relative to Resources/Models — see the csproj's link of
// the blockbench folder), loaded on first use.
using var blockModels = new BlockModelLibrary(host.Renderer, Path.Combine(AppContext.BaseDirectory, "Resources", "Models"));
var meshSystem    = new ChunkMeshSystem(host.World, host.Renderer, blockModels);
var gridSelection = new GridSelection(host.World);

host.AddSystem(host.Gui, SystemStage.Input); // opens ImGui's frame before Logic/PreRender systems run

// Game UI (immediate mode, laid out by Clay): opens its layout after ImGui's frame, since ImGui resets whether the UI
// has the mouse; Logic/PreRender systems declare elements; UiRenderSystem draws them in the HUD stage.
using var ui = new UiContext(host.Window, host.Input);
ui.AddFont(UiFont.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts", "PixelifySans.ttf")));
ui.Atlas.LoadSprites(Path.Combine(AppContext.BaseDirectory, "Resources", "Ui")); // the HUD's; name.9.png is nine-sliced
host.AddSystem(ui, SystemStage.Input);

// Fixed ticks: gameplay and physics run in the Tick stage, once per 1/60 s tick (0 or more times a frame, see
// TickClock). Mouse-look runs per frame before them; presses are collected per frame and handed to the next tick as
// the player's PlayerInput, which is all tick systems read. Drawing is smoothed between the last two ticks.
host.AddSystem(new LookInputSystem(host.World, host.Input), SystemStage.Input);
var inputSample = new InputSampleSystem(host.World, host.Input);
host.AddSystem(inputSample.Collect, SystemStage.Input);
var interpolation = new RenderInterpolationSystem(host.World, host.Time);
var hierarchy = new HierarchyTransformSystem(host.World);
host.AddSystem(interpolation.BeginTick, SystemStage.Tick); // the true poses back from the drawn ones
host.AddSystem(hierarchy, SystemStage.Tick);
host.AddSystem(inputSample, SystemStage.Tick);

// Commands: every discrete change goes through a registered handler, applied at one point in the tick. Single-player
// is a host with nobody connected, so every command's authority is here and it applies in the tick it was sent.
var commands = new CommandSystem(session, registry, () => host.Time.Tick);
var blockEntities = new BlockEntities(host.World, registry);
var editLimits = new EditLimits();
commands.Register(new EditVoxelsHandler(blockEntities, editLimits));
commands.Register(new SetLeverHandler(blockEntities));
commands.Register(new SetWheelHandler(blockEntities));
commands.Register(new SetGridLockedHandler(registry, host.Physics));
commands.Register(new RightGridHandler(registry, host.Physics));
commands.Register(new SetMoveModeHandler(registry));

var physicsBody = new PhysicsBodySystem(host.World, host.Physics);

// Streaming budget: how much GPU light storage the loaded world may use, in MB (3 KB per 8³ brick of surface). Chunks
// are loaded closest-first until it's spent (see ChunkLoadSystem); only surfaces use it, so solid stone inside an
// island is nearly free. The GPU store adds a fifth on top for headroom and ships. --light-budget-mb N overrides it,
// e.g. for a software renderer whose small max buffer size can't hold it (the store also shrinks it to fit).
int LightBudgetMb = 2048;
int budgetArg = Array.IndexOf(args, "--light-budget-mb");
if (budgetArg >= 0 && budgetArg + 1 < args.Length) LightBudgetMb = int.Parse(args[budgetArg + 1]);
// View distance: how far out (in blocks, horizontally) islands are streamed, if the budget reaches. The GPU's world
// index covers it both ways at 2 bytes per chunk position (~48 MB at 10000). --view-distance N overrides it.
float ViewDistance = 2000f;
int viewArg = Array.IndexOf(args, "--view-distance");
if (viewArg >= 0 && viewArg + 1 < args.Length) ViewDistance = float.Parse(args[viewArg + 1], System.Globalization.CultureInfo.InvariantCulture);
const int MinChunkY = 0; // streamed layers are -8..55 (blocks -256..1792): HeartGrid's WorldBottom..WorldTop

// World generator: islands cut out of a continental terrain around island hearts (see HeartWorldGenerator).
Func<IWorldGenerator> generatorFactory = () => new HeartWorldGenerator(seed);
SkySettings.CloudAltitude = 1250f; // the islands are mostly low: clouds among the hills
SkySettings.CloudSeaAltitude = HeartGrid.CloudSeaAltitude; // below its lowest islands

// Shared GPU voxel storage for lighting (world + ships).
var gridStore = new GridStore(host.Context, (int)((long)LightBudgetMb * 1024 * 1024 / GridStore.SlotBytes),
                              ChunkLoadSystem.WorldIndexDim(ViewDistance));
var chunkLoadSystem = new ChunkLoadSystem(host.World, staticVolume, gridStore, generatorFactory,
                                          ViewDistance, MinChunkY, "Hearts16");
host.Renderer.AttachGridStore(gridStore);
host.AddSystem(physicsBody, SystemStage.Tick);

// Character controller (ported from BepuPhysics2's own Demos/Demos/Characters — see
// Physics/Characters/): motion goals (WASD/jump/mode toggle) must be set before the physics step
// so Simulation.Timestep's CollisionsDetected analysis sees them this same tick.
host.AddSystem(new PlayerMovementSystem(host.World, commands), SystemStage.Tick);

// Milestone 5: airship flight (velocity control law + Fan/Buoyant propulsion, merged into one system —
// see AirshipFlightSystem), before the physics step so its impulses are integrated this same tick.
var gridPilot = new GridPilotSystem(host.World, host.Input, host.Physics, staticVolume, physicsBody, commands);
// Place, break, spawn and use controls (levers and wheels, whose control systems turn drags into commands), then apply
// every command sent this tick.
var blockActions = new BlockActionSystem(host.World, commands, editLimits, gridSelection);
host.AddSystem(blockActions, SystemStage.Tick);
host.AddSystem(new LeverControlSystem(host.World, commands), SystemStage.Tick);
host.AddSystem(new SteeringWheelControlSystem(host.World, commands), SystemStage.Tick);
host.AddSystem(commands, SystemStage.Tick);
var airshipFlight = new AirshipFlightSystem(host.World, host.Physics);
host.AddSystem(airshipFlight, SystemStage.Tick);
var presence = new EntityPresenceSystem(host.World, session, staticVolume, ViewDistance);
host.AddSystem(presence, SystemStage.Tick); // presence layers: bodies, drawing, terrain interest and colliders

host.AddSystem(host.Physics, SystemStage.Tick); // one step, once bodies/impulses for this tick are in
host.AddSystem(new PhysicsTransformSyncSystem(host.World, host.Physics), SystemStage.Tick); // body poses -> Transform
host.AddSystem(hierarchy, SystemStage.Tick); // e.g. volume Transforms -> chunk Transforms
host.AddSystem(new SupportSystem(host.World, host.Physics), SystemStage.Tick); // what each character stands on or rides with
host.AddSystem(interpolation.EndTick, SystemStage.Tick); // records this tick's poses

// Per frame, after the ticks: draw between the last two ticks (children follow), then stream terrain around the view.
host.AddSystem(interpolation, SystemStage.Logic);
host.AddSystem(hierarchy, SystemStage.Logic);
host.AddSystem(new CameraFollowSystem(host.World), SystemStage.Logic); // the camera at the local player's eye
host.AddSystem(chunkLoadSystem, SystemStage.Logic);
host.AddSystem(gridPilot, SystemStage.Logic);
host.AddSystem(new BlockTargetSystem(host.World, host.Input, host.Renderer, blockActions, editLimits), SystemStage.Logic);
host.AddSystem(new HudUi(ui, host.Input, blockActions, gridPilot, host.Renderer.Atlas,
                         Path.Combine(AppContext.BaseDirectory, "Resources", "Icons")), SystemStage.Logic); // crosshair, hotbar
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

host.AddSystem(new GpuResidencySystem(host.World, staticVolume, gridStore), SystemStage.PreRender);
host.AddSystem(new GpuLightSystem(host.World, staticVolume, host.Context, gridStore), SystemStage.PreRender);
host.AddSystem(meshSystem, SystemStage.PreRender);
host.AddSystem(new BlockModelSystem(host.World, blockModels), SystemStage.PreRender); // block entities -> RenderedModel
// Rendering: the host opens the frame, runs the render stages (systems in the order added within a stage), then
// closes it with ImGui and presents. Each render system is handed this frame's camera and time.
using var clouds = new CloudRenderSystem(host.Renderer, new HeartCloudDensity(seed));
host.AddSystem(new ChunkRenderSystem(host.World, host.Renderer), SystemStage.RenderWorld);
host.AddSystem(new ModelRenderSystem(host.World, host.Renderer), SystemStage.RenderWorld);
host.AddSystem(clouds, SystemStage.RenderWorld);
host.AddSystem(new SkyRenderSystem(host.Renderer), SystemStage.RenderSky);
host.AddSystem(new WireframeRenderSystem(host.World, host.Renderer), SystemStage.RenderOverlay);
host.AddSystem(new HudRenderSystem(host.World, host.Renderer), SystemStage.RenderHud);
using var uiRenderer = new UiRenderSystem(ui, host.Renderer);
host.AddSystem(uiRenderer, SystemStage.RenderHud);

// --camera x,y,z[,yaw,pitch]: start the camera at a given spot instead of overlooking the nearest cluster.
float[]? cameraOverride = null;
int camArg = Array.IndexOf(args, "--camera");
if (camArg >= 0 && camArg + 1 < args.Length)
    cameraOverride = args[camArg + 1].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
var camSpawn = TestScene.Build(host, registry, session, seed, cameraOverride, HeartSpawn(seed));

// Spawn: over a wide, flat stretch of plains 18 km east of the origin (found by scanning seed 1337 for flat, well-
// covered lowland), 60 blocks above the terrain surface there (which no piece's top reaches), looking north across it.
static (Vector3D<float> Position, float Yaw, float Pitch)? HeartSpawn(ulong seed)
{
    const float x = 18240f, z = 1088f;
    float y = ContinentTerrain.For(seed).Height(x, z) + 60f;
    return (new Vector3D<float>(x, y, z), MathF.PI, -0.15f);
}

// Ray-traced lighting prototype test ship (plan doc, task 4): a small solid hull with a Lamp exposed on
// top, placed near the camera's spawn so its shadow should visibly fall on the terrain below once the
// ray-traced toggle is on and ships are wired into GpuLightSystem's volume slots. Offset from camera
// spawn rather than re-deriving island geometry.
{
    var shipVoxels = new List<(int X, int Y, int Z, BlockId Id, BlockOrientation Orientation)>();
    for (int x = 0; x < 5; x++)
    for (int z = 0; z < 5; z++)
    for (int y = 0; y < 2; y++)
        shipVoxels.Add((x, y, z, BlockId.Wood, BlockOrientation.Upright));
    shipVoxels.Add((2, 2, 2, BlockId.Lamp, BlockOrientation.Upright)); // exposed on the hull's roof, open air on 5 sides
    // The helm, on the roof one row from the stern, facing a player standing on the stern row looking at the bow
    // (-Z): the wheel, and a lever per axis — forward/back, starboard/port, and up/down (standing out of a post
    // towards the player, so it levers vertically) — plus a second forward/back lever out of the east wall, which
    // moves with the first.
    shipVoxels.Add((2, 2, 3, BlockId.SteeringWheel, BlockOrientation.From(Direction.Up, Direction.South)));
    shipVoxels.Add((1, 2, 3, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.South)));
    shipVoxels.Add((3, 2, 3, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.East)));
    shipVoxels.Add((4, 2, 2, BlockId.Wood, BlockOrientation.Upright));
    shipVoxels.Add((4, 2, 3, BlockId.Lever, BlockOrientation.From(Direction.South, Direction.Up)));
    shipVoxels.Add((5, 1, 2, BlockId.Lever, BlockOrientation.From(Direction.East, Direction.North)));

    var shipSpawn = new Vector3(camSpawn.X + 10f, camSpawn.Y - 5f, camSpawn.Z + 45f);
    DynamicGridFactory.SpawnFromVoxels(host.World, gridSelection, shipSpawn, shipVoxels);
    Console.WriteLine($"[test-ship] spawned 5x2x5 hull + lamp at {shipSpawn}");
}

host.Run();

chunkLoadSystem.SaveAllDirty(); // graceful-exit flush; unload/autosave already cover the running game
gridStore.Dispose();
