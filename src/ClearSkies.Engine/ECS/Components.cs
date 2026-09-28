using ClearSkies.Engine.Math;
using ClearSkies.Engine.Physics.Characters;
using ClearSkies.Engine.Rendering;
using Silk.NET.Maths;

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

/// <summary>The player's look angles, turned per frame by <see cref="LookInputSystem"/>. The player's Transform turns
/// with the yaw only (<see cref="BodyRotation"/>); the pitch is the head's (<see cref="HeadRotation"/>), which the
/// player's <see cref="Eye"/> camera takes (see <see cref="EyeSystem"/>).</summary>
public struct MouseLookComponent
{
    public float Yaw;
    public float Pitch;
    public float LookSensitivity;

    /// <summary>The player's own rotation: the yaw only, so the body stays upright and what hangs off it (the eye)
    /// doesn't swing as the player looks up and down.</summary>
    public readonly Quaternion<float> BodyRotation => Quaternion<float>.CreateFromYawPitchRoll(Yaw, 0f, 0f);

    /// <summary>The head's pitch, relative to the body.</summary>
    public readonly Quaternion<float> HeadRotation => Quaternion<float>.CreateFromYawPitchRoll(0f, Pitch, 0f);

    /// <summary>The way the player looks, in world space.</summary>
    public readonly Vector3D<float> Forward =>
        Vec.Rotate(Quaternion<float>.CreateFromYawPitchRoll(Yaw, Pitch, 0f), new Vector3D<float>(0, 0, -1));

    /// <summary>How far the view was turned (yaw, pitch) by the latest tick: by the ship the player stands on
    /// (<see cref="TurnWith"/>), or to keep it on a control they hold (<see cref="TurnTo"/>). Ticks happen less often
    /// than frames, so the view is drawn that much behind, catching up by the next tick like the ship does (see
    /// <see cref="TickInterpolationSystem"/>).</summary>
    public float TurnYaw { readonly get; private set; }
    public float TurnPitch { readonly get; private set; }
    private bool _turned;

    /// <summary>Turns the view with its support this tick.</summary>
    public void TurnWith(float yaw, float pitch)
    {
        if (!_turned) TurnYaw = TurnPitch = 0f;
        Yaw += yaw;
        Pitch += pitch;
        TurnYaw += yaw;
        TurnPitch += pitch;
        _turned = true;
    }

    /// <summary>Turns the view to <paramref name="yaw"/> and <paramref name="pitch"/> this tick, the short way round.</summary>
    public void TurnTo(float yaw, float pitch)
    {
        float turn = MathF.IEEERemainder(yaw - Yaw, 2f * MathF.PI);
        TurnWith(turn, pitch - Pitch);
    }

    /// <summary>End of a tick: a tick that didn't turn the view leaves no turn to draw.</summary>
    internal void EndTick()
    {
        if (!_turned) TurnYaw = TurnPitch = 0f;
        _turned = false;
    }
}

/// <summary>A player's walking character body. Its Transform is the capsule's centre; the eye is
/// <see cref="EyeHeight"/> above it (less while crouching). See <see cref="PhysicsTransformSyncSystem"/> (pose readback),
/// <see cref="PlayerMovementSystem"/> (input → motion goals) and <see cref="EyeSystem"/>.</summary>
public struct CharacterControllerComponent
{
    public PlayerCharacter Character;
    public float EyeHeight;
}

/// <summary>Tag: the player is free-flying (noclip): they move their Transform directly, and their capsule is out of the
/// simulation until they land (see <see cref="Players.SetFreeFlying"/>). Toggled with V (see PlayerMovementSystem).
/// Systems for walking characters leave players with it out of their queries.</summary>
public struct FreeFlying
{
}

/// <summary>Marks a camera that looks out of its parent player's eyes: <see cref="EyeSystem"/> keeps it at the player's
/// eye height, pitched by their <see cref="MouseLookComponent"/>. Removed while the camera is elsewhere (piloting).</summary>
public struct Eye
{
}

/// <summary>Tag: set on exactly one DynamicGrid's root entity while GridPilotSystem is piloting it.
/// Read by AirshipFlightSystem to decide whether to take velocity targets from player input.</summary>
public struct PilotedComponent
{
}

/// <summary>Tag: set on the local player entity while the player is using an Interactive block (see
/// <see cref="BlockInteraction"/>): the mouse moves the control instead of turning the view, which
/// <see cref="BlockActionSystem"/> keeps on the part being moved. PlayerMovementSystem skips mouse-look meanwhile.</summary>
public struct LookLockedComponent
{
}

/// <summary>Tag: set on the local player entity while they pilot a grid (see GridPilotSystem): their keys and mouse fly
/// the grid and its camera, and the player stands still where they are, riding along if they're aboard.</summary>
public struct Piloting
{
}
