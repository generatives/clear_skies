using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game;
using ClearSkies.Game.Generation;
using Silk.NET.Input;

using var host = new EngineHost(new EngineOptions("Clear Skies", 1280, 720, LogGpuErrors: true));

// Phase 4.0: prove the GPU compute path (upload → dispatch → readback) before building lighting on it.
GpuComputeSelfTest.Run(host.Context);

var staticWorld = new StaticWorld(host.World);
var worldGen     = new SkyWorldGenerator();
var lightSystem  = new LightSystem(staticWorld);
var meshSystem   = new ChunkMeshSystem(staticWorld, host.Renderer);

host.AddSystem(new FreeFlyCameraSystem(host.World, host.Input), SystemStage.Logic);
host.AddSystem(new ChunkLoadSystem(host.World, staticWorld, worldGen, xzRadius: 3, yRadius: 2), SystemStage.Logic);
host.AddSystem(new StaticColliderSystem(staticWorld, host.Physics), SystemStage.Logic);
host.AddSystem(new GridShapeSystem(host.World, host.Physics), SystemStage.Logic);
host.AddSystem(new PlayerGridControlSystem(host.World, host.Physics, host.Input), SystemStage.Logic);
host.AddSystem(new PhysicsSystem(host.Physics, host.Time.FixedStep), SystemStage.Logic);
host.AddSystem(new GridTransformSystem(host.World, host.Physics), SystemStage.Logic);
host.AddSystem(new DebugDropSystem(host.World, host.Physics, host.Input, lightSystem, meshSystem, host.Renderer), SystemStage.Logic);
host.AddSystem(new BlockInteractionSystem(host.World, staticWorld, host.Physics, host.Input, host.Renderer), SystemStage.Logic);
host.AddSystem(lightSystem, SystemStage.Logic); // CPU sky BFS — seeds GPU flood each cycle
host.AddSystem(new LambdaSystem(() =>
{
    if (host.Input.WasKeyPressed(Key.Tab))
    {
        host.Renderer.WireframeMode = !host.Renderer.WireframeMode;
        Console.WriteLine($"[debug] wireframe: {host.Renderer.WireframeMode}");
    }
}), SystemStage.Logic);
host.AddSystem(new GpuResidencySystem(host.World, staticWorld, host.Context, host.Renderer), SystemStage.PreRender);
host.AddSystem(new GpuLightSystem(host.World, staticWorld, host.Context), SystemStage.PreRender);
host.AddSystem(meshSystem, SystemStage.PreRender);
host.AddSystem(new RenderSystem(host.World, host.Renderer), SystemStage.Render);

TestScene.Build(host);

host.Run();
