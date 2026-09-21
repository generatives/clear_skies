using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>Builds the camera uniform and issues a draw call per <see cref="MeshRenderer"/> entity.</summary>
public sealed class RenderSystem : ISystem, IDebugUiSystem
{
    private readonly EntitySet _cameras;
    private readonly EntitySet _meshes;
    private readonly EntitySet _wireframes;
    private readonly EntitySet _huds;
    private readonly Renderer _renderer;
    private readonly ImGuiController _gui;
    private readonly Time _time;

    public RenderSystem(World world, Renderer renderer, ImGuiController gui, Time time)
    {
        _renderer   = renderer;
        _gui        = gui;
        _time       = time;
        _cameras    = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _meshes     = world.GetEntities().With<Transform>().With<MeshRenderer>().AsSet();
        _wireframes = world.GetEntities().With<Transform>().With<WireframeRenderer>().AsSet();
        _huds       = world.GetEntities().With<HudRenderer>().AsSet();
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Renderer";

    // A/B switches for the chunk pass's performance work (see Update and the shader's shadeFast).
    private bool _sortFrontToBack = true;
    private bool _referenceLighting;

    // One visible chunk draw, collected so they can be issued nearest first.
    private readonly record struct ChunkDraw(float DistSq, GpuMesh Mesh, Mat4 Model, int Grid, ChunkPosition Chunk);
    private readonly List<ChunkDraw> _draws = new();
    private static readonly Comparison<ChunkDraw> NearestFirst = (a, b) => a.DistSq.CompareTo(b.DistSq);

    public void DrawDebugUi()
    {
        ImGui.Text($"{_time.FramesPerSecond} fps");
        ImGui.Text($"Draw calls: {_renderer.DrawCount:N0} ({_draws.Count:N0} chunks visible of {_meshes.Count:N0})");
        ImGui.Text($"Swapchain acquire wait: {_renderer.AcquireMs:F2} ms, present: {_renderer.PresentMs:F2} ms");
        ImGui.TextDisabled("A large acquire/present wait means the frame is waiting on the GPU (vsync is on).");
        bool wireframe = _renderer.WireframeMode;
        if (ImGui.Checkbox("Wireframe", ref wireframe))
            _renderer.WireframeMode = wireframe;
        ImGui.Checkbox("Draw chunks front to back", ref _sortFrontToBack);
        ImGui.Checkbox("Reference (slow) light + AO shader path", ref _referenceLighting);
    }

    public void Update(float dt)
    {
        if (!TryGetActiveCamera(out var camTransform, out var camera))
        {
            _gui.EndFrame(); // close the ImGui frame EngineHost opened even when nothing else renders
            return;
        }

        var uniform = new CameraUniform
        {
            View           = camera.GetView(camTransform),
            Projection     = camera.GetProjection(_renderer.AspectRatio),
            SunDirection   = SunLight.Direction,
            SunStrength    = SunLight.Strength,
            RayAoStrength  = RayLightingSettings.AoStrength,
            Ambient        = RayLightingSettings.Ambient,
            ReferenceLighting = _referenceLighting ? 1f : 0f,
        };

        if (!_renderer.BeginFrame())
        {
            _gui.EndFrame();
            return;
        }

        _renderer.SetCameraUniform(uniform);

        // Every loaded chunk mesh gets a MeshRenderer (see ChunkVolume.SetMesh); frustum-cull them. Every chunk
        // mesh is exactly ChunkData.Size local units on a side (GreedyMesher's local space), so the world AABB is
        // just that box transformed by the entity's own model matrix (handles a dynamic grid's rotation too).
        var camFrustum = Frustum.FromViewProjection(Mat4.Multiply(uniform.Projection, uniform.View));

        // Drawn nearest first, so the depth test rejects hidden fragments before the (expensive) lighting shader
        // runs on them instead of shading them and overwriting them later.
        _draws.Clear();
        var half = new Vector3D<float>(ChunkData.Size * 0.5f);
        foreach (ref readonly Entity e in _meshes.GetEntities())
        {
            ref readonly var t   = ref e.Get<Transform>();
            ref readonly var mr  = ref e.Get<MeshRenderer>();

            var model = t.ToMatrix();
            if (!ChunkBoundsIntersect(model, camFrustum)) continue;

            float distSq = Vector3D.DistanceSquared(model.TransformPoint(half), camTransform.Position);
            _draws.Add(new ChunkDraw(distSq, mr.Mesh, model, mr.Grid?.Index ?? -1, mr.ChunkPos));
        }
        if (_sortFrontToBack) _draws.Sort(NearestFirst);
        foreach (var d in _draws)
            _renderer.DrawMesh(d.Mesh, d.Model, d.Grid, d.Chunk);

        // Wireframe overlays drawn on top (pipeline switches mid-pass then restores).
        foreach (ref readonly Entity e in _wireframes.GetEntities())
        {
            ref readonly var t  = ref e.Get<Transform>();
            ref readonly var wr = ref e.Get<WireframeRenderer>();
            _renderer.DrawMeshWireframe(wr.Mesh, t.ToMatrix());
        }

        // HUD elements: screen-space NDC vertices, depth always passes.
        _renderer.BeginHudPass();
        foreach (ref readonly Entity e in _huds.GetEntities())
        {
            ref readonly var hr = ref e.Get<HudRenderer>();
            _renderer.DrawHudMesh(hr.Mesh, Mat4.Identity);
        }

        // ImGui draws last, on top of everything, in the same pass.
        _gui.EndFrame();
        _renderer.EndFrame();
    }

    /// <summary>True if the chunk-sized ([0,ChunkData.Size] local space, per GreedyMesher) box placed by
    /// <paramref name="model"/> intersects <paramref name="frustum"/>. Transforms all 8 local corners rather
    /// than assuming axis-alignment, since a dynamic grid's chunks are rotated (the static world's aren't,
    /// but there's no cheap way to tell which case this is from the matrix alone, and 8 corner transforms
    /// per chunk per frame is negligible next to the draw call it decides whether to skip).</summary>
    private static bool ChunkBoundsIntersect(in Mat4 model, in Frustum frustum)
    {
        const float S = ChunkData.Size;
        Vector3D<float> min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var local = new Vector3D<float>((i & 1) != 0 ? S : 0f, (i & 2) != 0 ? S : 0f, (i & 4) != 0 ? S : 0f);
            var world = model.TransformPoint(local);
            min = Vector3D.Min(min, world);
            max = Vector3D.Max(max, world);
        }
        return frustum.Intersects(min, max);
    }

    private bool TryGetActiveCamera(out Transform transform, out Camera camera)
    {
        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            ref readonly var cc = ref e.Get<CameraComponent>();
            if (cc.Active)
            {
                transform = e.Get<Transform>();
                camera = cc.Camera;
                return true;
            }
        }
        transform = default;
        camera = null!;
        return false;
    }
}
