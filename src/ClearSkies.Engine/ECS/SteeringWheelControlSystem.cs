using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Lets the player turn a ship's wheel by grabbing its rim. On the click (<see cref="InteractionPhase.Began"/>) it
/// notes where on the wheel's face the camera ray points, as an angle about the wheel's hub; each frame the button is
/// held it turns the wheel by however far that angle has moved since, so the part of the wheel the player grabbed
/// follows the crosshair round (rather than, say, the wheel's top snapping to it). Every turn also turns the ship's
/// <see cref="Helm"/> heading by the same amount, clockwise to starboard, which <see cref="AirshipFlightSystem"/>
/// then steers the ship to. Every frame it poses each wheel from its <see cref="SteeringWheel.Angle"/>.
///
/// The spin comes from the model: the wheel node turns about its own Z axis at its rest position, its face towards
/// the block's north face (where the player who placed it stands).
/// </summary>
public sealed class SteeringWheelControlSystem : ISystem, IDisposable, IDebugUiSystem
{
    private const string WheelNode = "wheel";

    // Pointing closer to the hub than this, the angle the ray points at swings wildly with tiny moves: ignore it.
    private const float MinGrabRadius = 0.05f;

    private readonly EntitySet   _wheels;
    private readonly IDisposable _subscription;

    // The wheel being turned and the angle about its hub the ray last pointed at, while one is.
    private Entity _turning;
    private float? _grabAngle;
    private Entity _lastUsed;

    public SteeringWheelControlSystem(World world)
    {
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
        if (!e.IsAlive || !e.Has<SteeringWheel>() || !e.Has<RenderedModel>() || !e.Has<Transform>()) return;

        if (interaction.Phase == InteractionPhase.Ended)
        {
            _turning = default;
            _grabAngle = null;
            return;
        }

        var pointed = PointedAngle(e.Get<RenderedModel>().Model, e.Get<Transform>(),
                                   interaction.RayOrigin, interaction.RayDirection);
        if (interaction.Phase == InteractionPhase.Began || _turning != e)
        {
            _turning   = e;
            _grabAngle = pointed;
            _lastUsed  = e;
            return;
        }
        if (pointed is not { } angle) return; // off the wheel's face for now: keep the grab where it was
        if (_grabAngle is not { } grab) { _grabAngle = angle; return; }

        // The shorter way round from the grab to where the ray points now.
        float delta = MathF.IEEERemainder(angle - grab, 2f * MathF.PI);
        _grabAngle = angle;
        Turn(e, delta);
    }

    /// <summary>Turns <paramref name="wheel"/> by <paramref name="delta"/> radians clockwise, and its ship's heading
    /// with it: to starboard, which is clockwise from above, so its heading (anticlockwise) goes down.</summary>
    private static void Turn(Entity wheel, float delta)
    {
        wheel.Get<SteeringWheel>().Angle += delta;
        if (!wheel.Has<BlockRef>()) return;
        var ship = wheel.Get<BlockRef>().Volume.Root;
        if (ship.IsAlive && ship.Has<Helm>()) ship.Get<Helm>().TargetHeading -= delta;
    }

    /// <summary>The angle about the wheel's hub, in the wheel's own frame (from its +X towards its +Y: clockwise, seen
    /// from its north face), at which the world-space ray crosses the wheel's face; null when it doesn't, or crosses
    /// too near the hub to tell, or the model has no wheel.</summary>
    private static float? PointedAngle(GpuModel model, in Transform transform,
                                       Vector3D<float> rayOrigin, Vector3D<float> rayDirection)
    {
        int wheel = model.FindNode(WheelNode);
        if (wheel < 0) return null;

        // The wheel's hub and axes at rest, in model space (= the block entity's own space).
        ref readonly var rest = ref model.RestPose[wheel];
        var hub    = new Vector3D<float>(rest.M12, rest.M13, rest.M14);
        var x      = Vector3D.Normalize(new Vector3D<float>(rest.M0, rest.M1, rest.M2));
        var y      = Vector3D.Normalize(new Vector3D<float>(rest.M4, rest.M5, rest.M6));
        var normal = Vector3D.Normalize(new Vector3D<float>(rest.M8, rest.M9, rest.M10));

        // The ray in the block entity's own space.
        var inverse = Vec.Conjugate(transform.Rotation);
        var origin  = Vec.Rotate(inverse, rayOrigin - transform.Position) / transform.Scale;
        var dir     = Vec.Rotate(inverse, rayDirection) / transform.Scale;

        float facing = Vector3D.Dot(dir, normal);
        if (MathF.Abs(facing) < 1e-5f) return null; // looking along the face
        float t = Vector3D.Dot(hub - origin, normal) / facing;
        if (t < 0f) return null; // the face is behind the camera

        var offset = origin + t * dir - hub;
        float ox = Vector3D.Dot(offset, x), oy = Vector3D.Dot(offset, y);
        if (ox * ox + oy * oy < MinGrabRadius * MinGrabRadius) return null;
        return MathF.Atan2(oy, ox);
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
            var ship = _lastUsed.Has<BlockRef>() ? _lastUsed.Get<BlockRef>().Volume.Root : default;
            if (ship.IsAlive && ship.Has<Helm>())
                ImGui.Text($"Its ship's heading: {ship.Get<Helm>().TargetHeading * 180f / MathF.PI:0}°");
        }
        else
        {
            ImGui.TextDisabled("Click and drag a wheel's rim to turn it.");
        }
    }
}
