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
/// and every frame the button is held), sets <see cref="Lever.Value"/> to the setting that brings the arm's tip closest
/// to the camera ray, so the tip follows the crosshair as nearly as the arm's range allows. (Unlike aiming at where
/// the ray crosses the arm's swing plane, this still works head-on, with the player standing in that plane, as they
/// do in front of a lever they placed.) Every frame it poses each lever's
/// arm from its <see cref="Lever.Value"/>, so anything else that sets the value moves the arm too.
///
/// The swing comes from the model: the arm node pivots about its own X axis at its rest position, so it levers north
/// and south: upright along its own +Y at 0, leaning <see cref="MaxAngle"/> towards its own -Z (the block's north
/// face) at 1 and towards +Z (south) at -1.
/// </summary>
public sealed class LeverControlSystem : ISystem, IDisposable, IDebugUiSystem
{
    private const string ArmNode = "arm_group";

    // The arm node's own axis it pivots about: turning +Y about -X leans it towards -Z (north) for positive angles.
    private static readonly Vector3D<float> PivotAxis = -Vector3D<float>.UnitX;

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
                Quaternion<float>.CreateFromAxisAngle(PivotAxis, value * MaxAngle));
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

        e.Get<Lever>().Value = angle / MaxAngle;
        _lastUsed = e;
    }

    /// <summary>The arm angle (about the arm's pivot axis, from upright, within ±<see cref="MaxAngle"/>) that brings the
    /// arm's tip closest to the world-space ray, or null when the model has no arm.</summary>
    private static float? ArmAngleTowards(GpuModel model, in Transform transform,
                                          Vector3D<float> rayOrigin, Vector3D<float> rayDirection)
    {
        int arm = model.FindNode(ArmNode);
        if (arm < 0) return null;

        // The arm's pivot and axes at rest, in model space (= the block entity's own space): its columns are the
        // arm node's local Y (upright) and Z (south; the arm leans the other way, north, at positive angles).
        ref readonly var rest = ref model.RestPose[arm];
        var pivot = new Vector3D<float>(rest.M12, rest.M13, rest.M14);
        var up    = Vector3D.Normalize(new Vector3D<float>(rest.M4, rest.M5, rest.M6));
        var north = -Vector3D.Normalize(new Vector3D<float>(rest.M8, rest.M9, rest.M10));

        // The arm's length: at rest it stands upright to the top of the model.
        float length = model.BoundsMax.Y - pivot.Y;
        if (length <= 0f) return null;

        // The ray in the block entity's own space.
        var inverse = Vec.Conjugate(transform.Rotation);
        var origin  = Vec.Rotate(inverse, rayOrigin - transform.Position) / transform.Scale;
        var dir     = Vector3D.Normalize(Vec.Rotate(inverse, rayDirection) / transform.Scale);

        // Turning the arm by +angle about PivotAxis takes its tip to pivot + length·(cos·up + sin·north). Find the angle
        // whose tip is nearest the ray: sample the range, then narrow in around the best sample.
        float TipDistance(float angle)
        {
            var tip = pivot + length * (MathF.Cos(angle) * up + MathF.Sin(angle) * north);
            var d   = tip - origin;
            float s = MathF.Max(Vector3D.Dot(d, dir), 0f);  // nearest point on the ray, not behind the camera
            return (d - s * dir).LengthSquared;
        }

        const int Samples = 32;
        float step = 2f * MaxAngle / Samples;
        float best = -MaxAngle, bestDistance = float.MaxValue;
        for (int i = 0; i <= Samples; i++)
        {
            float angle = -MaxAngle + i * step, distance = TipDistance(angle);
            if (distance < bestDistance) { best = angle; bestDistance = distance; }
        }

        // Golden-section search between the neighbouring samples.
        float lo = MathF.Max(best - step, -MaxAngle), hi = MathF.Min(best + step, MaxAngle);
        const float InvPhi = 0.618034f;
        for (int i = 0; i < 16; i++)
        {
            float a = hi - InvPhi * (hi - lo), b = lo + InvPhi * (hi - lo);
            if (TipDistance(a) < TipDistance(b)) hi = b; else lo = a;
        }
        return (lo + hi) * 0.5f;
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
