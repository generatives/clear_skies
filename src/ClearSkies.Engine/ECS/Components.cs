using ClearSkies.Engine.Physics.Characters;
using ClearSkies.Engine.Rendering;

namespace ClearSkies.Engine.ECS;

/// <summary>Marker flag: set on exactly one DynamicGrid's root entity at a time — the grid whose
/// blocks a UI action (e.g. Save) currently operates on. See <see cref="GridSelection"/> for the
/// invariant-preserving mutator.</summary>
public struct SelectedGridComponent
{
}

/// <summary>Marks an entity as a camera. Only the first active camera is used for rendering.</summary>
public struct CameraComponent
{
    public Camera Camera;
    public bool Active;
}

/// <summary>Free-fly (noclip) camera movement speed. See <see cref="MouseLookComponent"/> for the
/// look-angle state shared with the physics-driven walking mode.</summary>
public struct FreeFlyController
{
    public float MoveSpeed;
}

/// <summary>Accumulated mouse-look angles, shared by every camera movement mode (free-fly,
/// walking, grid-piloting) since they all just rotate the same camera Transform.</summary>
public struct MouseLookComponent
{
    public float Yaw;
    public float Pitch;
    public float LookSensitivity;
}

/// <summary>Tags the camera entity with its walking character body. See
/// <see cref="CharacterCameraSyncSystem"/> (pose readback) and
/// <see cref="PlayerMovementSystem"/> (input → motion goals).</summary>
public struct CharacterControllerComponent
{
    public PlayerCharacter Character;
    public float EyeHeight;

    /// <summary>The ship the character stood on last tick, and its orientation then, so the view can turn with it
    /// (see <see cref="CharacterCameraSyncSystem"/>). Null when it wasn't standing on one.</summary>
    public BepuPhysics.BodyHandle? RideBody;
    public System.Numerics.Quaternion RideOrientation;
}

/// <summary>Movement-mode toggle on the camera entity: true = free-fly noclip (today's default
/// behaviour, no collision/gravity), false = physics-driven walking via
/// <see cref="CharacterControllerComponent"/>. Toggled with V (see PlayerMovementSystem).</summary>
public struct CharacterModeComponent
{
    public bool FreeFly;
}

/// <summary>On a DynamicGrid's root entity: the heading the ship holds when not piloted, in radians about world up
/// (0 with its bow towards -Z, increasing anticlockwise seen from above). Set by <see cref="AirshipFlightSystem"/>
/// to the ship's heading when it first flies, and follows the ship while it's piloted; turning a
/// <see cref="SteeringWheel"/> on the ship turns it.</summary>
public struct Helm
{
    public float TargetHeading;
}

/// <summary>Tag: set on exactly one DynamicGrid's root entity while GridPilotSystem is piloting it.
/// Read by AirshipFlightSystem to decide whether to take velocity targets from player input.</summary>
public struct PilotedComponent
{
}

/// <summary>Tag: set on the free-fly camera entity while GridPilotSystem is flying it along a
/// piloted DynamicGrid. PlayerInputSystem skips WASD/mouse-look for a camera carrying this
/// component.</summary>
public struct CameraGridFollowComponent
{
}
