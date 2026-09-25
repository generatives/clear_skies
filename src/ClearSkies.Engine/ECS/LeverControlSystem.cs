using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
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
/// A volume's levers on the same axis move together: dragging one sets every other lever in its volume that levers
/// along the same line (north face the same way or the opposite way; the opposite way gets the negated value, so all
/// of them ask for the same thing), and a lever placed on an axis that already has levers picks up their setting.
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

    private readonly World       _world;
    private readonly EntitySet   _levers;
    private readonly IDisposable _subscription;
    private Entity _lastUsed;

    // Levers already synced with their axis; any other lever is new, and picks up its axis' setting.
    private readonly HashSet<Entity> _synced = new();

    public LeverControlSystem(World world)
    {
        _world        = world;
        _levers       = world.GetEntities().With<Lever>().With<RenderedModel>().AsSet();
        _subscription = world.Subscribe<BlockInteraction>(OnInteraction);
    }

    public void Update(float dt)
    {
        _synced.RemoveWhere(e => !e.IsAlive);
        foreach (ref readonly Entity e in _levers.GetEntities())
            if (_synced.Add(e)) AdoptAxisValue(e);

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
            lever.Value = angle / MaxAngle;
            SyncAxis(e);
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

    /// <summary>The line a lever levers along in its volume (0-2: the north/south, east/west or up/down axis), and
    /// which way along it its north face points (+1 or -1): <see cref="Direction"/>s come in opposite pairs.</summary>
    public static (int Axis, float Sign) LeverAxis(in BlockRef block)
    {
        int north = (int)block.Orientation.North;
        return (north / 2, north % 2 == 0 ? 1f : -1f);
    }

    /// <summary>Sets every other lever in <paramref name="source"/>'s volume on its axis to match it.</summary>
    private void SyncAxis(Entity source)
    {
        if (!source.Has<BlockRef>()) return;
        ref readonly var block = ref source.Get<BlockRef>();
        var (axis, sign) = LeverAxis(block);
        float value = source.Get<Lever>().Value * sign; // the setting along the axis' own direction

        foreach (ref readonly Entity other in _levers.GetEntities())
        {
            if (other == source || !other.Has<BlockRef>()) continue;
            ref readonly var otherBlock = ref other.Get<BlockRef>();
            if (otherBlock.Volume != block.Volume) continue;
            var (otherAxis, otherSign) = LeverAxis(otherBlock);
            if (otherAxis == axis) other.Get<Lever>().Value = value * otherSign;
        }
    }

    /// <summary>Sets a new lever to the setting of any lever already on its axis in its volume.</summary>
    private void AdoptAxisValue(Entity lever)
    {
        if (!lever.Has<BlockRef>()) return;
        ref readonly var block = ref lever.Get<BlockRef>();
        var (axis, sign) = LeverAxis(block);

        foreach (ref readonly Entity other in _levers.GetEntities())
        {
            if (other == lever || !_synced.Contains(other) || !other.Has<BlockRef>()) continue;
            ref readonly var otherBlock = ref other.Get<BlockRef>();
            if (otherBlock.Volume != block.Volume) continue;
            var (otherAxis, otherSign) = LeverAxis(otherBlock);
            if (otherAxis != axis) continue;
            lever.Get<Lever>().Value = other.Get<Lever>().Value * otherSign * sign;
            return;
        }
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
            if (ImGui.SliderFloat("Last used", ref lever.Value, -1f, 1f, "%.2f")) SyncAxis(_lastUsed);
        }
        else
        {
            ImGui.TextDisabled("Click and drag a lever's arm to move it.");
        }
    }
}
