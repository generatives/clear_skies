using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// One "Distant Terrain" window for every level of <see cref="LodTerrainSystem"/>: the settings they share (written to
/// every level), then a section per level with its own range, counts and step timings. Purely a UI aggregator, like
/// <see cref="AirshipDebugPanel"/>: each level updates and draws itself where it's scheduled.
/// </summary>
public sealed class DistantTerrainPanel : ISystem, IDebugUiSystem
{
    private readonly IReadOnlyList<LodTerrainSystem> _levels;

    /// <param name="levels">The levels, finest first.</param>
    public DistantTerrainPanel(IReadOnlyList<LodTerrainSystem> levels) => _levels = levels;

    public void Update(float dt) { } // pure UI aggregator

    public string DebugName => "Distant Terrain";

    public void DrawDebugUi()
    {
        var first = _levels[0];
        ImGui.Checkbox("Draw distant terrain coarse (LOD)", ref first.Enabled);
        ImGui.SliderFloat("Full detail out to (blocks)", ref first.FullDetailRadius, 64f, 1024f, "%.0f");
        float fade = first.FadeSeconds;
        if (ImGui.SliderFloat("Swap fade (seconds)", ref fade, 0f, 2f, "%.2f"))
            foreach (var l in _levels) l.FadeSeconds = fade;
        bool majority = first.Majority, plants = first.Plants;
        bool remake = ImGui.Checkbox("Coarse cells by majority (else any block)", ref majority);
        remake |= ImGui.Checkbox("Coarse plants (grass)", ref plants);
        ImGui.TextDisabled("  majority: 4 of 8 blocks fill a cell (leaves: any), so coarse ground meets full detail flush");
        if (remake)
            foreach (var l in _levels) { l.Majority = majority; l.Plants = plants; l.RemakeAll(); }
        ImGui.Text($"Full-detail chunks loaded: {first.FullDetailChunks:N0}");
        var farthest = _levels.LastOrDefault(l => l.Live) ?? first;
        ImGui.Text($"Fog held by: {farthest.FogHeldBy}");
        foreach (var l in _levels)
        {
            ImGui.Separator();
            if (ImGui.CollapsingHeader($"Level {l.Level}: {1 << l.Level}-block cells", ImGuiTreeNodeFlags.DefaultOpen))
                l.DrawLevelUi();
        }
    }
}
