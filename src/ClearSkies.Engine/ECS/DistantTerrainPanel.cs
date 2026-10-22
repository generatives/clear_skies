using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// One "Distant Terrain" window for the coarse levels of terrain drawn past full detail (see
/// <see cref="ChunkLoadSystem.SetLevels"/>): the swap settings, what the fog waits on, and each level's counts. Purely
/// UI: the loader and the swaps update the levels.
/// </summary>
public sealed class DistantTerrainPanel(ChunkLoadSystem load, TerrainSwapSystem swaps) : ISystem, IDebugUiSystem
{
    public void Update(float dt) { } // pure UI

    public string DebugName => "Distant Terrain";

    public void DrawDebugUi()
    {
        if (load.Levels.Count == 1)
        {
            ImGui.TextDisabled("Off: full detail as far as the view (--lod-levels N turns it on)");
            return;
        }
        swaps.DrawUi();
        load.DrawLevelsUi();
    }
}
