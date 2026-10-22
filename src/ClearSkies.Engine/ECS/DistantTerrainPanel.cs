using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Voxels;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// One "Distant Terrain" window for the coarse levels of terrain drawn past full detail (see
/// <see cref="ChunkLoadSystem.SetLevels"/>): whether they're on and how many, how far full detail and each level reach,
/// the swap fade, what the fog waits on, and each level's counts. Purely UI: the loader and the swaps update the levels.
/// </summary>
public sealed class DistantTerrainPanel(ChunkLoadSystem load, TerrainSwapSystem swaps) : ISystem, IDebugUiSystem
{
    /// <summary>How far full detail loads with distant terrain drawn coarse (blocks).</summary>
    public const float DefaultFullDetailRadius = 512f;

    /// <summary>How far each level reaches by default (blocks): one level to 2048; two, to 1024 and 4096.</summary>
    public static float[] DefaultRadii(int levels) => levels switch
    {
        <= 0 => Array.Empty<float>(),
        1 => new[] { 2048f },
        _ => new[] { 1024f, 4096f },
    };

    private float _fullDetail = DefaultFullDetailRadius;
    private float[] _radii = Array.Empty<float>();

    public void Update(float dt) { } // pure UI

    public string DebugName => "Distant Terrain";

    public void DrawDebugUi()
    {
        var levels = load.Levels.Skip(1).ToArray(); // the coarse ones
        if (_radii.Length != levels.Length) _radii = levels.Select(l => l.Radius).ToArray();
        if (load.FullDetailRadius is { } full) _fullDetail = full;

        int count = levels.Length;
        bool changed = ImGui.SliderInt("Coarse levels (0: off)", ref count, 0, 2);
        if (changed)
        {
            _radii = DefaultRadii(count);
            load.SetLevels(_fullDetail, _radii);
            return;
        }
        if (count == 0)
        {
            ImGui.TextDisabled("Full detail as far as the view");
            return;
        }
        // Applied when a slider is let go: each change reloads what moves in or out of range.
        ImGui.SliderFloat("Full detail out to (blocks)", ref _fullDetail, 64f, 1024f, "%.0f");
        changed |= ImGui.IsItemDeactivatedAfterEdit();
        for (int i = 0; i < _radii.Length; i++)
        {
            ImGui.SliderFloat($"Level {i + 1} ({2 << i}-block cells) out to (blocks)", ref _radii[i], 128f, GridStore.ScaledReach, "%.0f");
            changed |= ImGui.IsItemDeactivatedAfterEdit();
        }
        if (changed)
        {
            load.SetLevels(_fullDetail, _radii);
            _radii = load.Levels.Skip(1).Select(l => l.Radius).ToArray(); // as the loader took them
        }

        ImGui.Separator();
        swaps.DrawUi();
        load.DrawLevelsUi();
    }
}
