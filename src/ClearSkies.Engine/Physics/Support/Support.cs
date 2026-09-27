using System.Numerics;
using DefaultEcs;

namespace ClearSkies.Engine.Physics.Support;

/// <summary>
/// What a body is standing on or riding with: a large, stable body (<see cref="Supportable"/>), or nothing (world
/// space). Kept by <see cref="SupportSystem"/>. Positions are sent relative to the support, and drawing, the camera
/// and air control all read it. Replaces the character's old ride-body and air-reference ship tracking.
/// </summary>
public struct Support
{
    /// <summary>The supporting entity, or default for none.</summary>
    public Entity Supporter;

    /// <summary>The body's pose in the supporter's space, as of the last tick (world space with no supporter).</summary>
    public Vector3 LocalPosition;
    public Quaternion LocalRotation;

    /// <summary>Seconds since the body was last standing on or above the supporter.</summary>
    public float TimeAway;

    /// <summary>The supporter's rotation last tick, and whether the body was standing on it then, so the view can turn
    /// with it.</summary>
    internal Quaternion SupporterRotation;
    internal bool WasStanding;

    public readonly bool HasSupporter => Supporter.IsAlive;
}

/// <summary>Tag: a body that can be a <see cref="Support"/> (grids).</summary>
public struct Supportable
{
}
