using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Physics.Support;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Persistence;
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

// Background (non-blocking) full collections only while the game runs: a blocking one stops every thread for tens of
// milliseconds, a visible hitch. Needs concurrent GC, which is on by default.
System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

// --backend vulkan|dx12|metal|gl: the graphics API, instead of wgpu's choice (a window in the background can fare
// differently under each).
{
    int b = Array.IndexOf(args, "--backend");
    if (b >= 0 && b + 1 < args.Length)
        ClearSkies.Engine.Rendering.WebGpu.GpuContext.Backend = args[b + 1].ToLowerInvariant() switch
        {
            "vulkan" => Silk.NET.WebGPU.Extensions.WGPU.InstanceBackend.Vulkan,
            "dx12" => Silk.NET.WebGPU.Extensions.WGPU.InstanceBackend.DX12,
            "metal" => Silk.NET.WebGPU.Extensions.WGPU.InstanceBackend.Metal,
            "gl" => Silk.NET.WebGPU.Extensions.WGPU.InstanceBackend.GL,
            var other => throw new ArgumentException($"Unknown --backend {other}: vulkan, dx12, metal or gl."),
        };
}
using var host = new EngineHost(new EngineOptions("Clear Skies", 1280, 720, LogGpuErrors: true));

host.Renderer.LoadTextureAtlas(
    Path.Combine(AppContext.BaseDirectory, "Resources", "spritesheet_tiles.png"),
    Path.Combine(AppContext.BaseDirectory, "Resources", "spritesheet_tiles.xml"));

string ArgValue(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
// --fps N: vsync off, at most N frames a second, so a window in the background doesn't slow down (for playing a host
// and a client side by side, e.g. --fps 60 on both).
if (int.TryParse(ArgValue("--fps"), out int fpsCap) && fpsCap > 0) host.CapFrameRate(fpsCap);
// This machine's player: an ID kept in Saves/settings.txt, and a name. --name <player> plays as someone else, with
// their own settings file (Saves/settings-<player>.txt), so two instances on one machine are two players.
string nameArg = ArgValue("--name");
var localSettings = LocalSettings.LoadOrCreate(Path.Combine(AppContext.BaseDirectory, "Saves",
    nameArg.Length > 0 ? $"settings-{string.Concat(nameArg.Where(char.IsLetterOrDigit))}.txt" : "settings.txt"),
    nameArg.Length > 0 ? nameArg : Environment.UserName);
string playerName = localSettings.Name;

// Session: single-player is a host session with nobody connected (--host <port> lets others join); --join
// <address:port> joins someone else's instead. Joining happens now, before the world is built, because the host
// decides the seed.
var session = Session.SinglePlayer();
var registry = new EntityRegistry(host.World);
ulong generationChecksum = GenerationChecksum.Compute();
string joinAddress = ArgValue("--join");
bool joining = joinAddress.Length > 0;
ClearSkies.Net.Transport.LaggedTransport? transport = null;
ClearSkies.Net.Protocol.Welcome welcome = default;
if (joining)
{
    int colon = joinAddress.LastIndexOf(':');
    string address = colon > 0 ? joinAddress[..colon] : joinAddress;
    int port = colon > 0 ? int.Parse(joinAddress[(colon + 1)..]) : 7777;
    Console.WriteLine($"[net] joining {address}:{port} as {playerName}");
    transport = new ClearSkies.Net.Transport.LaggedTransport(ClearSkies.Net.Transport.LiteNetTransport.Join(address, port));
    welcome = ClearSkies.Net.Session.ClientSession.Connect(transport,
        new ClearSkies.Net.Protocol.Hello(ClearSkies.Net.Protocol.ProtocolVersion.Current, localSettings.PlayerId, playerName, generationChecksum),
        TimeSpan.FromSeconds(15));
    session.BecomeClient(welcome.Peer);
}
else if (ArgValue("--host") is { Length: > 0 } hostPort)
{
    transport = new ClearSkies.Net.Transport.LaggedTransport(ClearSkies.Net.Transport.LiteNetTransport.Host(int.Parse(hostPort)));
    Console.WriteLine($"[net] hosting on port {hostPort}");
}

// The world's save (the host's only): Saves/Worlds/<name>.db (--world <name>, default "Default"). A new world's seed
// is 1337 unless --seed <n> says otherwise; after that it's whatever the save says. A client takes the host's seed.
string worldName = ArgValue("--world") is { Length: > 0 } w ? w : "Default";
SaveDatabase? saveDb = joining ? null : SaveDatabase.Open(SaveDatabase.PathFor(worldName));
bool newWorld = saveDb is { Seed: null };
ulong seed = joining ? welcome.Seed : saveDb!.Seed ?? (ulong.TryParse(ArgValue("--seed"), out var seedArg) ? seedArg : 1337UL);
if (newWorld) saveDb!.Seed = seed;
if (saveDb != null) Console.WriteLine($"[save] world '{worldName}' ({(newWorld ? "new" : "loaded")}), seed {seed}");

// Entity IDs: the host hands out blocks from the save's next free ID, so they never repeat across sessions; a
// client gets blocks from the host.
var idAllocator = new EntityIdAllocator(saveDb?.NextFreeId ?? EntityRegistry.FirstFreeId);
if (!joining) registry.RequestBlock = idAllocator.NextBlock;

// The network session: first in every tick it receives, last it sends (set once the command system exists).
ClearSkies.Net.Session.NetSession? net = null;

// The static world is a volume like any other, with an identity Transform (set by ChunkVolume), and a
// reserved entity ID. Its chunks each decide their own presence layers (see EntityPresenceSystem).
var staticVolumeEntity = host.World.CreateEntity();
var staticVolume = new ChunkVolume(staticVolumeEntity, host.World) { MeshIgnoresNeighbours = true, ChunksOwnPresence = true };
staticVolumeEntity.Set(new ChunkGrid() { Volume = staticVolume });
staticVolumeEntity.Set(EntityRegistry.WorldVolume);
staticVolumeEntity.Set(session.OwnerFor(PeerId.Host));
staticVolumeEntity.Set<Rendered>();

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

// Fixed ticks: gameplay and physics run in the Simulation stage, once per 1/60 s tick (0 or more times a frame, see
// TickClock). Mouse-look runs per frame before them; presses are collected per frame and handed to the next tick as
// the player's PlayerInput, which is all tick systems read. Moving things are drawn between their last two ticks.
host.AddSystem(new LookInputSystem(host.World, host.Input), SystemStage.Input);
var inputSample = new InputSampleSystem(host.World, host.Input, host.Time);
host.AddSystem(inputSample, SystemStage.Input); // latches the frame's input
var interpolation = new TickInterpolationSystem(host.World, host.Time);
host.AddSystem(new LambdaSystem(() => net?.Receive()), SystemStage.Simulation); // commands, events, snapshots, session messages
var hierarchy = new HierarchyTransformSystem(host.World);
host.AddSystem(hierarchy, SystemStage.Simulation);

host.AddSystem(inputSample, SystemStage.Simulation);

// Commands: every discrete change goes through a registered handler, applied at one point in the tick. Single-player
// is a host with nobody connected, so every command's authority is here and it applies in the tick it was sent.
var commands = new CommandSystem(session, registry, () => host.Time.Tick);
var blockEntities = new BlockEntities(host.World, registry);
var editLimits = new EditLimits();
GameCommands.RegisterAll(commands, host.World, session, blockEntities, editLimits, registry, host.Physics, gridSelection);

// Persistence (the host's): entities load within 1,000 blocks of a player and unload past 1,100, written to the save
// as they go; everything is autosaved every 5 minutes and on exit, in one transaction.
WorldSaver? worldSaver = null;
if (saveDb != null)
{
    var storedIndex = new StoredEntityIndex(saveDb.ReadEntityIndex());
    worldSaver = new WorldSaver(host.World, saveDb, storedIndex, commands, idAllocator);
    host.AddSystem(new EntityStreamingSystem(host.World, saveDb, storedIndex, registry, commands, worldSaver), SystemStage.Simulation);
    host.AddSystem(worldSaver, SystemStage.Simulation);
}

var physicsBody = new PhysicsBodySystem(host.World, host.Physics);

// Streaming budget: how much GPU light storage the loaded world may use, in MB (3 KB per 8³ brick of surface). Chunks
// are loaded closest-first until it's spent (see ChunkLoadSystem); only surfaces use it, so solid stone inside an
// island is nearly free. The GPU store adds a fifth on top for headroom and ships. --light-budget-mb N overrides it,
// e.g. for a software renderer whose small max buffer size can't hold it (the store also shrinks it to fit).
int LightBudgetMb = 512;
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
                                          ViewDistance, MinChunkY, saveDb != null ? new DatabaseChunkStore(saveDb) : new NoChunkStore());
host.Renderer.AttachGridStore(gridStore);
host.AddSystem(physicsBody, SystemStage.Simulation);

// Character controller (ported from BepuPhysics2's own Demos/Demos/Characters — see
// Physics/Characters/): motion goals (WASD/jump/mode toggle) must be set before the physics step
// so Simulation.Timestep's CollisionsDetected analysis sees them this same tick.
host.AddSystem(new PlayerMovementSystem(host.World, commands), SystemStage.Simulation);

// Milestone 5: airship flight (velocity control law + Fan/Buoyant propulsion, merged into one system —
// see AirshipFlightSystem), before the physics step so its impulses are integrated this same tick.
var gridPilot = new GridPilotSystem(host.World, host.Input, host.Physics, staticVolume, physicsBody, commands);
// Place, break, spawn and use controls (levers and wheels, whose control systems turn drags into commands as the
// interactions are published), then apply every command sent this tick, then pose the controls from what the commands
// set, so an arm is posed this tick where the view was turned to keep on it.
var blockActions = new BlockActionSystem(host.World, commands, editLimits, gridSelection);
host.AddSystem(blockActions, SystemStage.Simulation);
var levers = new LeverControlSystem(host.World, commands);
var wheels = new SteeringWheelControlSystem(host.World, commands);
host.AddSystem(commands, SystemStage.Simulation);
host.AddSystem(levers, SystemStage.Simulation);
host.AddSystem(wheels, SystemStage.Simulation);
var airshipFlight = new AirshipFlightSystem(host.World, host.Physics);
host.AddSystem(airshipFlight, SystemStage.Simulation);
var presence = new EntityPresenceSystem(host.World, session, staticVolume, ViewDistance)
{
    // Entities are drawn as far as the terrain, but no further than the load window; a grid owned here gets a body
    // once the terrain around it has loaded with colliders, so nothing loaded from the save falls through the world.
    RenderDistanceLimit = EntityStreamingSystem.LoadWindow,
    TerrainReady = p => chunkLoadSystem.IsTerrainLoaded(new Vector3D<float>(p.X, p.Y, p.Z), 64f) &&
                        physicsBody.CollidersReady(staticVolume, p, 64f),
};
host.AddSystem(presence, SystemStage.Simulation); // presence layers: bodies, drawing, terrain interest and colliders

host.AddSystem(host.Physics, SystemStage.Simulation); // one step, once bodies/impulses for this tick are in
host.AddSystem(new PhysicsTransformSyncSystem(host.World, host.Physics), SystemStage.Simulation); // body poses -> Transform
host.AddSystem(hierarchy, SystemStage.Simulation); // e.g. volume Transforms -> chunk Transforms
host.AddSystem(new SupportSystem(host.World, host.Physics), SystemStage.Simulation); // what each character stands on or rides with
host.AddSystem(interpolation, SystemStage.Simulation); // records this tick's poses

// The network session, now the command system exists: a host (single-player: with the transport off) or a client.
var hostClock = new HostTickClock(host.Time, host.Clock);
if (joining)
{
    var client = new ClearSkies.Net.Session.ClientSession(transport!, welcome, session, commands, registry, host.World, hostClock,
        p => chunkLoadSystem.IsTerrainLoaded(new Vector3D<float>(p.X, p.Y, p.Z), 64f));
    client.Ended += reason => { Console.WriteLine($"[net] session ended: {reason}"); host.Window.Native.Close(); };
    net = client;
}
else
{
    var hostNet = new ClearSkies.Net.Session.HostSession(transport, session, commands, registry, host.World, hostClock, idAllocator, seed,
        generationChecksum, playerId =>
        {
            // A returning player spawns where they left off; a new one at the spawn point.
            var saved = saveDb!.ReadPlayer(playerId);
            if (saved != null)
            {
                var reader = new ClearSkies.Engine.Serialization.NetReader(saved);
                var spawn = ((SpawnPlayerHandler)commands.HandlerFor(CommandIds.SpawnPlayer)!).Read(ref reader);
                return (saved, spawn.Player.Position);
            }
            var p = HeartSpawn(seed)!.Value.Position;
            return (null, new Vector3(p.X, p.Y - PlayerFactory.EyeHeight, p.Z));
        });
    hostNet.NewPlayerLook = (HeartSpawn(seed)!.Value.Yaw, HeartSpawn(seed)!.Value.Pitch);
    // A leaving player is saved (the players table), then despawned.
    var leaving = new HashSet<EntityId>();
    hostNet.PlayerLeaving = player =>
    {
        leaving.Add(player.Get<EntityId>());
        DescribeRequest.Request(player, DescribePurpose.Store);
    };
    worldSaver!.Stored += id => { if (leaving.Remove(id)) commands.Send(new DespawnEntity { Entity = id, KeepStored = true }); };
    net = hostNet;
}
var bodySync = new ClearSkies.Net.Sync.BodySync(net, host.World, host.Physics);
host.AddSystem(bodySync, SystemStage.Simulation); // snapshots of owned bodies, every second tick
host.AddSystem(new ClearSkies.Net.Session.NetSendSystem(net), SystemStage.Simulation);
var remoteBodies = new ClearSkies.Net.Sync.RemoteBodySystem(host.World, registry, hostClock);
host.Gui.RegisterDebugUi(new ClearSkies.Net.Debug.NetDebugPanel(net, remoteBodies, transport));
gridPilot.Disabled = () => net.OthersConnected; // pilot mode and flight tuning: single-player only

// Moves the camera once a frame (not per tick) while flying; before the interpolation, which then draws it there.
// --flight-test flies once the world has loaded, then quits.
bool flightTest = args.Contains("--flight-test");
host.AddSystem(new StreamingFlightTest(host, flightTest, flightTest ? () => host.Window.Native.Close() : null),
               SystemStage.Frame);
// Per frame, after the ticks: draw between the last two ticks (children follow), then stream terrain around the view.
host.AddSystem(gridPilot, SystemStage.Frame); // puts the camera under a piloted grid...
host.AddSystem(new EyeSystem(host.World), SystemStage.Frame); // ...or at the local player's eye
host.AddSystem(interpolation, SystemStage.Frame);
host.AddSystem(remoteBodies, SystemStage.Frame); // bodies owned elsewhere, about 100 ms behind
host.AddSystem(hierarchy, SystemStage.Frame);
host.AddSystem(chunkLoadSystem, SystemStage.Frame);
host.AddSystem(new BlockTargetSystem(host.World, host.Input, host.Renderer, blockActions, editLimits), SystemStage.Frame);
host.AddSystem(new HudUi(ui, host.Input, blockActions, gridPilot, host.Renderer.Atlas,
                         Path.Combine(AppContext.BaseDirectory, "Resources", "Icons")), SystemStage.Frame); // crosshair, hotbar
var gridPersistence = new GridPersistenceSystem(host.World, meshSystem, host.Physics, gridSelection, commands);
host.AddSystem(gridPersistence, SystemStage.Frame);
// The airship-related debug panels above (Pilot/Flight/Save-Load) drew into their own separate "Systems"
// menu windows; combined here into one "Airship" window so they read as one feature.
host.AddSystem(new AirshipDebugPanel(gridPilot, airshipFlight, gridPersistence), SystemStage.Frame);
host.AddSystem(new LambdaSystem(() =>
{
    if (host.Input.WasKeyPressed(Key.Tab))
    {
        host.Renderer.WireframeMode = !host.Renderer.WireframeMode;
        Console.WriteLine($"[debug] wireframe: {host.Renderer.WireframeMode}");
    }
}), SystemStage.Frame);

host.AddSystem(new GpuResidencySystem(host.World, staticVolume, gridStore), SystemStage.PreRender);
host.AddSystem(new GpuLightSystem(host.World, staticVolume, host.Context, gridStore), SystemStage.PreRender);
host.AddSystem(meshSystem, SystemStage.PreRender);
host.AddSystem(new BlockModelSystem(host.World, blockModels), SystemStage.PreRender); // block entities -> RenderedModel
using var playerModels = new PlayerModelSystem(host.World, host.Renderer);
host.AddSystem(playerModels, SystemStage.PreRender); // other players, as boxes
// Rendering: the host opens the frame, runs the render stages (systems in the order added within a stage), then
// closes it with ImGui and presents. Each render system is handed this frame's camera and time.
using var clouds = new CloudRenderSystem(host.Renderer, new HeartCloudDensity(seed));
host.AddSystem(new ChunkRenderSystem(host.World, host.Renderer, staticVolume), SystemStage.RenderWorld);
host.AddSystem(new ModelRenderSystem(host.World, host.Renderer, host.Time), SystemStage.RenderWorld);
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
// A client's player is spawned by the host once it has joined; everyone else spawns their own here.
var camSpawn = TestScene.Build(host, commands, localSettings, saveDb?.ReadPlayer(localSettings.PlayerId), cameraOverride,
                               joining ? (new Vector3D<float>(welcome.Spawn.X, welcome.Spawn.Y + PlayerFactory.EyeHeight, welcome.Spawn.Z), MathF.PI, -0.15f) : HeartSpawn(seed),
                               spawnPlayer: !joining);

// Spawn: over a wide, flat stretch of plains 18 km east of the origin (found by scanning seed 1337 for flat, well-
// covered lowland), 60 blocks above the terrain surface there (which no piece's top reaches), looking north across it.
static (Vector3D<float> Position, float Yaw, float Pitch)? HeartSpawn(ulong seed)
{
    const float x = -825f, z = -1000f;
    float y = ContinentTerrain.For(seed).Height(x, z) + 60f;
    return (new Vector3D<float>(x, y, z), MathF.PI, -0.15f);
}

// Ray-traced lighting prototype test ship (plan doc, task 4): a small solid hull with a Lamp exposed on
// top, placed near the camera's spawn so its shadow should visibly fall on the terrain below once the
// ray-traced toggle is on and ships are wired into GpuLightSystem's volume slots. Offset from camera
// spawn rather than re-deriving island geometry. Only in a new world: after that it's in the save.
if (newWorld)
{
    var shipVoxels = new List<GridVoxel>();
    for (int x = 0; x < 5; x++)
    for (int z = 0; z < 5; z++)
    for (int y = 0; y < 2; y++)
        shipVoxels.Add(new(x, y, z, BlockId.Wood, BlockOrientation.Upright));
    shipVoxels.Add(new(2, 2, 2, BlockId.Lamp, BlockOrientation.Upright)); // exposed on the hull's roof, open air on 5 sides
    // The helm, on the roof one row from the stern, facing a player standing on the stern row looking at the bow
    // (-Z): the wheel, and a lever per axis — forward/back, starboard/port, and up/down (standing out of a post
    // towards the player, so it levers vertically) — plus a second forward/back lever out of the east wall, which
    // moves with the first.
    shipVoxels.Add(new(2, 2, 3, BlockId.SteeringWheel, BlockOrientation.From(Direction.Up, Direction.South)));
    shipVoxels.Add(new(1, 2, 3, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.South)));
    shipVoxels.Add(new(3, 2, 3, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.East)));
    shipVoxels.Add(new(4, 2, 2, BlockId.Wood, BlockOrientation.Upright));
    shipVoxels.Add(new(4, 2, 3, BlockId.Lever, BlockOrientation.From(Direction.South, Direction.Up)));
    shipVoxels.Add(new(5, 1, 2, BlockId.Lever, BlockOrientation.From(Direction.East, Direction.North)));

    var shipSpawn = new Vector3(camSpawn.X + 10f, camSpawn.Y - 5f, camSpawn.Z + 45f);
    commands.Send(new Spawn<GridDescription> { Description = GridDescription.FromVoxels(shipSpawn, shipVoxels), Select = true });
    Console.WriteLine($"[test-ship] spawned 5x2x5 hull + lamp at {shipSpawn}");
}

if (worldSaver != null) worldSaver.SaveChunks = chunkLoadSystem.SaveAllDirty;
// Ctrl+C or a kill closes the window (on the main thread, next frame) as if it were closed by hand, so the world is
// saved on the way out.
bool quitRequested = false;
void RequestQuit(System.Runtime.InteropServices.PosixSignalContext c) { c.Cancel = true; Volatile.Write(ref quitRequested, true); }
using var sigInt = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, RequestQuit);
using var sigTerm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, RequestQuit);
host.AddSystem(new LambdaSystem(() => { if (Volatile.Read(ref quitRequested)) host.Window.Native.Close(); }), SystemStage.Input);
host.Run();
BackgroundWork.Stop(TimeSpan.FromSeconds(5)); // no chunk still loading or meshing while the store is freed

worldSaver?.SaveNow(); // everything, in one transaction, on exit
net.Dispose(); // says goodbye to the host, or closes the game to clients
saveDb?.Dispose();
gridStore.Dispose();
