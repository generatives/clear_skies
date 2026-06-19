# Clear Skies — Project Plan

Each phase produces a runnable milestone. Later phases build directly on earlier ones. Phases within a milestone can be done in parallel where noted.

---

## Milestone 1: Engine Foundation

Get a window open, geometry on screen, and the ECS skeleton wired up. No game logic yet.

### Phase 1.1 — Project Skeleton
- Set up C# solution with projects: `Engine` (library) and `Game` (runnable entry point). The `Client`/`Server` split is deferred to Milestone 5 (Multiplayer)
- Add dependencies: Silk.NET (WebGPU), DefaultECS, BepuPhysics2
- Get a WebGPU window opening and closing cleanly
- Establish the DefaultECS world and a main loop that ticks systems

### Phase 1.2 — WebGPU Renderer (Basic)
- Vertex/index buffer pipeline
- Camera with view/projection matrices
- Render a single cube with a solid color
- Basic depth testing

### Phase 1.3 — Input
- Keyboard and mouse capture
- Free-fly camera controlled by input

**Milestone 1 exit criterion:** A free-fly camera moving through a scene containing a few manually placed cubes.

---

## Milestone 2: Static Voxel World

A terrain voxel system with chunk management, meshing, and world generation.

### Phase 2.1 — Chunk System
- Define block types and a chunk data structure (fixed 3D array, e.g. 32³)
- Chunk load/unload around a camera position
- ECS components: `ChunkPosition`, `ChunkData`, `ChunkMesh`

### Phase 2.2 — Greedy Meshing
- Generate chunk meshes with greedy meshing (face culling between adjacent blocks)
- Upload meshes to GPU; re-mesh only dirty chunks
- Handle chunk borders (don't cull faces against unloaded neighbours)

### Phase 2.3 — World Generation
- Noise-based floating island generation (vertical band of sky with island clusters)
- Simple initial implementation, no biomes or complexity
- Effectively infinite horizontal; bounded vertical range

### Phase 2.4 — Block Interaction
- Raycast from camera to select a block face
- Place and remove blocks; mark chunk dirty and re-mesh

**Milestone 2 exit criterion:** Fly around a generated sky world, place and break blocks.

---

## Milestone 3: Physics & Dynamic Voxels

Introduce BepuPhysics2 and the dynamic voxel grid system that airships will use. This comes before lighting so the lighting system can be built against real dynamic grids from the start (see the GPU lighting design in `lighting_design_details.md`).

### Phase 3.1 — Physics Integration
- BepuPhysics2 simulation ticking in a fixed-timestep system
- Static voxel terrain represented as a BepuPhysics2 compound of Box shapes
- Basic rigid body: drop a cube, it lands and rests

### Phase 3.2 — Dynamic Voxel Grid
- `DynamicGrid` ECS entity: owns a 3D block array + a BepuPhysics2 rigid body
- The 3D block array is made of multiple chunks like the static landscape.
- The player should be able to add more blocks and the number of chunks will expand to fit
- Grid mesh generated from its blocks; positioned/rotated from physics transform each frame
- Collision shape rebuilt from block occupancy (BepuPhysics2 compound of Box shapes)
- A lot of rendering, meshing, chunk data storage, and ECS components should be shared between dynamic and static chunks
- The dynamic chunks should be editable the same as static chunks

### Phase 3.3 — Grid–World & Grid–Grid Interaction
- Collision between dynamic grids and static terrain
- Collision between two dynamic grids
- This should be handled by bepuphysics, we just need to make sure the simulation is updated and configured correctly

### Phase 3.4 — Player Interaction For Testing
- The player should be able to apply forces to the dynamic chunks to move them around, and stop movement
- Use the arrow keys to move them horizontally, page up and page down for vertical. "end" should stop movement
- These inputs apply to all dynamic voxel grids
- The player should be able to spawn a dynamic voxel grid with a single stone block and expand it from there

**Milestone 3 exit criterion:** The player can spawn a dynamic grid, watch it fall and collide with terrain and with other grids. They should also be able to edit it by clicking, adding and removing blocks.

---

## Milestone 4: Lighting (GPU)

Minecraft-style per-voxel lighting extended to the dynamic voxel grids built in Milestone 3, computed on the GPU. **See `lighting_design_details.md` for the full design, rationale, and trade-offs** (it supersedes the earlier three-tier CPU design in `lighting_design.md`). Light is GPU-resident and sampled in the **fragment shader** (so meshes never re-bake on light change); shadow-map rendering, light injection, and BFS flood all run as GPU passes. One uniform mechanism handles the sun and lamps alike, and every volume can shade every other. See `milestone4_plan.md` for the detailed phasing.

### Phase 4.0 — GPU compute & voxel-data infrastructure
- Compute pipeline support in the renderer (storage buffers/textures, compute bind groups, dispatch) — net-new
- Per-chunk GPU residency: opacity buffer + light buffer (sky + block channels), double-buffered for ping-pong
- Keep GPU opacity/block data in sync with CPU `SetBlock` edits

### Phase 4.1 — Fragment-shader light sampling
- Remove per-vertex light + light from the greedy merge key (meshing becomes light-independent)
- Fragment samples the chunk light buffer at the air-side voxel (offset by normal), smoothed and opacity-masked
- Validate against a full-bright buffer (world looks identical) before any flood exists

### Phase 4.2 — GPU BFS flood + block light (lamps)
- Max-relaxation flood: global ping-pong storage buffers, one pass per dispatch, ~15 passes, opacity-blocked
- Inject `Lamp` emitters; clear → inject → flood from scratch at 1–2 Hz (no incremental removal in v1)

### Phase 4.3 — Sky & sun
- Sky channel = ambient sky-exposure (soft) + direct sun via a shadow map (sharp, occluded)
- Combine with the existing N·L term + ambient floor; one coarse sun map now, cascades later

### Phase 4.4 — Cross-grid & dynamic lights (unified)
- Lights from other volumes (and the sun) are depth-tested and injected into a target volume, then flooded locally — same inject+flood path everywhere
- All grids shade all grids; grids cast sun shadows on terrain (naive/unbounded for now)

### Phase 4.5 — Ambient occlusion
- AO via the fragment path (baked AO channel or in-shader from the opacity neighbourhood), same air-side sampling

### Phase 4.6 — Culling & change detection
- Dirty tracking, broadphase radius-gating of shadow maps, static bake/cache — recover steady-state cost from the naive "re-flood everything at 2 Hz" baseline

**Milestone 4 exit criterion:** Placed lamps illuminate surroundings; AO visible in corners; sunlight shades underground spaces and undersides; a lamp on a dynamic grid casts light and shadows onto nearby static terrain and other grids; a grid casts a sun shadow on the ground.

---

## Milestone 5: Multiplayer

Add a client-server architecture. All prior systems become authoritative on the server. This comes before airships so the airship simulation (Milestone 6) is built server-authoritative from the start rather than retrofitted onto single-player code.

### Phase 5.1 — Network Architecture
- Split the single `Game` executable (from Milestone 1) into `Client` and `Server` projects over the shared `Engine`/`Game` code
- Define client/server roles: server owns simulation; clients send input, receive state
- Choose transport (e.g. Silk.NET networking or a standalone library like LiteNetLib)
- Connect two clients; sync a moving rigid body

### Phase 5.2 — World State Sync
- Chunk data: server streams chunks to clients on enter; delta-sync block changes
- Dynamic grid sync: server sends transform + block state; clients interpolate

### Phase 5.3 — Player & Input
- Server-authoritative player positions with client-side prediction
- General input forwarded to server; server applies to the simulation (extended to airship controls in Milestone 6)

### Phase 5.4 — Latency Hiding
- Client-side interpolation for remote entities
- Lag compensation for block placement raycasts

**Milestone 5 exit criterion:** Two players on the same server move through a shared world, edit blocks, and see each other and a shared dynamic grid moving in real time.

---

## Milestone 6: Airship Systems

Layer game logic on top of dynamic voxel grids to make airships fly. All simulation runs server-authoritative on the foundation from Milestone 5; control input arrives over the network.

### Phase 6.1 — Power System
- `PowerNetwork` component attached to each `DynamicGrid`
- Blocks register as producers (Engine, Magic Generator) or consumers (Fan, Jet, etc.)
- Fuel inventory; engines burn fuel to charge batteries; batteries discharge to consumers
- Power availability gates whether a consumer block is active

### Phase 6.2 — Lift & Thrust Blocks
- Buoyant Block: applies upward force proportional to block count
- Magic Levitator: alternative arcane lift source
- Fan: applies horizontal force in block's facing direction
- Jet: higher-force directional thrust, higher power cost

### Phase 6.3 — Control Blocks
- Stabilizer: PID controller applying torque to counteract tilt
- Height Controller: drives lift blocks to hold target altitude
- Heading Controller: drives fans/jets to hold target heading
- Speed Controller: drives thrust to hold target speed
- Pilot Block: player entity attaches here; manual control input (forwarded via Milestone 5's input path) overrides controllers

### Phase 6.4 — Airship Feel & Tuning
- Mass distribution calculated from block positions (centre of mass)
- Tune lift/drag constants so ships feel weighty but controllable
- Test with asymmetric builds to verify tilt and stabilizer response

**Milestone 6 exit criterion:** Build an airship in-world, power it up, and pilot it between two islands — with a second player aboard seeing the ride in sync.

---

## Milestone 7: Modding API

Expose a stable public surface for third-party mods.

### Phase 7.1 — API Design
- Define what is moddable: block types, block behaviours, world gen, UI
- Isolate internal engine types from public API types
- Choose mod loading strategy (e.g. compiled .NET assemblies dropped into a `mods/` folder)

### Phase 7.2 — Block Registration
- Mods register block types with: ID, display name, mesh/texture, properties (solid, light-emitting, etc.)
- Mods register functional block behaviours (subclass of a `FunctionalBlock` base)

### Phase 7.3 — World Gen Hooks
- Mods register island generators and biome definitions
- Pipeline: built-in generator runs first; mods can add passes

### Phase 7.4 — Documentation & Example Mod
- Public API documented with XML doc comments
- Ship an example mod that adds one new block type and one world gen feature

**Milestone 7 exit criterion:** An example mod loads, its block can be placed and functions correctly, and its world gen feature appears during world generation.

---

## Deferred / Stretch Goals

These are desirable but not on the critical path:

- Block destruction and structural integrity simulation
- In-world crafting and inventory system
- NPC inhabitants on islands
- Procedurally generated island structures (ruins, villages)
- Weather and atmospheric effects (clouds, wind affecting airships)
- Steam/smoke particle effects from engines and jets
