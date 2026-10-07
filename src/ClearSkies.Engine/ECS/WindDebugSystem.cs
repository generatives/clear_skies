using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Weather;
using DefaultEcs;
using ImGuiNET;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// The "Wind" debug window, and wind arrows drawn over the view while testing: once a frame, a grid of arrows around the
/// camera, each pointing the way the wind blows there, as long as its speed times <see cref="_arrowScale"/>, coloured
/// on a red scale from pale pink (calm) to full red (fast). The window reads out the wind at the camera and what makes it up
/// (gust, dead zone, terrain), tunes the wind's settings live, and can override the wind with a fixed value everywhere.
/// The override is this machine's alone: it changes the wind for whatever this machine simulates.
/// </summary>
public sealed class WindDebugSystem : ISystem, IDebugUiSystem
{
    private readonly WindField _wind;
    private readonly EntitySet _cameras;

    private bool _showArrows;
    private float _arrowSpacing = 16f;  // m between arrows
    private int _arrowRadius = 6;       // arrows each way from the camera, sideways
    private int _arrowLayers = 2;       // layers each way from the camera, up and down
    private float _arrowScale = 2f;     // m of arrow per m/s

    private bool _override;
    private float _overrideSpeed = 5f, _overrideHeading, _overrideVertical;

    private Vector3 _cameraPosition;
    private bool _haveCamera;

    public WindDebugSystem(World world, WindField wind)
    {
        _wind = wind;
        _cameras = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
    }

    public string DebugName => "Wind";

    public void Update(float dt)
    {
        _haveCamera = false;
        foreach (ref readonly Entity e in _cameras.GetEntities())
        {
            ref readonly var cc = ref e.Get<CameraComponent>();
            if (!cc.Active) continue;
            var t = e.DrawnPose();
            _cameraPosition = PhysicsConv.ToBepu(t.Position);
            _haveCamera = true;
            if (_showArrows) DrawArrows(_cameraPosition, PhysicsConv.ToBepu(t.Rotation), cc.Camera.FovRadians, cc.Camera.NearPlane);
            break;
        }
    }

    private void DrawArrows(Vector3 eye, Quaternion rotation, float fov, float near)
    {
        var size = ImGui.GetIO().DisplaySize;
        if (size.X <= 0 || size.Y <= 0) return;
        var toView = Quaternion.Conjugate(rotation);
        float tanHalf = MathF.Tan(fov / 2);
        float aspect = size.X / size.Y;

        bool Project(Vector3 p, out Vector2 screen)
        {
            var v = Vector3.Transform(p - eye, toView);
            screen = default;
            if (v.Z > -near) return false; // behind the camera
            float x = v.X / (-v.Z * tanHalf * aspect), y = v.Y / (-v.Z * tanHalf);
            screen = new Vector2((x + 1) / 2 * size.X, (1 - y) / 2 * size.Y);
            return true;
        }

        var list = ImGui.GetBackgroundDrawList();
        var origin = new Vector3(MathF.Floor(eye.X / _arrowSpacing), MathF.Floor(eye.Y / _arrowSpacing),
                                 MathF.Floor(eye.Z / _arrowSpacing)) * _arrowSpacing;
        for (int dy = -_arrowLayers; dy <= _arrowLayers; dy++)
        for (int dz = -_arrowRadius; dz <= _arrowRadius; dz++)
        for (int dx = -_arrowRadius; dx <= _arrowRadius; dx++)
        {
            var p = origin + new Vector3(dx, dy, dz) * _arrowSpacing;
            var w = _wind.Sample(p);
            float speed = w.Length();
            if (speed < 0.05f) continue;
            if (!Project(p, out var a) || !Project(p + w * _arrowScale, out var b)) continue;
            uint colour = ImGui.GetColorU32(SpeedColour(speed));
            list.AddLine(a, b, colour, 2f);
            var d = b - a;
            float len = d.Length();
            if (len < 2f) continue;
            d /= len;
            var side = new Vector2(-d.Y, d.X);
            float head = MathF.Min(8f, len * 0.4f);
            list.AddLine(b, b - d * head + side * head * 0.5f, colour, 2f);
            list.AddLine(b, b - d * head - side * head * 0.5f, colour, 2f);
        }
    }

    // One red scale, so the arrows stand out against the sky: pale pink at calm, deepening to full red from 8 m/s.
    private static Vector4 SpeedColour(float speed)
    {
        float t = System.Math.Clamp(speed / 8f, 0f, 1f);
        return Vector4.Lerp(new Vector4(1f, 0.8f, 0.8f, 0.9f), new Vector4(0.9f, 0f, 0.05f, 0.9f), t);
    }

    public void DrawDebugUi()
    {
        if (_haveCamera)
        {
            var w = _wind.Sample(_cameraPosition);
            ImGui.Text($"At the camera: {w.Length():0.0} m/s  ({w.X:0.0}, {w.Y:0.0}, {w.Z:0.0})");
            float heading = (MathF.Atan2(w.X, -w.Z) * 180f / MathF.PI + 360f) % 360f;
            ImGui.Text($"Blowing towards {heading:0}° (0° = -Z), vertical {w.Y:0.0} m/s");
            if (_wind.Override is null)
            {
                var c = new ChunkPosition((int)MathF.Floor(_cameraPosition.X / ChunkData.Size),
                                          (int)MathF.Floor(_cameraPosition.Y / ChunkData.Size),
                                          (int)MathF.Floor(_cameraPosition.Z / ChunkData.Size));
                var parts = _wind.PartsAt(c);
                ImGui.Text($"Gust ×{parts.Gust:0.00}   Calm mask {parts.Calm:0.00}");
                ImGui.Text($"Terrain: wind ×{parts.Terrain:0.00} (terrain amount {parts.TerrainAmount:0.00}; 1 = a plain's surface)");
            }
        }
        ImGui.Text($"Wind time: {_wind.Time:0.0} s");

        ImGui.Separator();
        ImGui.Checkbox("Show wind arrows", ref _showArrows);
        ImGui.SliderFloat("Arrow spacing (m)", ref _arrowSpacing, 4f, 64f);
        ImGui.SliderInt("Arrows each way", ref _arrowRadius, 1, 16);
        ImGui.SliderInt("Layers up and down", ref _arrowLayers, 0, 6);
        ImGui.SliderFloat("Arrow length per m/s", ref _arrowScale, 0.25f, 10f);

        ImGui.Separator();
        bool changed = ImGui.Checkbox("Override wind (this machine only)", ref _override);
        ImGui.BeginDisabled(!_override);
        changed |= ImGui.SliderFloat("Speed (m/s)", ref _overrideSpeed, 0f, 40f);
        changed |= ImGui.SliderFloat("Heading (° from -Z, towards +X)", ref _overrideHeading, 0f, 360f);
        changed |= ImGui.SliderFloat("Vertical (m/s)", ref _overrideVertical, -10f, 10f);
        ImGui.EndDisabled();
        if (changed)
        {
            float h = _overrideHeading * MathF.PI / 180f;
            _wind.Override = _override
                ? new Vector3(MathF.Sin(h) * _overrideSpeed, _overrideVertical, -MathF.Cos(h) * _overrideSpeed)
                : null;
        }

        ImGui.Separator();
        if (ImGui.CollapsingHeader("Wind settings"))
        {
            var s = _wind.Settings;
            bool edited = false;
            edited |= ImGui.SliderFloat("Base speed", ref s.BaseSpeed, 0f, 20f);
            edited |= ImGui.SliderFloat("Base wavelength", ref s.BaseWavelength, 250f, 5000f);
            edited |= ImGui.SliderFloat("Stream sharpness", ref s.StreamSharpness, 0.1f, 6f);
            edited |= ImGui.SliderFloat("Eddy speed", ref s.EddySpeed, 0f, 10f);
            edited |= ImGui.SliderFloat("Eddy wavelength", ref s.EddyWavelength, 250f, 2000f);
            edited |= ImGui.SliderFloat("Eddy drift (m/s)", ref s.EddyDrift, 0f, 10f);
            edited |= ImGui.SliderFloat("Gust up", ref s.GustUp, 0f, 3f);
            edited |= ImGui.SliderFloat("Gust down (lull)", ref s.GustDown, 0f, 1f);
            edited |= ImGui.SliderFloat("Gust start", ref s.GustStart, 0f, 1f);
            edited |= ImGui.SliderFloat("Gust full", ref s.GustFull, 0f, 1f);
            edited |= ImGui.SliderFloat("Gust size (m)", ref s.GustSize, 64f, 2000f);
            edited |= ImGui.SliderFloat("Gust period (s)", ref s.GustPeriod, 2f, 120f);
            edited |= ImGui.SliderFloat("Calm full", ref s.CalmFull, -1f, 1f);
            edited |= ImGui.SliderFloat("Calm edge", ref s.CalmEdge, -1f, 1f);
            edited |= ImGui.SliderFloat("Calm size (m)", ref s.CalmSize, 250f, 8000f);
            edited |= ImGui.SliderFloat("Calm period (s)", ref s.CalmPeriod, 10f, 3600f);
            edited |= ImGui.SliderFloat("Terrain reach (chunks)", ref s.TerrainReach, 1f, 6f);
            edited |= ImGui.SliderFloat("Shelter exponent", ref s.ShelterExponent, 0.5f, 6f);
            edited |= ImGui.SliderFloat("Vertical scale", ref s.VerticalScale, 0f, 1f);
            if (edited) _wind.Invalidate();
        }
    }
}
