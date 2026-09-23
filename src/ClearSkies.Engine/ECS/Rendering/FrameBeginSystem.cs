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
/// Opens the frame (<see cref="SystemStage.BeginRender"/>): builds the camera uniform from the active camera, opens
/// the render pass and fills in the shared <see cref="RenderFrame"/> the later render stages draw with. Leaves the
/// frame closed when there's no active camera or no swapchain image, and every render-stage system then skips it.
/// </summary>
public sealed class FrameBeginSystem : ISystem, IDebugUiSystem
{
    private readonly RenderFrame _frame;
    private readonly EntitySet _cameras;
    private readonly Renderer _renderer;
    private readonly Time _time;

    public FrameBeginSystem(RenderFrame frame, World world, Renderer renderer, Time time)
    {
        _frame    = frame;
        _renderer = renderer;
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
        _frame.IsOpen = false;
        if (!TryGetActiveCamera(out var camTransform, out var camera))
            return;

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
            return;

        _renderer.SetCameraUniform(uniform);

        _frame.Context = new RenderContext(camTransform.Position, uniform.View, uniform.Projection,
                                           Frustum.FromViewProjection(Mat4.Multiply(uniform.Projection, uniform.View)),
                                           _time.TotalSeconds);
        _frame.IsOpen = true;
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
