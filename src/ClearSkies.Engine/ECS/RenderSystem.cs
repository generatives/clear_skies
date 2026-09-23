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

/// <summary>
/// Owns the frame: builds the camera uniform, opens the render pass, runs every registered
/// <see cref="IWorldRenderPass"/> (e.g. <see cref="ChunkRenderSystem"/>) and draws the standalone
/// <see cref="ModelRenderer"/> entities, then clouds, sky, wireframe overlays, HUD and ImGui.
/// </summary>
public sealed class RenderSystem : ISystem, IDebugUiSystem
{
    private readonly EntitySet _cameras;
    private readonly EntitySet _wireframes;
    private readonly EntitySet _models;
    private readonly EntitySet _huds;
    private readonly Renderer _renderer;
    private readonly ImGuiController _gui;
    private readonly Time _time;
    private readonly CloudLayer _clouds;

    public RenderSystem(World world, Renderer renderer, ImGuiController gui, Time time)
    {
        _renderer   = renderer;
        _gui        = gui;
        _time       = time;
        _clouds     = new CloudLayer(renderer);
        _cameras    = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _wireframes = world.GetEntities().With<Transform>().With<WireframeRenderer>().AsSet();
        _models     = world.GetEntities().With<Transform>().With<ModelRenderer>().AsSet();
        _huds       = world.GetEntities().With<HudRenderer>().AsSet();
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Renderer";
    private bool _referenceLighting;

    private readonly List<IWorldRenderPass> _worldPasses = new();

    /// <summary>Adds a pass drawn each frame after the camera is set up and before clouds and sky, in the order
    /// added.</summary>
    public void AddWorldPass(IWorldRenderPass pass) => _worldPasses.Add(pass);

    public void DrawDebugUi()
    {
        ImGui.Text($"{_time.FramesPerSecond} fps");
        ImGui.Text($"Draw calls: {_renderer.DrawCount:N0}");
        ImGui.Text($"Swapchain acquire wait: {_renderer.AcquireMs:F2} ms, present: {_renderer.PresentMs:F2} ms");
        ImGui.TextDisabled("A large acquire/present wait means the frame is waiting on the GPU (vsync is on).");
        bool wireframe = _renderer.WireframeMode;
        if (ImGui.Checkbox("Wireframe", ref wireframe))
            _renderer.WireframeMode = wireframe;
        ImGui.Checkbox("Reference (slow) light + AO shader path", ref _referenceLighting);

        ImGui.SeparatorText("Sky & fog");
        ImGui.Checkbox("Distance fog", ref SkySettings.FogEnabled);
        ImGui.SliderFloat("Fog start (horizontal)", ref SkySettings.FogStartFraction, 0f, 0.95f);
        ImGui.SliderFloat("Fog start (vertical)", ref SkySettings.VerticalFogStartFraction, 0f, 0.95f);
        ImGui.TextDisabled($"Fraction of the loaded distance ({SkySettings.LoadedHorizontal:F0} blocks across, " +
                           $"{SkySettings.LoadedVertical:F0} up/down); fog is total at the edge.");
        ImGui.ColorEdit3("Zenith", ref SkySettings.ZenithColor);
        ImGui.ColorEdit3("Horizon / fog", ref SkySettings.HorizonColor);
        ImGui.Checkbox("Clouds", ref SkySettings.CloudsEnabled);
        ImGui.SliderFloat("Cloud coverage", ref SkySettings.CloudCoverage, 0f, 1f);
        ImGui.SliderFloat("Cloud altitude", ref SkySettings.CloudAltitude, 0f, 600f);
        ImGui.SliderFloat("Wind speed (blocks/s)", ref SkySettings.WindSpeed, 0f, 30f);
    }

    private static Vector3D<float> ToVector3D(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

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
            CameraPosition = camTransform.Position,
            ZenithColor    = ToVector3D(SkySettings.ZenithColor),
            HorizonColor   = ToVector3D(SkySettings.HorizonColor),
            CloudFogStart  = CloudLayer.FogStart,
            CloudFogEnd    = CloudLayer.FogEnd,
        };
        if (SkySettings.FogEnabled)
        {
            uniform.FogHorizontalEnd   = SkySettings.LoadedHorizontal;
            uniform.FogHorizontalStart = SkySettings.LoadedHorizontal * SkySettings.FogStartFraction;
            uniform.FogVerticalEnd     = SkySettings.LoadedVertical;
            uniform.FogVerticalStart   = SkySettings.LoadedVertical * SkySettings.VerticalFogStartFraction;
        }
        else
        {
            // Past the far plane: never reached, so nothing fogs.
            uniform.FogHorizontalStart = uniform.FogVerticalStart = 1e8f;
            uniform.FogHorizontalEnd   = uniform.FogVerticalEnd   = 2e8f;
        }

        if (!_renderer.BeginFrame())
        {
            _gui.EndFrame();
            return;
        }

        _renderer.SetCameraUniform(uniform);

        var frame = new WorldRenderContext(camTransform.Position,
                                           Frustum.FromViewProjection(Mat4.Multiply(uniform.Projection, uniform.View)));
        foreach (var pass in _worldPasses)
            pass.Draw(frame);

        foreach (ref readonly Entity e in _models.GetEntities())
        {
            ref readonly var mr = ref e.Get<ModelRenderer>();
            var model = e.Get<Transform>().ToMatrix();
            if (!frame.Frustum.Intersects(model, mr.Model.BoundsMin, mr.Model.BoundsMax)) continue;
            _renderer.DrawModel(mr.Model, model);
        }

        if (SkySettings.CloudsEnabled) _clouds.Draw(camTransform.Position, _time.TotalSeconds);

        // Sky after the world and clouds, so it only shades the pixels they left uncovered.
        _renderer.DrawSky();

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
