using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
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
/// gravity/impulses); <c>Home</c> resets the Selected Grid's rotation to upright. Owns a dedicated
/// pilot camera entity (no <see cref="FreeFlyController"/>, so PlayerInputSystem never drives or
/// raycasts from it) that is toggled <see cref="CameraComponent.Active"/> in place of the free-fly
/// camera while piloting — the single-active-camera convention already used everywhere else.
/// </summary>
public sealed class GridPilotSystem : ISystem
{
    private const float ThirdPersonBack = 16f;
    private const float ThirdPersonUp   = 8f;
    private const float LockedUp        = 2f;

    private readonly EntitySet    _freeFlyCameras;
    private readonly EntitySet    _selectedGrid;
    private readonly InputManager _input;
    private readonly PhysicsWorld _physics;
    private readonly StaticWorld  _staticWorld;
    private readonly PhysicsBodySystem _physicsBody;

    private readonly Entity _pilotCamera;

    private Entity _pilotedGridRoot;
    private bool   _isPiloting;
    private GridCameraMode _cameraMode = GridCameraMode.ThirdPerson;

    public GridPilotSystem(World world, InputManager input, PhysicsWorld physics,
                            StaticWorld staticWorld, PhysicsBodySystem physicsBody)
    {
        _input           = input;
        _physics         = physics;
        _staticWorld     = staticWorld;
        _physicsBody     = physicsBody;
        _freeFlyCameras  = world.GetEntities().With<Transform>().With<CameraComponent>().With<FreeFlyController>().AsSet();
        _selectedGrid    = world.GetEntities().With<DynamicGridComponent>().With<SelectedGridComponent>().AsSet();

        _pilotCamera = world.CreateEntity();
        _pilotCamera.Set(Transform.Identity);
        _pilotCamera.Set(new CameraComponent { Camera = new Camera(), Active = false });
    }

    public void Update(float dt)
    {
        if (_isPiloting && !_pilotedGridRoot.IsAlive)
            StopPiloting(); // the piloted grid was despawned out from under us

        if (_input.WasKeyPressed(Key.F))
        {
            if (_isPiloting) StopPiloting();
            else TryStartPiloting();
        }

        if (_isPiloting && _input.WasKeyPressed(Key.C))
            _cameraMode = _cameraMode == GridCameraMode.ThirdPerson ? GridCameraMode.Locked : GridCameraMode.ThirdPerson;

        HandleLockAndRight();

        if (_isPiloting)
            UpdatePilotCamera();
    }

    private void TryStartPiloting()
    {
        foreach (ref readonly Entity e in _selectedGrid.GetEntities())
        {
            var grid = e.Get<DynamicGridComponent>().Grid;
            if (!grid.BodyCreated) return; // nothing solid yet; can't pilot an empty grid

            _pilotedGridRoot = e;
            _isPiloting = true;
            e.Set(new PilotedComponent());

            foreach (ref readonly Entity cam in _freeFlyCameras.GetEntities())
            {
                ref var cc = ref cam.Get<CameraComponent>();
                cc.Active = false;
            }
            ref var pilotCc = ref _pilotCamera.Get<CameraComponent>();
            pilotCc.Active = true;
            return;
        }
    }

    private void StopPiloting()
    {
        if (_pilotedGridRoot.IsAlive) _pilotedGridRoot.Remove<PilotedComponent>();

        _isPiloting = false;
        _pilotedGridRoot = default;

        ref var pilotCc = ref _pilotCamera.Get<CameraComponent>();
        pilotCc.Active = false;

        // Snapshot before handing control back, so the free-fly camera picks up exactly where you
        // were looking from while piloting — same world position and facing — instead of snapping
        // back to wherever it was parked before you started (it's been frozen there the whole time).
        var pilotTransform = _pilotCamera.Get<Transform>();
        var pilotForward = Vec.Rotate(pilotTransform.Rotation, new Vector3D<float>(0, 0, -1));

        foreach (ref readonly Entity cam in _freeFlyCameras.GetEntities())
        {
            ref var cc = ref cam.Get<CameraComponent>();
            cc.Active = true;

            ref var t = ref cam.Get<Transform>();
            t.Position = pilotTransform.Position;
            t.Rotation = pilotTransform.Rotation;

            // FreeFlyController reconstructs Rotation from Yaw/Pitch on the next mouse-look update, so
            // both must be re-derived here too — otherwise the very first mouse move snaps the view
            // back to whatever stale Yaw/Pitch it had before piloting. Matches the exact convention
            // PlayerInputSystem builds Rotation with: forward = (-sin(yaw)cos(pitch), sin(pitch), -cos(yaw)cos(pitch)).
            ref var c = ref cam.Get<FreeFlyController>();
            c.Pitch = MathF.Asin(System.Math.Clamp(pilotForward.Y, -1f, 1f));
            c.Yaw   = MathF.Atan2(-pilotForward.X, -pilotForward.Z);

            break; // only one free-fly camera exists today
        }
    }

    private void HandleLockAndRight()
    {
        bool lockPressed  = _input.WasKeyPressed(Key.End);
        bool rightPressed = _input.WasKeyPressed(Key.Home);
        if (!lockPressed && !rightPressed) return;

        foreach (ref readonly Entity e in _selectedGrid.GetEntities())
        {
            var grid = e.Get<DynamicGridComponent>().Grid;
            if (!grid.BodyCreated) return;

            if (lockPressed)
            {
                grid.Locked = !grid.Locked;
                _physics.SetBodyKinematic(grid.Body, grid.Locked, grid.Inertia);
            }

            if (rightPressed)
            {
                var (pos, _) = _physics.GetBodyPose(grid.Body);
                _physics.SetBodyPose(grid.Body, pos, System.Numerics.Quaternion.Identity);
                _physics.SetBodyAngularVelocity(grid.Body, PhysVec.Zero);
            }
            return;
        }
    }

    private void UpdatePilotCamera()
    {
        if (!_pilotedGridRoot.IsAlive) return;
        var grid = _pilotedGridRoot.Get<DynamicGridComponent>().Grid;
        if (!grid.BodyCreated) return;

        var (pos, rot) = _physics.GetBodyPose(grid.Body);
        var gridPos = PhysicsConv.ToSilk(pos);
        var gridRot = PhysicsConv.ToSilk(rot);

        var localOffset = _cameraMode == GridCameraMode.ThirdPerson
            ? new Vector3D<float>(0, ThirdPersonUp, ThirdPersonBack)
            : new Vector3D<float>(0, LockedUp, 0);

        ref var t = ref _pilotCamera.Get<Transform>();
        t.Position = gridPos + Vec.Rotate(gridRot, localOffset);
        t.Rotation = gridRot;
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    // Drawn as a section inside AirshipDebugPanel's combined "Airship" window, not its own panel.
    public void DrawDebugUi()
    {
        ImGui.Text(_isPiloting ? $"Piloting — camera: {_cameraMode}" : "Not piloting");
        ImGui.Text("F: take/release control of the Selected Grid");
        ImGui.Text("C: swap third-person / locked camera (while piloting)");
        ImGui.Text("W/S: forward/back   A/D: left/right   Space/Shift: up/down   Q/E: yaw");
        ImGui.Separator();
        ImGui.Text("End: toggle Lock on the Selected Grid");
        ImGui.Text("Home: right (reset rotation of) the Selected Grid");

        foreach (ref readonly Entity e in _selectedGrid.GetEntities())
        {
            var grid = e.Get<DynamicGridComponent>().Grid;
            ImGui.Text(grid.Locked ? "Selected grid: LOCKED" : "Selected grid: unlocked");
            if (grid.BodyCreated)
            {
                var (pos, _) = _physics.GetBodyPose(grid.Body);
                var vel = _physics.GetBodyLinearVelocity(grid.Body);
                ImGui.Text($"Position: ({pos.X:0.00}, {pos.Y:0.00}, {pos.Z:0.00})");
                ImGui.Text($"Velocity: ({vel.X:0.00}, {vel.Y:0.00}, {vel.Z:0.00})  |{vel.Length():0.00}|");
                ImGui.Text($"Mass: {_physics.GetBodyMass(grid.Body):0.0} (0 while locked/kinematic)");

                // Directly answers "is there actually a collider where this grid currently is" —
                // distinguishes a chunk-streaming/collider gap from a genuine collision-resolution bug.
                var chunkPos = new ChunkPosition(
                    (int)MathF.Floor(pos.X / ChunkData.Size),
                    (int)MathF.Floor(pos.Y / ChunkData.Size),
                    (int)MathF.Floor(pos.Z / ChunkData.Size));
                bool chunkLoaded = _staticWorld.IsLoaded(chunkPos);
                bool hasCollider = _physicsBody.HasCollider(chunkPos);
                ImGui.Text($"Grid's chunk {chunkPos}: loaded={chunkLoaded}  hasCollider={hasCollider}");
            }
            break;
        }
    }
}
