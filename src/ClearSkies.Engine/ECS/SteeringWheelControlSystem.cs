using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Lets the player turn a ship's wheel by grabbing its rim. Clicking takes hold of the wheel where the crosshair is on
/// its face (at the rim, when the click is on the hub); while the button is held the view stays on that spot as the
/// wheel turns (see <see cref="InteractionFocus"/>), and the mouse, instead of turning the view, drags the spot round:
/// its movement along the way the spot moves on screen turns the wheel, slowly, for fine control. So the part of the
/// wheel the player grabbed goes round with the mouse, rather than the wheel's top snapping to it. The wheel turns up
/// to <see cref="SteeringWheel.MaxAngle"/> either way, and a ship's wheels turn together. What the wheel asks of the
/// ship is <see cref="AirshipFlightSystem"/>'s business. Every frame it poses each wheel from its
/// <see cref="SteeringWheel.Angle"/>.
///
/// The spin comes from the model: the wheel node turns about its own Z axis at its rest position, its face towards
/// the block's north face (where the player who placed it stands).
/// </summary>
public sealed class SteeringWheelControlSystem : ISystem, IDisposable, IDebugUiSystem
{
    private const string WheelNode = "wheel";

    // How far the wheel turns per pixel the mouse moves along the held spot's path on screen (radians): about 400
    // pixels from centred to either end.
    private const float Sensitivity = 0.008f;

    // The hold's distance from the hub: at least MinGrabRadius (nearer, its path is too small to drag along) and at most
    // RimRadius, the rim (a click beside the wheel, still in its block, holds the rim), which is also where a click
    // on the hub itself holds.
    private const float RimRadius = 0.44f, MinGrabRadius = 0.2f;

    private readonly World       _world;
    private readonly EntitySet   _wheels;
    private readonly IDisposable _subscription;

    // The spot held on the wheel being turned: its angle about the hub from the wheel's own +X (turning with the
    // wheel) and its distance from the hub.
    private float  _grabAngle, _grabRadius;
    private Entity _lastUsed;

    public SteeringWheelControlSystem(World world)
    {
        _world        = world;
        _wheels       = world.GetEntities().With<SteeringWheel>().With<RenderedModel>().AsSet();
        _subscription = world.Subscribe<BlockInteraction>(OnInteraction);
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _wheels.GetEntities())
            e.Get<RenderedModel>().SetRotationFromRest(WheelNode,
                Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitZ, e.Get<SteeringWheel>().Angle));
    }

    private void OnInteraction(in BlockInteraction interaction)
    {
        var e = interaction.Block;
        if (interaction.Phase == InteractionPhase.Ended) return;
        if (!e.IsAlive || !e.Has<SteeringWheel>() || !e.Has<RenderedModel>() || !e.Has<Transform>()) return;
        if (WheelFrame(e.Get<RenderedModel>().Model) is not { } frame) return;
        ref readonly var transform = ref e.Get<Transform>();
        ref var wheel = ref e.Get<SteeringWheel>();

        if (interaction.Phase == InteractionPhase.Began)
        {
            // Hold the spot clicked, as it sits on the wheel at its current angle.
            var (angle, radius) = PointedAt(frame, transform, interaction.RayOrigin, interaction.RayDirection)
                                  ?? (MathF.PI / 2f, RimRadius);
            _grabAngle  = angle - wheel.Angle;
            _grabRadius = System.Math.Clamp(radius, MinGrabRadius, RimRadius);
            _lastUsed   = e;
        }
        else
        {
            var (_, tangent) = Spot(frame, transform, wheel.Angle);
            float pixels = InteractionDrag.AlongScreen(tangent, interaction.RayDirection, interaction.MouseDelta);
            if (pixels != 0f)
            {
                wheel.Angle = System.Math.Clamp(wheel.Angle + pixels * Sensitivity,
                                                -SteeringWheel.MaxAngle, SteeringWheel.MaxAngle);
                SyncShip(e);
            }
        }

        _world.Publish(new InteractionFocus(Spot(frame, transform, wheel.Angle).Point)); // keep the crosshair on it
    }

    /// <summary>The wheel's hub and axes at rest, in model space (= the block entity's own space).</summary>
    private readonly record struct Frame(Vector3D<float> Hub, Vector3D<float> X, Vector3D<float> Y, Vector3D<float> Normal);

    private static Frame? WheelFrame(GpuModel model)
    {
        int wheel = model.FindNode(WheelNode);
        if (wheel < 0) return null;
        ref readonly var rest = ref model.RestPose[wheel];
        return new Frame(
            new Vector3D<float>(rest.M12, rest.M13, rest.M14),
            Vector3D.Normalize(new Vector3D<float>(rest.M0, rest.M1, rest.M2)),
            Vector3D.Normalize(new Vector3D<float>(rest.M4, rest.M5, rest.M6)),
            Vector3D.Normalize(new Vector3D<float>(rest.M8, rest.M9, rest.M10)));
    }

    /// <summary>The held spot in world space with the wheel at <paramref name="wheelAngle"/>, and which way it moves
    /// as the wheel turns clockwise.</summary>
    private (Vector3D<float> Point, Vector3D<float> Tangent) Spot(in Frame f, in Transform transform, float wheelAngle)
    {
        float a = _grabAngle + wheelAngle;
        var point   = f.Hub + _grabRadius * (MathF.Cos(a) * f.X + MathF.Sin(a) * f.Y);
        var tangent = _grabRadius * (-MathF.Sin(a) * f.X + MathF.Cos(a) * f.Y);
        return (InteractionDrag.ToWorld(transform, point), InteractionDrag.DirectionToWorld(transform, tangent));
    }

    /// <summary>Where the world-space ray crosses the wheel's face: the angle about the hub, in the wheel's own frame
    /// (from its +X towards its +Y: clockwise, seen from its north face), and the distance from the hub; null when it
    /// doesn't cross it.</summary>
    private static (float Angle, float Radius)? PointedAt(in Frame f, in Transform transform,
                                                         Vector3D<float> rayOrigin, Vector3D<float> rayDirection)
    {
        var inverse = Vec.Conjugate(transform.Rotation);
        var origin  = Vec.Rotate(inverse, rayOrigin - transform.Position) / transform.Scale;
        var dir     = Vec.Rotate(inverse, rayDirection) / transform.Scale;

        float facing = Vector3D.Dot(dir, f.Normal);
        if (MathF.Abs(facing) < 1e-5f) return null; // looking along the face
        float t = Vector3D.Dot(f.Hub - origin, f.Normal) / facing;
        if (t < 0f) return null; // the face is behind the camera

        var offset = origin + t * dir - f.Hub;
        float ox = Vector3D.Dot(offset, f.X), oy = Vector3D.Dot(offset, f.Y);
        float radius = MathF.Sqrt(ox * ox + oy * oy);
        return radius < 1e-4f ? null : (MathF.Atan2(oy, ox), radius);
    }

    /// <summary>Turns every other wheel on <paramref name="source"/>'s ship to match it.</summary>
    private void SyncShip(Entity source)
    {
        if (!source.Has<BlockRef>()) return;
        var volume = source.Get<BlockRef>().Volume;
        float angle = source.Get<SteeringWheel>().Angle;
        foreach (ref readonly Entity other in _wheels.GetEntities())
            if (other != source && other.Has<BlockRef>() && other.Get<BlockRef>().Volume == volume)
                other.Get<SteeringWheel>().Angle = angle;
    }

    public void Dispose() => _subscription.Dispose();

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Steering wheels";

    public void DrawDebugUi()
    {
        ImGui.Text($"Steering wheels: {_wheels.Count:N0}");
        if (_lastUsed.IsAlive && _lastUsed.Has<SteeringWheel>())
        {
            float degrees = _lastUsed.Get<SteeringWheel>().Angle * 180f / MathF.PI;
            ImGui.Text($"Last used: turned {degrees:0}° clockwise");
        }
        else
        {
            ImGui.TextDisabled("Click and drag a wheel's rim to turn it.");
        }
    }
}
