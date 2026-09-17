using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game;
using ClearSkies.Game.Generation;
using ImGuiNET;
using Silk.NET.Input;

using var host = new EngineHost(new EngineOptions("Clear Skies", 1280, 720, LogGpuErrors: true));

host.Renderer.LoadTextureAtlas(
    Path.Combine(AppContext.BaseDirectory, "Resources", "spritesheet_tiles.png"),
    Path.Combine(AppContext.BaseDirectory, "Resources", "spritesheet_tiles.xml"));

// Phase 4.0: prove the GPU compute path (upload → dispatch → readback) before building lighting on it.
GpuComputeSelfTest.Run(host.Context);

var staticWorld = new StaticWorld(host.World);
var worldGen     = new SkyWorldGenerator();
var meshSystem   = new ChunkMeshSystem(staticWorld, host.Renderer);

host.AddSystem(host.Gui, SystemStage.Input); // opens ImGui's frame before Logic/PreRender systems run

host.AddSystem(new ChunkLoadSystem(host.World, staticWorld, worldGen, xzRadius: 3, yRadius: 2), SystemStage.Logic);
host.AddSystem(new StaticColliderSystem(staticWorld, host.Physics), SystemStage.Logic);
host.AddSystem(new GridShapeSystem(host.World, host.Physics), SystemStage.Logic);
host.AddSystem(new PlayerGridControlSystem(host.World, host.Physics, host.Input), SystemStage.Logic);
host.AddSystem(host.Physics, SystemStage.Logic); // steps the simulation once bodies/impulses for this frame are in
host.AddSystem(new GridTransformSystem(host.World, host.Physics), SystemStage.Logic);
host.AddSystem(new PlayerInputSystem(host.World, staticWorld, host.Physics, host.Input, meshSystem, host.Renderer), SystemStage.Logic);
host.AddSystem(new LambdaSystem(() =>
{
    if (host.Input.WasKeyPressed(Key.Tab))
    {
        host.Renderer.WireframeMode = !host.Renderer.WireframeMode;
        Console.WriteLine($"[debug] wireframe: {host.Renderer.WireframeMode}");
    }
}), SystemStage.Logic);

// F1 toggles the debug UI. Releases mouse capture while it's open — otherwise the disabled
// cursor mode used for FPS look leaves nothing for ImGui to click on.
bool debugUiOpen = false;
bool showDemoWindow = false;
host.AddSystem(new LambdaSystem(() =>
{
    if (host.Input.WasKeyPressed(Key.F1))
    {
        debugUiOpen = !debugUiOpen;
        host.Input.CursorCaptured = !debugUiOpen;
    }
    if (!debugUiOpen) return;

    if (ImGui.Begin("Debug", ref debugUiOpen))
    {
        ImGui.Text($"{host.Time.FramesPerSecond} fps");
        bool wireframe = host.Renderer.WireframeMode;
        if (ImGui.Checkbox("Wireframe", ref wireframe))
            host.Renderer.WireframeMode = wireframe;
        ImGui.Checkbox("Demo window", ref showDemoWindow);
    }
    ImGui.End();

    if (showDemoWindow)
        ImGui.ShowDemoWindow(ref showDemoWindow);
}), SystemStage.Logic);
host.AddSystem(new GpuResidencySystem(host.World, staticWorld, host.Context, host.Renderer), SystemStage.PreRender);
host.AddSystem(new GpuLightSystem(host.World, staticWorld, host.Context, host.Physics, host.Renderer), SystemStage.PreRender);
host.AddSystem(meshSystem, SystemStage.PreRender);
host.AddSystem(new RenderSystem(host.World, host.Renderer, host.Gui), SystemStage.Render);

TestScene.Build(host);

host.Run();
