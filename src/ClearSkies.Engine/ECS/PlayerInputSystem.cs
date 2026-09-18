using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using PhysVec = System.Numerics.Vector3;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// The single system for all first-person player input: WASD/QE + mouse-look move the camera,
/// G spawns a single-block dynamic grid in front of it, and left/right click place/break blocks
/// on whichever volume (static world or dynamic grid) the camera is aimed at. The targeted face is
/// highlighted and a crosshair is always shown at the screen centre.
/// </summary>
public sealed class PlayerInputSystem : ISystem, IDisposable, IDebugUiSystem
{
    private const float ReachBlocks = 32f;

    private readonly World        _world;
    private readonly EntitySet    _cameras;
    private readonly EntitySet    _grids;
    private readonly StaticWorld  _staticWorld;
    private readonly PhysicsWorld _physics;
    private readonly InputManager _input;
    private readonly ChunkMeshSystem _meshSystem;
    private readonly GridSelection   _selection;

    private readonly GpuMesh _faceMesh;
    private readonly Entity  _faceEntity;
    private readonly GpuMesh _crosshairMesh;
    private readonly Entity  _crosshairEntity;

    // Cached so face vertices are only re-uploaded when the targeted cell/volume changes.
    private Vector3D<int> _lastBlock;
    private Vector3D<int> _lastNormal;
    private object?       _lastVolume;
    private bool          _faceVisible;

    public Vector3D<int>? TargetBlock  { get; private set; }
    public Vector3D<int>? TargetNormal { get; private set; }

    // Block placed by left-click on an air cell. Cycle with L, or pick directly from the "Place block"
    // dropdown in DrawDebugUi — both keep _placeIndex/_placeBlock in sync.
    private static readonly BlockId[] PlaceableBlocks =
        { BlockId.Stone, BlockId.Wood, BlockId.Grass, BlockId.Dirt, BlockId.Lamp, BlockId.Fan, BlockId.Buoyant };
    private static readonly string[] PlaceableNames =
        Array.ConvertAll(PlaceableBlocks, id => BlockRegistry.Get(id).Name);

    private int _placeIndex = 0; // index into PlaceableBlocks
    private BlockId _placeBlock = PlaceableBlocks[0];

    public PlayerInputSystem(World world, StaticWorld staticWorld, PhysicsWorld physics, InputManager input,
                              ChunkMeshSystem meshSystem, Renderer renderer, GridSelection selection)
    {
        _world       = world;
        _cameras     = world.GetEntities().With<Transform>().With<CameraComponent>().With<FreeFlyController>().AsSet();
        _grids       = world.GetEntities().With<DynamicGridComponent>().AsSet();
        _staticWorld = staticWorld;
        _physics     = physics;
        _input       = input;
        _meshSystem  = meshSystem;
        _selection   = selection;

        // Face outline entity: the WireframeRenderer component is added/removed to show/hide.
        _faceMesh   = BuildFaceMesh(renderer);
        _faceEntity = world.CreateEntity();
        _faceEntity.Set(Transform.Identity);

        // Crosshair: always visible; vertices are in NDC so no Transform needed.
        _crosshairMesh   = BuildCrosshairMesh(renderer);
        _crosshairEntity = world.CreateEntity();
        _crosshairEntity.Set(new HudRenderer { Mesh = _crosshairMesh });
    }

    public void Update(float dt)
    {
        UpdateCameraMovement(dt);
        UpdateBlockSpawning();
        UpdateBlockEditing();
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Player Input";

    public void DrawDebugUi()
    {
        ImGui.Text(TargetBlock is { } b ? $"Target: ({b.X}, {b.Y}, {b.Z})" : "Target: none");
        if (ImGui.Combo("Place block", ref _placeIndex, PlaceableNames, PlaceableNames.Length))
            _placeBlock = PlaceableBlocks[_placeIndex];
        ImGui.TextDisabled("(or press L to cycle)");
    }

    // ── Movement + camera ────────────────────────────────────────────────────

    private void UpdateCameraMovement(float dt)
    {
        // Esc unlocks the cursor; clicking the window re-locks it.
        if (_input.WasKeyPressed(Key.Escape) && _input.CursorCaptured)
            _input.CursorCaptured = false;
        else if (_input.WasMouseButtonPressed(MouseButton.Left) && !_input.CursorCaptured)
        {
            _input.CursorCaptured = true;
            // Swallow this click so the same press that recaptures the cursor doesn't also place a block.
            _input.ConsumeMouseButtonPress(MouseButton.Left);
        }

        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            // Skip the camera while GridPilotSystem is flying it along a piloted grid — otherwise it
            // keeps reading the same WASD/mouse input in the background and fights the ship-following
            // position/rotation being written elsewhere.
            if (e.Has<CameraGridFollowComponent>()) continue;

            ref var t = ref e.Get<Transform>();
            ref var c = ref e.Get<FreeFlyController>();

            if (_input.CursorCaptured)
            {
                var delta = _input.MouseDelta;
                c.Yaw -= delta.X * c.LookSensitivity;
                c.Pitch -= delta.Y * c.LookSensitivity;
                float limit = MathF.PI / 2f - 0.01f;
                c.Pitch = System.Math.Clamp(c.Pitch, -limit, limit);
                t.Rotation = Quaternion<float>.CreateFromYawPitchRoll(c.Yaw, c.Pitch, 0f);
            }

            var forward = Vec.Rotate(t.Rotation, new Vector3D<float>(0, 0, -1));
            var right = Vec.Rotate(t.Rotation, new Vector3D<float>(1, 0, 0));
            var up = new Vector3D<float>(0, 1, 0);

            bool speedUp = false;

            var move = Vector3D<float>.Zero;
            if (_input.IsKeyDown(Key.W)) move += forward;
            if (_input.IsKeyDown(Key.S)) move -= forward;
            if (_input.IsKeyDown(Key.D)) move += right;
            if (_input.IsKeyDown(Key.A)) move -= right;
            if (_input.IsKeyDown(Key.Space)) move += up;
            if (_input.IsKeyDown(Key.ShiftLeft) || _input.IsKeyDown(Key.ShiftRight)) move -= up;
            if (_input.IsKeyDown(Key.E)) c.MoveSpeed += 2;
            if (_input.IsKeyDown(Key.Q)) c.MoveSpeed -= 2;
            if (_input.IsKeyDown(Key.ControlLeft) || _input.IsKeyDown(Key.ControlRight)) speedUp = true;

            c.MoveSpeed = MathF.Max(2f, c.MoveSpeed);

            float speed = speedUp ? c.MoveSpeed * 3f : c.MoveSpeed;

            if (move.LengthSquared > 1e-6f)
                t.Position += Vector3D.Normalize(move) * speed * dt;
        }
    }

    // ── Block spawning ───────────────────────────────────────────────────────

    private void UpdateBlockSpawning()
    {
        if (!_input.WasKeyPressed(Key.G) || !CameraUtil.TryGetActive(_cameras, out var t))
            return;

        var spawn = CameraUtil.SpawnPointInFrontOf(t);
        DynamicGridFactory.SpawnSingleBlock(_world, _meshSystem, _selection, new PhysVec(spawn.X, spawn.Y, spawn.Z), BlockId.Stone);
        Console.WriteLine($"[spawn] grid at ({spawn.X:0.0},{spawn.Y:0.0},{spawn.Z:0.0})");
    }

    // ── Block editing ────────────────────────────────────────────────────────

    private void UpdateBlockEditing()
    {
        TargetBlock  = null;
        TargetNormal = null;

        if (!_input.CursorCaptured || !TryGetCameraRay(out var origin, out var dir))
        {
            HideFace();
            return;
        }

        // Find the nearest hit across the static world and every dynamic grid.
        float        bestDist   = float.MaxValue;
        ChunkVolume? bestVolume = null;
        Entity       bestGridEntity = default;
        Vector3D<int> bestBlock  = default, bestNormal = default;
        bool          bestIsGrid = false;
        Vector3D<float>    gridPos = default, gridCom = default;
        Quaternion<float>  gridRot = Quaternion<float>.Identity;

        if (VoxelRaycaster.Cast(_staticWorld, origin, dir, ReachBlocks, out var sb, out var sn, out var sd) && sd < bestDist)
        {
            bestDist = sd; bestVolume = _staticWorld; bestBlock = sb; bestNormal = sn; bestIsGrid = false;
        }

        foreach (ref readonly Entity e in _grids.GetEntities())
        {
            var grid = e.Get<DynamicGridComponent>().Grid;
            if (!grid.BodyCreated) continue;

            var (p, q) = _physics.GetBodyPose(grid.Body);
            var gp  = PhysicsConv.ToSilk(p);
            var gr  = PhysicsConv.ToSilk(q);
            var com = PhysicsConv.ToSilk(grid.CenterOfMass);
            var inv = Conjugate(gr);

            // Transform the ray into grid-local space: localPoint = com + R⁻¹·(world − gridPos).
            var lo = com + Vec.Rotate(inv, origin - gp);
            var ld = Vec.Rotate(inv, dir);

            if (VoxelRaycaster.Cast(grid, lo, ld, ReachBlocks, out var gb, out var gn, out var gd) && gd < bestDist)
            {
                bestDist = gd; bestVolume = grid; bestBlock = gb; bestNormal = gn; bestIsGrid = true;
                bestGridEntity = e;
                gridPos = gp; gridRot = gr; gridCom = com;
            }
        }

        if (bestVolume is null)
        {
            HideFace();
            return;
        }

        TargetBlock  = bestBlock;
        TargetNormal = bestNormal;
        ShowFace(bestVolume, bestBlock, bestNormal, bestIsGrid, gridPos, gridRot, gridCom);

        if (_input.WasKeyPressed(Key.L))
        {
            _placeIndex = (_placeIndex + 1) % PlaceableBlocks.Length;
            _placeBlock = PlaceableBlocks[_placeIndex];
            Console.WriteLine($"[place] selected block: {_placeBlock}");
        }

        if (_input.WasMouseButtonPressed(MouseButton.Left))
        {
            var t = bestBlock + bestNormal;
            if (bestVolume.GetBlock(t.X, t.Y, t.Z) == BlockId.Air)
            {
                // Facing = away from the face it was placed on (bestNormal already is exactly one of
                // the 6 axis directions), so e.g. a Fan placed against a ship's east wall faces east —
                // away from the ship, not wherever the camera happened to be pointed.
                var facing = FacingExtensions.FromNormal(bestNormal);
                bestVolume.SetBlock(t.X, t.Y, t.Z, _placeBlock, facing);
                if (bestIsGrid) _selection.Select(bestGridEntity);
                Console.WriteLine($"[place] {_placeBlock} in {(bestIsGrid ? "grid" : "world")} ({t.X},{t.Y},{t.Z})");
            }
        }
        else if (_input.WasMouseButtonPressed(MouseButton.Right))
        {
            bestVolume.SetBlock(bestBlock.X, bestBlock.Y, bestBlock.Z, BlockId.Air);
            Console.WriteLine($"[break] {(bestIsGrid ? "grid" : "world")} ({bestBlock.X},{bestBlock.Y},{bestBlock.Z})");

            if (bestIsGrid)
            {
                var grid = (DynamicGrid)bestVolume;
                if (grid.IsEmpty())
                {
                    DynamicGridFactory.Despawn(_physics, _meshSystem, grid);
                    HideFace(); // the outlined face no longer has a volume behind it
                }
                else
                {
                    _selection.Select(bestGridEntity);
                }
            }
        }
    }

    public void Dispose()
    {
        _faceMesh.Dispose();
        _crosshairMesh.Dispose();
        if (_faceEntity.IsAlive)      _faceEntity.Dispose();
        if (_crosshairEntity.IsAlive) _crosshairEntity.Dispose();
    }

    // ── Face highlight ────────────────────────────────────────────────────────

    private void HideFace()
    {
        if (_faceVisible)
        {
            _faceEntity.Remove<WireframeRenderer>();
            _faceVisible = false;
            _lastVolume  = null;
        }
    }

    private void ShowFace(ChunkVolume volume, Vector3D<int> block, Vector3D<int> normal,
                          bool isGrid, Vector3D<float> gridPos, Quaternion<float> gridRot, Vector3D<float> gridCom)
    {
        // Corners are in the volume's local space; re-upload only when the cell or volume changes.
        if (!_faceVisible || block != _lastBlock || normal != _lastNormal || !ReferenceEquals(volume, _lastVolume))
        {
            UploadFaceVertices(block, normal);
            _lastBlock  = block;
            _lastNormal = normal;
            _lastVolume = volume;
        }

        // Map the local-space face into the world. For a grid: world = gridPos + R·(local − com),
        // expressed as a Transform of rotation R and position gridPos − R·com. For the static world the
        // local space is world space, so the transform is identity.
        ref var t = ref _faceEntity.Get<Transform>();
        if (isGrid)
        {
            t.Rotation = gridRot;
            t.Position = gridPos - Vec.Rotate(gridRot, gridCom);
        }
        else
        {
            t.Rotation = Quaternion<float>.Identity;
            t.Position = Vector3D<float>.Zero;
        }

        if (!_faceVisible)
        {
            _faceEntity.Set(new WireframeRenderer { Mesh = _faceMesh });
            _faceVisible = true;
        }
    }

    private void UploadFaceVertices(Vector3D<int> block, Vector3D<int> normal)
    {
        Span<Vector3D<float>> corners = stackalloc Vector3D<float>[4];
        GetFaceCorners(block, normal, corners);

        var faceNormal = new Vector3D<float>(normal.X, normal.Y, normal.Z);
        var color      = new Vector3D<float>(1f, 0f, 0f); // red

        Span<Vertex> verts = stackalloc Vertex[4];
        for (int i = 0; i < 4; i++)
            verts[i] = new Vertex(corners[i], faceNormal, color); // Light=(1,0) full-bright via ctor

        _faceMesh.VertexBuffer.Write<Vertex>(0, verts);
    }

    /// <summary>Computes the 4 corners (in the hit volume's local space) of the face indicated by
    /// <paramref name="normal"/>, slightly offset outward to prevent z-fighting.</summary>
    private static void GetFaceCorners(Vector3D<int> block, Vector3D<int> normal, Span<Vector3D<float>> out4)
    {
        const float eps = 0.002f;
        float bx = block.X, by = block.Y, bz = block.Z;
        int   nx = normal.X, ny = normal.Y, nz = normal.Z;

        if (nx != 0)
        {
            float fx = bx + (nx > 0 ? 1 : 0) + nx * eps;
            out4[0] = new(fx, by,     bz);
            out4[1] = new(fx, by + 1, bz);
            out4[2] = new(fx, by + 1, bz + 1);
            out4[3] = new(fx, by,     bz + 1);
        }
        else if (ny != 0)
        {
            float fy = by + (ny > 0 ? 1 : 0) + ny * eps;
            out4[0] = new(bx,     fy, bz);
            out4[1] = new(bx + 1, fy, bz);
            out4[2] = new(bx + 1, fy, bz + 1);
            out4[3] = new(bx,     fy, bz + 1);
        }
        else
        {
            float fz = bz + (nz > 0 ? 1 : 0) + nz * eps;
            out4[0] = new(bx,     by,     fz);
            out4[1] = new(bx + 1, by,     fz);
            out4[2] = new(bx + 1, by + 1, fz);
            out4[3] = new(bx,     by + 1, fz);
        }
    }

    private static GpuMesh BuildFaceMesh(Renderer renderer)
    {
        // 4 placeholder vertices updated at runtime via QueueWriteBuffer.
        var n = Vector3D<float>.UnitY;
        var c = Vector3D<float>.One;
        var verts = new[] { new Vertex(default, n, c), new Vertex(default, n, c),
                            new Vertex(default, n, c), new Vertex(default, n, c) };
        uint[] tris  = { 0, 1, 2, 0, 2, 3 };             // 2 dummy triangles (never drawn solid)
        uint[] edges = { 0, 1,  1, 2,  2, 3,  3, 0 };    // 4 border edges
        return renderer.UploadMesh(verts, tris, edges);
    }

    // ── Crosshair ─────────────────────────────────────────────────────────────

    private static GpuMesh BuildCrosshairMesh(Renderer renderer)
    {
        // NDC coordinates for a classic gap-crosshair on a 1280×720 viewport, built as filled quads
        // (not a GPU line list, whose width is fixed at 1px) so the arms have real on-screen thickness.
        const float hw = 15f / 640f;  // arm half-length, horizontal arms (X)
        const float hh = 15f / 360f;  // arm half-length, vertical arms (Y)
        const float gw =  4f / 640f;  // gap half-length, X
        const float gh =  4f / 360f;  // gap half-length, Y
        const float tw =  1.0f / 640f; // arm half-thickness, X (thickness of the vertical arms)
        const float th =  1.0f / 360f; // arm half-thickness, Y (thickness of the horizontal arms)

        var white = new Vector3D<float>(1f, 1f, 1f);
        var n     = Vector3D<float>.Zero;

        Vertex V(float x, float y) => new(new(x, y, 0), n, white);

        // 4 quads (4 verts each): left arm, right arm, bottom arm, top arm.
        var verts = new[]
        {
            V(-hw, -th), V(-gw, -th), V(-gw, th), V(-hw, th),
            V( gw, -th), V( hw, -th), V( hw, th), V( gw, th),
            V(-tw, -hh), V( tw, -hh), V( tw, -gh), V(-tw, -gh),
            V(-tw,  gh), V( tw,  gh), V( tw,  hh), V(-tw,  hh),
        };

        uint[] tris =
        {
            0, 1, 2,  0, 2, 3,
            4, 5, 6,  4, 6, 7,
            8, 9, 10, 8, 10, 11,
            12, 13, 14, 12, 14, 15,
        };

        return renderer.UploadMesh(verts, tris);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Quaternion<float> Conjugate(Quaternion<float> q) => new(-q.X, -q.Y, -q.Z, q.W);

    private bool TryGetCameraRay(out Vector3D<float> origin, out Vector3D<float> dir)
    {
        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            ref readonly var cc = ref e.Get<CameraComponent>();
            if (!cc.Active) continue;
            ref readonly var t = ref e.Get<Transform>();
            origin = t.Position;
            dir    = Vector3D.Normalize(Vec.Rotate(t.Rotation, new Vector3D<float>(0, 0, -1)));
            return true;
        }
        origin = default;
        dir    = default;
        return false;
    }
}
