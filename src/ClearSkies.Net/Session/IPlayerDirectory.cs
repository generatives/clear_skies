using System.Numerics;
using ClearSkies.Engine.Entities;
using DefaultEcs;

namespace ClearSkies.Net.Session;

/// <summary>
/// What the host needs from the game to let players in and out: who a name is, where they spawn, and keeping them when
/// they leave. The game's is the world's save (players are known by name there); tests keep players in memory.
/// </summary>
public interface IPlayerDirectory
{
    /// <summary>The player called <paramref name="name"/>, given an ID the first time the name is seen.</summary>
    PlayerId PlayerFor(string name);

    /// <summary>The player's saved description, if they've played this world before (they spawn where they left off).</summary>
    PlayerDescription? Saved(PlayerId player);

    /// <summary>Where a new player spawns (their Transform: the character capsule's centre), and which way they face.</summary>
    (Vector3 Position, float Yaw, float Pitch) NewPlayerSpawn { get; }

    /// <summary>A player is leaving: keep them (their entity is despawned straight after).</summary>
    void Leaving(Entity player);
}
