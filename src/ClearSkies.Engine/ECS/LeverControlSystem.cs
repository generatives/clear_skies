using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Lets the player drag a lever's arm across its range. Clicking a lever takes hold of the arm's tip: while the button
/// is held the view stays on the tip (see <see cref="InteractionFocus"/>) and the mouse, instead of turning the view,
/// drags the tip along its arc: its movement along the way the tip moves on screen turns the arm, slowly, for fine
/// control, and movement across that is ignored. Every frame it poses each lever's arm from its
/// <see cref="Lever.Value"/>, so anything else that sets the value moves the arm too.
///
/// Dragging sends SetLever commands; the handler moves every lever on the same axis together, and a lever placed on
/// an axis that already has levers picks up their setting when it's placed (see EditVoxelsHandler).
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

    // How far the arm turns per pixel the mouse moves along the tip's path on screen (radians): about 200 pixels from
    // upright to either end, much slower than the view turns, for fine control.
    private const float Sensitivity = 0.004f;

    private readonly World         _world;
    private readonly CommandSystem _commands;
    private readonly EntitySet     _levers;
    private readonly IDisposable   _subscription;
    private Entity _lastUsed;

    public LeverControlSystem(World world, CommandSystem commands)
    {
        _world        = world;
        _commands     = commands;
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

        var model = e.Get<RenderedModel>().Model;
        ref readonly var transform = ref e.Get<Transform>();
        ref var lever = ref e.Get<Lever>();
        float angle = System.Math.Clamp(lever.Value, -1f, 1f) * MaxAngle;
        if (ArmTip(model, transform, angle) is not { } arm) return;

        // Drag the tip along its arc: the mouse's movement along the way the tip moves on screen turns the arm.
        float pixels = InteractionDrag.AlongScreen(arm.Tangent, interaction.RayDirection, interaction.MouseDelta);
        if (pixels != 0f)
        {
            angle = System.Math.Clamp(angle + pixels * Sensitivity, -MaxAngle, MaxAngle);
            Send(e, angle / MaxAngle);
            arm = ArmTip(model, transform, angle)!.Value;
        }

        _lastUsed = e;
        _world.Publish(new InteractionFocus(arm.Tip)); // keep the crosshair on the tip
    }

    /// <summary>Where the arm's tip is in world space with the arm at <paramref name="angle"/> (about its pivot axis,
    /// from upright), and which way it moves as the angle grows; null when the model has no arm.</summary>
    private static (Vector3D<float> Tip, Vector3D<float> Tangent)? ArmTip(GpuModel model, in Transform transform, float angle)
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

        // Turning the arm by +angle about PivotAxis takes its tip to pivot + length·(cos·up + sin·north).
        var tip     = pivot + length * (MathF.Cos(angle) * up + MathF.Sin(angle) * north);
        var tangent = length * (-MathF.Sin(angle) * up + MathF.Cos(angle) * north);
        return (InteractionDrag.ToWorld(transform, tip), InteractionDrag.DirectionToWorld(transform, tangent));
    }

    /// <summary>Asks for a lever's setting; the SetLever handler moves the other levers on its axis with it.</summary>
    private void Send(Entity lever, float value)
    {
        if (BlockEntities.AddressOf(lever) is { } address)
            _commands.Send(new SetLever { Lever = address, Value = value });
    }

    public void Dispose() => _subscription.Dispose();

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Levers";

    public void DrawDebugUi()
    {
        ImGui.Text($"Levers: {_levers.Count:N0}");
        if (_lastUsed.IsAlive && _lastUsed.Has<Lever>())
        {
            float value = _lastUsed.Get<Lever>().Value;
            if (ImGui.SliderFloat("Last used", ref value, -1f, 1f, "%.2f")) Send(_lastUsed, value);
        }
        else
        {
            ImGui.TextDisabled("Click and drag a lever's arm to move it.");
        }
    }
}
