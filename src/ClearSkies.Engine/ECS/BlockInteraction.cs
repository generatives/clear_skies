using DefaultEcs;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Math;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

public enum InteractionPhase : byte
{
    /// <summary>The player just clicked the block.</summary>
    Began,
    /// <summary>The button is still held: sent every frame after <see cref="Began"/>, wherever the ray now points
    /// (it may have left the block), so a control can follow a drag.</summary>
    Held,
    /// <summary>The button was released (or the interaction was cut short: the cursor released, the block gone).
    /// The ray is the last one sent.</summary>
    Ended,
}

/// <summary>
/// Published on the <see cref="World"/> (<c>world.Subscribe&lt;BlockInteraction&gt;</c>) by
/// <see cref="PlayerInputSystem"/> while the player uses an <see cref="Interactive"/> block: once when they click it,
/// every frame while they hold the button, and once when they let go. Carries the camera ray in world space (where
/// the player clicked, on <see cref="InteractionPhase.Began"/>) and the mouse's movement this frame, in pixels (x
/// right, y down). While the button is held the mouse doesn't turn the view: each block's control system moves its
/// control by the mouse's movement instead, and keeps the crosshair on the part being moved by publishing an
/// <see cref="InteractionFocus"/>. Handlers run synchronously inside the publisher's update.
/// </summary>
public readonly record struct BlockInteraction(
    Entity           Block,
    InteractionPhase Phase,
    Vector3D<float>  RayOrigin,
    Vector3D<float>  RayDirection,
    Vector2D<float>  MouseDelta);

/// <summary>
/// Published by a block's control system while handling a <see cref="BlockInteraction"/>: the world-space point the
/// player has hold of (a lever's tip, a spot on a wheel's rim). <see cref="PlayerInputSystem"/> turns the view to keep
/// the crosshair on it, so the player's hold follows the control as it moves.
/// </summary>
public readonly record struct InteractionFocus(Vector3D<float> Point);

/// <summary>Helpers for block controls driven by <see cref="BlockInteraction.MouseDelta"/>.</summary>
public static class InteractionDrag
{
    /// <summary>How far, in pixels, the mouse moved along the way <paramref name="tangent"/> (a world-space direction
    /// the held part can move) points on screen, seen along <paramref name="viewDirection"/>: the mouse's movement
    /// projected onto the part's path, so moving the mouse along the part's on-screen path moves it and moving across
    /// doesn't. Zero when the path points straight at or away from the camera.</summary>
    public static float AlongScreen(Vector3D<float> tangent, Vector3D<float> viewDirection, Vector2D<float> mouseDelta)
    {
        // The view never rolls, so the screen's right is level and its up is square to that and the view.
        var view  = Vector3D.Normalize(viewDirection);
        var right = Vector3D.Cross(view, Vector3D<float>.UnitY);
        if (right.LengthSquared < 1e-8f) right = Vector3D<float>.UnitX; // looking straight up or down
        right = Vector3D.Normalize(right);
        var up = Vector3D.Cross(right, view);

        var onScreen = new Vector2D<float>(Vector3D.Dot(tangent, right), Vector3D.Dot(tangent, up));
        if (onScreen.LengthSquared < 1e-10f) return 0f;
        onScreen = Vector2D.Normalize(onScreen);
        return mouseDelta.X * onScreen.X - mouseDelta.Y * onScreen.Y; // the mouse's y runs down the screen
    }

    /// <summary>A point in a block entity's own (model) space, in world space.</summary>
    public static Vector3D<float> ToWorld(in Transform transform, Vector3D<float> local) =>
        transform.Position + Vec.Rotate(transform.Rotation, local * transform.Scale);

    /// <summary>A direction in a block entity's own (model) space, in world space.</summary>
    public static Vector3D<float> DirectionToWorld(in Transform transform, Vector3D<float> local) =>
        Vec.Rotate(transform.Rotation, local * transform.Scale);
}
