using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Every frame, before the drawn poses are written (TickInterpolationSystem): keeps each <see cref="Eye"/> camera at its
/// parent player's eye, <see cref="CharacterControllerComponent.EyeHeight"/> above their Transform (less while
/// crouching), pitched by their <see cref="MouseLookComponent"/>; the player's own Transform carries the yaw. The camera
/// is an ordinary hierarchy child, so it moves with the player, and is drawn where they're drawn, with no further help.
/// An active camera attached to nothing goes to the local player's eye, e.g. once they've spawned.
/// </summary>
public sealed class EyeSystem : ISystem
{
    private readonly EntitySet _eyes;
    private readonly EntitySet _loose;
    private readonly EntitySet _localPlayers;

    public EyeSystem(World world)
    {
        _eyes = world.GetEntities().With<Eye>().With<Parent>().AsSet();
        _loose = world.GetEntities().With<CameraComponent>().Without<Parent>().AsSet();
        _localPlayers = world.GetEntities().With<LocalPlayer>().With<MouseLookComponent>().With<Transform>().AsSet();
    }

    public void Update(float dt)
    {
        if (_localPlayers.Count > 0)
            foreach (var camera in _loose.GetEntities().ToArray())
                if (camera.Get<CameraComponent>().Active) Attach(camera, _localPlayers.GetEntities()[0]);

        foreach (ref readonly Entity camera in _eyes.GetEntities())
        {
            var player = camera.Get<Parent>().Value;
            if (!player.IsAlive || !player.Has<MouseLookComponent>()) continue;
            camera.Set(Local(player));
        }
    }

    /// <summary>Where a player's eye is, in their own space.</summary>
    public static LocalTransform Local(Entity player) => new()
    {
        Position = new Vector3D<float>(0, EyeOffset(player), 0),
        Rotation = player.Get<MouseLookComponent>().HeadRotation,
        Scale = Vector3D<float>.One,
    };

    /// <summary>How far above the player's Transform their eye is.</summary>
    public static float EyeOffset(Entity player)
    {
        if (!player.Has<CharacterControllerComponent>()) return 0f;
        ref readonly var cc = ref player.Get<CharacterControllerComponent>();
        return cc.Character.EyeOffset(cc.EyeHeight);
    }

    /// <summary>Puts <paramref name="camera"/> at <paramref name="player"/>'s eye, as their <see cref="Eye"/>.</summary>
    public static void Attach(Entity camera, Entity player)
    {
        Hierarchy.SetParent(camera, player, Local(player));
        camera.Set<Eye>();
    }
}
