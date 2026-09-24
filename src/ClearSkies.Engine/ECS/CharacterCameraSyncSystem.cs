using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Post-physics: while a camera is in Walking mode (not FreeFly, not grid-following), reads its
/// character capsule's new pose (after this tick's Simulation.Timestep, including the ported
/// BepuPhysics2 character-controller constraint — see Physics/Characters/) and writes the
/// first-person eye position into <see cref="Transform"/>. Mirrors the existing
/// PhysicsTransformSyncSystem/GridPilotSystem post-physics pose-readback precedent.
/// </summary>
public sealed class CharacterCameraSyncSystem : ISystem, IDebugUiSystem
{
    private readonly EntitySet _cameras;

    public CharacterCameraSyncSystem(World world)
    {
        _cameras = world.GetEntities()
            .With<Transform>().With<CharacterControllerComponent>().With<CharacterModeComponent>()
            .AsSet();
    }

    public void Update(float dt)
    {
        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            if (e.Has<CameraGridFollowComponent>()) continue;
            if (e.Get<CharacterModeComponent>().FreeFly) continue;

            ref readonly var cc = ref e.Get<CharacterControllerComponent>();
            ref var t = ref e.Get<Transform>();
            var eye = cc.Character.GetEyePosition(cc.EyeHeight);
            t.Position = new Vector3D<float>(eye.X, eye.Y, eye.Z);
        }
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Character";

    public void DrawDebugUi()
    {
        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            ref readonly var cc = ref e.Get<CharacterControllerComponent>();
            bool freeFly = e.Get<CharacterModeComponent>().FreeFly;
            ImGui.Text($"Mode: {(freeFly ? "FreeFly" : "Walking")} (V to toggle)");
            if (!freeFly)
            {
                ImGui.Text($"Supported: {cc.Character.Supported}");
                var v = cc.Character.LinearVelocity;
                ImGui.Text($"Velocity: ({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})");
            }
        }
    }
}
