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
/// The frame the render stages draw into, owned by <see cref="EngineHost"/> (<see cref="EngineHost.Frame"/>). Each
/// render tick the host calls <see cref="TryBegin"/> — which builds the camera uniform from the active camera and
/// opens the render pass — runs the render stages only if that succeeded, passing each <see cref="IRenderSystem"/>
/// the frame's <see cref="Context"/>, then calls <see cref="End"/>.
/// </summary>
public sealed class RenderFrame : IDebugUiSystem
{
    private readonly EntitySet _cameras;
    private readonly Renderer _renderer;
    private readonly ImGuiController _gui;
    private readonly Time _time;
    private bool _open;

    /// <summary>This frame's camera and time, handed to every render system. Valid after a successful
    /// <see cref="TryBegin"/>.</summary>
    internal RenderContext Context { get; private set; }

    internal RenderFrame(World world, Renderer renderer, ImGuiController gui, Time time)
    {
        _renderer = renderer;
        _gui      = gui;
        _time     = time;
        _cameras  = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
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
        ImGui.SliderFloat("Fog band (blocks)", ref SkySettings.FogBand, 0f, 512f);
        ImGui.TextDisabled($"Fades in over this many blocks before the loaded distance ({SkySettings.FogDistance:F0} blocks).");
        ImGui.ColorEdit3("Zenith", ref SkySettings.ZenithColor);
        ImGui.ColorEdit3("Horizon / fog", ref SkySettings.HorizonColor);
        ImGui.Checkbox("Clouds", ref SkySettings.CloudsEnabled);
        ImGui.SliderFloat("Cloud coverage, open sky", ref SkySettings.CloudCoverageOpen, 0f, 1f);
        ImGui.SliderFloat("Cloud coverage, near islands", ref SkySettings.CloudCoverageIslands, 0f, 1f);
        ImGui.SliderFloat("Cloud altitude (middle layer)", ref SkySettings.CloudAltitude, 0f, 800f);
        ImGui.SliderFloat("Wind speed (blocks/s)", ref SkySettings.WindSpeed, 0f, 30f);
    }

    private static Vector3D<float> ToVector3D(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Opens the frame, or returns false to skip it: there's no active camera, or no swapchain image (e.g.
    /// the window is minimized, or the surface was just resized and is being reconfigured).</summary>
    internal bool TryBegin()
    {
        if (!TryGetActiveCamera(out var camTransform, out var camera))
            return false;

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
            uniform.FogHorizontalEnd   = SkySettings.FogDistance;
            uniform.FogHorizontalStart = System.Math.Max(0f, SkySettings.FogDistance - SkySettings.FogBand);
            // Streaming loads whole chunk columns, so the loaded world has no top or bottom edge to hide.
            uniform.FogVerticalStart   = 1e8f;
            uniform.FogVerticalEnd     = 2e8f;
        }
        else
        {
            // Past the far plane: never reached, so nothing fogs.
            uniform.FogHorizontalStart = uniform.FogVerticalStart = 1e8f;
            uniform.FogHorizontalEnd   = uniform.FogVerticalEnd   = 2e8f;
        }

        if (!_renderer.BeginFrame())
            return false;

        _renderer.SetCameraUniform(uniform);

        Context = new RenderContext(camTransform.Position, uniform.View, uniform.Projection,
                                    Frustum.FromViewProjection(Mat4.Multiply(uniform.Projection, uniform.View)),
                                    _time.TotalSeconds);
        _open = true;
        return true;
    }

    /// <summary>Closes the frame: ImGui on top of everything, then submit and present. Called every render tick,
    /// skipped frames included, since ImGui's frame (opened in the Input stage) must be closed either way.</summary>
    internal void End()
    {
        _gui.EndFrame();
        if (!_open) return;
        _renderer.EndFrame();
        _open = false;
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
