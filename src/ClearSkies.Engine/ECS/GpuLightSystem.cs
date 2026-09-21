using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Drives the GPU light flood. Two concerns:
///
/// <para><b>Local floods</b> (every frame, one volume): whenever a volume has a dirty chunk (a block edit),
/// re-flood the bounding box of its dirty chunks. Light still crosses chunk boundaries within the region.</para>
///
/// <para><b>Cross-volume relight</b> (Phase 4.4, ~2 Hz): a point lamp in one volume lights <i>other</i>
/// volumes. Each cycle every lamp is expressed in world space; for each volume reached by an external lamp we
/// render that lamp's omnidirectional cube depth map against the whole posed scene (<see cref="LightShadowPass"/>),
/// then run a depth-tested injection pass (inside <see cref="GpuLightFlood"/>) over the lamp's reach AABB —
/// exactly the unified "depth-map → inject → flood" path the sun uses, generalised to point lamps. Because the
/// cube map contains every volume's geometry, occlusion by the source ship's hull, the target's own walls, and
/// any other grid is all correct. v1 re-floods the affected region from scratch each cycle (clear → inject →
/// flood); a one-cycle region latch clears a lamp's light after it moves away.</para>
///
/// Runs after <c>GpuResidencySystem</c> (opacity uploaded + RenderBindGroup created first) and before the mesh
/// system / RenderSystem.
/// </summary>
public sealed partial class GpuLightSystem : ISystem, IDisposable, IDebugUiSystem
{
    /// <summary>Cross-volume relight cadence. The naive v1 re-floods affected regions from scratch at this rate
    /// (see lighting_design_details.md); moving lamps update within one period.</summary>
    private const float RelightPeriod = 0.5f; // 2 Hz

    /// <summary>Cap on a local-edit flood region's XZ span, in chunks (Y is always full-height — see
    /// FloodVolume's doc comment). Without this, a load-in burst that dirties most of the loaded footprint at
    /// once forces one flood covering nearly the whole volume; chunks outside the capped window stay dirty and
    /// get swept up by a later call, so a big dirty footprint is processed as several smaller ones over several
    /// frames instead of one huge one on a single frame.</summary>
    private const int MaxFloodChunksXZ = 8;

    /// <summary>Relaxation passes run per frame for a local-edit flood, out of GpuLightFlood.Passes total.
    /// Must evenly divide Passes (each call needs an even count — see GpuLightFlood.RelaxPasses). Spreads one
    /// region's convergence across multiple frames instead of paying for all of it on the frame it starts.</summary>
    private const int RelaxPassesPerFrame = 4;

    private const int S = ChunkData.Size; // 32

    private readonly ChunkVolume    _staticWorld;
    private readonly EntitySet      _grids;
    private readonly EntitySet      _meshes;
    private readonly EntitySet      _cameras;
    private readonly PhysicsWorld   _physics;
    private readonly Renderer       _renderer;
    private readonly GpuLightFlood  _flood;
    private readonly LightShadowPass _lightShadow;
    private readonly GpuSunVisPass  _sunVis;
    private readonly GpuRayLightPass _rayLight;
    private readonly GpuContext     _ctx;

    private float _relightTimer;

    // ── Ray-traced prototype toggle (see plan doc) ──────────────────────────────
    // A/B switch: when true, bypasses the flood/cube-map/shadow-map path entirely and dispatches
    // GpuRayLightPass for every loaded volume instead. When false, behaves exactly as before —
    // zero regression risk.
    private bool _rayTracedLighting = true;

    // Ray-traced lamp pass's flat ambient (0-15, Minecraft-style level — see GpuRayLightPass.DispatchLamps),
    // debug-panel adjustable. Deliberately its own setting, decoupled from VolumeGpuResources.BaseSkyLevel
    // (the old flood's ambient, unaffected by this).
    private float _ambientLevel = 2f;

    // How strongly ray AO (measured by the bounce rays, GpuRayLightPass bounce_main) darkens the ambient term,
    // 0-1. Passed to the fragment shader through RayLightingSettings.AoStrength; forced to 0 while the old path
    // is active or bounce is off.
    private float _aoStrength = 1f;

    // Bounce (GpuRayLightPass bounce_main): albedo feeds the pass (changing it re-evaluates everything); each voxel
    // has a fixed set of rays x cycle directions, one slice of rays per evaluation, blended as a running average
    // over the first cycle and then with a weight of one cycle; hold frames is how many evaluations a changed
    // area gets (rounded up to whole cycles); scale only multiplies the stored value in the fragment shader.
    private bool _bounceEnabled = true;
    private float _bounceAlbedo = 0.5f;
    private int _bounceRays = 8;
    private int _bounceCycle = 4;   // evaluations per full ray set: each voxel's fixed set is rays x cycle directions
    private int _bounceHoldFrames = 4;
    private float _bounceScale = 1f;

    // A brick that changes again while still being evaluated has its evaluation count capped at this (blend
    // weight 1/(n+1)) instead of restarting at 0, so continuously changing areas stay a little smoothed.
    private int _bounceRechangeN = 2;

    // Held bricks within this many voxels of the camera are evaluated this many times per frame.
    private int _bounceNearRepeats = 4;
    private float _bounceNearRadius = 64f;

    // Reused scratch for the ray-traced path (avoid per-frame allocation). _slotGpu[i] is the
    // VolumeGpuResources backing _slots[i] (0..volumeCount-1), so the per-volume dispatch knows which
    // SunVis/LightA buffer to write into — RayVolumeSlot itself only carries what the shader needs.
    // _slotPos/_slotRot/_slotCom mirror the same indices with the volume's raw pose (not baked into a
    // Mat4), needed separately to call LampAabb when building each volume's filtered lamp sublist.
    private readonly RayVolumeSlot[] _slots = new RayVolumeSlot[GpuRayLightPass.MaxRayVolumes];
    private readonly VolumeGpuResources?[] _slotGpu = new VolumeGpuResources?[GpuRayLightPass.MaxRayVolumes];
    private readonly ChunkVolume?[] _slotVol = new ChunkVolume?[GpuRayLightPass.MaxRayVolumes];
    private readonly Vector3D<float>[] _slotPos = new Vector3D<float>[GpuRayLightPass.MaxRayVolumes];
    private readonly Quaternion<float>[] _slotRot = new Quaternion<float>[GpuRayLightPass.MaxRayVolumes];
    private readonly Vector3D<float>[] _slotCom = new Vector3D<float>[GpuRayLightPass.MaxRayVolumes];
    private readonly List<Vector4D<float>> _volumeLampScratch = new();
    private bool _loggedTooManyVolumes;

    // ── Perf pass (task 6, see plan doc): CPU-side submission timing only. WebGPU's queue is
    // asynchronous, so a CPU Stopwatch around Dispatch() measures encoding + submission cost, NOT actual
    // GPU execution time — real GPU cost would need timestamp queries, which this prototype doesn't have.
    // Still a real, useful number: it's the cost of NOT caching the ray pass's bind groups (see
    // GpuRayLightPass's "rebuild every dispatch" decision) plus everything else on the CPU side of these
    // paths. Compare against the Renderer panel's FPS counter for a total-cost (CPU+GPU) signal.
    private const double EmaAlpha = 0.1;
    private readonly Stopwatch _sunTimer = new();
    private readonly Stopwatch _lampTimer = new();
    private readonly Stopwatch _bounceTimer = new();
    private readonly Stopwatch _oldPathTimer = new();
    private double _rtSunMsEma, _rtLampMsEma, _rtBounceMsEma, _oldPathMsEma;

    private static double Ema(double prev, double sample) => prev <= 0.0 ? sample : prev + EmaAlpha * (sample - prev);

    // Reused scratch (avoid per-cycle allocation).
    private readonly List<ChunkVolume>             _volumes  = new();
    private readonly List<WorldLamp>               _lamps    = new();
    private readonly List<(GpuMesh mesh, Mat4 model)> _casters = new();
    private readonly Dictionary<ChunkVolume, FloodRegion> _prevCrossRegion = new();

    // In-progress local-edit flood (see FloodVolume) — at most one at a time, since only one volume floods
    // per Update() call. Null/_relaxVol == null means nothing is mid-relax.
    private ChunkVolume?        _relaxVol;
    private FloodRegion         _relaxRegion;
    private int                 _relaxGeneration;
    private int                 _relaxPassesLeft;
    private List<ChunkPosition>? _relaxChunks;

    private readonly record struct WorldLamp(ChunkVolume Source, Vector3D<float> World, int Level);

    public GpuLightSystem(World world, ChunkVolume staticWorld, GpuContext ctx, PhysicsWorld physics, Renderer renderer)
    {
        _staticWorld = staticWorld;
        _physics     = physics;
        _renderer    = renderer;
        _grids       = world.GetEntities().With<DynamicGridComponent>().AsSet();
        _cameras     = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _meshes      = world.GetEntities().With<Transform>().With<MeshRenderer>().AsSet();
        _flood       = new GpuLightFlood(ctx);
        _lightShadow = new LightShadowPass(ctx);
        _sunVis      = new GpuSunVisPass(ctx);
        _rayLight    = new GpuRayLightPass(ctx);
        _ctx         = ctx;
    }

    // ── Debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "GPU Lighting (Ray-Traced Prototype)";

    public void DrawDebugUi()
    {
        ImGui.Checkbox("Ray-Traced Lighting (A/B)", ref _rayTracedLighting);
        ImGui.Separator();
        ImGui.Text($"Old path (sun-vis + cross-volume + flood): {_oldPathMsEma:F2} ms/frame");
        ImGui.Text($"Ray-traced sun dispatch:                   {_rtSunMsEma:F2} ms/frame");
        ImGui.Text($"Ray-traced lamp dispatch:                  {_rtLampMsEma:F2} ms/frame");
        ImGui.Text($"Ray-traced bounce + AO dispatch:           {_rtBounceMsEma:F2} ms/frame");
        ImGui.TextDisabled("CPU submission time only (queue is async) — compare FPS for total GPU+CPU cost.");

        ImGui.Separator();
        ImGui.Text("Lighting settings");
        ImGui.SliderFloat("Ambient level (ray-traced only)", ref _ambientLevel, 0f, 15f, "%.0f");
        ImGui.SliderFloat("Ray AO strength (ray-traced only)", ref _aoStrength, 0f, 1f, "%.2f");
        if (_rayLight.BounceSupported)
        {
            ImGui.Checkbox("Bounce light + AO rays (ray-traced only)", ref _bounceEnabled);
            ImGui.SliderFloat("Bounce albedo", ref _bounceAlbedo, 0f, 0.9f, "%.2f");
            ImGui.SliderInt("Bounce rays per evaluation", ref _bounceRays, 1, 32);
            ImGui.SliderInt("Evaluations per full ray set", ref _bounceCycle, 1, 16);
            ImGui.TextDisabled($"  = {_bounceRays * _bounceCycle} fixed directions per voxel");
            ImGui.SliderInt("Bounce hold frames", ref _bounceHoldFrames, 1, 64);
            ImGui.SliderInt("Bounce re-change restart count", ref _bounceRechangeN, 0, 16);
            ImGui.SliderInt("Near-camera evaluations per frame", ref _bounceNearRepeats, 1, 4);
            ImGui.SliderFloat("Near-camera radius", ref _bounceNearRadius, 8f, 256f, "%.0f");
            ImGui.SliderFloat("Bounce display scale", ref _bounceScale, 0f, 4f, "%.2f");
        }
        else ImGui.TextDisabled("Bounce light unavailable: too few storage buffers per shader stage (see console).");
        if (_rayTracedLighting)
        {
            ImGui.TextDisabled($"Bricks relit this frame: {_lastDirtyTotal:N0} of {_lastBrickTotal:N0} surface bricks");
            ImGui.TextDisabled($"Bricks bounced this frame: {_lastBounceTotal:N0}, plus near-camera repeats: {_lastNearTotal:N0}");
            ImGui.TextDisabled($"  full relight: {(_dbgFullReason == "" ? "no" : _dbgFullReason)}, volumes reallocated: {_dbgReallocs}");
            ImGui.TextDisabled($"  world chunks uploaded: {_dbgChangedChunks}, ships moved: {_dbgShipsMoved}, lamp changes: {_dbgLampChanges}");
        }

        float sunLevel = SunLight.Level;
        if (ImGui.SliderFloat("Sun level", ref sunLevel, 0f, 15f, "%.0f")) SunLight.Level = sunLevel;

        float azimuth = SunLight.AzimuthDegrees;
        if (ImGui.SliderFloat("Sun azimuth", ref azimuth, 0f, 360f, "%.0f°")) SunLight.AzimuthDegrees = azimuth;

        float elevation = SunLight.ElevationDegrees;
        if (ImGui.SliderFloat("Sun elevation", ref elevation, -90f, 90f, "%.0f°")) SunLight.ElevationDegrees = elevation;

        ImGui.TextDisabled("Sun level/azimuth/elevation affect BOTH lighting paths (shared SunLight); ambient is ray-traced only.");
    }

    public void Update(float dt)
    {
        // AO is measured by the bounce rays, so it goes with them.
        bool bounceRays = _rayTracedLighting && _bounceEnabled && _rayLight.BounceSupported;
        RayLightingSettings.AoStrength  = bounceRays ? _aoStrength : 0f;
        RayLightingSettings.BounceScale = bounceRays ? _bounceScale : 0f;
        if (_rayTracedLighting) { UpdateRayTracedLighting(); return; } // old path fully bypassed — see plan doc
        ResetRayTracedTracking();

        _oldPathTimer.Restart();
        try
        {
            // Per-voxel sun visibility: recompute every frame from the (previous frame's) sun shadow map, for
            // every volume. Independent of the flood — separate buffer, separate cadence.
            UpdateSunVisibility();

            // Cross-volume relight on the fixed cadence (moving lamps + cross-grid light).
            _relightTimer += dt;
            if (_relightTimer >= RelightPeriod)
            {
                _relightTimer = 0f;
                CrossVolumeRelight();
            }

            // Responsive local-edit floods: one volume per frame.
            if (FloodVolume(_staticWorld)) return;
            foreach (ref readonly Entity e in _grids.GetEntities())
                if (FloodVolume(e.Get<DynamicGridComponent>().Grid)) return;
        }
        finally
        {
            _oldPathTimer.Stop();
            _oldPathMsEma = Ema(_oldPathMsEma, _oldPathTimer.Elapsed.TotalMilliseconds);
        }
    }

    // ── Ray-traced prototype (A/B toggle) ───────────────────────────────────────

    /// <summary>
    /// Ray-traced sun + lamp lighting for every loaded volume (static world + up to
    /// <see cref="GpuRayLightPass.MaxRayVolumes"/>-1 ships), replacing <see cref="UpdateSunVisibility"/> /
    /// <see cref="CrossVolumeRelight"/> / the local-edit flood entirely while the toggle is on. Each
    /// volume's sun and lamp rays test occlusion against every OTHER loaded volume's occupancy too (see
    /// GpuRayLightPass.anyOccluderAlongSegment), so a ship correctly shadows terrain and terrain/other
    /// ships correctly shadow it, and lamps on any volume correctly light and shadow any other. No AO, no
    /// bounce, no color: the lamp pass writes a flat ambient sky byte (no flood dependency), so LightA is
    /// fully self-contained and recomputed from scratch every frame — nothing here reads its own previous
    /// value.
    /// </summary>
    private void UpdateRayTracedLighting()
    {
        int volumeCount = 0;
        if (FillSlot(_staticWorld, volumeCount)) volumeCount++;

        int gridsSeen = 0;
        foreach (ref readonly Entity e in _grids.GetEntities())
        {
            gridsSeen++;
            if (volumeCount >= GpuRayLightPass.MaxRayVolumes) continue; // still counted below, for the overflow log
            if (FillSlot(e.Get<DynamicGridComponent>().Grid, volumeCount)) volumeCount++;
        }

        if (gridsSeen + 1 > GpuRayLightPass.MaxRayVolumes && !_loggedTooManyVolumes)
        {
            _loggedTooManyVolumes = true;
            Console.WriteLine(
                $"[ray-lighting] {gridsSeen + 1} volumes loaded, only the first {GpuRayLightPass.MaxRayVolumes} " +
                "get ray-traced lighting this session — the rest keep whatever SunVis/LightA they last had.");
        }

        // Every frame now (not the old 2 Hz cadence) — cheap at prototype lamp counts, and the lamp pass
        // needs a fresh world-space list every dispatch since nothing here is cached across frames.
        // GatherLamps walks _volumes, which only the old path otherwise refreshes: without this, starting with
        // the ray-traced path on found no lamps at all, and a ship spawned while it was on had its lamps missed.
        BuildVolumes();
        GatherLamps();

        RayTracedDispatch(volumeCount); // change tracking + dispatch: see GpuLightSystem.RayDirty.cs
    }

    /// <summary>Fills <see cref="_slots"/>[<paramref name="index"/>] (and its matching <see cref="_slotGpu"/>/
    /// <see cref="_slotVol"/>/<see cref="_slotPos"/>/<see cref="_slotRot"/>/<see cref="_slotCom"/> entries)
    /// from <paramref name="vol"/>'s current GPU resources and physics pose. Returns false (leaving the
    /// slot untouched) if the volume isn't ready yet — same gating <see cref="SunVisVolume"/> already uses.</summary>
    private bool FillSlot(ChunkVolume vol, int index)
    {
        var gpu = vol.VolumeGpu;
        if (gpu == null || gpu.RenderBindGroup == 0) return false;
        if (!TryPose(vol, out var pos, out var rot, out var com)) return false;

        _slots[index] = new RayVolumeSlot(
            gpu.Opacity, VoxelToWorld(gpu, pos, rot, com), WorldToVoxelMatrix(gpu, pos, rot, com),
            gpu.VW, gpu.VH, gpu.VD);
        _slotGpu[index] = gpu;
        _slotVol[index] = vol;
        _slotPos[index] = pos; _slotRot[index] = rot; _slotCom[index] = com;
        return true;
    }

    // ── Per-voxel sun visibility ──────────────────────────────────────────────

    /// <summary>
    /// Recomputes per-voxel directional-sun visibility for every volume against the world-space sun shadow map.
    /// Runs in PreRender, so it reads the <i>previous</i> frame's shadow map + light matrix (the renderer renders
    /// the new one later this frame in the Render stage). They're a matched pair (both from last frame), and with
    /// texel-snapping the static-world shadow is stable between frames, so the one-frame lag is invisible. The
    /// fragment shader then samples <c>SunVis</c> instead of doing per-pixel PCF.
    /// </summary>
    private void UpdateSunVisibility()
    {
        nint shadowView = _renderer.ShadowDepthView;
        if (shadowView == 0) return;
        var lvp = _renderer.LastLightViewProj;

        SunVisVolume(_staticWorld, lvp, shadowView);
        foreach (ref readonly Entity e in _grids.GetEntities())
            SunVisVolume(e.Get<DynamicGridComponent>().Grid, lvp, shadowView);
    }

    private void SunVisVolume(ChunkVolume vol, in Mat4 lvp, nint shadowView)
    {
        var gpu = vol.VolumeGpu;
        if (gpu == null || gpu.RenderBindGroup == 0) return; // not yet resident (SunVis allocated with the rest)
        if (!TryPose(vol, out var p, out var r, out var c)) return;

        var voxelToWorld = VoxelToWorld(gpu, p, r, c);
        // Whole volume each frame; non-surface/out-of-frustum voxels early-out cheaply in the shader.
        var region = new FloodRegion(0, 0, 0, gpu.VW, gpu.VH, gpu.VD);
        _sunVis.Compute(gpu, shadowView, lvp, voxelToWorld, region);
    }

    // ── Cross-volume relight ──────────────────────────────────────────────────

    private void CrossVolumeRelight()
    {
        BuildVolumes();
        GatherLamps();
        if (_lamps.Count == 0 && _prevCrossRegion.Count == 0) return; // nothing to do, and nothing to clear

        BuildCasters();

        Span<Mat4> faceVP = stackalloc Mat4[LightShadowPass.Faces];
        foreach (var vol in _volumes)
        {
            var gpu = vol.VolumeGpu;
            if (gpu == null || gpu.RenderBindGroup == 0) continue;

            // Lamps from OTHER volumes that reach this one, with their reach AABB in this volume's voxel space.
            bool hasPose = TryPose(vol, out var vp, out var vr, out var vc);
            FloodRegion? current = null;
            var reaching = new List<(WorldLamp lamp, int ax, int ay, int az, int sx, int sy, int sz)>();
            if (hasPose)
            {
                foreach (var lamp in _lamps)
                {
                    if (ReferenceEquals(lamp.Source, vol)) continue; // own lamps are handled by the local flood
                    if (!LampAabb(vol, gpu, vp, vr, vc, lamp, out int ax, out int ay, out int az, out int sx, out int sy, out int sz))
                        continue;
                    reaching.Add((lamp, ax, ay, az, sx, sy, sz));
                    var laxz = new FloodRegion(ax, 0, az, sx, gpu.VH, sz);
                    current = current is { } cur ? UnionXZ(cur, laxz, gpu.VH) : laxz;
                }
            }

            // Region to recompute this cycle = union(this cycle's lamp footprint, last cycle's) so a lamp that
            // moved/left has its old lit region cleared. Full-height in Y (sky is a vertical-column effect).
            _prevCrossRegion.TryGetValue(vol, out var prev);
            bool hadPrev = _prevCrossRegion.ContainsKey(vol);
            FloodRegion? region = current;
            if (hadPrev) region = region is { } r ? UnionXZ(r, prev, gpu.VH) : prev;
            if (region is not { } reg) continue; // no lamps now and none before

            // Recompute the region from scratch, injecting each reaching lamp between clear/scatter and relax.
            _flood.PrepareRegion(gpu, vol.All, reg);
            foreach (var (lamp, ax, ay, az, sx, sy, sz) in reaching)
            {
                LightShadowPass.BuildFaceMatrices(lamp.World, lamp.Level, faceVP);
                _lightShadow.RenderCube(lamp.World, faceVP, _casters);
                var voxelToWorld = VoxelToWorld(gpu, vp, vr, vc);
                _flood.Inject(gpu, _lightShadow.DistanceArrayViewHandle, voxelToWorld, faceVP,
                              lamp.World, lamp.Level, lamp.Level, ax, ay, az, sx, sy, sz);
            }
            _flood.FinishRegion(gpu, reg);

            // Latch this cycle's footprint so next cycle clears it if the lamp leaves.
            if (current is { } c) _prevCrossRegion[vol] = c;
            else                  _prevCrossRegion.Remove(vol);
        }
    }

    private void BuildVolumes()
    {
        _volumes.Clear();
        _volumes.Add(_staticWorld);
        foreach (ref readonly Entity e in _grids.GetEntities())
            _volumes.Add(e.Get<DynamicGridComponent>().Grid);
    }

    private void GatherLamps()
    {
        _lamps.Clear();
        foreach (var vol in _volumes)
        {
            if (!TryPose(vol, out var pos, out var rot, out var com)) continue;
            foreach (var (cpos, entry) in vol.All)
            {
                if (entry.Emitters.Count == 0) continue;
                var origin = cpos.WorldOrigin;
                foreach (var em in entry.Emitters)
                {
                    var local = origin + new Vector3D<float>(em.Lx + 0.5f, em.Ly + 0.5f, em.Lz + 0.5f);
                    var world = pos + Vec.Rotate(rot, local - com);
                    _lamps.Add(new WorldLamp(vol, world, em.Level));
                }
            }
        }
    }

    private void BuildCasters()
    {
        _casters.Clear();
        foreach (ref readonly Entity e in _meshes.GetEntities())
        {
            ref readonly var t = ref e.Get<Transform>();
            _casters.Add((e.Get<MeshRenderer>().Mesh, t.ToMatrix()));
        }
    }

    /// <summary>Reach AABB of <paramref name="lamp"/> in <paramref name="vol"/>'s voxel space (clamped to the
    /// volume). Returns false if the lamp's reach does not overlap the volume.</summary>
    private bool LampAabb(ChunkVolume vol, VolumeGpuResources gpu,
                          Vector3D<float> vp, Quaternion<float> vr, Vector3D<float> vc, WorldLamp lamp,
                          out int ax, out int ay, out int az, out int sx, out int sy, out int sz)
    {
        ax = ay = az = sx = sy = sz = 0;
        var center = WorldToVoxel(gpu, vp, vr, vc, lamp.World);
        float r = lamp.Level;

        if (!AxisRange(center.X, r, gpu.VW, out ax, out sx)) return false;
        if (!AxisRange(center.Y, r, gpu.VH, out ay, out sy)) return false;
        if (!AxisRange(center.Z, r, gpu.VD, out az, out sz)) return false;
        return true;
    }

    private static bool AxisRange(float center, float r, int dim, out int origin, out int size)
    {
        int lo = (int)MathF.Floor(center - r);
        int hi = (int)MathF.Floor(center + r);
        if (hi < 0 || lo >= dim) { origin = size = 0; return false; }
        origin = System.Math.Max(0, lo);
        int end = System.Math.Min(dim - 1, hi);
        size = end - origin + 1;
        return size > 0;
    }

    // ── Volume world↔voxel transforms ─────────────────────────────────────────

    /// <summary>Pose of a volume: a dynamic grid's body pose + centre-of-mass, or identity for the static world.
    /// Returns false for a grid whose body isn't created yet (skip it this cycle).</summary>
    private bool TryPose(ChunkVolume vol, out Vector3D<float> pos, out Quaternion<float> rot, out Vector3D<float> com)
    {
        if (vol is DynamicGrid g)
        {
            if (!g.BodyCreated) { pos = default; rot = Quaternion<float>.Identity; com = default; return false; }
            var (p, q) = _physics.GetBodyPose(g.Body);
            pos = PhysicsConv.ToSilk(p);
            rot = PhysicsConv.ToSilk(q);
            com = PhysicsConv.ToSilk(g.CenterOfMass);
            return true;
        }
        pos = Vector3D<float>.Zero; rot = Quaternion<float>.Identity; com = Vector3D<float>.Zero;
        return true;
    }

    // Volume voxel (vx,vy,vz) ↔ world. A volume voxel maps to local position Min*32 + voxel (grid-local for a
    // grid, world for the static world); the grid pose then carries it to world space.
    private static Mat4 VoxelToWorld(VolumeGpuResources gpu, Vector3D<float> pos, Quaternion<float> rot, Vector3D<float> com)
    {
        var min32 = Min32(gpu);
        var t1 = Mat4.Translation(min32 - com);
        var r  = Mat4.FromQuaternion(rot);
        var t0 = Mat4.Translation(pos);
        return Mat4.Multiply(Mat4.Multiply(t0, r), t1); // world = T(pos)·R·T(Min*32 − com)·voxel
    }

    private static Vector3D<float> WorldToVoxel(VolumeGpuResources gpu,
                                                Vector3D<float> pos, Quaternion<float> rot, Vector3D<float> com,
                                                Vector3D<float> world)
    {
        // local = com + R⁻¹·(world − pos);  voxel = local − Min*32.
        var local = com + Vec.Rotate(Conjugate(rot), world - pos);
        return local - Min32(gpu);
    }

    /// <summary>Mat4-returning sibling of <see cref="WorldToVoxel"/>, for the ray-traced pass — the shader
    /// needs the whole transform (to rotate ray directions too, not just transform one point), not a
    /// single-point helper. Built analytically since the transform is rigid: rotation is orthonormal, so
    /// its conjugate quaternion is its inverse, and there's no scale to invert.
    /// voxel = T(com−Min·32) · R⁻¹ · T(−pos) · world</summary>
    private static Mat4 WorldToVoxelMatrix(VolumeGpuResources gpu, Vector3D<float> pos, Quaternion<float> rot, Vector3D<float> com)
    {
        var min32 = Min32(gpu);
        var rInv  = Mat4.FromQuaternion(Conjugate(rot));
        var t0    = Mat4.Translation(com - min32);
        return Mat4.Multiply(Mat4.Multiply(t0, rInv), Mat4.Translation(-pos));
    }

    private static Vector3D<float> Min32(VolumeGpuResources gpu)
        => new(gpu.Min.X * S, gpu.Min.Y * S, gpu.Min.Z * S);

    private static Quaternion<float> Conjugate(Quaternion<float> q) => new(-q.X, -q.Y, -q.Z, q.W);

    private static FloodRegion UnionXZ(FloodRegion a, FloodRegion b, int vh)
    {
        int minX = System.Math.Min(a.Ox, b.Ox);
        int maxX = System.Math.Max(a.Ox + a.Sx, b.Ox + b.Sx);
        int minZ = System.Math.Min(a.Oz, b.Oz);
        int maxZ = System.Math.Max(a.Oz + a.Sz, b.Oz + b.Sz);
        return new FloodRegion(minX, 0, minZ, maxX - minX, vh, maxZ - minZ);
    }

    // ── Local-edit flood ────────────────────────────────────────────────────

    /// <summary>
    /// Advances the volume's local-edit flood by one frame's worth of work and returns true if anything was
    /// submitted. Two things can happen:
    ///
    /// <para><b>Continue an in-progress relax</b> (see <see cref="_relaxVol"/>): a previous call started
    /// flooding a region but its 16 relaxation passes didn't all fit in one frame's budget
    /// (<see cref="RelaxPassesPerFrame"/>) — run the next batch. Aborts (discarding progress) if the volume
    /// was reallocated since (its buffers are fresh/reset, so resuming would relax garbage); the affected
    /// chunks are still flagged dirty and get pursued fresh next time.</para>
    ///
    /// <para><b>Start a new region</b>, otherwise: bounding box of dirty chunks, full-height in Y (sky
    /// occlusion is a vertical column effect) and the dirty X/Z footprint plus a one-chunk lateral margin
    /// (≥ the max propagation radius of 15) so the relaxation's border reads stay correct — then capped to
    /// <see cref="MaxFloodChunksXZ"/> so a load-in burst that dirties most of the loaded footprint doesn't
    /// force one region covering nearly the whole volume. Only the chunks that actually fall inside the
    /// (possibly capped) region are recorded to have their NeedsFlood flag cleared on completion; any dirty
    /// chunk the cap left out stays dirty for a later call.</para>
    /// </summary>
    private bool FloodVolume(ChunkVolume vol)
    {
        if (_relaxVol == vol)
            return ContinueRelax(vol);
        if (_relaxVol != null)
            return false; // a different volume is mid-relax this cycle; this one waits its turn

        var gpu = vol.VolumeGpu;
        if (gpu == null) return false;                   // GPU buffers not yet allocated
        if (gpu.RenderBindGroup == 0) return false;      // render bind group not yet ready

        // Also track the dirty chunk nearest the volume centre (≈ camera, since GpuResidencySystem keeps the
        // window centred on loaded chunks) as a seed for capping below. During normal streaming the dirty set
        // is typically a thin ring at the edge of the loaded volume (MarkNeighboursDirty on each newly-added
        // chunk), not a filled area — its bounding box spans the whole loaded volume even though the ring
        // itself is thin, so capping a window around the *bbox's own midpoint* can land in the ring's empty
        // interior with no dirty chunks in it at all, making an entire multi-frame relax cycle a no-op that
        // silently never converges. Anchoring on an actual dirty chunk guarantees real progress every cycle.
        int minCX = int.MaxValue, minCZ = int.MaxValue, maxCX = int.MinValue, maxCZ = int.MinValue;
        int centerCX = gpu.DX / 2, centerCZ = gpu.DZ / 2;
        ChunkPosition? seedPos = null;
        int bestDist = int.MaxValue;
        bool anyPendingUpload = false;
        foreach (var (pos, e) in vol.All)
        {
            // A chunk with a pending opacity upload has stale (usually all-air, post-reallocation) data in
            // the Opacity buffer the flood shaders actually read. Flooding a region that overlaps ANY such
            // chunk -- not just one that's itself due for reflooding -- would read it as non-occluding and
            // let ambient sky light flash straight through terrain that just hasn't had its real opacity
            // written back yet. So: don't flood this volume AT ALL while anything in it is upload-pending,
            // even chunks this cycle wouldn't otherwise have touched.
            if (e.NeedsGpuUpload) { anyPendingUpload = true; continue; }
            if (!e.NeedsFlood) continue;
            int cx = pos.X - gpu.Min.X, cz = pos.Z - gpu.Min.Z;
            if (cx < minCX) minCX = cx; if (cx > maxCX) maxCX = cx;
            if (cz < minCZ) minCZ = cz; if (cz > maxCZ) maxCZ = cz;

            int d = (cx - centerCX) * (cx - centerCX) + (cz - centerCZ) * (cz - centerCZ);
            if (d < bestDist) { bestDist = d; seedPos = pos; }
        }
        if (anyPendingUpload) return false; // wait for GpuResidencySystem's upload backlog to fully drain first
        if (seedPos is not { } seed) return false; // nothing dirty (and ready)

        minCX = System.Math.Max(0, minCX - 1); maxCX = System.Math.Min(gpu.DX - 1, maxCX + 1);
        minCZ = System.Math.Max(0, minCZ - 1); maxCZ = System.Math.Min(gpu.DZ - 1, maxCZ + 1);

        // Cap the window, centred on the seed chunk (not the bbox's own midpoint — see above).
        if (maxCX - minCX + 1 > MaxFloodChunksXZ)
        {
            int seedCX = seed.X - gpu.Min.X;
            minCX = System.Math.Clamp(seedCX - MaxFloodChunksXZ / 2, minCX, maxCX - MaxFloodChunksXZ + 1);
            maxCX = minCX + MaxFloodChunksXZ - 1;
        }
        if (maxCZ - minCZ + 1 > MaxFloodChunksXZ)
        {
            int seedCZ = seed.Z - gpu.Min.Z;
            minCZ = System.Math.Clamp(seedCZ - MaxFloodChunksXZ / 2, minCZ, maxCZ - MaxFloodChunksXZ + 1);
            maxCZ = minCZ + MaxFloodChunksXZ - 1;
        }

        var region = new FloodRegion(
            Ox: minCX * S, Oy: 0, Oz: minCZ * S,
            Sx: (maxCX - minCX + 1) * S, Sy: gpu.VH, Sz: (maxCZ - minCZ + 1) * S);

        // Exactly which dirty chunks fall inside the (possibly capped) region — only these get NeedsFlood
        // cleared once the relax finishes, regardless of what else is dirty elsewhere in the volume.
        var chunks = new List<ChunkPosition>();
        foreach (var (pos, e) in vol.All)
        {
            if (!e.NeedsFlood || e.NeedsGpuUpload) continue;
            int cx = pos.X - gpu.Min.X, cz = pos.Z - gpu.Min.Z;
            if (cx >= minCX && cx <= maxCX && cz >= minCZ && cz <= maxCZ)
                chunks.Add(pos);
        }

        _flood.PrepareRegion(gpu, vol.All, region);
        _flood.BeginRelax(gpu);

        int firstBatch = System.Math.Min(RelaxPassesPerFrame, GpuLightFlood.Passes);
        _flood.RelaxPasses(gpu, region, firstBatch);
        int remaining = GpuLightFlood.Passes - firstBatch;

        if (remaining <= 0)
        {
            FinishChunks(vol, chunks);
        }
        else
        {
            _relaxVol = vol; _relaxRegion = region; _relaxGeneration = gpu.Generation;
            _relaxPassesLeft = remaining; _relaxChunks = chunks;
        }

        return true;
    }

    private bool ContinueRelax(ChunkVolume vol)
    {
        var gpu = vol.VolumeGpu;
        if (gpu == null || gpu.Generation != _relaxGeneration)
        {
            // Reallocated mid-relax: LightA/LightB are fresh/reset, so there's nothing sensible to resume.
            // The chunks we were relaxing are still NeedsFlood=true (never cleared) and get retried fresh.
            _relaxVol = null; _relaxChunks = null;
            return false;
        }

        int passes = System.Math.Min(RelaxPassesPerFrame, _relaxPassesLeft);
        _flood.RelaxPasses(gpu, _relaxRegion, passes);
        _relaxPassesLeft -= passes;

        if (_relaxPassesLeft <= 0)
        {
            FinishChunks(vol, _relaxChunks!);
            _relaxVol = null; _relaxChunks = null;
        }

        return true;
    }

    /// <summary>Clears NeedsFlood for exactly the given chunks, skipping any that unloaded mid-relax (gone from
    /// the volume) or were edited again mid-relax (NeedsGpuUpload still true — stays dirty for a fresh reflood
    /// once its opacity upload catches up, since this relax may have run against a mix of old/new opacity).</summary>
    private static void FinishChunks(ChunkVolume vol, List<ChunkPosition> chunks)
    {
        foreach (var pos in chunks)
        {
            var e = vol.GetEntry(pos);
            if (e != null && !e.NeedsGpuUpload)
                e.NeedsFlood = false;
        }
    }

    public void Dispose()
    {
        _flood.Dispose();
        _lightShadow.Dispose();
        _sunVis.Dispose();
        _rayLight.Dispose();
        foreach (var st in _rayStates.Values) st.Dispose();
    }
}
