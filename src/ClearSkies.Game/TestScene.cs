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
    public static Vector3D<float> Build(EngineHost host, ulong worldSeed)
    {
        var cam = host.World.CreateEntity();
        var camTransform = Transform.Identity;

        // Find the nearest island to the default spawn area and stand off south of it, so the
        // player always starts overlooking real terrain instead of empty sky (region cells are
        // sparsely populated — ~55% chance each — so the origin cell itself often has none).
        if (TryFindNearestIsland(worldSeed, FallbackSpawn.X, FallbackSpawn.Z, out var island))
        {
            float standoff = island.Radius * 0.6f + 40f;
            camTransform.Position = new Vector3D<float>(island.CenterX, island.BaseY + 40f, island.CenterZ - standoff);
        }
        else
        {
            camTransform.Position = FallbackSpawn;
        }
        cam.Set(camTransform);
        cam.Set(new CameraComponent { Camera = new Camera(), Active = true });
        cam.Set(new MouseLookComponent
        {
            LookSensitivity = 0.0025f,
            Yaw             = MathF.PI,  // face +Z (yaw=π rotates default -Z forward to +Z)
            Pitch           = -0.45f,    // ~26° downward — sees island surface at ~75 units ahead
        });
        cam.Set(new FreeFlyController { MoveSpeed = 10f });

        // Capsule spawns under the camera's eye position (PhysicsConv is internal to
        // ClearSkies.Engine, so convert by hand here — it's just field access).
        var spawnPosition = new Vector3(camTransform.Position.X, camTransform.Position.Y - 0.8f, camTransform.Position.Z);
        var shape = new Capsule(radius: 0.3f, length: 1.0f);
        var character = new PlayerCharacter(host.Physics.Characters, spawnPosition, shape,
            minimumSpeculativeMargin: 0.01f, mass: 10f,
            // Sharp start/stop: accel = force/mass = 50 m/s², reaches the 5 m/s target in ~0.1s (same
            // cap governs stopping) and gives the motion constraint plenty of headroom to hold the
            // character's velocity to an accelerating support (e.g. a thrusting airship deck) without
            // lagging behind. MaximumVerticalGlueForce raised to match for the vertical half of that grip.
            maximumHorizontalForce: 500f, maximumVerticalGlueForce: 350f,
            // JumpVelocity paired with PlayerCharacter's default ExtraFallGravity (12, on top of the
            // world's own gentle -6 gravity -> 18 effective while airborne) for a ~1-block peak jump
            // height: v²/(2·g) = 6²/(2·18) = 1.0. Also makes falls heavier/snappier instead of floaty.
            jumpVelocity: 6f, speed: 5f,
            // Strong (not full) air control, per request — lets you correct your trajectory mid-air.
            airControlForceScale: 0.6f, airControlSpeedScale: 0.8f);
        cam.Set(new CharacterControllerComponent { Character = character, EyeHeight = 0.7f });
        cam.Set(new CharacterModeComponent { FreeFly = true }); // start in FreeFly — zero regression risk vs. today

        host.Input.CursorCaptured = true;
        return camTransform.Position;
    }

    /// <summary>
    /// Spirals outward over region cells (see <see cref="RegionGrid"/>) from the cell containing
    /// (aroundX, aroundZ) looking for the closest island center. Once at least one island is found,
    /// searches one extra ring beyond it — an island can sit near its cell's edge, so a slightly
    /// farther ring can still hold something physically closer.
    /// </summary>
    private static bool TryFindNearestIsland(ulong worldSeed, float aroundX, float aroundZ, out IslandDef nearest)
    {
        int cellX = (int)MathF.Floor(aroundX) >> RegionGrid.CellShift;
        int cellZ = (int)MathF.Floor(aroundZ) >> RegionGrid.CellShift;

        nearest = default;
        bool found = false;
        float bestDistSq = float.MaxValue;
        int foundAtRing = -1;
        Span<IslandDef> islands = stackalloc IslandDef[4];

        for (int ring = 0; ring <= 32; ring++)
        {
            if (foundAtRing >= 0 && ring > foundAtRing + 1) break;

            for (int dx = -ring; dx <= ring; dx++)
            for (int dz = -ring; dz <= ring; dz++)
            {
                if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dz)) != ring) continue; // ring perimeter only

                int n = RegionGrid.ResolveIslandsForCell(worldSeed, cellX + dx, cellZ + dz, islands);
                for (int i = 0; i < n; i++)
                {
                    float ddx = islands[i].CenterX - aroundX;
                    float ddz = islands[i].CenterZ - aroundZ;
                    float distSq = ddx * ddx + ddz * ddz;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        nearest = islands[i];
                        found = true;
                    }
                }
            }

            if (found && foundAtRing < 0) foundAtRing = ring;
        }

        return found;
    }
}
