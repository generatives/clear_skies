using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game;
using ClearSkies.Game.Generation;
using Silk.NET.Input;

using var host = new EngineHost(new EngineOptions("Clear Skies", 1280, 720, LogGpuErrors: true));

var chunkManager = new ChunkManager(host.World);
var worldGen     = new SkyWorldGenerator();

host.AddSystem(new FreeFlyCameraSystem(host.World, host.Input), SystemStage.Logic);
host.AddSystem(new ChunkLoadSystem(host.World, chunkManager, worldGen, xzRadius: 5, yRadius: 2), SystemStage.Logic);
host.AddSystem(new BlockInteractionSystem(host.World, chunkManager, host.Input, host.Renderer), SystemStage.Logic);
host.AddSystem(new LambdaSystem(() =>
{
    if (host.Input.WasKeyPressed(Key.Tab))
    {
        host.Renderer.WireframeMode = !host.Renderer.WireframeMode;
        Console.WriteLine($"[debug] wireframe: {host.Renderer.WireframeMode}");
    }
}), SystemStage.Logic);
host.AddSystem(new ChunkMeshSystem(chunkManager, host.Renderer), SystemStage.PreRender);
host.AddSystem(new RenderSystem(host.World, host.Renderer), SystemStage.Render);

TestScene.Build(host);

host.Run();
