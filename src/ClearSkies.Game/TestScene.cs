using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using ClearSkies.Game.Startup;
using Silk.NET.Maths;

namespace ClearSkies.Game;

/// <summary>What a game starts with: the camera, the local player (free-fly by default, with a walking character body:
/// toggle with V — see PlayerMovementSystem), and in a new world, a test ship.</summary>
public static class TestScene
{
    /// <summary>
    /// The camera, at <paramref name="eye"/> facing (<paramref name="yaw"/>, <paramref name="pitch"/>), or where
    /// <c>--camera x,y,z[,yaw,pitch]</c> puts it. It stays there until the local player spawns, then views from their
    /// eye (SpawnPlayerHandler puts it there). Returns where it is and how it faces, for spawning a new player there.
    /// </summary>
    public static (Vector3D<float> Eye, float Yaw, float Pitch) AddCamera(EngineHost host, Vector3D<float> eye, float yaw, float pitch,
                                                                         float[]? cameraOption)
    {
        if (cameraOption is { Length: >= 3 })
        {
            eye = new Vector3D<float>(cameraOption[0], cameraOption[1], cameraOption[2]);
            if (cameraOption.Length >= 5) (yaw, pitch) = (cameraOption[3], cameraOption[4]);
        }
        var cam = host.World.CreateEntity();
        cam.Set(new Transform { Position = eye, Rotation = Quaternion<float>.CreateFromYawPitchRoll(yaw, pitch, 0f), Scale = Vector3D<float>.One });
        cam.Set(new CameraComponent { Camera = new Camera(), Active = true });
        return (eye, yaw, pitch);
    }

    /// <summary>
    /// The ray-traced lighting prototype's test ship: a small solid hull with a Lamp exposed on top, near
    /// <paramref name="eye"/>, so its shadow should visibly fall on the terrain below once the ray-traced toggle is on.
    /// Spawned in a new world only: after that it's in the save.
    /// </summary>
    public static void SpawnTestShip(CommandSystem commands, Vector3D<float> eye)
    {
        var voxels = new List<GridVoxel>();
        for (int x = 0; x < 5; x++)
        for (int z = 0; z < 5; z++)
        for (int y = 0; y < 2; y++)
            voxels.Add(new(x, y, z, BlockId.Wood, BlockOrientation.Upright));
        voxels.Add(new(2, 2, 2, BlockId.Lamp, BlockOrientation.Upright)); // exposed on the hull's roof, open air on 5 sides
        // The helm, on the roof one row from the stern, facing a player standing on the stern row looking at the bow
        // (-Z): the wheel, and a lever per axis — forward/back, starboard/port, and up/down (standing out of a post
        // towards the player, so it levers vertically) — plus a second forward/back lever out of the east wall, which
        // moves with the first.
        voxels.Add(new(2, 2, 3, BlockId.SteeringWheel, BlockOrientation.From(Direction.Up, Direction.South)));
        voxels.Add(new(1, 2, 3, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.South)));
        voxels.Add(new(3, 2, 3, BlockId.Lever, BlockOrientation.From(Direction.Up, Direction.East)));
        voxels.Add(new(4, 2, 2, BlockId.Wood, BlockOrientation.Upright));
        voxels.Add(new(4, 2, 3, BlockId.Lever, BlockOrientation.From(Direction.South, Direction.Up)));
        voxels.Add(new(5, 1, 2, BlockId.Lever, BlockOrientation.From(Direction.East, Direction.North)));

        var at = new Vector3(eye.X + 10f, eye.Y - 5f, eye.Z + 45f);
        commands.Send(new Spawn<GridDescription> { Description = GridDescription.FromVoxels(at, voxels), Select = true });
        Console.WriteLine($"[test-ship] spawned 5x2x5 hull + lamp at {at}");
    }
}
