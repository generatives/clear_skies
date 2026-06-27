using ClearSkies.Engine.Core;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Physics;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using Silk.NET.Maths;

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
public sealed class GpuLightSystem : ISystem, IDisposable
{
    /// <summary>Cross-volume relight cadence. The naive v1 re-floods affected regions from scratch at this rate
    /// (see lighting_design_details.md); moving lamps update within one period.</summary>
    private const float RelightPeriod = 0.5f; // 2 Hz

    private const int S = ChunkData.Size; // 32

    private readonly ChunkVolume    _staticWorld;
    private readonly EntitySet      _grids;
    private readonly EntitySet      _meshes;
    private readonly PhysicsWorld   _physics;
    private readonly Renderer       _renderer;
    private readonly GpuLightFlood  _flood;
    private readonly LightShadowPass _lightShadow;
    private readonly GpuSunVisPass  _sunVis;

    private float _relightTimer;

    // Reused scratch (avoid per-cycle allocation).
    private readonly List<ChunkVolume>             _volumes  = new();
    private readonly List<WorldLamp>               _lamps    = new();
    private readonly List<(GpuMesh mesh, Mat4 model)> _casters = new();
    private readonly Dictionary<ChunkVolume, FloodRegion> _prevCrossRegion = new();

    private readonly record struct WorldLamp(ChunkVolume Source, Vector3D<float> World, int Level);

    public GpuLightSystem(World world, ChunkVolume staticWorld, GpuContext ctx, PhysicsWorld physics, Renderer renderer)
    {
        _staticWorld = staticWorld;
        _physics     = physics;
        _renderer    = renderer;
        _grids       = world.GetEntities().With<DynamicGridComponent>().AsSet();
        _meshes      = world.GetEntities().With<Transform>().With<MeshRenderer>().AsSet();
        _flood       = new GpuLightFlood(ctx);
        _lightShadow = new LightShadowPass(ctx);
        _sunVis      = new GpuSunVisPass(ctx);
    }

    public void Update(float dt)
    {
        // Per-voxel sun visibility: recompute every frame from the (previous frame's) sun shadow map, for every
        // volume. Independent of the flood — separate buffer, separate cadence.
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

    // ── Local-edit flood (unchanged behaviour) ────────────────────────────────

    /// <summary>
    /// Floods the volume if any chunk is dirty and GPU residency is ready. The flood is scoped to the
    /// bounding box of dirty chunks: full-height in Y (sky occlusion is a vertical column effect) and the dirty
    /// X/Z footprint plus a one-chunk lateral margin (≥ the max propagation radius of 15) so the relaxation's
    /// border reads stay correct. Returns true if a flood was submitted.
    /// </summary>
    private bool FloodVolume(ChunkVolume vol)
    {
        var gpu = vol.VolumeGpu;
        if (gpu == null) return false;                   // GPU buffers not yet allocated
        if (gpu.RenderBindGroup == 0) return false;      // render bind group not yet ready

        int minCX = int.MaxValue, minCZ = int.MaxValue, maxCX = int.MinValue, maxCZ = int.MinValue;
        foreach (var (pos, e) in vol.All)
        {
            if (!e.NeedsFlood || e.NeedsGpuUpload) continue;
            int cx = pos.X - gpu.Min.X, cz = pos.Z - gpu.Min.Z;
            if (cx < minCX) minCX = cx; if (cx > maxCX) maxCX = cx;
            if (cz < minCZ) minCZ = cz; if (cz > maxCZ) maxCZ = cz;
        }
        if (maxCX < minCX) return false; // nothing dirty (and ready)

        minCX = System.Math.Max(0, minCX - 1); maxCX = System.Math.Min(gpu.DX - 1, maxCX + 1);
        minCZ = System.Math.Max(0, minCZ - 1); maxCZ = System.Math.Min(gpu.DZ - 1, maxCZ + 1);

        var region = new FloodRegion(
            Ox: minCX * S, Oy: 0, Oz: minCZ * S,
            Sx: (maxCX - minCX + 1) * S, Sy: gpu.VH, Sz: (maxCZ - minCZ + 1) * S);

        _flood.Flood(gpu, vol.All, region);

        foreach (var (_, e) in vol.All)
            if (!e.NeedsGpuUpload)
                e.NeedsFlood = false;

        return true; // one flood per Update
    }

    public void Dispose()
    {
        _flood.Dispose();
        _lightShadow.Dispose();
        _sunVis.Dispose();
    }
}
