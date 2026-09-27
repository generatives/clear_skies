using ClearSkies.Engine.Core;
using ClearSkies.Engine.Entities;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Debug overlay (off by default; toggled in its debug panel or with --show-ownership): each grid's bounds (whole
/// chunks, so it's cheap every frame) as a wireframe box tinted by who
/// simulates it (green: this machine; otherwise a colour per peer, the host orange), and around each player two
/// horizontal rings, tinted by their bubble owner: half the player merge distance (two players' inner rings touch
/// when their bubbles merge) and half the split distance.
/// </summary>
public sealed class OwnershipOverlaySystem : IRenderSystem, IDebugUiSystem
{
    private const int RingSegments = 64;

    private static readonly Vector3D<float>[] Palette =
    {
        new(1.0f, 0.6f, 0.1f),  // the host
        new(0.3f, 0.6f, 1.0f),
        new(1.0f, 0.3f, 0.8f),
        new(1.0f, 1.0f, 0.3f),
        new(0.6f, 0.4f, 1.0f),
        new(0.3f, 1.0f, 1.0f),
    };
    private static readonly Vector3D<float> Ours = new(0.3f, 1.0f, 0.3f);

    private readonly Renderer _renderer;
    private readonly Session _session;
    private readonly EntitySet _grids;
    private readonly EntitySet _players;
    private readonly Dictionary<Vector3D<float>, (GpuMesh Box, GpuMesh Ring)> _meshes = new();
    private readonly float _mergeDistance, _splitDistance;

    public OwnershipOverlaySystem(World world, Renderer renderer, Session session, float playerMergeDistance, float playerSplitDistance)
    {
        _renderer = renderer;
        _session = session;
        _mergeDistance = playerMergeDistance;
        _splitDistance = playerSplitDistance;
        _grids = world.GetEntities().With<DynamicGrid>().With<ChunkGrid>().With<Transform>().With<Rendered>().AsSet();
        _players = world.GetEntities().With<Player>().With<Transform>().AsSet();
    }

    public bool Enabled { get; set; }

    public void Render(in RenderContext frame)
    {
        if (!Enabled) return;
        foreach (ref readonly var e in _grids.GetEntities())
        {
            var owner = e.Has<NetOwner>() ? e.Get<NetOwner>() : _session.LocalOwner();
            var volume = e.Get<ChunkGrid>().Volume;
            if (!Bounds(volume, out var min, out var max)) continue;
            ref readonly var t = ref e.Get<Transform>();
            // world = position + rotation·(voxel − pivot), for voxel = min + unit·(max − min).
            var model = Mat4.Multiply(Mat4.Translation(t.Position),
                        Mat4.Multiply(Mat4.FromQuaternion(t.Rotation),
                        Mat4.Multiply(Mat4.Translation(min - volume.Pivot), Mat4.Scale(max - min))));
            _renderer.DrawMeshWireframe(MeshesFor(ColourOf(owner.IsLocal, owner.Owner)).Box, model);
        }
        foreach (ref readonly var e in _players.GetEntities())
        {
            var peer = e.Has<NetOwner>() ? e.Get<NetOwner>().Owner : _session.LocalPeer;
            var bubbleOwner = _session.BubbleOwnerOf(peer);
            var ring = MeshesFor(ColourOf(bubbleOwner == _session.LocalPeer, bubbleOwner)).Ring;
            var at = e.Get<Transform>().Position;
            foreach (float radius in new[] { _mergeDistance / 2, _splitDistance / 2 })
                _renderer.DrawMeshWireframe(ring, Mat4.Multiply(Mat4.Translation(at), Mat4.Scale(new Vector3D<float>(radius, 1, radius))));
        }
    }

    private static Vector3D<float> ColourOf(bool local, PeerId owner) =>
        local ? Ours : Palette[(int)((owner.Value - 1) % (uint)Palette.Length)];

    private static bool Bounds(ChunkVolume volume, out Vector3D<float> min, out Vector3D<float> max)
    {
        bool any = false;
        min = max = default;
        foreach (var (pos, entry) in volume.All)
        {
            if (!entry.Data.HasAnySolid()) continue;
            var lo = new Vector3D<float>(pos.X, pos.Y, pos.Z) * ChunkData.Size;
            var hi = lo + new Vector3D<float>(ChunkData.Size);
            (min, max) = any ? (Vector3D.Min(min, lo), Vector3D.Max(max, hi)) : (lo, hi);
            any = true;
        }
        return any;
    }

    private (GpuMesh Box, GpuMesh Ring) MeshesFor(Vector3D<float> colour)
    {
        if (_meshes.TryGetValue(colour, out var m)) return m;
        var n = Vector3D<float>.UnitY;

        // A unit box, 0..1 on each axis: 8 corners, 12 edges.
        var corners = new Vertex[8];
        for (int i = 0; i < 8; i++) corners[i] = new Vertex(new Vector3D<float>(i & 1, (i >> 1) & 1, (i >> 2) & 1), n, colour);
        uint[] boxEdges = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 };
        var box = _renderer.UploadMesh(corners, new uint[] { 0, 1, 2 }, boxEdges);

        // A unit circle in the horizontal plane.
        var ringVerts = new Vertex[RingSegments];
        var ringEdges = new uint[RingSegments * 2];
        for (int i = 0; i < RingSegments; i++)
        {
            float a = i * MathF.Tau / RingSegments;
            ringVerts[i] = new Vertex(new Vector3D<float>(MathF.Cos(a), 0, MathF.Sin(a)), n, colour);
            ringEdges[i * 2] = (uint)i;
            ringEdges[i * 2 + 1] = (uint)((i + 1) % RingSegments);
        }
        var ring = _renderer.UploadMesh(ringVerts, new uint[] { 0, 1, 2 }, ringEdges);
        return _meshes[colour] = (box, ring);
    }

    public string DebugName => "Ownership overlay";

    public void DrawDebugUi()
    {
        bool enabled = Enabled;
        if (ImGui.Checkbox("Show who simulates each grid, and bubble rings", ref enabled)) Enabled = enabled;
        ImGui.TextDisabled("Green: simulated here. Orange: the host. Other colours: other players.");
    }
}
