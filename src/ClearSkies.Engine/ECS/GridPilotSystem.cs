using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Commands;
using ClearSkies.Engine.Commands.Handlers;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.ECS;

public enum GridCameraMode { ThirdPerson, Locked }

/// <summary>
/// Debug "take control" of a DynamicGrid (Milestone 5 Phase 5.1). <c>F</c> toggles piloting the
/// currently Selected Grid; <c>C</c> swaps between third-person and locked camera modes while
/// piloting; <c>End</c> toggles Lock on the Selected Grid (freezes it in place — kinematic, ignores
/// gravity/impulses); <c>Home</c> resets the Selected Grid's rotation to upright. Piloting drives
/// the player's own camera: while piloting it's a hierarchy child of the grid, orbiting it (third person) or sitting in
/// it (locked), so it moves, and is drawn, with the grid; when piloting stops it goes back to the player's eye. The
/// player stays where they are meanwhile (<see cref="Piloting"/>: standing still, riding along if they're aboard), so
/// they carry on from there.
/// </summary>
public sealed class GridPilotSystem : ISystem
{
    private const float ThirdPersonUp   = 4f;
    private const float LockedUp        = 1f;

    private readonly EntitySet    _players;
    private readonly EntitySet    _cameras;
    private readonly EntitySet    _selectedGrid;
    private readonly InputManager _input;
    private readonly CommandSystem _commands;
    private readonly PhysicsWorld _physics;
    private readonly ChunkVolume _staticVolume;
    private readonly PhysicsBodySystem _physicsBody;

    private Entity _pilot;  // the player piloting, tagged Piloting
    private Entity _camera; // their camera, under the grid meanwhile
    private Entity _pilotedGridRoot;
    private bool   _isPiloting;
    private GridCameraMode _cameraMode = GridCameraMode.ThirdPerson;

    // Mouse-look relative to the grid's own rotation (see PlaceCamera), so looking around while piloting orbits the
    // camera around the ship and keeps that same relative bearing as the ship turns.
    private float _localYaw;
    private float _localPitch;
    private float _cameraDistance = 16f;

    /// <summary>True while the player is flying a grid (the scroll wheel then zooms the camera).</summary>
    public bool IsPiloting => _isPiloting;

    public GridPilotSystem(World world, InputManager input, PhysicsWorld physics,
                            ChunkVolume staticVolume, PhysicsBodySystem physicsBody, CommandSystem commands)
    {
        _commands        = commands;
        _input           = input;
        _physics         = physics;
        _staticVolume     = staticVolume;
        _physicsBody     = physicsBody;
        _players         = world.GetEntities().With<LocalPlayer>().With<MouseLookComponent>().AsSet();
        // Runs before the drawn poses are written, so the camera is drawn where the grid is this frame.
        _cameras         = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _selectedGrid    = world.GetEntities().With<DynamicGrid>().With<SelectedGridComponent>().AsSet();
    }

    /// <summary>Pilot mode is a single-player debug tool: while this says so (other players are connected), it's off.</summary>
    public Func<bool>? Disabled { get; set; }

    public void Update(float dt)
    {
        if (_isPiloting && !_pilotedGridRoot.IsAlive)
            StopPiloting(); // the piloted grid was despawned out from under us
        if (Disabled?.Invoke() == true)
        {
            if (_isPiloting) StopPiloting();
            return;
        }

        if (_input.WasKeyPressed(Key.F))
        {
            if (_isPiloting) StopPiloting();
            else TryStartPiloting();
        }

        if (_isPiloting && _input.WasKeyPressed(Key.C))
            _cameraMode = _cameraMode == GridCameraMode.ThirdPerson ? GridCameraMode.Locked : GridCameraMode.ThirdPerson;

        HandleLockAndRight();

        if (_isPiloting)
        {
            UpdateLookInput();
            PlaceCamera();
        }
    }

    private void TryStartPiloting()
    {
        foreach (ref readonly Entity e in _selectedGrid.GetEntities())
        {
            if (!e.Has<PhysicsBodyComponent>()) return; // nothing solid yet; can't pilot an empty grid

            _pilotedGridRoot = e;
            _isPiloting = true;
            e.Set(new PilotedComponent());
            _localYaw = 0f;
            _localPitch = 0f;

            foreach (ref readonly Entity player in _players.GetEntities())
            {
                _pilot = player;
                player.Set<Piloting>();
                break;
            }
            foreach (ref readonly Entity cam in _cameras.GetEntities())
            {
                if (!cam.Get<CameraComponent>().Active) continue;
                _camera = cam;
                if (cam.Has<Eye>()) cam.Remove<Eye>();
                Hierarchy.SetParent(cam, e, CameraLocal());
                break;
            }
            return;
        }
    }

    private void UpdateLookInput()
    {
        if (!_input.CursorCaptured || !_pilot.IsAlive) return;

        float sensitivity = _pilot.Get<MouseLookComponent>().LookSensitivity;
        var delta = _input.MouseDelta;
        _localYaw -= delta.X * sensitivity;
        _localPitch -= delta.Y * sensitivity;

        float limit = MathF.PI / 2f - 0.01f;
        _localPitch = System.Math.Clamp(_localPitch, -limit, limit);

        _cameraDistance += -_input.ScrollDelta.Y;
    }

    private void StopPiloting()
    {
        if (_pilotedGridRoot.IsAlive) _pilotedGridRoot.Remove<PilotedComponent>();

        _isPiloting = false;
        _pilotedGridRoot = default;

        // Back to the player's eye; they're where they were (Hierarchy detached the camera if the grid was destroyed).
        if (_pilot.IsAlive)
        {
            _pilot.Remove<Piloting>();
            if (_camera.IsAlive) EyeSystem.Attach(_camera, _pilot);
        }

        _pilot = default;
        _camera = default;
    }

    private void HandleLockAndRight()
    {
        bool lockPressed  = _input.WasKeyPressed(Key.End);
        bool rightPressed = _input.WasKeyPressed(Key.Home);
        if (!lockPressed && !rightPressed) return;

        foreach (ref readonly Entity e in _selectedGrid.GetEntities())
        {
            if (!e.Has<NetId>()) return;
            uint id = e.Get<NetId>().Value;
            if (lockPressed) _commands.Send(new SetGridLocked { Grid = id, Locked = !e.Get<DynamicGrid>().Locked });
            if (rightPressed) _commands.Send(new RightGrid { Grid = id });
            return;
        }
    }

    private void PlaceCamera()
    {
        if (_pilotedGridRoot.IsAlive && _camera.IsAlive) _camera.Set(CameraLocal());
    }

    /// <summary>The camera in the grid's block space, about its centre of mass (its body's offset, which edits move):
    /// the look turns it relative to the grid, and in third person it sits behind and above along that look, orbiting
    /// the grid as the mouse moves.</summary>
    private LocalTransform CameraLocal()
    {
        var look = Quaternion<float>.CreateFromYawPitchRoll(_localYaw, _localPitch, 0f);
        var offset = _cameraMode == GridCameraMode.ThirdPerson
            ? new Vector3D<float>(0, ThirdPersonUp, _cameraDistance)
            : new Vector3D<float>(0, LockedUp, 0);
        var centre = _pilotedGridRoot.Has<PhysicsBodyComponent>() ? _pilotedGridRoot.Get<PhysicsBodyComponent>().Offset : default;
        return new LocalTransform { Position = centre + Vec.Rotate(look, offset), Rotation = look, Scale = Vector3D<float>.One };
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    // Drawn as a section inside AirshipDebugPanel's combined "Airship" window, not its own panel.
    public void DrawDebugUi()
    {
        ImGui.Text(_isPiloting ? $"Piloting — camera: {_cameraMode}" : "Not piloting");
        ImGui.Text("F: take/release control of the Selected Grid");
        ImGui.Text("C: swap third-person / locked camera (while piloting)");
        ImGui.Text("Mouse: look around, relative to the ship's own facing");
        ImGui.Text("W/S: forward/back   A/D: left/right   Space/Shift: up/down   Q/E: yaw");
        ImGui.Separator();
        ImGui.Text("End: toggle Lock on the Selected Grid");
        ImGui.Text("Home: right (reset rotation of) the Selected Grid");

        foreach (ref readonly Entity e in _selectedGrid.GetEntities())
        {
            var grid = e.Get<DynamicGrid>();
            ImGui.Text(grid.Locked ? "Selected grid: LOCKED" : "Selected grid: unlocked");
            if (e.Has<PhysicsBodyComponent>())
            {
                var body = e.Get<PhysicsBodyComponent>().Body;
                var (bodyPos, _) = _physics.GetBodyPose(body); // the centre of mass
                var pos = PhysicsConv.ToSilk(bodyPos);
                var vel = _physics.GetBodyLinearVelocity(body);
                ImGui.Text($"Position: ({pos.X:0.00}, {pos.Y:0.00}, {pos.Z:0.00})");
                ImGui.Text($"Velocity: ({vel.X:0.00}, {vel.Y:0.00}, {vel.Z:0.00})  |{vel.Length():0.00}|");
                ImGui.Text($"Mass: {_physics.GetBodyMass(body):0.0} (0 while locked/kinematic)");

                // Directly answers "is there actually a collider where this grid currently is" —
                // distinguishes a chunk-streaming/collider gap from a genuine collision-resolution bug.
                var chunkPos = new ChunkPosition(
                    (int)MathF.Floor(pos.X / ChunkData.Size),
                    (int)MathF.Floor(pos.Y / ChunkData.Size),
                    (int)MathF.Floor(pos.Z / ChunkData.Size));
                bool chunkLoaded = _staticVolume.IsLoaded(chunkPos);
                bool hasCollider = _physicsBody.HasCollider(chunkPos);
                ImGui.Text($"Grid's chunk {chunkPos}: loaded={chunkLoaded}  hasCollider={hasCollider}");
            }
            break;
        }
    }
}
