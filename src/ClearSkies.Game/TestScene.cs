using System.Numerics;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Persistence;
using ClearSkies.Engine.Rendering;
using Silk.NET.Maths;

namespace ClearSkies.Game;

/// <summary>Spawns the local player (free-fly by default, with a walking character body: toggle with V — see
/// PlayerMovementSystem) overlooking the procedural sky world, and the camera at their eye.</summary>
public static class TestScene
{
    // Fallback spawn if the world has no spawn of its own (no island cluster found nearby).
    private static readonly Vector3D<float> FallbackSpawn = new(16f, 45f, -30f);

    /// <summary>Builds the scene and returns the resolved camera spawn position, so callers (e.g. the
    /// ray-traced lighting prototype's test ship — see the plan doc) can place things relative to it.</summary>
    /// <param name="cameraOverride">Launch option (<c>--camera x,y,z[,yaw,pitch]</c>): puts the camera here instead of
    /// overlooking the nearest island, e.g. to reproduce a view for a screenshot.</param>
    /// <param name="spawnView">Where the camera starts and how it faces (yaw, pitch); <see cref="FallbackSpawn"/> if
    /// null.</param>
    /// <param name="savedPlayer">The local player's saved SpawnPlayer command, if they've played this world before.</param>
    public static Vector3D<float> Build(EngineHost host, CommandSystem commands, PlayerId playerId, string playerName, byte[]? savedPlayer,
                                        float[]? cameraOverride = null,
                                        (Vector3D<float> Position, float Yaw, float Pitch)? spawnView = null)
    {
        var eyeTransform = Transform.Identity;

        float yaw = MathF.PI, pitch = -0.45f;
        if (spawnView is { } view)
        {
            eyeTransform.Position = view.Position;
            (yaw, pitch) = (view.Yaw, view.Pitch);
        }
        else
        {
            eyeTransform.Position = FallbackSpawn;
        }
        if (cameraOverride is { Length: >= 3 })
        {
            eyeTransform.Position = new Vector3D<float>(cameraOverride[0], cameraOverride[1], cameraOverride[2]);
            if (cameraOverride.Length >= 5) (yaw, pitch) = (cameraOverride[3], cameraOverride[4]);
        }

        // The player: spawned through the command system (applied in the first tick). From the save if they've played
        // this world before (where they left off); otherwise at the eye position less the eye height (its Transform is
        // the character capsule's centre).
        if (savedPlayer is not null)
            commands.SendSerialized(CommandIds.SpawnPlayer, savedPlayer);
        else
        {
            var p = eyeTransform.Position;
            commands.Send(new Spawn<PlayerDescription>
            {
                Description = new PlayerDescription
                {
                    Id = playerId,
                    Name = playerName,
                    FreeFly = true, // start in FreeFly — zero regression risk vs. today
                    Position = new Vector3(p.X, p.Y - PlayerFactory.EyeHeight, p.Z),
                    Yaw = yaw,
                    Pitch = pitch,
                },
            });
        }

        // The camera: at the spawn until the player exists, then a child of the player at their eye (SpawnPlayerHandler
        // puts it there when the spawn is applied, in the first tick).
        var cam = host.World.CreateEntity();
        eyeTransform.Rotation = Quaternion<float>.CreateFromYawPitchRoll(yaw, pitch, 0f);
        cam.Set(eyeTransform);
        cam.Set(new CameraComponent { Camera = new Camera(), Active = true });

        host.Input.CursorCaptured = false; // the F1 debug menu starts open, and F1 frees the cursor with it
        return eyeTransform.Position;
    }
}
