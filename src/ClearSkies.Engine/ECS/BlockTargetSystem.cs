using ClearSkies.Engine.Commands.Handlers;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Every frame, presentation only: outlines the face of the block the camera is aimed at, within the game mode's
/// reach, from the drawn camera (so it matches what's on screen). Hidden while the cursor is free or a control is in
/// use. <see cref="BlockActionSystem"/> acts on blocks, each tick.
/// </summary>
public sealed class BlockTargetSystem : ISystem, IDisposable, Gui.IDebugUiSystem
{
    private readonly EntitySet _cameras;
    private readonly EntitySet _volumes;
    private readonly Input.InputManager _input;
    private readonly BlockActionSystem _actions;
    private readonly EditLimits _limits;

    private readonly GpuMesh _faceMesh;
    private readonly Entity  _faceEntity;

    // Cached so face vertices are only re-uploaded when the targeted cell/volume changes.
    private Vector3D<int> _lastBlock;
    private Vector3D<int> _lastNormal;
    private object?       _lastVolume;
    private bool          _faceVisible;

    public Vector3D<int>? TargetBlock  { get; private set; }
    public Vector3D<int>? TargetNormal { get; private set; }

    public BlockTargetSystem(World world, Input.InputManager input, Renderer renderer, BlockActionSystem actions, EditLimits limits)
    {
        _cameras = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _volumes = world.GetEntities().With<ChunkGrid>().With<Transform>().AsSet();
        _input = input;
        _actions = actions;
        _limits = limits;

        // Face outline entity: the WireframeRenderer component is added/removed to show/hide.
        _faceMesh   = BuildFaceMesh(renderer);
        _faceEntity = world.CreateEntity();
        _faceEntity.Set(Transform.Identity);
    }

    public void Update(float dt)
    {
        TargetBlock  = null;
        TargetNormal = null;
        if (!_input.CursorCaptured || _actions.Interacting || !TryGetCameraRay(out var origin, out var dir)
            || BlockRaycast.Nearest(_volumes, origin, dir, _limits.Reach, drawn: true) is not { } hit)
        {
            HideFace();
            return;
        }
        TargetBlock = hit.Block;
        TargetNormal = hit.Normal;
        ShowFace(hit.Volume, hit.Root.DrawnPose(), hit.Block, hit.Normal); // on the ship as it's drawn
    }

    public string DebugName => "Block target";

    public void DrawDebugUi() =>
        ImGui.Text(TargetBlock is { } b ? $"Target: ({b.X}, {b.Y}, {b.Z})" : "Target: none");

    public void Dispose()
    {
        _faceMesh.Dispose();
        if (_faceEntity.IsAlive) _faceEntity.Dispose();
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

    private void ShowFace(ChunkVolume volume, in Transform root, Vector3D<int> block, Vector3D<int> normal)
    {
        // Corners are in the volume's local space; re-upload only when the cell or volume changes.
        if (!_faceVisible || block != _lastBlock || normal != _lastNormal || !ReferenceEquals(volume, _lastVolume))
        {
            UploadFaceVertices(block, normal);
            _lastBlock  = block;
            _lastNormal = normal;
            _lastVolume = volume;
        }

        // Map the volume-space face into the world: a Transform with the root's rotation whose origin is
        // volume-space (0,0,0).
        ref var t = ref _faceEntity.Get<Transform>();
        t.Rotation = root.Rotation;
        t.Position = volume.VoxelToWorld(root, Vector3D<float>.Zero);

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool TryGetCameraRay(out Vector3D<float> origin, out Vector3D<float> dir)
    {
        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            ref readonly var cc = ref e.Get<CameraComponent>();
            if (!cc.Active) continue;
            var t = e.DrawnPose(); // the view on screen
            origin = t.Position;
            dir    = Vector3D.Normalize(Vec.Rotate(t.Rotation, new Vector3D<float>(0, 0, -1)));
            return true;
        }
        origin = default;
        dir    = default;
        return false;
    }
}
