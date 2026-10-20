using ClearSkies.Engine.Entities;
using ClearSkies.Game.Generation;
using Silk.NET.Maths;

namespace ClearSkies.Game.Startup;

/// <summary>Where a new player first sees the world: over a wide, flat stretch of plains (found by scanning seed 1337 for
/// flat, well-covered lowland), 60 blocks above the terrain surface there (which no piece's top reaches), looking along
/// -Z, where land fills the view out to 450 blocks (toward +Z it covers about half). --flight-test flies this way from
/// here.</summary>
public static class WorldSpawn
{
    /// <summary>The eye's position, and which way it faces.</summary>
    public static (Vector3D<float> Eye, float Yaw, float Pitch) For(ulong seed)
    {
        const float x = -825f, z = -1000f;
        float y = ContinentTerrain.For(seed).Height(x, z) + 60f;
        return (new Vector3D<float>(x, y, z), 0f, -0.15f);
    }

    /// <summary>A player's Transform (the character capsule's centre) for an eye at <paramref name="eye"/>.</summary>
    public static System.Numerics.Vector3 PlayerAt(Vector3D<float> eye) => new(eye.X, eye.Y - PlayerFactory.EyeHeight, eye.Z);

    /// <summary>The eye of a player at <paramref name="player"/>.</summary>
    public static Vector3D<float> EyeAt(System.Numerics.Vector3 player) => new(player.X, player.Y + PlayerFactory.EyeHeight, player.Z);
}
