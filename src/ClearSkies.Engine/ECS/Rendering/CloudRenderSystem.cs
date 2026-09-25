using ImGuiNET;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;

namespace ClearSkies.Engine.ECS;

/// <summary>Draws the <see cref="CloudLayer"/>s around the camera while <see cref="SkySettings.CloudsEnabled"/>.
/// Depth-tested real geometry, so it runs in <see cref="SystemStage.RenderWorld"/> (after the terrain, which occludes
/// more of it than it occludes of the terrain).</summary>
public sealed class CloudRenderSystem : IRenderSystem, IDebugUiSystem, IDisposable
{
    private readonly CloudLayer _clouds;

    /// <param name="density">Where the world wants clouds (e.g. around islands); null for the same everywhere.</param>
    public CloudRenderSystem(Renderer renderer, ICloudDensityMap? density = null)
    {
        _clouds = new CloudLayer(renderer, density);
    }

    public void Render(in RenderContext frame)
    {
        if (SkySettings.CloudsEnabled) _clouds.Draw(frame.CameraPosition, frame.TimeSeconds);
    }

    public string DebugName => "Clouds";

    public void DrawDebugUi()
    {
        ImGui.Text($"Tiles: {_clouds.TileCount}, cloud cells drawn: {_clouds.CellCount:N0}");
        ImGui.TextDisabled("Coverage, altitude and wind are under Sky & fog.");
    }

    public void Dispose() => _clouds.Dispose();
}
