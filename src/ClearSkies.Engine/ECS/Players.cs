using ClearSkies.Engine.Physics.Support;
using DefaultEcs;
using Silk.NET.Maths;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.ECS;

/// <summary>Changes to a player that have to keep their Transform and their character capsule in step.</summary>
public static class Players
{
    /// <summary>Starts or stops free-flying. Flying takes the capsule out of the simulation, so nothing bumps into or
    /// stands on a player flying through it and no system for walking characters runs on them (see
    /// <see cref="FreeFlying"/>); landing puts it back at the player's position, at rest.</summary>
    public static void SetFreeFlying(Entity player, bool flying)
    {
        if (flying == player.Has<FreeFlying>()) return;
        if (flying)
        {
            player.Set<FreeFlying>();
            if (player.Has<CharacterControllerComponent>()) player.Get<CharacterControllerComponent>().Character.Suspend();
            if (player.Has<Support>()) player.Set(new Support()); // nothing supports a flying player
        }
        else
        {
            player.Remove<FreeFlying>();
            if (player.Has<CharacterControllerComponent>()) // (another machine's player has none here)
                player.Get<CharacterControllerComponent>().Character.Resume(ToPhys(player.Get<Transform>().Position));
        }
    }

    /// <summary>Moves the player, with their capsule if it's in the simulation, to <paramref name="position"/> (the
    /// capsule's centre), at rest.</summary>
    public static void Teleport(Entity player, Vector3D<float> position)
    {
        player.Get<Transform>().Position = position;
        if (player.Has<CharacterControllerComponent>()) player.Get<CharacterControllerComponent>().Character.TeleportTo(ToPhys(position));
    }

    private static PhysVec ToPhys(Vector3D<float> v) => new(v.X, v.Y, v.Z);
}
