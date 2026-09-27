using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using DefaultEcs;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Every frame, after the drawn poses are written (TickInterpolationSystem): puts the active camera at the local
/// player's eye where they're drawn, looking the way the player looks (which already turns with their support, see
/// SupportSystem). The camera is only a viewpoint, so its Transform is simply that drawn pose. Skipped while GridPilotSystem
/// flies the camera along a grid.
/// </summary>
public sealed class CameraFollowSystem : ISystem
{
    private readonly EntitySet _players;
    private readonly EntitySet _cameras;

    public CameraFollowSystem(World world)
    {
        _players = world.GetEntities().With<LocalPlayer>().With<Transform>().AsSet();
        _cameras = world.GetEntities().With<CameraComponent>().With<Transform>().AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity player in _players.GetEntities())
        {
            if (player.Has<CameraGridFollowComponent>()) return;
            var pt = player.DrawnPose(); // the camera shows the player where they're drawn
            float eye = player.Has<CharacterControllerComponent>()
                ? player.Get<CharacterControllerComponent>().Character.EyeOffset(player.Get<CharacterControllerComponent>().EyeHeight)
                : 0f;
            foreach (ref readonly Entity camera in _cameras.GetEntities())
            {
                if (!camera.Get<CameraComponent>().Active) continue;
                ref var ct = ref camera.Get<Transform>();
                ct.Position = pt.Position + new Vector3D<float>(0, eye, 0);
                ct.Rotation = pt.Rotation;
            }
            return;
        }
    }

    /// <summary>The local player entity, if there is one.</summary>
    public static bool TryGetLocalPlayer(EntitySet players, out Entity player)
    {
        foreach (ref readonly Entity e in players.GetEntities()) { player = e; return true; }
        player = default;
        return false;
    }
}
