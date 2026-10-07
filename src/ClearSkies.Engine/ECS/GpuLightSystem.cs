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

    // Flat ambient (0-15, Minecraft-style level), baked into each voxel's displayed light (darkened by its ray AO) by
    // the compose pass; changing it relights everything.
    private float _ambientLevel = 1f;

    // How strongly ray AO (measured by the bounce rays, GpuRayLightPass bounce_main) darkens the ambient term,
    // 0-1. Applied by the compose pass (changing it relights everything); forced to 0 while bounce is off.
    private float _aoStrength = 0.95f;

    // Bounce (GpuRayLightPass bounce_main): albedo feeds the pass (changing it re-evaluates everything); each voxel
    // has a fixed set of rays x cycle directions, one slice of rays per evaluation, blended as a running average
    // over the first cycle and then with a weight of one cycle; the hold is how many evaluations a changed area gets
    // (rounded up to whole cycles); scale multiplies the stored bounce when the display is composed.
    // AO only: the same rays measure only ambient occlusion, which depends on the shape of the terrain alone, so only
    // changed geometry is re-evaluated: shadows sweeping over the ground, lamps and the sun moving cost no rays.
    private enum BounceMode { Off, AoOnly, Full }
    private BounceMode _bounceMode = BounceMode.Full;
    private bool _bounceEnabled => _bounceMode != BounceMode.Off;
    private bool _aoOnly => _bounceMode == BounceMode.AoOnly;
    private float _bounceAlbedo = 0.5f;
    // How far bounce and AO rays reach (voxels); everything within that of a change is re-evaluated, in whole bricks.
    private float _bounceReach = 16f;
    // Within the near radius of the camera, a change runs its whole hold in the frame it happens (each evaluation reads
    // the one before, adding a hop), so it is settled that frame; with the hold at one full cycle that is exactly its
    // full ray set. Farther away, one evaluation per frame averages the set in over the hold's frames.
    private int _bounceRays = 8;
    private int _bounceCycle = 4;   // evaluations per full ray set: each voxel's fixed set is rays x cycle directions
    private int _bounceHoldFrames = 4;   // evaluations after a change
    private float _bounceNearRadius = 64f;
    private float _bounceScale = 1f;

    // Checkerboard bounce (see bouncePhase in GpuRayLightPass): 1 = every surface voxel fires all the rays each
    // evaluation; 2 or 4 = each fires only that share, neighbours firing the others, and the compose pass's smoothing
    // averages them. Each evaluation costs about 1/spread of the rays.
    private int _bounceSpread = 1;
    private static readonly int[] Spreads = { 1, 2, 4 };
    private static readonly string[] SpreadNames = { "Off (all rays per voxel)", "1/2 of the rays per voxel", "1/4 of the rays per voxel" };

    /// <summary>Checkerboard bounce: 1 (off), 2 or 4: each surface voxel fires 1/that of the rays; other values round to the
    /// nearest of those.</summary>
    public int BounceSpread
    {
        get => _bounceSpread;
        set => _bounceSpread = value >= 4 ? 4 : value >= 2 ? 2 : 1;
    }

    // Gradual bounce: a changed world brick gets the full hold only within the first radius of the camera; out to
    // the second it gets the middle count, and past it the far count. As the camera comes closer, a brick is topped
    // up to its new distance's count, continuing its running average, so distant terrain costs a fraction of the
    // bounce work and sharpens as it's approached.
    private float _bounceFullRadius = 256f;
    private float _bounceMidRadius = 768f;
    private int _bounceMidEvals = 2;
    private int _bounceFarEvals = 1;

    // Per-frame work caps (world bricks, nearest the camera first; the rest wait for later frames). Ships are always
    // relit and bounced whole, on top of these.
    private int _maxRelitPerFrame = 1024;
    private int _maxBouncedPerFrame = 4096;

    /// <summary>A quality level: every setting above that trades lighting quality for GPU time.</summary>
    private readonly record struct LightingPreset(string Name, BounceMode Mode, int Rays, int Spread, int Cycle, int Hold,
                                                  float Reach, float NearRadius, float FullRadius, float MidRadius,
                                                  int MidEvals, int FarEvals, int MaxRelit, int MaxBounced);

    // High is the field values above; the game starts on Medium (see the constructor). Below High: the checkerboard
    // (each voxel fires 1/Spread of the rays, its neighbours the rest, so a patch still covers all of them), shorter
    // rays, changes near the camera spread over a few frames instead of settled in one (each change then costs one
    // evaluation a frame, not its whole hold), smaller full-quality radii and lower per-frame caps; Low keeps only
    // AO, Minimal only direct light.
    private static readonly LightingPreset[] Presets =
    {
        //   Name       Mode               Rays Spread Cycle Hold Reach Near  FullR  MidR  Mid Far Relit Bounced
        new("Minimal", BounceMode.Off,    4,   1,     4,    4,   8f,  0f,  96f, 256f, 1,  1,  256,  512),
        new("Low",     BounceMode.AoOnly, 8,   4,     4,    4,   8f,  0f,  96f, 256f, 1,  1,  256, 1024),
        new("Medium",  BounceMode.Full,   8,   4,     4,    4,   8f, 32f, 128f, 384f, 2,  1,  512, 2048),
        new("High",    BounceMode.Full,   8,   1,     4,    4,  16f, 64f, 256f, 768f, 2,  1, 1024, 4096),
    };

    private const string DefaultPreset = "Medium";

    /// <summary>Applies the quality preset of this name (minimal, low, medium or high, any case); false if there is
    /// none.</summary>
    public bool ApplyPreset(string name)
    {
        foreach (var p in Presets)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { ApplyPreset(p); return true; }
        return false;
    }

    private void ApplyPreset(in LightingPreset p)
    {
        _bounceMode = p.Mode;
        _bounceRays = p.Rays;
        _bounceSpread = p.Spread;
        _bounceCycle = p.Cycle;
        _bounceHoldFrames = p.Hold;
        _bounceReach = p.Reach;
        _bounceNearRadius = p.NearRadius;
        _bounceFullRadius = p.FullRadius;
        _bounceMidRadius = p.MidRadius;
        _bounceMidEvals = p.MidEvals;
        _bounceFarEvals = p.FarEvals;
        _maxRelitPerFrame = p.MaxRelit;
        _maxBouncedPerFrame = p.MaxBounced;
    }

    private bool Matches(in LightingPreset p)
        => _bounceMode == p.Mode && _bounceRays == p.Rays && _bounceSpread == p.Spread && _bounceCycle == p.Cycle && _bounceHoldFrames == p.Hold
           && _bounceReach == p.Reach && _bounceNearRadius == p.NearRadius && _bounceFullRadius == p.FullRadius
           && _bounceMidRadius == p.MidRadius && _bounceMidEvals == p.MidEvals && _bounceFarEvals == p.FarEvals
           && _maxRelitPerFrame == p.MaxRelit && _maxBouncedPerFrame == p.MaxBounced;

    // CPU-side submission timing only: WebGPU's queue is asynchronous, so a Stopwatch around Dispatch() measures
    // encoding + submission, not GPU execution. Compare against the Renderer panel's FPS for total cost.
    private const double EmaAlpha = 0.1;
    private readonly Stopwatch _sunTimer = new();
    private readonly Stopwatch _lampTimer = new();
    private readonly Stopwatch _bounceTimer = new();
    private double _rtSunMsEma, _rtLampMsEma, _rtBounceMsEma;

    private static double Ema(double prev, double sample) => prev <= 0.0 ? sample : prev + EmaAlpha * (sample - prev);

    /// <summary>CPU time of each phase of a frame's lighting, for the debug panel.</summary>
    private readonly StepTimer _phaseTimer = new(
        "Poses + grid upload", "Gathering lamps", "Marking changes", "Choosing relit bricks", "Bounce holds",
        "Choosing bounce bricks", "Chunk lists", "Dispatch + upload") { Owner = "GPU lighting" };

    /// <summary>A grid with a pose this frame.</summary>
    private readonly record struct LitGrid(ChunkVolume Vol, GridHandle Handle, Mat4 VoxelToWorld,
                                           Vector3D<float> Pos, Quaternion<float> Rot);

    private readonly List<LitGrid>             _lit   = new();
    private readonly List<WorldLamp>           _lamps = new();

    // Grid/Local identify the lamp block (grid index, grid-space voxel), so a lamp riding a moving ship stays the
    // same lamp; World is where it is this frame. Open: which of its faces (bit f: +x, -x, +y, -y, +z, -z in grid
    // space) border a cell light passes through; its light leaves from those.
    private readonly record struct WorldLamp(Vector3D<float> World, int Level, Vector3D<float> Color,
                                             int Grid, Vector3D<int> Local, int Open);

    public GpuLightSystem(World world, ChunkVolume staticVolume, GpuContext ctx, GridStore store)
    {
        _staticVolume = staticVolume;
        _store       = store;
        _grids       = world.GetEntities().With<ChunkGrid>().With<Transform>().With<Rendered>().AsSet();
        _cameras     = world.GetEntities().With<Transform>().With<CameraComponent>().AsSet();
        _rayLight    = new GpuRayLightPass(ctx);
        _ctx         = ctx;
        ApplyPreset(DefaultPreset);
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
        _phaseTimer.Draw("CPU time by phase (ms):");
        ImGui.TextDisabled($"  queued: {_lastRelitWaiting:N0} to relight, {_lastBounceWaiting:N0} to bounce");

        ImGui.Separator();
        ImGui.Text("Quality preset");
        string current = "Custom";
        for (int i = 0; i < Presets.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            if (ImGui.Button(Presets[i].Name)) ApplyPreset(Presets[i]);
            if (Matches(Presets[i])) current = Presets[i].Name;
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"({current})");
        ImGui.TextDisabled("  Minimal: direct light only. Low: AO only, short rays. Medium: fewer, shorter bounce rays.");

        ImGui.Separator();
        ImGui.Text("Lighting settings");
        ImGui.SliderFloat("Ambient level", ref _ambientLevel, 0f, 15f, "%.0f");
        ImGui.SliderFloat("Exposure (lit surfaces)", ref RayLightingSettings.Exposure, 0.5f, 3f, "%.2f");
        ImGui.SliderFloat("Ray AO strength", ref _aoStrength, 0f, 1f, "%.2f");
        int mode = (int)_bounceMode;
        if (ImGui.Combo("Bounce rays", ref mode, "Off\0AO only\0Bounce light + AO\0")) _bounceMode = (BounceMode)mode;
        ImGui.SliderFloat("Bounce albedo", ref _bounceAlbedo, 0f, 0.9f, "%.2f");
        ImGui.SliderInt("Bounce rays per evaluation", ref _bounceRays, 1, 32);
        ImGui.SliderFloat("Bounce ray reach (blocks)", ref _bounceReach, 4f, 16f, "%.0f");
        ImGui.SliderInt("Evaluations per full ray set", ref _bounceCycle, 1, 64);
        ImGui.TextDisabled($"  = {_bounceRays * _bounceCycle} fixed directions per voxel");
        int spreadIdx = System.Array.IndexOf(Spreads, _bounceSpread);
        if (ImGui.Combo("Checkerboard bounce", ref spreadIdx, SpreadNames, SpreadNames.Length)) _bounceSpread = Spreads[spreadIdx];
        ImGui.TextDisabled("  neighbouring voxels fire different rays; compose smoothing averages them");
        ImGui.SliderInt("Bounce evaluations after a change", ref _bounceHoldFrames, 1, 64);
        ImGui.SliderFloat("Settled-in-one-frame radius", ref _bounceNearRadius, 0f, 256f, "%.0f");
        ImGui.TextDisabled("  changes within it run all their evaluations the frame they happen");
        ImGui.SliderFloat("Bounce display scale", ref _bounceScale, 0f, 4f, "%.2f");
        ImGui.Text("Gradual bounce (world bricks)");
        ImGui.SliderFloat("Full evaluations within (blocks)", ref _bounceFullRadius, 16f, 4096f, "%.0f");
        ImGui.SliderFloat("Middle evaluations within (blocks)", ref _bounceMidRadius, 16f, 8192f, "%.0f");
        ImGui.SliderInt("Middle evaluations", ref _bounceMidEvals, 1, 64);
        ImGui.SliderInt("Far evaluations", ref _bounceFarEvals, 1, 64);
        ImGui.TextDisabled($"  of {HoldEvals()} after a change; " +
                           $"topped up as the camera nears. Waiting for more: {_coarse.Count:N0}, topped up this frame: {_dbgTopUps:N0}");

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
        ImGui.TextDisabled($"Accumulation: {_store.AccSlotsInUse:N0} / {_store.AccSlotCapacity:N0} bricks being evaluated " +
                           $"({(long)_store.AccSlotCapacity * GridStore.AccSlotBytes / (1024 * 1024)} MB)");
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

        _phaseTimer.Start();
        _lit.Clear();
        foreach (ref readonly Entity e in _grids.GetEntities())
            AddLit(e.Get<ChunkGrid>().Volume, e.DrawnPose()); // lit where it's drawn
        _store.UploadGrids();
        _phaseTimer.Lap(0);

        GatherLamps();
        _phaseTimer.Lap(1);
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
        var accPool = Read(_store.AccPool, 0, _store.AccSlotCapacity * GridStore.AccWordsPerSlot);
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

        // Per voxel of every live slot: display sun/brightness/warmth, accumulated AO and bounce.
        long voxels = 0, shadowed = 0, lit = 0, ao = 0, bounce = 0, coloured = 0, unlit = 0;
        for (int s = 0; s < hw; s++)
        {
            if (_store.SlotGrid[s] < 0) continue;
            int b0 = s * GridStore.WordsPerSlot;
            for (int k = 0; k < 512; k++)
            {
                uint d = (pool[b0 + (k >> 1)] >> (16 * (k & 1))) & 0xFFFF;
                int a = _store.AccOf[s];
                uint acc = a >= 0 ? accPool[a * GridStore.AccWordsPerSlot + k] : 0u;
                voxels++;
                if ((d & 511) == 511) { unlit++; continue; } // not composed yet
                if (((d >> 14) & 3) < 3) shadowed++;
                if ((d & 511) != 0) lit++;
                if (((d >> 9) & 31) != 15) coloured++;
                if ((acc >> 24) != 0) ao++;
                if ((acc & 0xFFFFFF) != 0) bounce++;
            }
        }
        Console.WriteLine($"[probe] voxels={voxels} not-composed={unlit} sun-shadowed={shadowed} lit={lit} tinted={coloured} ao={ao} bounce={bounce} held={_heldQueue.Count}");
    }

    /// <summary>Poses a registered grid for this frame from its root <see cref="Transform"/>.</summary>
    private void AddLit(ChunkVolume vol, in Transform root)
    {
        var h = vol.Gpu;
        if (h.Index < 0) return;
        var v2w = VoxelToWorld(root.Position, root.Rotation);
        _store.SetPose(h, v2w, WorldToVoxel(root.Position, root.Rotation));
        _lit.Add(new LitGrid(vol, h, v2w, root.Position, root.Rotation));
    }

    private void GatherLamps()
    {
        _lamps.Clear();
        foreach (var lg in _lit)
            foreach (var (cpos, entry) in lg.Handle.EmitterChunks) // only the chunks with lamps, not every loaded one
            {
                var origin = cpos.WorldOrigin;
                foreach (var em in entry.Emitters)
                {
                    var local = origin + new Vector3D<float>(em.Lx + 0.5f, em.Ly + 0.5f, em.Lz + 0.5f);
                    var world = lg.VoxelToWorld.TransformPoint(local);
                    var col = BlockRegistry.Get(em.Block).EffectiveLightColor;
                    var voxel = new Vector3D<int>(cpos.X * ChunkData.Size + em.Lx, cpos.Y * ChunkData.Size + em.Ly, cpos.Z * ChunkData.Size + em.Lz);
                    _lamps.Add(new WorldLamp(world, em.Level, col, lg.Handle.Index, voxel, OpenFaces(lg.Vol, voxel)));
                }
            }
    }

    /// <summary>Which faces of the block at <paramref name="v"/> border a cell light passes through (bit f: +x, -x,
    /// +y, -y, +z, -z). Unloaded neighbours count as open.</summary>
    private static int OpenFaces(ChunkVolume vol, Vector3D<int> v)
    {
        int open = 0;
        for (int f = 0; f < 6; f++)
        {
            int s = (f & 1) == 0 ? 1 : -1;
            var n = f < 2 ? new Vector3D<int>(s, 0, 0) : f < 4 ? new Vector3D<int>(0, s, 0) : new Vector3D<int>(0, 0, s);
            if (!BlockRegistry.Get(vol.GetBlock(v.X + n.X, v.Y + n.Y, v.Z + n.Z)).BlocksLight) open |= 1 << f;
        }
        return open;
    }

    // ── Grid transforms ───────────────────────────────────────────────────────

    // A grid's voxel space is its local block space (chunk*32 + local); its root Transform carries it to world
    // space: world = T(pos)·R·voxel (see ChunkVolume). The static world's is the identity.
    private static Mat4 VoxelToWorld(Vector3D<float> pos, Quaternion<float> rot)
        => Mat4.Multiply(Mat4.Translation(pos), Mat4.FromQuaternion(rot));

    /// <summary>Inverse of <see cref="VoxelToWorld"/>, built analytically since the transform is rigid:
    /// voxel = R⁻¹ · T(−pos) · world.</summary>
    private static Mat4 WorldToVoxel(Vector3D<float> pos, Quaternion<float> rot)
        => Mat4.Multiply(Mat4.FromQuaternion(Vec.Conjugate(rot)), Mat4.Translation(-pos));

    public void Dispose()
    {
        _rayLight.Dispose();
        _lightWork?.Dispose();
        _bounceWork?.Dispose();
        foreach (var b in _nearWorks) b?.Dispose();
        _composeWork?.Dispose();
        _finalComposeWork?.Dispose();
        _nearComposeWork?.Dispose();
        _clearWork?.Dispose();
        _zeroWork?.Dispose();
    }
}
