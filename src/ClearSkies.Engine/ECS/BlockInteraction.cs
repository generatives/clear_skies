using DefaultEcs;
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
/// every frame while they hold the button, and once when they let go. Carries the camera ray in world space, so
/// each block's control system can work out what the player is pointing at in its own terms (e.g. the lever, where
/// the ray crosses the plane its arm swings in). Handlers run synchronously inside the publisher's update.
/// </summary>
public readonly record struct BlockInteraction(
    Entity           Block,
    InteractionPhase Phase,
    Vector3D<float>  RayOrigin,
    Vector3D<float>  RayDirection);
