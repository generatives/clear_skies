using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;
using System.Diagnostics;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Ray-traced voxel lighting (see the "Ray-Traced Voxel Lighting" design doc). Every frame: pose each grid (the
/// static world and every ship) in the shared <see cref="GridStore"/>, work out which surface bricks' lighting
/// can have changed, and dispatch <see cref="GpuRayLightPass"/>'s sun, lamp and bounce passes over just those.
/// Rays test occlusion against every grid that can reach them, so ships shadow terrain and each other, and lamps on
/// any grid light any other. Change tracking and dispatch: GpuLightSystem.RayDirty.cs; per-chunk grid and lamp
/// lists: GpuLightSystem.Lists.cs.
///
/// Runs after <c>GpuResidencySystem</c> (occupancy and light storage up to date) and before the render stages.
/// </summary>
public sealed partial class GpuLightSystem : ISystem, IDisposable, IDebugUiSystem
{
    private readonly ChunkVolume     _staticVolume;
    private readonly EntitySet       _grids;
    private readonly EntitySet       _cameras;
    private readonly GridStore       _store;
    private readonly GpuRayLightPass _rayLight;
    private readonly GpuContext      _ctx;

    // Flat ambient (0-15, Minecraft-style level), passed to the fragment shader; changing it relights nothing.
    private float _ambientLevel = 2f;

    // How strongly ray AO (measured by the bounce rays, GpuRayLightPass bounce_main) darkens the ambient term,
    // 0-1. Passed to the fragment shader through RayLightingSettings.AoStrength; forced to 0 while bounce is off.
    private float _aoStrength = 1f;

    // Bounce (GpuRayLightPass bounce_main): albedo feeds the pass (changing it re-evaluates everything); each voxel
    // has a fixed set of rays x cycle directions, one slice of rays per evaluation, blended as a running average
    // over the first cycle and then with a weight of one cycle; the hold is how many evaluations a changed area gets
    // (rounded up to whole cycles), counting near-camera repeats, so one near the camera can finish in one frame;
    // scale multiplies the stored bounce when the display is composed.
    private bool _bounceEnabled = true;
    private float _bounceAlbedo = 0.5f;
    // With the hold at one full cycle and as many near-camera evaluations as the cycle, a change near the camera runs
    // exactly its full ray set in one frame and stops. Farther away, one evaluation per frame averages the set in over
    // cycle frames; cycle 1 (all rays each evaluation) would make those exact every frame too, at cycle x the rays.
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

    // Per-frame work caps (world bricks, nearest the camera first; the rest wait for later frames). Ships are always
    // relit and bounced whole, on top of these.
    private int _maxRelitPerFrame = 1024;
    private int _maxBouncedPerFrame = 4096;

    // CPU-side submission timing only: WebGPU's queue is asynchronous, so a Stopwatch around Dispatch() measures
    // encoding + submission, not GPU execution. Compare against the Renderer panel's FPS for total cost.
    private const double EmaAlpha = 0.1;
    private readonly Stopwatch _sunTimer = new();
    private readonly Stopwatch _lampTimer = new();
    private readonly Stopwatch _bounceTimer = new();
    private double _rtSunMsEma, _rtLampMsEma, _rtBounceMsEma;

    private static double Ema(double prev, double sample) => prev <= 0.0 ? sample : prev + EmaAlpha * (sample - prev);

    /// <summary>CPU time of each phase of a frame's lighting (ms, smoothed), for the debug panel.</summary>
    private sealed class PhaseTimer
    {
        public static readonly string[] Names =
            { "Marking changes", "Choosing relit bricks", "Bounce holds", "Choosing bounce bricks", "Chunk lists", "Dispatch + upload" };
        public readonly double[] Ms = new double[Names.Length];
        private readonly Stopwatch _sw = new();
        private double _last;

        public void Start() { _sw.Restart(); _last = 0; }

        /// <summary>Ends phase <paramref name="i"/> (the time since the previous lap).</summary>
        public void Lap(int i)
        {
            double t = _sw.Elapsed.TotalMilliseconds;
            Ms[i] = Ema(Ms[i], t - _last);
            _last = t;
        }

        public void Stop() => _sw.Stop();
    }

    private readonly PhaseTimer _phaseTimer = new();

    /// <summary>A grid with a pose this frame.</summary>
    private readonly record struct LitGrid(ChunkVolume Vol, GridHandle Handle, Mat4 VoxelToWorld,
                                           Vector3D<float> Pos, Quaternion<float> Rot);

    private readonly List<LitGrid>             _lit   = new();
    private readonly List<WorldLamp>           _lamps = new();

    // Grid/Local identify the lamp block (grid index, grid-space voxel), so a lamp riding a moving ship stays the
    // same lamp; World is where it is this frame.
    private readonly record struct WorldLamp(Vector3D<float> World, int Level, Vector3D<float> Color,
                                             int Grid, Vector3D<int> Local);

    public GpuLightSystem(World world, ChunkVolume staticVolume, GpuContext ctx, GridStore store)
    {
        _staticVolume = staticVolume;
        _store       = store;
        _grids       = world.GetEntities().With<ChunkGrid>().With<Transform>().AsSet();
        _cameras     = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _rayLight    = new GpuRayLightPass(ctx);
        _ctx         = ctx;
    }

    // ── Debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "GPU Lighting (Ray-Traced)";

    public void DrawDebugUi()
    {
        if (ImGui.Button("Relight everything")) _relightRequested = true;
        ImGui.SameLine();
        if (ImGui.Button("Probe (console)")) _probeRequested = true;
        ImGui.Text($"Sun dispatch:            {_rtSunMsEma:F2} ms/frame");
        ImGui.Text($"Compose (lamps) dispatch: {_rtLampMsEma:F2} ms/frame");
        ImGui.Text($"Bounce + AO dispatch:    {_rtBounceMsEma:F2} ms/frame");
        ImGui.TextDisabled("CPU submission time only (queue is async) — compare FPS for total GPU+CPU cost.");
        ImGui.Text("CPU time by phase:");
        for (int i = 0; i < PhaseTimer.Names.Length; i++)
            ImGui.Text($"  {_phaseTimer.Ms[i],6:F2} ms  {PhaseTimer.Names[i]}");
        ImGui.TextDisabled($"  queued: {_lastRelitWaiting:N0} to relight, {_lastBounceWaiting:N0} to bounce");

        ImGui.Separator();
        ImGui.Text("Lighting settings");
        ImGui.SliderFloat("Ambient level", ref _ambientLevel, 0f, 15f, "%.0f");
        ImGui.SliderFloat("Ray AO strength", ref _aoStrength, 0f, 1f, "%.2f");
        ImGui.Checkbox("Bounce light + AO rays", ref _bounceEnabled);
        ImGui.SliderFloat("Bounce albedo", ref _bounceAlbedo, 0f, 0.9f, "%.2f");
        ImGui.SliderInt("Bounce rays per evaluation", ref _bounceRays, 1, 64);
        ImGui.SliderInt("Evaluations per full ray set", ref _bounceCycle, 1, 64);
        ImGui.TextDisabled($"  = {_bounceRays * _bounceCycle} fixed directions per voxel");
        ImGui.SliderInt("Bounce evaluations after a change", ref _bounceHoldFrames, 1, 64);
        ImGui.SliderInt("Bounce re-change restart count", ref _bounceRechangeN, 0, 16);
        ImGui.SliderInt("Near-camera evaluations per frame", ref _bounceNearRepeats, 1, 64);
        ImGui.SliderFloat("Near-camera radius", ref _bounceNearRadius, 8f, 256f, "%.0f");
        ImGui.SliderFloat("Bounce display scale", ref _bounceScale, 0f, 4f, "%.2f");

        ImGui.Separator();
        ImGui.SliderInt("Max bricks relit per frame", ref _maxRelitPerFrame, 64, 16384);
        ImGui.SliderInt("Max bricks bounced per frame", ref _maxBouncedPerFrame, 64, 32768);
        ImGui.TextDisabled($"Bricks relit this frame: {_lastDirtyTotal:N0} of {_store.LightSlotsInUse:N0} surface bricks, waiting: {_lastRelitWaiting:N0}");
        ImGui.TextDisabled($"Bricks bounced this frame: {_lastBounceTotal:N0}, plus near-camera repeats: {_lastNearTotal:N0}, waiting: {_lastBounceWaiting:N0}");
        ImGui.TextDisabled($"  full relight: {(_dbgFullReason == "" ? "no" : _dbgFullReason)}, new bricks: {_dbgNewSlots}");
        ImGui.TextDisabled($"  world chunks changed: {_dbgChangedChunks}, ships moved: {_dbgShipsMoved}, lamp changes: {_dbgLampChanges}");
        ImGui.TextDisabled($"Chunk lists: {_dbgListChunks:N0} chunks, avg {(_dbgListChunks > 0 ? (float)_dbgListGrids / _dbgListChunks : 0f):F1} grids " +
                           $"and {(_dbgListChunks > 0 ? (float)_dbgListLamps / _dbgListChunks : 0f):F1} lamps each (of {_lit.Count} grids, {_lamps.Count} lamps)");
        ImGui.TextDisabled($"Light pool: {_store.LightSlotsInUse:N0} / {_store.LightSlotCapacity:N0} bricks " +
                           $"({(long)_store.LightSlotCapacity * GridStore.SlotBytes / (1024 * 1024)} MB), high water {_store.LightSlotHighWater:N0}");
        ImGui.TextDisabled($"  {(_store.WorldChunkCount > 0 ? (float)_store.LightSlotsInUse / _store.WorldChunkCount : 0f):F1} bricks per " +
                           $"loaded world chunk ({_store.WorldChunkCount:N0} chunks), world budget {_store.WorldLightBudget:N0} bricks");
        ImGui.TextDisabled($"Occupancy pool: {_store.OccSlotsInUse:N0} / {_store.OccSlotCapacity:N0} chunks " +
                           $"({(long)_store.OccSlotCapacity * GridStore.WordsPerChunk * 4 / (1024 * 1024)} MB)");

        ImGui.Separator();
        float sunLevel = SunLight.Level;
        if (ImGui.SliderFloat("Sun level", ref sunLevel, 0f, 15f, "%.0f")) SunLight.Level = sunLevel;

        float azimuth = SunLight.AzimuthDegrees;
        if (ImGui.SliderFloat("Sun azimuth", ref azimuth, 0f, 360f, "%.0f°")) SunLight.AzimuthDegrees = azimuth;

        float elevation = SunLight.ElevationDegrees;
        if (ImGui.SliderFloat("Sun elevation", ref elevation, -90f, 90f, "%.0f°")) SunLight.ElevationDegrees = elevation;
    }

    public void Update(float dt)
    {
        // AO is measured by the bounce rays, so it goes with them.
        RayLightingSettings.Ambient     = _ambientLevel / 15f;
        RayLightingSettings.AoStrength  = _bounceEnabled ? _aoStrength : 0f;

        _lit.Clear();
        foreach (ref readonly Entity e in _grids.GetEntities())
            AddLit(e.Get<ChunkGrid>().Volume, e.Get<Transform>());
        _store.UploadGrids();

        GatherLamps();
        RayTracedDispatch(); // change tracking + dispatch: see GpuLightSystem.RayDirty.cs
        if (_probeRequested) { _probeRequested = false; Probe(); }
    }

    private bool _probeRequested;
    /// <summary>Debug: reads back the world grid descriptor, the camera chunk's table entry and one of its
    /// light bricks, and prints them next to the CPU mirror.</summary>
    private void Probe()
    {
        var world = _staticVolume.Gpu;
        if (!CameraUtil.TryGetActive(_cameras, out var cam) || world.Index < 0) return;
        var cp = new ChunkPosition((int)MathF.Floor(cam.Position.X / 32f), (int)MathF.Floor(cam.Position.Y / 32f), (int)MathF.Floor(cam.Position.Z / 32f));
        ChunkRecord? rec = null;
        // Nearest chunk below the camera that has light bricks.
        for (int dy = 0; dy < 8 && rec == null; dy++)
            if (world.Chunks.TryGetValue(cp.Offset(0, -dy, 0), out var r) && r.BrickSlots != null) rec = r;
        Console.WriteLine($"[probe] world idx={world.Index} base={world.TableBase} dims={world.TDX}x{world.TDY}x{world.TDZ} box={world.BoxMin.X},{world.BoxMin.Y},{world.BoxMin.Z}..{world.BoxMax.X},{world.BoxMax.Y},{world.BoxMax.Z} records={world.Chunks.Count} slots={world.Slots.Count}");

        uint[] Read(GpuBuffer src, ulong offset, int words)
        {
            using var rb = GpuBuffer.CreateReadback(_ctx, (ulong)words * 4);
            _ctx.CopyBufferToBuffer(src, rb, (ulong)words * 4, offset);
            var bytes = _ctx.ReadBuffer(rb, words * 4);
            var w = new uint[words];
            Buffer.BlockCopy(bytes, 0, w, 0, bytes.Length);
            return w;
        }

        var desc = Read(_store.Grids, 0, 44);
        Console.WriteLine($"[probe] gpu desc table=({(int)desc[32]},{(int)desc[33]},{(int)desc[34]},{(int)desc[35]}) bmin=({(int)desc[36]},{(int)desc[37]},{(int)desc[38]}) bmax=({(int)desc[40]},{(int)desc[41]},{(int)desc[42]})");
        if (rec == null) { Console.WriteLine("[probe] no lit chunk under camera"); return; }

        var e = Read(_store.ChunkTable, (ulong)rec.TableIndex * GridStore.ChunkEntryBytes, 8);
        Console.WriteLine($"[probe] chunk {rec.Pos.X},{rec.Pos.Y},{rec.Pos.Z} tableIdx={rec.TableIndex} occ={rec.OccSlot} solid={rec.Solid:X16}; " +
                          $"gpu entry=({(int)e[0]},{(int)e[1]},{(int)e[2]},{(int)e[3]}) solid={((ulong)e[5] << 32 | e[4]):X16}");
        int hw = _store.LightSlotHighWater;
        var pool = Read(_store.LightPool, 0, hw * GridStore.WordsPerSlot);
        var info = Read(_store.SlotInfo, 0, hw * 4);
        int badInfo = 0;
        for (int s = 0; s < hw; s++)
        {
            if (_store.SlotGrid[s] < 0) continue;
            var c = _store.SlotChunk[s];
            if ((int)info[4 * s] != (_store.SlotBrick[s] | (_store.SlotGrid[s] << 6)) || (int)info[4 * s + 1] != c.X ||
                (int)info[4 * s + 2] != c.Y || (int)info[4 * s + 3] != c.Z) badInfo++;
        }
        Console.WriteLine($"[probe] slotInfo mismatches: {badInfo} of {hw}; last frame relit={_lastDirtyTotal} bounced={_lastBounceTotal}");

        // Per voxel of every live slot: display sun/RGB/AO and accumulated bounce.
        long voxels = 0, shadowed = 0, lit = 0, ao = 0, bounce = 0, coloured = 0;
        for (int s = 0; s < hw; s++)
        {
            if (_store.SlotGrid[s] < 0) continue;
            int b0 = s * GridStore.WordsPerSlot;
            for (int k = 0; k < 512; k++)
            {
                uint d = (pool[b0 + (k >> 1)] >> (16 * (k & 1))) & 0xFFFF;
                uint acc = pool[b0 + 256 + k];
                voxels++;
                if (((d >> 12) & 3) < 3) shadowed++;
                if ((d & 0xFFF) != 0) lit++;
                if ((d & 15) != ((d >> 4) & 15) || (d & 15) != ((d >> 8) & 15)) coloured++;
                if ((d >> 14) != 0) ao++;
                if ((acc & 0xFFFFFF) != 0) bounce++;
            }
        }
        Console.WriteLine($"[probe] voxels={voxels} sun-shadowed={shadowed} rgb-lit={lit} coloured={coloured} ao={ao} bounce={bounce} held={_heldList.Count}");
    }

    /// <summary>Poses a registered grid for this frame from its root <see cref="Transform"/> and pivot.</summary>
    private void AddLit(ChunkVolume vol, in Transform root)
    {
        var h = vol.Gpu;
        if (h.Index < 0) return;
        var v2w = VoxelToWorld(root.Position, root.Rotation, vol.Pivot);
        _store.SetPose(h, v2w, WorldToVoxel(root.Position, root.Rotation, vol.Pivot));
        _lit.Add(new LitGrid(vol, h, v2w, root.Position, root.Rotation));
    }

    private void GatherLamps()
    {
        _lamps.Clear();
        foreach (var lg in _lit)
            foreach (var (cpos, entry) in lg.Vol.All)
            {
                if (entry.Emitters.Count == 0) continue;
                var origin = cpos.WorldOrigin;
                foreach (var em in entry.Emitters)
                {
                    var local = origin + new Vector3D<float>(em.Lx + 0.5f, em.Ly + 0.5f, em.Lz + 0.5f);
                    var world = lg.VoxelToWorld.TransformPoint(local);
                    var col = BlockRegistry.Get(em.Block).EffectiveLightColor;
                    var voxel = new Vector3D<int>(cpos.X * ChunkData.Size + em.Lx, cpos.Y * ChunkData.Size + em.Ly, cpos.Z * ChunkData.Size + em.Lz);
                    _lamps.Add(new WorldLamp(world, em.Level, col, lg.Handle.Index, voxel));
                }
            }
    }

    // ── Grid transforms ───────────────────────────────────────────────────────

    // A grid's voxel space is its local block space (chunk*32 + local); its root Transform and pivot carry it to
    // world space: world = T(pos)·R·T(−pivot)·voxel (see ChunkVolume). The static world's is the identity.
    private static Mat4 VoxelToWorld(Vector3D<float> pos, Quaternion<float> rot, Vector3D<float> pivot)
        => Mat4.Multiply(Mat4.Multiply(Mat4.Translation(pos), Mat4.FromQuaternion(rot)), Mat4.Translation(-pivot));

    /// <summary>Inverse of <see cref="VoxelToWorld"/>, built analytically since the transform is rigid:
    /// voxel = T(pivot) · R⁻¹ · T(−pos) · world.</summary>
    private static Mat4 WorldToVoxel(Vector3D<float> pos, Quaternion<float> rot, Vector3D<float> pivot)
        => Mat4.Multiply(Mat4.Multiply(Mat4.Translation(pivot), Mat4.FromQuaternion(Vec.Conjugate(rot))), Mat4.Translation(-pos));

    public void Dispose()
    {
        _rayLight.Dispose();
        _lightWork?.Dispose();
        _bounceWork?.Dispose();
        _nearWork?.Dispose();
        _composeWork?.Dispose();
        _nearComposeWork?.Dispose();
        _clearWork?.Dispose();
    }
}
