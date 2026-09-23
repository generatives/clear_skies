using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Owns the frame: builds the camera uniform, opens the render pass, then runs each <see cref="RenderPass"/> in order
/// — its setup, then every <see cref="IRenderSystem"/> added to it, in the order added — and closes with ImGui.
/// Holds no drawing of its own: chunks, models, clouds, sky, overlays and HUD are all render systems (see
/// <see cref="Add"/>).
/// </summary>
public sealed class RenderSystem : ISystem, IDebugUiSystem, IDisposable
{
    private static readonly RenderPass[] PassOrder = Enum.GetValues<RenderPass>();

    private readonly EntitySet _cameras;
    private readonly Renderer _renderer;
    private readonly ImGuiController _gui;
    private readonly Time _time;
    private readonly List<IRenderSystem>[] _passes = new List<IRenderSystem>[PassOrder.Length];

    public RenderSystem(World world, Renderer renderer, ImGuiController gui, Time time)
    {
        _renderer = renderer;
        _gui      = gui;
        _time     = time;
        _cameras  = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        for (int i = 0; i < _passes.Length; i++) _passes[i] = new List<IRenderSystem>();
    }

    /// <summary>Adds <paramref name="system"/> to <paramref name="pass"/>, drawn after the systems already in it.
    /// A system that implements <see cref="IDebugUiSystem"/> gets its panel registered too.</summary>
    public RenderSystem Add(RenderPass pass, IRenderSystem system)
    {
        _passes[(int)pass].Add(system);
        if (system is IDebugUiSystem debugUi) _gui.RegisterDebugUi(debugUi);
        return this;
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Renderer";
    private bool _referenceLighting;

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

        var frame = new RenderContext(camTransform.Position, uniform.View, uniform.Projection,
                                      Frustum.FromViewProjection(Mat4.Multiply(uniform.Projection, uniform.View)),
                                      _time.TotalSeconds);
        foreach (var pass in PassOrder)
        {
            BeginPass(pass);
            foreach (var system in _passes[(int)pass])
                system.Render(frame);
        }

        // ImGui draws last, on top of everything, in the same pass.
        _gui.EndFrame();
        _renderer.EndFrame();
    }

    /// <summary>Per-pass state its systems start from. World, Sky and Overlay share the world camera and pipeline
    /// BeginFrame bound (each Renderer draw restores it after switching); Hud switches to the HUD pipeline and
    /// identity camera for the rest of the frame.</summary>
    private void BeginPass(RenderPass pass)
    {
        if (pass == RenderPass.Hud) _renderer.BeginHudPass();
    }

    public void Dispose()
    {
        foreach (var list in _passes)
            foreach (var system in list)
                (system as IDisposable)?.Dispose();
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
