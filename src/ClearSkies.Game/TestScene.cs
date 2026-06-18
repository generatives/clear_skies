using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Rendering;
using Silk.NET.Maths;

namespace ClearSkies.Game;

/// <summary>Spawns a free-fly camera overlooking the procedural sky world.</summary>
public static class TestScene
{
    public static void Build(EngineHost host)
    {
        var cam = host.World.CreateEntity();
        var camTransform = Transform.Identity;
        // Positioned behind the origin, facing +Z so the first loaded chunks (Z>0) are
        // directly in front of the camera. Pitch tilts down to see island tops at ~75 units ahead.
        camTransform.Position = new Vector3D<float>(16f, 45f, -30f);
        cam.Set(camTransform);
        cam.Set(new CameraComponent { Camera = new Camera(), Active = true });
        cam.Set(new FreeFlyController
        {
            MoveSpeed       = 30f,
            LookSensitivity = 0.0025f,
            Yaw             = MathF.PI,  // face +Z (yaw=π rotates default -Z forward to +Z)
            Pitch           = -0.45f,    // ~26° downward — sees island surface at ~75 units ahead
        });

        host.Input.CursorCaptured = true;
    }
}
