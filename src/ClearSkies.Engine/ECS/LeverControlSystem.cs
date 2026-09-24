using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Lets the player drag a lever's arm across its range. On each <see cref="BlockInteraction"/> for a lever (the click
/// and every frame the button is held), finds where the camera ray crosses the plane the arm swings in and sets
/// <see cref="Lever.Value"/> to put the arm as near that point as its range allows. Every frame it poses each lever's
/// arm from its <see cref="Lever.Value"/>, so anything else that sets the value moves the arm too.
///
/// The swing comes from the model: the arm node pivots about its own Z axis at its rest position, upright along its
/// own +Y at 0, reaching <see cref="MaxAngle"/> either way at ±1.
/// </summary>
public sealed class LeverControlSystem : ISystem, IDisposable, IDebugUiSystem
{
    private const string ArmNode = "arm_group";

    /// <summary>How far the arm swings from upright at <see cref="Lever.Value"/> ±1.</summary>
    public const float MaxAngle = MathF.PI / 4f;

    private readonly EntitySet   _levers;
    private readonly IDisposable _subscription;
    private Entity _lastUsed;

    public LeverControlSystem(World world)
    {
        _levers       = world.GetEntities().With<Lever>().With<RenderedModel>().AsSet();
        _subscription = world.Subscribe<BlockInteraction>(OnInteraction);
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _levers.GetEntities())
        {
            float value = System.Math.Clamp(e.Get<Lever>().Value, -1f, 1f);
            e.Get<RenderedModel>().SetRotationFromRest(ArmNode,
                Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitZ, value * MaxAngle));
        }
    }

    private void OnInteraction(in BlockInteraction interaction)
    {
        if (interaction.Phase == InteractionPhase.Ended) return;
        var e = interaction.Block;
        if (!e.IsAlive || !e.Has<Lever>() || !e.Has<RenderedModel>() || !e.Has<Transform>()) return;

        if (ArmAngleTowards(e.Get<RenderedModel>().Model, e.Get<Transform>(),
                            interaction.RayOrigin, interaction.RayDirection) is not { } angle)
            return;

        e.Get<Lever>().Value = System.Math.Clamp(angle / MaxAngle, -1f, 1f);
        _lastUsed = e;
    }

    /// <summary>The arm angle (about the arm's pivot axis, from upright) that points the arm at where the world-space
    /// ray crosses its swing plane, or null when the ray doesn't cross it ahead of the camera. Unclamped: past the arm's
    /// range, the nearest end of the range is the closest the arm can get.</summary>
    private static float? ArmAngleTowards(GpuModel model, in Transform transform,
                                          Vector3D<float> rayOrigin, Vector3D<float> rayDirection)
    {
        int arm = model.FindNode(ArmNode);
        if (arm < 0) return null;

        // The arm's pivot and axes at rest, in model space (= the block entity's own space): its columns are the
        // arm node's local X (the side it leans to at negative angles), Y (upright) and Z (the pivot axis).
        ref readonly var rest = ref model.RestPose[arm];
        var pivot  = new Vector3D<float>(rest.M12, rest.M13, rest.M14);
        var side   = Vector3D.Normalize(new Vector3D<float>(rest.M0, rest.M1, rest.M2));
        var up     = Vector3D.Normalize(new Vector3D<float>(rest.M4, rest.M5, rest.M6));
        var normal = Vector3D.Normalize(new Vector3D<float>(rest.M8, rest.M9, rest.M10));

        // The ray in the block entity's own space.
        var inverse = Vec.Conjugate(transform.Rotation);
        var origin  = Vec.Rotate(inverse, rayOrigin - transform.Position) / transform.Scale;
        var dir     = Vec.Rotate(inverse, rayDirection) / transform.Scale;

        // Where it crosses the swing plane (through the pivot, across the pivot axis).
        float denom = Vector3D.Dot(dir, normal);
        if (MathF.Abs(denom) < 1e-6f) return null;            // running along the plane
        float s = Vector3D.Dot(pivot - origin, normal) / denom;
        if (s < 0f) return null;                              // behind the camera
        var towards = origin + s * dir - pivot;

        // Turning the arm by +angle about its Z takes its +Y to (-sin, cos) in its X/Y.
        float x = Vector3D.Dot(towards, side), y = Vector3D.Dot(towards, up);
        if (x * x + y * y < 1e-10f) return null;              // right on the pivot: no direction
        return MathF.Atan2(-x, y);
    }

    public void Dispose() => _subscription.Dispose();

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Levers";

    public void DrawDebugUi()
    {
        ImGui.Text($"Levers: {_levers.Count:N0}");
        if (_lastUsed.IsAlive && _lastUsed.Has<Lever>())
        {
            ref var lever = ref _lastUsed.Get<Lever>();
            ImGui.SliderFloat("Last used", ref lever.Value, -1f, 1f, "%.2f");
        }
        else
        {
            ImGui.TextDisabled("Click and drag a lever's arm to move it.");
        }
    }
}
