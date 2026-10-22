using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Sets the fog (<see cref="SkySettings.FogDistance"/>) at the far edge of the terrain drawn around the view (the first
/// terrain interest with a draw radius): its draw radius, or, with coarse levels of terrain drawn past full detail, the
/// farthest level's (<see cref="Range"/>). The fog doesn't follow loading: terrain still on its way isn't hidden behind
/// it, but fades in chunk by chunk as it can be drawn (see ChunkRenderSystem), and swaps between levels of detail fade
/// one into the other (see TerrainSwapSystem). A change of range is eased in.
/// </summary>
public sealed class FogSystem : ISystem, IDebugUiSystem
{
    private readonly float _viewDistance;
    private readonly EntitySet _interests;
    private float _distance, _target;

    /// <param name="viewDistance">The farthest terrain is drawn in full detail (the same limit as
    /// <see cref="ChunkLoadSystem"/>'s).</param>
    public FogSystem(World world, float viewDistance)
    {
        _viewDistance = viewDistance;
        _interests = world.GetEntities().With<TerrainInterest>().With<Transform>().AsSet();
    }

    /// <summary>Distance from the view at which the drawn world stops. Fog is total by here.</summary>
    public float Distance => _distance;

    /// <summary>With coarse levels of terrain on, how far the farthest is drawn (null while they're off).</summary>
    public Func<float?>? Range { get; set; }

    public void Update(float dt)
    {
        float? radius = null;
        foreach (ref readonly Entity e in _interests.GetEntities())
        {
            float draw = MathF.Min(e.Get<TerrainInterest>().DrawRadius, _viewDistance);
            if (draw <= 0) continue;
            radius = Range?.Invoke() ?? draw;
            break;
        }
        if (radius is not { } target) return;

        _target = target;
        _distance = _distance <= 0f ? target : _distance + (target - _distance) * (1f - MathF.Exp(-dt));
        SkySettings.SetFogDistance(_distance);
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Fog";

    public void DrawDebugUi() => ImGui.Text($"Fog distance: {_distance:F0} (range {_target:F0})");
}
