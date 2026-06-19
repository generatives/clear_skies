using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Casts a ray from the active camera each frame against the static world and every dynamic grid,
/// picking the nearest hit. Left-click breaks the targeted block; right-click places a stone block on
/// the hit face. Editing routes to whichever volume was hit, so dynamic grids are edited exactly like
/// terrain. The targeted face is highlighted (following the grid if it is one); a crosshair is always
/// shown at the screen centre.
/// </summary>
public sealed class BlockInteractionSystem : ISystem, IDisposable
{
    private const float ReachBlocks = 32f;

    private readonly EntitySet    _cameras;
    private readonly EntitySet    _grids;
    private readonly StaticWorld  _staticWorld;
    private readonly PhysicsWorld _physics;
    private readonly InputManager _input;

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

    // Block placed by right-click; toggle Stone/Lamp with the L key (Lamp tests block-light flood).
    private BlockId _placeBlock = BlockId.Stone;

    public BlockInteractionSystem(World world, StaticWorld staticWorld, PhysicsWorld physics, InputManager input, Renderer renderer)
    {
        _cameras     = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _grids       = world.GetEntities().With<DynamicGridComponent>().AsSet();
        _staticWorld = staticWorld;
        _physics     = physics;
        _input       = input;

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
            _placeBlock = _placeBlock == BlockId.Stone ? BlockId.Lamp : BlockId.Stone;
            Console.WriteLine($"[place] selected block: {_placeBlock}");
        }

        if (_input.WasMouseButtonPressed(MouseButton.Left))
        {
            bestVolume.SetBlock(bestBlock.X, bestBlock.Y, bestBlock.Z, BlockId.Air);
            Console.WriteLine($"[break] {(bestIsGrid ? "grid" : "world")} ({bestBlock.X},{bestBlock.Y},{bestBlock.Z})");
        }
        else if (_input.WasMouseButtonPressed(MouseButton.Right))
        {
            var t = bestBlock + bestNormal;
            if (bestVolume.GetBlock(t.X, t.Y, t.Z) == BlockId.Air)
            {
                bestVolume.SetBlock(t.X, t.Y, t.Z, _placeBlock);
                Console.WriteLine($"[place] {_placeBlock} in {(bestIsGrid ? "grid" : "world")} ({t.X},{t.Y},{t.Z})");
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
        var color      = new Vector3D<float>(1f, 0.45f, 0f); // orange

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
        // NDC coordinates for a classic gap-crosshair on a 1280×720 viewport.
        const float hw = 12f / 640f;  // half-arm width  (≈12 px horizontal)
        const float hh = 12f / 360f;  // half-arm height (≈12 px vertical)
        const float gw =  4f / 640f;  // gap radius x
        const float gh =  4f / 360f;  // gap radius y

        var white = new Vector3D<float>(1f, 1f, 1f);
        var n     = Vector3D<float>.Zero;

        // 8 verts: left arm (0-1), right arm (2-3), bottom arm (4-5), top arm (6-7)
        var verts = new Vertex[]
        {
            new(new(-hw,  0,  0), n, white),
            new(new(-gw,  0,  0), n, white),
            new(new( gw,  0,  0), n, white),
            new(new( hw,  0,  0), n, white),
            new(new(  0, -hh, 0), n, white),
            new(new(  0, -gh, 0), n, white),
            new(new(  0,  gh, 0), n, white),
            new(new(  0,  hh, 0), n, white),
        };

        uint[] tris  = { 0, 1, 2,  1, 2, 3,  4, 5, 6,  5, 6, 7 }; // dummy solid
        uint[] edges = { 0, 1,  2, 3,  4, 5,  6, 7 };               // 4 line segments

        return renderer.UploadMesh(verts, tris, edges);
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
