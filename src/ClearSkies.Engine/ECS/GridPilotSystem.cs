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
/// the same free-fly camera entity the player always uses — no separate camera entity is created
/// or swapped in — so chunk streaming, raycasting, etc. keep tracking one stable Entity the whole
/// time. <see cref="CameraGridFollowComponent"/> is set on that entity while piloting so
/// <see cref="BlockActionSystem"/> stops reading WASD/mouse-look into it.
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

    private Entity _followedPlayer; // look angles and the follow tag
    private Entity _followedCamera; // placed by UpdateCameraFollow
    private Entity _pilotedGridRoot;
    private bool   _isPiloting;
    private GridCameraMode _cameraMode = GridCameraMode.ThirdPerson;

    // Mouse-look offset applied on top of the grid's own rotation (see UpdateCameraFollow), so
    // looking around while piloting orbits the camera around the ship and keeps that same
    // relative bearing as the ship turns, instead of snapping back to dead-behind every frame.
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
            UpdateCameraFollow();
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
                _followedPlayer = player;
                player.Set(new CameraGridFollowComponent());
                break;
            }
            foreach (ref readonly Entity cam in _cameras.GetEntities())
            {
                if (!cam.Get<CameraComponent>().Active) continue;
                _followedCamera = cam;
                break;
            }
            return;
        }
    }

    private void UpdateLookInput()
    {
        if (!_input.CursorCaptured || !_followedPlayer.IsAlive) return;

        float sensitivity = _followedPlayer.Get<MouseLookComponent>().LookSensitivity;
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

        if (_followedPlayer.IsAlive && _followedCamera.IsAlive)
        {
            _followedPlayer.Remove<CameraGridFollowComponent>();

            // MouseLookComponent reconstructs Rotation from Yaw/Pitch on the next mouse-look update, so
            // both must be re-derived here from the camera's current facing — otherwise the very first
            // mouse move snaps the view back to whatever stale Yaw/Pitch it had before piloting. Matches
            // the exact convention PlayerMovementSystem builds Rotation with:
            // forward = (-sin(yaw)cos(pitch), sin(pitch), -cos(yaw)cos(pitch)).
            var rotation = _followedCamera.Get<Transform>().Rotation;
            var forward = Vec.Rotate(rotation, new Vector3D<float>(0, 0, -1));

            ref var look = ref _followedPlayer.Get<MouseLookComponent>();
            look.Pitch = MathF.Asin(System.Math.Clamp(forward.Y, -1f, 1f));
            look.Yaw   = MathF.Atan2(-forward.X, -forward.Z);
            _followedPlayer.Get<Transform>().Rotation = Quaternion<float>.CreateFromYawPitchRoll(look.Yaw, look.Pitch, 0f);

            // The player carries on from where the camera was, as when the camera itself was the player.
            float eye = _followedPlayer.Has<CharacterControllerComponent>() ? _followedPlayer.Get<CharacterControllerComponent>().EyeHeight : 0f;
            _followedPlayer.Get<Transform>().Position = _followedCamera.Get<Transform>().Position - new Vector3D<float>(0, eye, 0);
        }

        _followedPlayer = default;
        _followedCamera = default;
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

    private void UpdateCameraFollow()
    {
        if (!_pilotedGridRoot.IsAlive || !_followedCamera.IsAlive) return;

        // Where the grid is drawn (its body pose, the centre of mass, between its last two ticks): the camera follows
        // what's on screen.
        var gridTransform = _pilotedGridRoot.DrawnPose();
        var gridPos = gridTransform.Position;
        var gridRot = gridTransform.Rotation;

        // lookRot first (relative to the ship's own facing), then gridRot on top — so panning the
        // mouse orbits the camera around the ship, and turning the ship carries that bearing with it.
        var lookRot = Quaternion<float>.CreateFromYawPitchRoll(_localYaw, _localPitch, 0f);
        var cameraRot = gridRot * lookRot;

        var localOffset = _cameraMode == GridCameraMode.ThirdPerson
            ? new Vector3D<float>(0, ThirdPersonUp, _cameraDistance)
            : new Vector3D<float>(0, LockedUp, 0);

        ref var t = ref _followedCamera.Get<Transform>();
        t.Position = gridPos + Vec.Rotate(cameraRot, localOffset);
        t.Rotation = cameraRot;
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
                var pos = e.Get<Transform>().Position;
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
