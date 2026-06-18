# Clear Skies — Milestone 1 Detailed Plan: Engine Foundation

## Goal

Stand up the solution skeleton, open a WebGPU window, and get a free-fly camera moving through a scene of manually-placed, depth-tested cubes — all driven through the DefaultECS world and a clean main loop. No voxels, physics, or game logic yet.

**Exit criterion:** A free-fly camera (WASD + mouse-look) moving through a scene containing several manually placed cubes, rendered with correct depth, surviving window resize.

WebGPU is used instead of Vulkan for simplicity: the implementation manages memory allocation and GPU synchronization, the surface replaces manual swapchain management, and shaders are written in WGSL and compiled at runtime (no SPIR-V build step). This removes most of the boilerplate a Vulkan foundation would require.

---

## Solution & Project Layout

```
ClearSkies.sln
src/
  ClearSkies.Engine/      # Reusable engine: windowing, WebGPU, ECS infra, input, math helpers
  ClearSkies.Game/        # Runnable entry point: scene setup + block/airship logic (later)
shaders/
  basic.wgsl              # WGSL source, loaded at runtime (no precompile step)
```

`Client`/`Server` projects are **not** created in Milestone 1 — `Game` is the runnable executable. The Client/Server split is introduced in Milestone 5 (Multiplayer) over the shared `Engine`/`Game` code.

**Project references:** `Game → Engine`. Engine references nothing game-specific.

### NuGet dependencies

| Package | Project | Purpose |
|---|---|---|
| `Silk.NET.Windowing` | Engine | Cross-platform window + main loop events |
| `Silk.NET.Input` | Engine | Keyboard / mouse |
| `Silk.NET.WebGPU` | Engine | WebGPU bindings |
| `Silk.NET.WebGPU.Native.WGPU` | Engine | Native `wgpu-native` implementation backing the bindings |
| `Silk.NET.WebGPU.Extensions.WGPU` | Engine | wgpu-native–specific functions (e.g. `SurfacePresent`, `DevicePoll`) not in core WebGPU |
| `Silk.NET.Maths` | Engine | `Vector3D<float>`, `Matrix4X4<float>`, `Quaternion<float>` |
| `DefaultEcs` | Engine | ECS world, entities, systems |
| `BepuPhysics` | Engine | Added now for skeleton parity; **unused in M1** |

Configure projection for WebGPU's clip space: **Y-up NDC with `[0,1]` depth range** (like D3D, unlike Vulkan's Y-down). Use a `[0,1]`-depth perspective matrix and *no* Vulkan-style Y-flip.

---

## Phase Breakdown

### Phase 1.1 — Project Skeleton

1. Create the solution and two projects (`net8.0`), set the `Game → Engine` reference, add NuGet packages.
2. Implement `GameWindow` wrapping `Silk.NET.Windowing.IWindow`; open and cleanly close a window.
3. Implement `EngineHost` + `GameLoop`: subscribe to window `Load`/`Update`/`Render`/`Resize`/`Closing`, drive a `Time` clock, tick registered systems.
4. Create the DefaultECS `World` and the `ISystem` registration/ordering plumbing.
5. `Game/Program.cs` constructs `EngineHost`, runs it; window opens, loop ticks, closes cleanly.

**Checkpoint:** Window opens, title shows FPS, closes without errors or leaks.

### Phase 1.2 — WebGPU Renderer (Basic)

WebGPU bring-up is short. Render *something* at each step before moving on:

1. `GpuContext`: create instance → request adapter → request device + queue → create surface from the window → configure the surface (format, size) → create the depth texture. (Adapter/device requests are async callbacks; await them during init.)
2. `ShaderModule`: load `basic.wgsl` at runtime into a shader module.
3. `RenderPipeline`: bind group layouts (camera UBO at group 0; per-object model UBO with dynamic offset at group 1), pipeline layout, render pipeline with the vertex layout, depth-stencil state (depth test/write), and the surface color format.
4. `GpuBuffer`: create + upload vertex and index buffers via `Queue.WriteBuffer` (WebGPU has no explicit staging step for this).
5. `Renderer`: orchestrate `BeginFrame → set pipeline + camera bind group → DrawMesh (bind model offset, vertex/index buffers, draw indexed) → EndFrame (finish encoder, submit, present)`. Reconfigure surface + recreate depth texture on resize.

**Checkpoint:** A single hard-coded cube renders, depth-correct, and survives a resize.

### Phase 1.3 — Input & Free-Fly Camera

1. `InputManager` wrapping `Silk.NET.Input` (key state, mouse delta, cursor capture toggle).
2. `Camera` + `CameraComponent`; `FreeFlyController` component.
3. `FreeFlyCameraSystem`: WASD/QE movement in camera space, mouse-look (yaw/pitch), updates the camera entity's `Transform`.
4. `RenderSystem`: build view/projection from the active camera, write the camera UBO, iterate `Transform + MeshRenderer` entities and issue `DrawMesh` calls.
5. `Game` builds a scene: a handful of cube entities at varied positions; one camera entity with a `FreeFlyController`.

**Checkpoint (Milestone exit):** Fly around several cubes with correct depth and responsive controls.

---

## C# Class Structure

Namespaces rooted at `ClearSkies.Engine`. Signatures show responsibility and the key surface — not every member.

### Core

```csharp
namespace ClearSkies.Engine.Core;

// Owns the window, the ECS world, the system schedule, and the lifetime of the renderer.
public sealed class EngineHost : IDisposable
{
    public World World { get; }            // DefaultEcs world
    public GameWindow Window { get; }
    public Renderer Renderer { get; }
    public InputManager Input { get; }
    public Time Time { get; }

    public EngineHost(EngineOptions options);
    public void AddSystem(ISystem system, SystemStage stage);
    public void Run();                     // blocks; drives GameLoop until window closes
    public void Dispose();
}

public sealed record EngineOptions(string Title, int Width, int Height, bool LogGpuErrors);

// Translates window events into ordered system ticks; separates variable-step (render/update)
// from a fixed-step accumulator reserved for future physics.
public sealed class GameLoop
{
    public void OnUpdate(double dtSeconds);   // variable-step game/camera logic
    public void OnRender(double dtSeconds);   // record + submit a frame
    public void OnResize(Vector2D<int> size);
}

public sealed class Time
{
    public float DeltaSeconds { get; }
    public double TotalSeconds { get; }
    public float FixedStep { get; }           // e.g. 1/60; accumulator for later physics
    public int FramesPerSecond { get; }
}

// Minimal scheduling contract over DefaultEcs systems so engine + game systems share an order.
public interface ISystem
{
    void Update(float dt);
}

public enum SystemStage { Input, Logic, PreRender, Render }
```

### Windowing

```csharp
namespace ClearSkies.Engine.Windowing;

// Thin wrapper over Silk.NET IWindow: creation, event forwarding, native handles for the surface.
public sealed class GameWindow : IDisposable
{
    public IWindow Native { get; }
    public Vector2D<int> FramebufferSize { get; }

    public event Action? Load;
    public event Action<double>? Update;
    public event Action<double>? Render;
    public event Action<Vector2D<int>>? Resize;
    public event Action? Closing;

    public GameWindow(EngineOptions options);   // configures GraphicsApi.None for manual WebGPU
    public void Run();
    public void Dispose();
}
```

### Rendering — WebGPU backend

WebGPU collapses the Vulkan object graph into a handful of types. There is no explicit instance/device/swapchain/render-pass/framebuffer/command-pool/semaphore/fence split — the surface, queue, and command encoder cover those roles, and the implementation owns memory and synchronization.

**Resource ownership & disposal convention.** `Silk.NET.WebGPU` exposes raw native pointers (`Device*`, `Surface*`, …). To keep that unsafe surface out of the rest of the engine, every WebGPU object is owned by exactly one managed `IDisposable` wrapper class that holds its native handle **privately** and releases it in `Dispose` via the matching `Api.*Release` call (`DeviceRelease`, `SurfaceRelease`, `BufferRelease`, …). Wrappers dispose their dependencies in reverse creation order. Where a native handle genuinely must cross between our own wrapper classes, it is exposed as an `internal` member (same assembly), never `public`. A small `GpuHandle` helper centralises the release pattern:

```csharp
namespace ClearSkies.Engine.Rendering.WebGpu;

// RAII wrapper over a single native WebGPU handle. Stores the pointer as nint and releases it
// once on Dispose via the supplied release callback. Centralises the unsafe boundary.
public sealed class GpuHandle : IDisposable
{
    internal nint Raw { get; }
    internal unsafe T* As<T>() where T : unmanaged => (T*)Raw;
    internal GpuHandle(nint raw, Action<nint> release);
    public void Dispose();              // idempotent; calls release(Raw) once
}

// Instance, adapter, device, queue, window surface (+ its configuration), and the depth texture.
// Owns surface (re)configuration on resize. Adapter/device are requested asynchronously at init.
// Native handles are private/internal — no raw pointers in the public surface.
public sealed class GpuContext : IDisposable
{
    public WebGPU Api { get; }
    public TextureFormat SurfaceFormat { get; }
    public Extent2D Size { get; }

    internal GpuHandle Device { get; }      // internal: used by sibling wrappers, not public
    internal GpuHandle Queue { get; }
    internal GpuHandle DepthView { get; }

    public static ValueTask<GpuContext> CreateAsync(GameWindow window, bool logGpuErrors);
    public void Configure(Vector2D<int> size);    // (re)configures surface + rebuilds depth texture
    internal GpuHandle AcquireCurrentView();       // surface.GetCurrentTexture → view; null if lost
    public void Present();                         // wgpu SurfacePresent
    public void Dispose();                         // releases depth, surface, queue, device, adapter, instance
}

// Loads WGSL source into a shader module at runtime (no SPIR-V/precompile).
public sealed class ShaderModule : IDisposable
{
    public ShaderModule(GpuContext ctx, string wgslSource);
    internal GpuHandle Handle { get; }
}

// Bind group layouts (camera UBO @group0; per-object model UBO @group1, dynamic offset),
// pipeline layout, and the render pipeline (vertex layout + depth-stencil + color target).
public sealed class RenderPipeline : IDisposable
{
    internal GpuHandle Handle { get; }
    internal GpuHandle CameraLayout { get; }
    internal GpuHandle ModelLayout { get; }
    public RenderPipeline(GpuContext ctx, ShaderModule shader, TextureFormat colorFormat);
}

// Generic GPU buffer + uploads. Replaces Vulkan staging: Queue.WriteBuffer handles uploads.
public sealed class GpuBuffer : IDisposable
{
    internal GpuHandle Handle { get; }
    public ulong SizeBytes { get; }

    public static GpuBuffer CreateVertex<T>(GpuContext ctx, ReadOnlySpan<T> data) where T : unmanaged;
    public static GpuBuffer CreateIndex(GpuContext ctx, ReadOnlySpan<uint> data);
    public static GpuBuffer CreateUniform(GpuContext ctx, ulong size);   // for camera + model UBOs
    public void Write<T>(GpuContext ctx, ulong offset, ReadOnlySpan<T> data) where T : unmanaged;
}

// The orchestrator. Composes the context + pipeline and exposes a tiny per-frame API.
// Owns the camera bind group and a dynamic-offset model uniform buffer + bind group.
public sealed class Renderer : IDisposable
{
    public static ValueTask<Renderer> CreateAsync(GameWindow window, EngineOptions options);

    public bool BeginFrame();                       // acquire view; begin encoder + render pass
    public void SetCameraUniform(in CameraUniform camera);
    public void DrawMesh(GpuMesh mesh, in Matrix4X4<float> model);  // writes model at next dynamic offset
    public void EndFrame();                         // end pass, finish encoder, submit, present
    public void OnResize(Vector2D<int> size);

    public GpuMesh UploadMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices);
}
```

### Rendering — frontend types

```csharp
namespace ClearSkies.Engine.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    public Vector3D<float> Position;
    public Vector3D<float> Normal;     // unused for shading in M1, but in the layout for later
    public Vector3D<float> Color;

    public static VertexBufferLayout GetLayout();   // WebGPU vertex layout + attributes
}

// Per-frame camera uniform block (must match the @group0 binding in basic.wgsl, std140-style).
[StructLayout(LayoutKind.Sequential)]
public struct CameraUniform
{
    public Matrix4X4<float> View;
    public Matrix4X4<float> Projection;
}

// Handle to uploaded GPU buffers for one mesh.
public sealed class GpuMesh
{
    public GpuBuffer VertexBuffer { get; }
    public GpuBuffer IndexBuffer { get; }
    public uint IndexCount { get; }
}

// CPU-side helper that produces cube geometry for the test scene.
public static class PrimitiveFactory
{
    public static (Vertex[] vertices, uint[] indices) Cube(Vector3D<float> color);
}

public sealed class Camera
{
    public float FovRadians { get; set; }   // e.g. 60°
    public float NearPlane { get; set; }     // 0.1
    public float FarPlane { get; set; }      // 1000
    public Matrix4X4<float> GetView(in Transform t);
    public Matrix4X4<float> GetProjection(float aspect);   // WebGPU clip space: Y-up, depth [0,1]
}
```

### Input

```csharp
namespace ClearSkies.Engine.Input;

public sealed class InputManager : IDisposable
{
    public InputManager(GameWindow window);
    public void NewFrame();                          // latch per-frame deltas
    public bool IsKeyDown(Key key);
    public bool WasKeyPressed(Key key);
    public Vector2D<float> MouseDelta { get; }
    public bool CursorCaptured { get; set; }         // toggles raw mouse-look
}
```

### ECS — components & systems

```csharp
namespace ClearSkies.Engine.ECS;

public struct Transform
{
    public Vector3D<float> Position;
    public Quaternion<float> Rotation;
    public Vector3D<float> Scale;
    public Matrix4X4<float> ToMatrix();
}

public struct MeshRenderer { public GpuMesh Mesh; }

public struct CameraComponent { public Camera Camera; public bool Active; }

public struct FreeFlyController
{
    public float MoveSpeed;       // units/sec
    public float LookSensitivity; // radians per pixel
    public float Yaw, Pitch;
}

// Reads input, updates the active camera entity's Transform.
public sealed class FreeFlyCameraSystem : ISystem
{
    public FreeFlyCameraSystem(World world, InputManager input);
    public void Update(float dt);
}

// Builds view/projection, writes the camera UBO, draws all Transform+MeshRenderer entities.
public sealed class RenderSystem : ISystem
{
    public RenderSystem(World world, Renderer renderer);
    public void Update(float dt);   // called in SystemStage.Render
}
```

### Game & entry point

```csharp
namespace ClearSkies.Game;

// Populates the world with the M1 test scene and registers game systems.
public static class TestScene
{
    public static void Build(EngineHost host);   // spawn cubes + a free-fly camera entity
}
```

```csharp
// ClearSkies.Game/Program.cs   — the runnable entry point for M1 (no Client/Server yet)
var host = new EngineHost(new EngineOptions("Clear Skies", 1280, 720, LogGpuErrors: true));
host.AddSystem(new FreeFlyCameraSystem(host.World, host.Input), SystemStage.Logic);
host.AddSystem(new RenderSystem(host.World, host.Renderer), SystemStage.Render);
ClearSkies.Game.TestScene.Build(host);
host.Run();
```

---

## Frame Lifecycle (Renderer.BeginFrame → EndFrame)

1. `GpuContext.AcquireCurrentView()` → current surface texture view. If null/lost → reconfigure surface, skip frame.
2. Update the camera UBO (`Queue.WriteBuffer`).
3. Create a `CommandEncoder`; `BeginRenderPass` with the surface view as color attachment (clear) and the depth view as depth attachment (clear). Set pipeline; set camera bind group (@group0).
4. For each `DrawMesh`: write the model matrix into the model uniform buffer at the next aligned offset, set the model bind group (@group1) with that dynamic offset, set vertex + index buffers, `DrawIndexed`.
5. `EndRenderPass`; `Finish` the encoder → command buffer; `Queue.Submit`.
6. `GpuContext.Present()`.

**Per-object model matrices:** WebGPU core has no push constants. Use a single uniform buffer sized for N objects, each model matrix at an offset aligned to `minUniformBufferOffsetAlignment` (typically 256 B), and bind the model bind group with a **dynamic offset** per draw. This is the idiomatic WebGPU replacement for Vulkan push constants and stays simple for M1's handful of cubes.

---

## Shaders

`basic.wgsl` — a single WGSL file with both stages:
- **Vertex:** `position = projection * view * model * vec4(in.position, 1.0)`; passes vertex color through. Camera (`view`, `projection`) from a uniform at `@group(0) @binding(0)`; `model` from a uniform at `@group(1) @binding(0)`.
- **Fragment:** outputs interpolated vertex color (no lighting in M1).

Loaded from disk (or embedded as a resource) at runtime and handed to `ShaderModule` — **no SPIR-V or `glslc` build step**, which is one of the main simplifications over the Vulkan plan.

---

## Risks & Notes

- **Async init.** Adapter and device requests are callback/async in WebGPU; `GpuContext.CreateAsync` / `Renderer.CreateAsync` must complete before the first frame. Keep the rest of the API synchronous.
- **Native backend.** `Silk.NET.WebGPU.Native.WGPU` must ship the native `wgpu-native` library for the target platforms; verify it lands in the output directory.
- **Surface loss / resize.** Reconfigure the surface and rebuild the depth texture on resize and on a lost/outdated surface texture. This replaces Vulkan swapchain recreation and is a single `Configure` call.
- **Clip space.** WebGPU NDC is **Y-up with `[0,1]` depth** (D3D-like). Use a `[0,1]`-depth perspective and skip the Vulkan Y-flip, or geometry will be inverted/clipped.
- **Error reporting.** wgpu-native reports errors through a device error callback (`SetUncapturedErrorCallback`), not Vulkan-style validation layers; wire it up behind `EngineOptions.LogGpuErrors` and log uncaptured errors during all of M1.
- **Memory & sync are implicit.** Unlike Vulkan, no manual allocation or semaphores/fences — the queue and surface handle ordering and presentation. This is the core reason WebGPU is simpler here.
- **Safe handles, hand-rolled.** There is no official `Silk.NET.WebGPU.Extensions.Disposal` package; the bindings hand back raw native pointers. The `GpuHandle`/owning-wrapper convention above keeps that unsafe surface internal so the rest of the engine never touches a raw pointer. If preferred, the third-party `SilkyWebGPU` abstraction provides similar managed wrappers as an alternative to hand-rolling.
- **wgpu-native present/poll.** `SurfacePresent` and `DevicePoll` are wgpu-native extensions in `Silk.NET.WebGPU.Extensions.WGPU`, not core WebGPU — `GpuContext.Present()` calls into that package.
- **BepuPhysics** is referenced for skeleton parity only; no physics code in M1.

---

## Milestone 1 Done Checklist

- [ ] Solution builds; both projects compile; `basic.wgsl` loads at runtime.
- [ ] Window opens, reports FPS, closes cleanly with no uncaptured WebGPU errors.
- [ ] A cube renders with correct depth testing.
- [ ] Multiple cubes render at distinct positions (dynamic-offset model UBO).
- [ ] WASD + mouse-look free-fly camera moves smoothly through the scene.
- [ ] Window resize works without crashes or distortion.
