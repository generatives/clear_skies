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

    public void DrawDebugUi()
    {
        ImGui.Text($"{_time.FramesPerSecond} fps");
        bool wireframe = _renderer.WireframeMode;
        if (ImGui.Checkbox("Wireframe", ref wireframe))
            _renderer.WireframeMode = wireframe;
    }

    public void Update(float dt)
    {
        if (!TryGetActiveCamera(out var camTransform, out var camera))
        {
            _gui.EndFrame(); // close the ImGui frame EngineHost opened even when nothing else renders
            return;
        }

        var sunDir = SunLight.Direction;

        var uniform = new CameraUniform
        {
            View          = camera.GetView(camTransform),
            Projection    = camera.GetProjection(_renderer.AspectRatio),
            SunDirection  = sunDir,
            SunStrength   = SunLight.Strength,
            LightViewProj = BuildLightViewProj(sunDir, camTransform.Position),
            RayAoStrength  = RayLightingSettings.AoStrength,
            RayBounceScale = RayLightingSettings.BounceScale,
        };

        // Camera uniform carries lightViewProj, which the shadow pass's depth shader reads — write it
        // before the shadow pass, then render all casters from the sun's POV into the shadow map.
        //
        // Every loaded chunk mesh gets a MeshRenderer (see ChunkVolume.SetMesh) — with no culling, at a
        // large view distance that meant drawing every loaded chunk twice a frame (shadow + main pass)
        // regardless of whether it was anywhere near visible, the dominant per-frame cost once generation/
        // meshing/lighting throughput stopped being the bottleneck. Frustum-cull both passes: the shadow
        // pass against the sun's own (much smaller, fixed-radius — see BuildLightViewProj) orthographic
        // volume, the main pass against the camera's. Every chunk mesh is exactly ChunkData.Size local
        // units on a side (GreedyMesher's local space), so the world AABB is just that box transformed by
        // the entity's own model matrix (handles rotation for a dynamic grid's chunks too, not just the
        // static world's axis-aligned ones).
        var lightFrustum = Frustum.FromViewProjection(uniform.LightViewProj);

        _renderer.SetCameraUniform(uniform);
        _renderer.BeginShadowPass();
        foreach (ref readonly Entity e in _meshes.GetEntities())
        {
            ref readonly var t = ref e.Get<Transform>();
            var model = t.ToMatrix();
            if (!ChunkBoundsIntersect(model, lightFrustum)) continue;
            _renderer.DrawShadowMesh(e.Get<MeshRenderer>().Mesh, model);
        }
        _renderer.EndShadowPass();

        if (!_renderer.BeginFrame())
        {
            _gui.EndFrame();
            return;
        }

        _renderer.SetCameraUniform(uniform);

        var camFrustum = Frustum.FromViewProjection(Mat4.Multiply(uniform.Projection, uniform.View));

        foreach (ref readonly Entity e in _meshes.GetEntities())
        {
            ref readonly var t   = ref e.Get<Transform>();
            ref readonly var mr  = ref e.Get<MeshRenderer>();

            var model = t.ToMatrix();
            if (!ChunkBoundsIntersect(model, camFrustum)) continue;

            // Derive chunkBase + volume dims live from the current volume state. This stays correct
            // across volume reallocations (which move every chunk's base and resize the volume)
            // without needing to remesh. Fallback for non-chunk meshes: base 0, size 32 (full-bright).
            nint lbg = 0;
            int cbx = 0, cby = 0, cbz = 0;
            int vsx = ChunkData.Size, vsy = ChunkData.Size, vsz = ChunkData.Size;
            if (mr.VolumeGpu is { } gpu)
            {
                lbg = gpu.RenderBindGroup;
                var (bx, by, bz) = gpu.ChunkVoxelBase(mr.ChunkPos);
                cbx = bx; cby = by; cbz = bz;
                vsx = gpu.VW; vsy = gpu.VH; vsz = gpu.VD;
            }

            _renderer.DrawMesh(mr.Mesh, model, lbg, cbx, cby, cbz, vsx, vsy, vsz);
        }

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

    // Orthographic light-space matrix for the directional sun, framing a box around the camera so the
    // shadow map covers the loaded region at high texel density. ~160-unit radius ≈ the load region plus
    // margin; 2048² map → ~6 texels/voxel.
    private static Mat4 BuildLightViewProj(Vector3D<float> sunDir, Vector3D<float> cameraPos)
    {
        const float radius = 160f;
        var eye = cameraPos - sunDir * radius;
        var up  = MathF.Abs(sunDir.Y) > 0.99f ? new Vector3D<float>(0, 0, 1) : new Vector3D<float>(0, 1, 0);
        var view = Mat4.LookAtRh(eye, cameraPos, up);
        var proj = Mat4.OrthoRhZo(-radius, radius, -radius, radius, 0.1f, 2f * radius);
        var lvp  = Mat4.Multiply(proj, view);

        // Texel-snap the light frustum: the eye tracks the camera, so without snapping the projected world
        // slides sub-texel every frame and shadow edges shimmer/crawl even on a static scene. Project the world
        // origin into light clip space, quantise its XY to whole shadow-map texels, and fold the correction back
        // into the clip-space translation so the world→texel mapping only ever moves in whole-texel steps.
        const float mapSize = SunShadowPass.MapSize;
        float ox = lvp.M12, oy = lvp.M13;            // origin (0,0,0) → clip-space xy (ortho, w = 1)
        float halfTexels = mapSize * 0.5f;           // clip [-1,1] spans mapSize texels
        float dx = (MathF.Round(ox * halfTexels) - ox * halfTexels) / halfTexels;
        float dy = (MathF.Round(oy * halfTexels) - oy * halfTexels) / halfTexels;
        lvp.M12 += dx;
        lvp.M13 += dy;
        return lvp;
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
