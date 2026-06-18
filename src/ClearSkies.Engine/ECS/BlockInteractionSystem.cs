using ClearSkies.Engine.Core;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Casts a ray from the active camera each frame. Left-click breaks the targeted block;
/// right-click places a new stone block on the face the ray hit.
/// The targeted face is highlighted with a coloured outline; a crosshair is always shown
/// at the screen centre.
/// </summary>
public sealed class BlockInteractionSystem : ISystem, IDisposable
{
    private const float ReachBlocks = 8f;

    private readonly EntitySet    _cameras;
    private readonly ChunkManager _manager;
    private readonly InputManager _input;

    private readonly GpuMesh _faceMesh;
    private readonly Entity  _faceEntity;
    private readonly GpuMesh _crosshairMesh;
    private readonly Entity  _crosshairEntity;

    // Cached to avoid re-uploading vertices every frame when the target hasn't changed.
    private Vector3D<int> _lastBlock;
    private Vector3D<int> _lastNormal;
    private bool          _lastHadTarget;

    public Vector3D<int>? TargetBlock  { get; private set; }
    public Vector3D<int>? TargetNormal { get; private set; }

    public BlockInteractionSystem(World world, ChunkManager manager, InputManager input, Renderer renderer)
    {
        _cameras = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _manager = manager;
        _input   = input;

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

        if (!_input.CursorCaptured ||
            !TryGetCameraRay(out var origin, out var dir) ||
            !VoxelRaycaster.Cast(_manager, origin, dir, ReachBlocks, out var block, out var normal))
        {
            HideFace();
            return;
        }

        TargetBlock  = block;
        TargetNormal = normal;
        ShowFace(block, normal);

        if (_input.WasMouseButtonPressed(MouseButton.Left))
        {
            _manager.SetBlockWorld(block.X, block.Y, block.Z, BlockId.Air);
            Console.WriteLine($"[break] ({block.X},{block.Y},{block.Z})");
        }
        else if (_input.WasMouseButtonPressed(MouseButton.Right))
        {
            var p = block + normal;
            if (_manager.GetBlockWorld(p.X, p.Y, p.Z) == BlockId.Air)
            {
                _manager.SetBlockWorld(p.X, p.Y, p.Z, BlockId.Stone);
                Console.WriteLine($"[place] ({p.X},{p.Y},{p.Z})");
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
        if (_lastHadTarget)
        {
            _faceEntity.Remove<WireframeRenderer>();
            _lastHadTarget = false;
        }
    }

    private void ShowFace(Vector3D<int> block, Vector3D<int> normal)
    {
        if (!_lastHadTarget || block != _lastBlock || normal != _lastNormal)
        {
            UploadFaceVertices(block, normal);
            _lastBlock  = block;
            _lastNormal = normal;
        }

        if (!_lastHadTarget)
        {
            _faceEntity.Set(new WireframeRenderer { Mesh = _faceMesh });
            _lastHadTarget = true;
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
            verts[i] = new Vertex { Position = corners[i], Normal = faceNormal, Color = color };

        _faceMesh.VertexBuffer.Write<Vertex>(0, verts);
    }

    /// <summary>Computes the 4 world-space corners of the face indicated by <paramref name="normal"/>,
    /// slightly offset outward to prevent z-fighting.</summary>
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
        var verts = new Vertex[4];
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
            new() { Position = new(-hw,  0,  0), Normal = n, Color = white },
            new() { Position = new(-gw,  0,  0), Normal = n, Color = white },
            new() { Position = new( gw,  0,  0), Normal = n, Color = white },
            new() { Position = new( hw,  0,  0), Normal = n, Color = white },
            new() { Position = new(  0, -hh, 0), Normal = n, Color = white },
            new() { Position = new(  0, -gh, 0), Normal = n, Color = white },
            new() { Position = new(  0,  gh, 0), Normal = n, Color = white },
            new() { Position = new(  0,  hh, 0), Normal = n, Color = white },
        };

        uint[] tris  = { 0, 1, 2,  1, 2, 3,  4, 5, 6,  5, 6, 7 }; // dummy solid
        uint[] edges = { 0, 1,  2, 3,  4, 5,  6, 7 };               // 4 line segments

        return renderer.UploadMesh(verts, tris, edges);
    }

    // ── Camera ray ────────────────────────────────────────────────────────────

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
