using System.Numerics;
using BepuPhysics.Collidables;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.ECS;
using ClearSkies.Engine.Physics.Characters;
using ClearSkies.Engine.Rendering;
using ClearSkies.Game.Generation;
using Silk.NET.Maths;

namespace ClearSkies.Game;

/// <summary>Spawns the player camera (free-fly by default) overlooking the procedural sky world,
/// plus its walking character body (toggle with V — see PlayerMovementSystem).</summary>
public static class TestScene
{
    // Fallback spawn if no island is found nearby at all (astronomically unlikely given islands
    // are seeded across the whole plane, but keeps this well-defined).
    private static readonly Vector3D<float> FallbackSpawn = new(16f, 45f, -30f);

    /// <summary>Builds the scene and returns the resolved camera spawn position, so callers (e.g. the
    /// ray-traced lighting prototype's test ship — see the plan doc) can place things relative to it
    /// without re-deriving island geometry via <see cref="TryFindNearestIsland"/>.</summary>
    /// <param name="cameraOverride">Launch option (<c>--camera x,y,z[,yaw,pitch]</c>): puts the camera here instead of
    /// overlooking the nearest island, e.g. to reproduce a view for a screenshot.</param>
    /// <param name="spawnView">Where the camera starts and how it faces (yaw, pitch), if the world has its own idea of
    /// that; otherwise it overlooks the nearest large island of <see cref="IslandGrid"/>.</param>
    public static Vector3D<float> Build(EngineHost host, ulong worldSeed, float[]? cameraOverride = null,
                                        (Vector3D<float> Position, float Yaw, float Pitch)? spawnView = null)
    {
        var cam = host.World.CreateEntity();
        var camTransform = Transform.Identity;

        // Find the nearest large island to the default spawn area and stand off south of it, a little above its
        // ground, so the player always starts overlooking real terrain instead of empty sky.
        float yaw = MathF.PI, pitch = -0.45f;
        if (spawnView is { } view)
        {
            camTransform.Position = view.Position;
            (yaw, pitch) = (view.Yaw, view.Pitch);
        }
        else if (TryFindNearestIsland(worldSeed, FallbackSpawn.X, FallbackSpawn.Z, out var island))
        {
            float standoff = island.Reach + 100f;
            camTransform.Position = new Vector3D<float>(island.CenterX, island.BaseY + island.Lip + island.Crown + 80f,
                                                        island.CenterZ - standoff);
            pitch = -0.15f;
        }
        else
        {
            camTransform.Position = FallbackSpawn;
        }
        if (cameraOverride is { Length: >= 3 })
        {
            camTransform.Position = new Vector3D<float>(cameraOverride[0], cameraOverride[1], cameraOverride[2]);
            if (cameraOverride.Length >= 5) (yaw, pitch) = (cameraOverride[3], cameraOverride[4]);
            camTransform.Rotation = Quaternion<float>.CreateFromYawPitchRoll(yaw, pitch, 0f);
        }
        cam.Set(camTransform);
        cam.Set(new CameraComponent { Camera = new Camera(), Active = true });
        cam.Set(new MouseLookComponent
        {
            LookSensitivity = 0.0025f,
            Yaw             = yaw,    // default π: face +Z (yaw=π rotates default -Z forward to +Z)
            Pitch           = pitch,  // default ~26° downward — sees island surface at ~75 units ahead
        });
        cam.Set(new FreeFlyController { MoveSpeed = 10f });

        // Capsule spawns under the camera's eye position (PhysicsConv is internal to
        // ClearSkies.Engine, so convert by hand here — it's just field access).
        var spawnPosition = new Vector3(camTransform.Position.X, camTransform.Position.Y - 0.8f, camTransform.Position.Z);
        var shape = new Capsule(radius: 0.3f, length: 1.0f);
        var character = new PlayerCharacter(host.Physics.Characters, spawnPosition, shape,
            // Light (two Wood blocks' worth): the character pushes off the deck it walks on as hard as it pushes
            // itself, so a heavy character with strong forces shoved and twisted ships as hard as their Fans.
            minimumSpeculativeMargin: 0.01f, mass: 2f,
            // Sharp start/stop: accel = force/mass = 50 m/s², reaches the 5 m/s target in ~0.1s (same
            // cap governs stopping) and gives the motion constraint plenty of headroom to hold the
            // character's velocity to an accelerating support (e.g. a thrusting airship deck) without
            // lagging behind. MaximumVerticalGlueForce raised to match for the vertical half of that grip.
            // Both scale with the mass, so the feel stays the same at any mass.
            maximumHorizontalForce: 100f, maximumVerticalGlueForce: 70f,
            // JumpVelocity paired with PlayerCharacter's default ExtraFallGravity (12, on top of the
            // world's own gentle -6 gravity -> 18 effective while airborne) for a ~1-block peak jump
            // height: v²/(2·g) = 6²/(2·18) = 1.0. Also makes falls heavier/snappier instead of floaty.
            jumpVelocity: 6f, speed: 5f,
            // Strong (not full) air control, per request — lets you correct your trajectory mid-air.
            airControlForceScale: 0.6f, airControlSpeedScale: 0.8f);
        cam.Set(new CharacterControllerComponent { Character = character, EyeHeight = 0.7f });
        cam.Set(new CharacterModeComponent { FreeFly = true }); // start in FreeFly — zero regression risk vs. today

        host.Input.CursorCaptured = false; // the F1 debug menu starts open, and F1 frees the cursor with it
        return camTransform.Position;
    }

    /// <summary>
    /// Spirals outward over the large islands' cells (see <see cref="IslandGrid"/>) from the cell containing
    /// (aroundX, aroundZ) looking for the closest island center. Once at least one island is found,
    /// searches one extra ring beyond it — an island can sit near its cell's edge, so a slightly
    /// farther ring can still hold something physically closer.
    /// </summary>
    private static bool TryFindNearestIsland(ulong worldSeed, float aroundX, float aroundZ, out IslandDef nearest)
    {
        int size = IslandGrid.CellSize(IslandClass.Large);
        int cellX = (int)MathF.Floor(aroundX / size);
        int cellZ = (int)MathF.Floor(aroundZ / size);

        nearest = default;
        bool found = false;
        float bestDistSq = float.MaxValue;
        int foundAtRing = -1;

        for (int ring = 0; ring <= 32; ring++)
        {
            if (foundAtRing >= 0 && ring > foundAtRing + 1) break;

            for (int dx = -ring; dx <= ring; dx++)
            for (int dz = -ring; dz <= ring; dz++)
            {
                if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dz)) != ring) continue; // ring perimeter only
                if (!IslandGrid.TryResolve(worldSeed, IslandClass.Large, cellX + dx, 0, cellZ + dz, out var island)) continue;

                float ddx = island.CenterX - aroundX;
                float ddz = island.CenterZ - aroundZ;
                float distSq = ddx * ddx + ddz * ddz;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    nearest = island;
                    found = true;
                }
            }

            if (found && foundAtRing < 0) foundAtRing = ring;
        }

        return found;
    }
}
