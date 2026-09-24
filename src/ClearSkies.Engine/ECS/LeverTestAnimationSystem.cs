using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Test harness for per-entity model animation: rocks every lever's arm back and forth about its pivot, each lever
/// on its own phase (from its cell), so levers side by side visibly move independently. Writes only the lever's
/// own <see cref="AnimatedModel"/>. Stands in until levers get real behaviour (a use key flipping
/// <see cref="Lever.On"/>); toggle it off in its debug panel to put the arms back at rest.
/// </summary>
public sealed class LeverTestAnimationSystem : ISystem, IDebugUiSystem
{
    private const string ArmNode = "arm_group";

    private readonly EntitySet _levers;
    private bool  _enabled = true;
    private float _speed = 2f;                          // radians of phase per second
    private float _amplitude = MathF.PI / 4f;           // 45° each way
    private float _time;

    public LeverTestAnimationSystem(World world)
    {
        _levers = world.GetEntities().With<Lever>().With<BlockRef>().With<AnimatedModel>().AsSet();
    }

    public void Update(float dt)
    {
        if (!_enabled) return;
        _time += dt;

        foreach (ref readonly Entity e in _levers.GetEntities())
        {
            var cell  = e.Get<BlockRef>().Position;
            float phase = cell.X * 0.7f + cell.Y * 1.3f + cell.Z * 0.9f;
            float angle = _amplitude * MathF.Sin(_time * _speed + phase);

            // The lever's base is wide along X, so the arm swings in the XY plane: about Z.
            e.Get<AnimatedModel>().SetRotationFromRest(ArmNode,
                Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitZ, angle));
        }
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Lever Test Animation";

    public void DrawDebugUi()
    {
        if (ImGui.Checkbox("Rock lever arms", ref _enabled) && !_enabled) ResetArms();
        ImGui.SliderFloat("Speed (rad/s)", ref _speed, 0f, 10f, "%.1f");
        ImGui.SliderAngle("Amplitude", ref _amplitude, 0f, 90f);
        ImGui.Text($"Levers animated: {_levers.Count:N0}");
    }

    private void ResetArms()
    {
        foreach (ref readonly Entity e in _levers.GetEntities())
            e.Get<AnimatedModel>().ResetRotation(ArmNode);
    }
}
