# Clear Skies — Milestone 2 Detailed Plan: Static Voxel World

## Goal

Build a chunk-based voxel world that generates floating-island terrain on demand, renders it efficiently with greedy meshing, and lets the player place and remove blocks. Milestone 1's free-fly camera, renderer, and ECS plumbing are reused unchanged.

**Exit criterion:** Fly through a procedurally generated sky world of floating islands. Left-click removes a targeted block; right-click places one. Chunks load and unload as the camera moves.

---

## Key Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Chunk size | 32³ | 32 K blocks per chunk; large enough for few draw calls, small enough that re-meshing one chunk is fast |
| Block ID storage | `byte` (values 0–255) | 256 types is ample for M2–M7; a single byte per block keeps a 32³ chunk at 32 KB |
| Vertex format | Keep existing `Vertex` (Position, Normal, Color) | No textures yet; block color from registry fills `Color`. A `LightLevel` float will be added in M4 — no format churn now |
| Chunk-to-ECS pattern | Each chunk is an entity with `ChunkPosition` + `ChunkData` + `Transform` + `MeshRenderer` | The existing `RenderSystem` then renders chunks for free, with no new render path |
| Load radius | 6 horizontal / 3 vertical (configurable) | (13² × 7) = 1183 chunks max in memory; comfortable against the `MaxObjects` cap after it is raised |
| `MaxObjects` in `Renderer` | Raise from 1024 → 4096 | Accommodate 1000+ chunk draw calls plus objects |
| Unloaded-neighbour border policy | Show face (never cull against an absent chunk) | Prevents holes; slight over-draw at borders, negligible at chunk scale |
| World generator location | `IWorldGenerator` interface in Engine; `SkyWorldGenerator` implementation in Game | Clean seam for the Milestone 7 modding API |
| Noise library | `FastNoiseLite` NuGet | Single-class, no native code, good domain-warp support for organic island shapes |
| Raycast algorithm | DDA (voxel traversal) | Exact, O(distance), trivially gives the struck face normal |

---

## Solution Layout After M2

```
ClearSkies.sln
src/
  ClearSkies.Engine/
    Voxels/
      BlockId.cs            # enum + registry
      BlockDef.cs           # color, solid flag, future: light emission
      BlockRegistry.cs      # static table, Get(BlockId)
      ChunkData.cs          # 32³ block array, dirty flag
      ChunkPosition.cs      # chunk-space coords, hashing, world origin
      ChunkManager.cs       # dictionary of loaded chunks; Get/Set block; MarkDirty
      GreedyMesher.cs       # produces Vertex[]/uint[] from a chunk + 6 optional neighbours
      VoxelRaycast.cs       # DDA traversal → HitResult
    ECS/
      ChunkLoadSystem.cs    # loads/unloads chunks around the camera each frame
      ChunkMeshSystem.cs    # re-meshes dirty chunks, uploads to GPU
      BlockInteractionSystem.cs  # click → remove/place block
    World/
      IWorldGenerator.cs    # interface: GenerateChunk(pos, data)
  ClearSkies.Game/
    World/
      SkyWorldGenerator.cs  # floating-island density function using FastNoiseLite
    GameScene.cs            # replaces TestScene; wires systems, creates world
```

---

## NuGet Changes

| Package | Project | Change |
|---|---|---|
| `FastNoiseLite` | Engine | **Add** — noise for world gen |

All other packages unchanged from M1.

---

## Phase Breakdown

### Phase 2.1 — Block Types & Chunk Data

Everything needed to represent voxel data in memory; no rendering yet.

1. Define `BlockId : byte` enum: `Air = 0`, `Grass`, `Dirt`, `Stone`. More types added freely later.
2. `BlockDef` struct: `BlockId Id`, `string Name`, `Vector3D<float> Color`, `bool IsSolid`.
3. `BlockRegistry` static class: a fixed array indexed by `(byte)BlockId`; `BlockDef Get(BlockId)`.
4. `ChunkPosition` struct: `int X, Y, Z` in chunk space. Implement `IEquatable`, override `GetHashCode` (for `Dictionary` keying). `Vector3D<float> WorldOrigin => new(X * ChunkData.Size, Y * ChunkData.Size, Z * ChunkData.Size)`.
5. `ChunkData` class: `const int Size = 32`; `private BlockId[] _blocks` (length `Size³`); `Get(x,y,z)`, `Set(x,y,z,id)`, `bool IsDirty`. Static `int Index(x,y,z) = x + Size*(y + Size*z)`.
6. `ChunkManager`: `Dictionary<ChunkPosition, (ChunkData data, Entity entity)>`; `CreateChunk(pos)`, `GetChunk(pos) → ChunkData?`, `GetBlock(worldX,worldY,worldZ)`, `SetBlock(...)` (marks owning chunk and any directly adjacent chunk on a shared face dirty), `MarkDirty(pos)`.

**Checkpoint:** Unit tests: `ChunkData` round-trips `Set`/`Get`; `ChunkPosition.GetHashCode` is stable; `ChunkManager.SetBlock` correctly marks a neighbour dirty when the block sits on a chunk border.

---

### Phase 2.2 — Greedy Mesher

Converts block data into an efficient mesh; no world gen or interaction yet.

**Algorithm (one pass per axis direction, 6 total):**

For a given face direction (e.g. +X):
1. Sweep each X-slice (0 → 31). For a face to be present at `(x, y, z)` in the +X direction: the block at `(x, y, z)` is solid AND the block at `(x+1, y, z)` is air (consulting the +X neighbour chunk when `x == 31`).
2. Build a 2D grid `face[y][z]` of `BlockId` (or `Air` if no face).
3. Greedy merge: scan the grid for the first non-Air cell, expand a rectangle as wide as possible (same block type), then as tall as possible. Emit one quad for the rectangle and mark its cells as consumed.
4. The quad's normal is the face direction; its color is `BlockRegistry.Get(blockId).Color`.

Emit two triangles per quad with correct winding order (CCW from the outside).

`GreedyMesher.Mesh(ChunkData chunk, ChunkData? nx, px, ny, py, nz, pz)` returns `(Vertex[], uint[])` in **chunk-local space** (vertex positions in `[0, 32]`). The chunk entity's `Transform.Position` offsets these to world space, matching how the M1 cube rendering works.

**Checkpoint:** Manually construct a small `ChunkData` (e.g. a solid 4×4×4 cube of Stone at one corner) and verify the output mesh has the right face count, no interior faces, and correct normals. Run this as a plain C# unit test without GPU.

---

### Phase 2.3 — World Generator & Chunk Loading

Generating and streaming chunk data around the camera.

#### `IWorldGenerator` (Engine)

```csharp
namespace ClearSkies.Engine.World;

public interface IWorldGenerator
{
    void GenerateChunk(ChunkPosition pos, ChunkData data);
}
```

#### `SkyWorldGenerator` (Game)

Floating-island density function using `FastNoiseLite`:

1. **Cluster noise (2D):** low-frequency 2D noise on `(x, z)` determines whether a location is part of an island cluster and its base altitude. Sparse: most of the sky is empty.
2. **Shape noise (3D):** medium-frequency 3D noise for the island interior.
3. **Vertical falloff:** subtract `|y - baseAltitude| * falloffRate`. The falloff is asymmetric: steeper below (flat, rocky undersides) than above (sloped grassy tops).
4. **Density threshold:** a cell is solid if `shapNoise + clusterBias - verticalFalloff > threshold`.
5. **Block assignment:** scan each column top-down. First solid block exposed to air → Grass. Next 1–3 solid blocks → Dirt. Everything deeper → Stone.

Parameters (tunable constants): `IslandScale`, `ClusterFrequency`, `FalloffAbove`, `FalloffBelow`, `SolidThreshold`. Sensible defaults produce islands roughly 60–120 blocks wide and 20–40 blocks tall at altitude Y = 0 ± a few chunks.

#### `ChunkLoadSystem`

Runs in `SystemStage.Logic` each frame:

1. Read camera entity `Transform.Position`; compute which chunk it occupies.
2. For every chunk position within (HorizontalRadius=6, VerticalRadius=3) of the camera chunk:
   - If not in `ChunkManager`, create it: `CreateChunk(pos)`, call `_generator.GenerateChunk(pos, data)`, create an ECS entity with `ChunkPosition`, `ChunkData`, and `Transform` (position = `pos.WorldOrigin`); mark dirty.
3. For every chunk in `ChunkManager` outside (HorizontalRadius+1, VerticalRadius+1):
   - Destroy its ECS entity (which disposes its `GpuMesh`), remove from dictionary.

Generation is synchronous for M2. Background generation is a later optimisation.

#### `ChunkMeshSystem`

Runs in `SystemStage.PreRender`:

1. Query ECS for chunk entities where `ChunkData.IsDirty == true`.
2. For each: fetch the six neighbour `ChunkData` from `ChunkManager` (null if unloaded).
3. Call `GreedyMesher.Mesh(...)`.
4. If the entity already has a `MeshRenderer`, dispose the old `GpuMesh` buffers.
5. Upload new mesh via `Renderer.UploadMesh`; set/update `MeshRenderer` component; clear `IsDirty`.
6. Skip if mesh is empty (no solid faces — pure-air chunk).

**Checkpoint:** Fly around the generated world; islands appear as the camera approaches; removed chunks disappear cleanly; no GPU validation errors.

---

### Phase 2.4 — Block Interaction

Raycast from the camera and respond to mouse clicks.

#### `VoxelRaycast`

DDA algorithm (Amanatides & Woo):

```
tMax(axis) = distance to first grid crossing on that axis from the ray origin
tDelta(axis) = distance between successive grid crossings on that axis
```

Step through voxels by advancing the axis with the smallest `tMax`, recording the entry face. Stop when:
- A solid block is hit → return `HitResult { BlockPos, FaceNormal, Distance }`
- `Distance > maxDistance` (cap at 8 blocks)

`FaceNormal` is the integer vector pointing back from the entered face (e.g. entering from the +X side → normal `(-1,0,0)`). This is the face the player can see and is also the offset direction for block placement.

#### `BlockInteractionSystem`

Runs in `SystemStage.Logic`, after `FreeFlyCameraSystem`:

1. If cursor is not captured, do nothing.
2. Call `VoxelRaycast.Cast(chunkManager, cameraPos, cameraForward, maxDistance: 8f, out hit)`.
3. On left-click (`WasMouseButtonPressed(Left)` — already wired):
   - `chunkManager.SetBlock(hit.BlockPos, BlockId.Air)`.
4. On right-click (`WasMouseButtonPressed(Right)` — **new**, add to `InputManager`):
   - `placePos = hit.BlockPos + hit.FaceNormal`
   - Only place if `placePos` is not occupied by the camera (collision check: block's AABB does not overlap camera position ± 0.4).
   - `chunkManager.SetBlock(placePos, _heldBlock)` — hardcode to `BlockId.Stone` for now.

Add `WasMouseButtonPressed(MouseButton.Right)` to `InputManager` — same pattern as left-click, already in place after the M1 mouse-button fix.

#### `GameScene` (replaces `TestScene`)

```csharp
public static class GameScene
{
    public static void Build(EngineHost host, IWorldGenerator generator)
    {
        var chunks = new ChunkManager(host.World);
        host.AddSystem(new ChunkLoadSystem(host.World, chunks, generator), SystemStage.Logic);
        host.AddSystem(new ChunkMeshSystem(host.World, chunks, host.Renderer), SystemStage.PreRender);
        host.AddSystem(new BlockInteractionSystem(host.World, chunks, host.Input), SystemStage.Logic);
        host.AddSystem(new FreeFlyCameraSystem(host.World, host.Input), SystemStage.Logic);
        host.AddSystem(new RenderSystem(host.World, host.Renderer), SystemStage.Render);

        // Camera starts above the first island cluster
        var cam = host.World.CreateEntity();
        var t = Transform.Identity;
        t.Position = new Vector3D<float>(0, 60, 0);
        cam.Set(t);
        cam.Set(new CameraComponent { Camera = new Camera(), Active = true });
        cam.Set(new FreeFlyController { MoveSpeed = 20f, LookSensitivity = 0.0025f });
        host.Input.CursorCaptured = true;
    }
}
```

**Checkpoint (Milestone exit):** Fly through generated islands; left-click removes blocks; right-click places Stone; chunk borders have no holes; flying away from a chunk unloads it cleanly.

---

## C# Class Structure

### `ClearSkies.Engine.Voxels`

```csharp
public enum BlockId : byte { Air = 0, Grass, Dirt, Stone }

public struct BlockDef
{
    public BlockId Id;
    public string Name;
    public Vector3D<float> Color;
    public bool IsSolid;
}

public static class BlockRegistry
{
    public static BlockDef Get(BlockId id);        // indexed by (byte)id; O(1)
}

public sealed class ChunkData
{
    public const int Size = 32;
    public bool IsDirty { get; set; }
    public BlockId Get(int x, int y, int z);
    public void Set(int x, int y, int z, BlockId id);
    public static int Index(int x, int y, int z);
}

public readonly struct ChunkPosition : IEquatable<ChunkPosition>
{
    public int X { get; }
    public int Y { get; }
    public int Z { get; }
    public Vector3D<float> WorldOrigin { get; }
    public ChunkPosition Offset(int dx, int dy, int dz);
}

public sealed class ChunkManager
{
    public ChunkManager(World ecs);
    public ChunkData? GetChunk(ChunkPosition pos);
    public Entity? GetEntity(ChunkPosition pos);
    public BlockId GetBlock(int wx, int wy, int wz);
    public void SetBlock(int wx, int wy, int wz, BlockId id);  // marks chunk + border neighbours dirty
    public void CreateChunk(ChunkPosition pos);                  // allocates ChunkData + ECS entity
    public void DestroyChunk(ChunkPosition pos);                 // removes entity + disposes mesh
    public IEnumerable<ChunkPosition> LoadedPositions { get; }
}

public sealed class GreedyMesher
{
    // All six neighbour chunks may be null (unloaded → show border faces).
    public (Vertex[] vertices, uint[] indices) Mesh(
        ChunkData chunk,
        ChunkData? nX, ChunkData? pX,
        ChunkData? nY, ChunkData? pY,
        ChunkData? nZ, ChunkData? pZ);
}

public static class VoxelRaycast
{
    public readonly struct HitResult
    {
        public Vector3D<int> BlockPos    { get; init; }
        public Vector3D<int> FaceNormal  { get; init; }
        public float Distance            { get; init; }
    }

    public static bool Cast(
        ChunkManager world,
        Vector3D<float> origin,
        Vector3D<float> direction,
        float maxDistance,
        out HitResult hit);
}
```

### `ClearSkies.Engine.World`

```csharp
public interface IWorldGenerator
{
    void GenerateChunk(ChunkPosition pos, ChunkData data);
}
```

### `ClearSkies.Engine.ECS` (additions)

```csharp
// Loads/unloads chunks based on active camera position.
public sealed class ChunkLoadSystem : ISystem
{
    public ChunkLoadSystem(World ecs, ChunkManager chunks, IWorldGenerator generator,
                           int horizontalRadius = 6, int verticalRadius = 3);
    public void Update(float dt);
}

// Remeshes dirty chunks each frame and uploads to GPU.
public sealed class ChunkMeshSystem : ISystem
{
    public ChunkMeshSystem(World ecs, ChunkManager chunks, Renderer renderer);
    public void Update(float dt);
}

// Raycast + click → place/remove blocks.
public sealed class BlockInteractionSystem : ISystem
{
    public BlockInteractionSystem(World ecs, ChunkManager chunks, InputManager input,
                                  float reach = 8f);
    public void Update(float dt);
}
```

### `ClearSkies.Game.World`

```csharp
public sealed class SkyWorldGenerator : IWorldGenerator
{
    public SkyWorldGenerator(int seed = 0);
    public void GenerateChunk(ChunkPosition pos, ChunkData data);
}
```

---

## Renderer Change: Raise `MaxObjects`

In `Renderer.cs`, change:
```csharp
private const int MaxObjects = 1024;
```
to:
```csharp
private const int MaxObjects = 4096;
```

This expands the model uniform buffer from 256 KB to 1 MB — still trivial for a GPU — and accommodates up to 4096 simultaneous draw calls (chunks + any future objects).

---

## Frame Lifecycle After M2

```
glfwPollEvents()
  └─ MouseMove → _accumDelta

OnUpdate(dt):
  ChunkLoadSystem     (Logic)  — create/destroy chunk entities; generate new ones
  BlockInteractionSystem (Logic) — raycast; place/remove on click
  FreeFlyCameraSystem (Logic)  — camera movement + look
  ChunkMeshSystem  (PreRender) — remesh dirty chunks, upload GPU buffers
  Input.NewFrame()             — clear delta/edge state

OnRender(dt):
  RenderSystem    (Render)     — draws all Transform+MeshRenderer entities
                                  (camera entities excluded by missing MeshRenderer)
```

---

## Floating Island World Generation — Detail

The density function is evaluated per-block during `GenerateChunk`. Only blocks within the chunk's bounding box are evaluated, so the cost per chunk is exactly 32³ = 32 768 evaluations.

```
clusterId(x, z)   = FastNoiseLite.GetNoise2D(x * clusterFreq, z * clusterFreq)
                    → high values (> 0.3) indicate an island cluster exists here
baseAlt(x, z)     = FastNoiseLite.GetNoise2D(x * altFreq, z * altFreq) * altRange
                    → island altitude varies ±altRange blocks around 0

shape(x, y, z)    = FastNoiseLite.GetNoise3D(x * shapeFreq, y * shapeFreq, z * shapeFreq)

dy                = worldY - baseAlt(worldX, worldZ)
vertFalloff       = dy > 0 ? dy * falloffAbove : -dy * falloffBelow
                    (falloffBelow > falloffAbove → steeper underside)

density           = shape + clusterId * clusterWeight - vertFalloff

solid             = density > solidThreshold
```

Suggested starting constants:
| Constant | Value |
|---|---|
| `clusterFreq` | 0.003 |
| `altFreq` | 0.005 |
| `altRange` | 40 (blocks) |
| `shapeFreq` | 0.04 |
| `clusterWeight` | 1.2 |
| `falloffAbove` | 0.06 |
| `falloffBelow` | 0.10 |
| `solidThreshold` | 0.2 |

Block assignment after density pass: scan each column from top to bottom within the chunk. Track `exposedToAir = true` initially. First solid block encountered → Grass and set `exposedToAir = false`. Next 1–3 solid blocks → Dirt (count resets when air is found again). Beyond that → Stone.

---

## Risks & Notes

- **Greedy meshing correctness at borders:** The mesher must query the +X face at `x==31` from the `pX` neighbour's `x==0` column. Off-by-one here creates invisible faces or z-fighting. Verify with the unit test before plugging into the live world.
- **`ChunkMeshSystem` budget:** Re-meshing many chunks in one frame causes spikes. For M2, limit to N remeshes per frame (e.g. 4) with a dirty queue. Background threading is a later optimisation.
- **GpuMesh lifetime:** When a chunk entity is destroyed or re-meshed, the old `GpuMesh` must be disposed (calls `GpuBuffer.Dispose` on vertex and index buffers). Forgetting this leaks GPU memory.
- **Empty chunks:** A chunk of all Air produces an empty mesh. Store `null` in `MeshRenderer.Mesh` and skip the draw call in `RenderSystem` — don't upload a zero-vertex buffer.
- **Block placement collision:** Before placing a block, confirm its AABB does not intersect the camera position; otherwise the player gets stuck inside the block.
- **Vertex format stability:** Do not add a `LightLevel` field to `Vertex` yet — wait for M4. The shader and pipeline layout must match the struct exactly; changing the vertex format mid-milestone without updating the pipeline descriptor causes a GPU validation error that can be hard to diagnose.
- **`ChunkPosition` in ECS:** DefaultECS stores components by type. Since `ChunkPosition` is a plain struct, it is stored inline — good. `ChunkData` is a class; DefaultECS handles class components fine but they live on the managed heap. No special treatment needed.

---

## Milestone 2 Done Checklist

- [ ] `BlockRegistry` returns correct color and solid flag for each block type.
- [ ] `ChunkData.Set`/`Get` round-trips correctly; `MarkDirty` propagates to border neighbours.
- [ ] `GreedyMesher` produces no interior faces on a solid 32³ chunk (only the 6 outer faces × greedy quads).
- [ ] `GreedyMesher` correctly handles unloaded-neighbour borders (shows face rather than culling).
- [ ] World generates floating islands visible from spawn; islands have Grass tops, Dirt underlayer, Stone core.
- [ ] Chunks load as the camera approaches and unload as it moves away.
- [ ] No GPU memory leak: re-meshed and unloaded chunks dispose their old buffers.
- [ ] Left-click removes a targeted block; the chunk re-meshes on the next frame.
- [ ] Right-click places a Stone block on the targeted face; cannot place inside the camera.
- [ ] No holes at chunk borders: shared faces are visible from both sides until both chunks are loaded.
- [ ] `MaxObjects` raised to 4096; no draw-call overflow with a full load radius.
