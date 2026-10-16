using ClearSkies.Engine.Entities;
using ClearSkies.Game.Generation;
using Silk.NET.Maths;

namespace ClearSkies.Game.Startup;

/// <summary>Where a new player first sees the world: over solid ground near the middle of the landmass nearest the
/// origin, 60 blocks above it, looking along -Z. --flight-test flies this way from here.</summary>
public static class WorldSpawn
{
    /// <summary>The eye's position, and which way it faces.</summary>
    public static (Vector3D<float> Eye, float Yaw, float Pitch) For(ulong seed)
    {
        var (x, top, z) = new IslandWorldGenerator(seed).GroundNear(0f, 0f);
        return (new Vector3D<float>(x, top + 60f, z), 0f, -0.15f);
    }

    /// <summary>A player's Transform (the character capsule's centre) for an eye at <paramref name="eye"/>.</summary>
    public static System.Numerics.Vector3 PlayerAt(Vector3D<float> eye) => new(eye.X, eye.Y - PlayerFactory.EyeHeight, eye.Z);

    /// <summary>The eye of a player at <paramref name="player"/>.</summary>
    public static Vector3D<float> EyeAt(System.Numerics.Vector3 player) => new(player.X, player.Y + PlayerFactory.EyeHeight, player.Z);
}
