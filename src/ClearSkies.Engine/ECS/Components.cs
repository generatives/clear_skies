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

/// <summary>The player's look angles, turned per frame by <see cref="LookInputSystem"/>. The player's Transform rotation
/// follows them, and the camera takes it from there (see <see cref="CameraFollowSystem"/>).</summary>
public struct MouseLookComponent
{
    public float Yaw;
    public float Pitch;
    public float LookSensitivity;

    /// <summary>How far the ship the player stands on turned the view (yaw, pitch) in the latest tick. The ship is
    /// drawn between its last two ticks, so the view is drawn that much behind too (see
    /// <see cref="RenderInterpolationSystem"/>); cleared at the start of each tick.</summary>
    public float TurnYaw, TurnPitch;
}

/// <summary>A player's walking character body. Its Transform is the capsule's centre; the eye is
/// <see cref="EyeHeight"/> above it (less while crouching). See <see cref="PhysicsTransformSyncSystem"/> (pose readback),
/// <see cref="PlayerMovementSystem"/> (input → motion goals) and <see cref="CameraFollowSystem"/>.</summary>
public struct CharacterControllerComponent
{
    public PlayerCharacter Character;
    public float EyeHeight;
}

/// <summary>Movement-mode toggle on the player entity: true = free-fly noclip (today's default
/// behaviour, no collision/gravity), false = physics-driven walking via
/// <see cref="CharacterControllerComponent"/>. Toggled with V (see PlayerMovementSystem).</summary>
public struct CharacterModeComponent
{
    public bool FreeFly;
}

/// <summary>Tag: set on exactly one DynamicGrid's root entity while GridPilotSystem is piloting it.
/// Read by AirshipFlightSystem to decide whether to take velocity targets from player input.</summary>
public struct PilotedComponent
{
}

/// <summary>Tag: set on the local player entity while the player is using an Interactive block (see
/// <see cref="BlockInteraction"/>): the mouse moves the control instead of turning the view, which
/// <see cref="PlayerInputSystem"/> keeps on the part being moved. PlayerMovementSystem skips mouse-look meanwhile.</summary>
public struct LookLockedComponent
{
}

/// <summary>Tag: set on the local player entity while GridPilotSystem flies the camera along a piloted DynamicGrid.
/// Movement, mouse-look and the camera follow skip a player carrying it.</summary>
public struct CameraGridFollowComponent
{
}
