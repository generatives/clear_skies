using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Input;
using ClearSkies.Engine.Math;
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
/// on whichever volume (static world or dynamic grid) the camera is aimed at. Left-clicking an
/// <see cref="Interactive"/> block uses it instead of placing against it: <see cref="BlockInteraction"/>s are
/// published for it until the button is released. The targeted face is highlighted and a crosshair is always
/// shown at the screen centre.
/// </summary>
public sealed class PlayerInputSystem : ISystem, IDisposable, IDebugUiSystem
{
    private const float ReachBlocks = 128f;

    private readonly World        _world;
    private readonly EntitySet    _cameras;
    private readonly EntitySet    _volumes;
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

    // The Interactive block being used, from the click on it until the button is released (see BlockInteraction),
    // and the last ray sent for it.
    private bool            _interacting;
    private Entity          _interactBlock;
    private Vector3D<float> _interactOrigin, _interactDir;

    public Vector3D<int>? TargetBlock  { get; private set; }
    public Vector3D<int>? TargetNormal { get; private set; }

    // Block placed by left-click on an air cell. Cycle with L, or pick directly from the "Place block"
    // dropdown in DrawDebugUi — both keep _placeIndex/_placeBlock in sync.
    private static readonly BlockId[] PlaceableBlocks =
        { BlockId.Stone, BlockId.Wood, BlockId.Grass, BlockId.Dirt, BlockId.Lamp, BlockId.RedLamp, BlockId.GreenLamp,
          BlockId.BlueLamp, BlockId.Fan, BlockId.Buoyant, BlockId.Lever };
    private static readonly string[] PlaceableNames =
        Array.ConvertAll(PlaceableBlocks, id => BlockRegistry.Get(id).Name);

    private int _placeIndex = 0; // index into PlaceableBlocks
    private BlockId _placeBlock = PlaceableBlocks[0];
    private int _blockBrushRadius = 0;

    public PlayerInputSystem(World world, InputManager input, ChunkMeshSystem meshSystem, Renderer renderer,
                              GridSelection selection)
    {
        _world       = world;
        _cameras     = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _volumes     = world.GetEntities().With<ChunkGrid>().With<Transform>().AsSet();
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
        ImGui.SliderInt("Brush Size", ref _blockBrushRadius, 0, 32);
        ImGui.TextDisabled("(or press L to cycle)");
    }

    // ── Block spawning ───────────────────────────────────────────────────────

    private void UpdateBlockSpawning()
    {
        if (!_input.WasKeyPressed(Key.G) || !CameraUtil.TryGetActive(_cameras, out var t))
            return;

        var spawn = CameraUtil.SpawnPointInFrontOf(t);
        DynamicGridFactory.SpawnSingleBlock(_world, _selection, new PhysVec(spawn.X, spawn.Y, spawn.Z), BlockId.Stone);
        Console.WriteLine($"[spawn] grid at ({spawn.X:0.0},{spawn.Y:0.0},{spawn.Z:0.0})");
    }

    // ── Block editing ────────────────────────────────────────────────────────

    private void UpdateBlockEditing()
    {
        TargetBlock  = null;
        TargetNormal = null;

        if (!_input.CursorCaptured || !TryGetCameraRay(out var origin, out var dir))
        {
            EndInteraction();
            HideFace();
            return;
        }

        // Using an Interactive block: it has the left button until that comes up, following the ray wherever it
        // points (even off the block, so a drag can overshoot), with no targeting or editing meanwhile.
        if (_interacting)
        {
            if (_interactBlock.IsAlive && _input.IsMouseButtonDown(MouseButton.Left))
            {
                PublishInteraction(InteractionPhase.Held, origin, dir);
                HideFace();
                return;
            }
            EndInteraction();
        }

        // Find the nearest hit across every volume (the static world and each dynamic grid), casting the ray in
        // each volume's own space. Rotation preserves length, so hit distances compare directly.
        float        bestDist   = float.MaxValue;
        ChunkVolume? bestVolume = null;
        Entity       bestEntity = default;
        Vector3D<int> bestBlock  = default, bestNormal = default;
        Vector3D<float> bestEye  = default; // the camera in the hit volume's voxel space

        foreach (ref readonly Entity e in _volumes.GetEntities())
        {
            var volume = e.Get<ChunkGrid>().Volume;
            ref readonly var root = ref e.Get<Transform>();
            var lo = volume.WorldToVoxel(root, origin);
            var ld = Vec.Rotate(Vec.Conjugate(root.Rotation), dir);

            if (VoxelRaycaster.Cast(volume, lo, ld, ReachBlocks, out var b, out var n, out var d) && d < bestDist)
            {
                bestDist = d; bestVolume = volume; bestBlock = b; bestNormal = n; bestEntity = e; bestEye = lo;
            }
        }

        if (bestVolume is null)
        {
            HideFace();
            return;
        }

        bool bestIsDynamicGrid = bestEntity.Has<DynamicGrid>();
        TargetBlock  = bestBlock;
        TargetNormal = bestNormal;
        ShowFace(bestVolume, bestEntity.Get<Transform>(), bestBlock, bestNormal);

        if (_input.WasKeyPressed(Key.L))
        {
            _placeIndex = (_placeIndex + 1) % PlaceableBlocks.Length;
            _placeBlock = PlaceableBlocks[_placeIndex];
            Console.WriteLine($"[place] selected block: {_placeBlock}");
        }

        if (_input.WasMouseButtonPressed(MouseButton.Left)
            && bestVolume.TryGetBlockEntity(bestBlock.X, bestBlock.Y, bestBlock.Z, out var block) && block.Has<Interactive>())
        {
            _interacting   = true;
            _interactBlock = block;
            PublishInteraction(InteractionPhase.Began, origin, dir);
        }
        else if (_input.WasMouseButtonPressed(MouseButton.Left))
        {
            var t = bestBlock + bestNormal;
            if (bestVolume.GetBlock(t.X, t.Y, t.Z) == BlockId.Air)
            {
                // Bottom on the face it was placed against: its top points away from that face (bestNormal is
                // already exactly one of the 6 axis directions), so e.g. a Fan placed against a ship's east wall
                // faces east, away from the ship. Then its north face turns towards the player as far as it can
                // while keeping that: onto whichever axis across the face is nearest the direction to the camera.
                var towards = bestEye - (new Vector3D<float>(t.X, t.Y, t.Z) + new Vector3D<float>(0.5f));
                var orientation = BlockOrientation.Placed(DirectionExtensions.FromNormal(bestNormal), towards);
                for (int x = t.X - _blockBrushRadius; x <= t.X + _blockBrushRadius; x++)
                {
                    for (int y = t.Y - _blockBrushRadius; y <= t.Y + _blockBrushRadius; y++)
                    {
                        for (int z = t.Z - _blockBrushRadius; z <= t.Z + _blockBrushRadius; z++)
                        {
                            bestVolume.SetBlock(x, y, z, _placeBlock, orientation);
                        }
                    }
                }
                if (bestIsDynamicGrid) _selection.Select(bestEntity);
                Console.WriteLine($"[place] {_placeBlock} in {(bestIsDynamicGrid ? "grid" : "world")} ({t.X},{t.Y},{t.Z})");
            }
        }
        else if (_input.WasMouseButtonPressed(MouseButton.Right))
        {
            for (int x = bestBlock.X - _blockBrushRadius; x <= bestBlock.X + _blockBrushRadius; x++)
            {
                for (int y = bestBlock.Y - _blockBrushRadius; y <= bestBlock.Y + _blockBrushRadius; y++)
                {
                    for (int z = bestBlock.Z - _blockBrushRadius; z <= bestBlock.Z + _blockBrushRadius; z++)
                    {
                        bestVolume.SetBlock(x, y, z, BlockId.Air);
                    }
                }
            }
            Console.WriteLine($"[break] {(bestIsDynamicGrid ? "grid" : "world")} ({bestBlock.X},{bestBlock.Y},{bestBlock.Z})");

            if (bestIsDynamicGrid)
            {
                if (bestVolume.IsEmpty())
                {
                    Hierarchy.DestroyRecursive(bestVolume.Root); // its chunks with it
                    HideFace(); // the outlined face no longer has a volume behind it
                }
                else
                {
                    _selection.Select(bestEntity);
                }
            }
        }
    }

    // ── Interaction ──────────────────────────────────────────────────────────

    private void PublishInteraction(InteractionPhase phase, Vector3D<float> origin, Vector3D<float> dir)
    {
        _interactOrigin = origin;
        _interactDir    = dir;
        _world.Publish(new BlockInteraction(_interactBlock, phase, origin, dir));
    }

    /// <summary>Ends the current interaction, if any, telling its block with the last ray it was sent.</summary>
    private void EndInteraction()
    {
        if (!_interacting) return;
        _interacting = false;
        PublishInteraction(InteractionPhase.Ended, _interactOrigin, _interactDir);
        _interactBlock = default;
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
