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
    private float _ambientLevel = 0.5f; // of 15: a floor under bounce, so it hides any bounce dimmer than it

    // How strongly ray AO (measured by the bounce rays, GpuRayLightPass bounce_main) darkens the ambient term,
    // 0-1. Applied by the compose pass (changing it relights everything); forced to 0 while bounce is off.
    private float _aoStrength = 0.95f;

    // Bounce (GpuRayLightPass bounce_main): albedo feeds the pass (changing it updates everything); each voxel has
    // fixed directions, rays per group x direction groups of them; scale multiplies the stored
    // bounce when the display is composed. How a brick's bounce is updated after any change depends only on its
    // distance from the camera (see the bands below and GpuLightSystem.RayDirty.cs).
    // AO only: the same rays measure only ambient occlusion, which depends on the shape of the terrain alone, so only
    // changed geometry is updated: shadows sweeping over the ground, lamps and the sun moving cost no rays.
    private enum BounceMode { Off, AoOnly, Full }
    private BounceMode _bounceMode = BounceMode.Full;
    private bool _bounceEnabled => _bounceMode != BounceMode.Off;
    private bool _aoOnly => _bounceMode == BounceMode.AoOnly;
    private float _bounceAlbedo = 0.5f;
    // How far bounce and AO rays reach (voxels); everything within that of a change is updated, in whole bricks.
    private float _bounceReach = 16f;
    private int _bounceRays = 8;     // rays per group
    private int _bounceGroups = 4;   // direction groups: each voxel has rays x groups fixed directions
    private float _bounceScale = 1f;

    /// <summary>
    /// A distance band (near: within Radius of the camera; middle: within its Radius; far: the rest). A changed brick
    /// gets Updates updates there, one per turn, at most once every Every frames (1: every frame, so a one-update
    /// change is settled the frame it happens). An update is Hops hops back to back in one frame, each one dispatch
    /// that reads what the last wrote (so each adds a hop), together firing Groups of the direction groups, always the
    /// same ones. Each update reads the light the last one left, so a change gets Updates x Hops hops in all. Spread
    /// is its rays per voxel (see bouncePhase in GpuRayLightPass): 1 = every surface voxel fires all of each group's
    /// rays; 2 or 4 = each fires only
    /// that share, neighbours firing the others, and the compose pass's smoothing averages them, for about 1/Spread of
    /// the rays. Every spread fires part of the same fixed ray set, so a brick crossing bands keeps its directions.
    /// </summary>
    private struct BounceBand
    {
        public float Radius;
        public int Hops, Groups, Updates, Every, Spread;
        public BounceBand(float radius, int hops, int groups, int updates, int every, int spread)
        {
            Radius = radius; Hops = hops; Groups = groups; Updates = updates; Every = every; Spread = spread;
        }
    }

    private readonly BounceBand[] _bands = { new(256f, 4, 4, 3, 4, 1), new(768f, 2, 2, 3, 4, 1), new(0f, 1, 1, 3, 8, 1) };

    private static readonly int[] Spreads = { 1, 2, 4 };
    private static readonly string[] SpreadNames = { "All", "1/2 (neighbours fire the rest)", "1/4 (neighbours fire the rest)" };

    private static int ToSpread(int value) => value >= 4 ? 4 : value >= 2 ? 2 : 1;

    /// <summary>Rays per voxel in every band: 1 (all), 2 or 4: each surface voxel fires 1/that of the
    /// rays; other values round to the nearest of those.</summary>
    public int BounceSpread
    {
        get => _bands[0].Spread;
        set { for (int i = 0; i < _bands.Length; i++) _bands[i].Spread = ToSpread(value); }
    }

    // Per-frame work caps (world bricks, nearest the camera first; the rest wait for later frames): bricks relit, and
    // groups fired by bounce updates, per brick. Ships are always relit and bounced whole, on top of these.
    private int _maxRelitPerFrame = 1024;
    private int _maxGroupsPerFrame = 4096;

    /// <summary>A quality level: every setting above that trades lighting quality for GPU time.</summary>
    private readonly record struct LightingPreset(string Name, BounceMode Mode, int Rays, int Groups, float Reach,
                                                  BounceBand Near, BounceBand Mid, BounceBand Far, int MaxRelit, int MaxGroups);

    // High is the field values above; the game starts on Medium (see the constructor). Below High: fewer rays per voxel
    // (each voxel fires 1/spread of the rays, its neighbours the rest, so a patch still covers all of them) beyond the
    // near band (everywhere on Low), shorter rays, smaller near and middle bands and lower per-frame caps; Low keeps
    // only AO, Minimal only direct light.
    // Near: every direction group in 4 hops. Medium and High: half of them in 2 hops in the middle band; Minimal and
    // Low a quarter in 1, as far away. Three updates per change, at most every 4 frames, 8 far.
    private static readonly LightingPreset[] Presets =
    {
        //   Name       Mode               Rays Groups Reach Near (radius, hops, groups, updates, every, spread)  Mid                    Far                 Relit Groups
        new("Minimal", BounceMode.Off,    4,   4,    8f,   new(96f, 4, 4, 3, 4, 1),  new(256f, 1, 1, 3, 4, 1), new(0f, 1, 1, 3, 8, 1),  256,  512),
        new("Low",     BounceMode.AoOnly, 8,   4,    8f,   new(96f, 4, 4, 3, 4, 4),  new(256f, 1, 1, 3, 4, 4), new(0f, 1, 1, 3, 8, 4),  256, 1024),
        new("Medium",  BounceMode.Full,   8,   4,    8f,   new(128f, 4, 4, 3, 4, 1), new(384f, 2, 2, 3, 4, 4), new(0f, 1, 1, 3, 8, 4),  512, 2048),
        new("High",    BounceMode.Full,   8,   4,   16f,   new(256f, 4, 4, 3, 4, 1), new(768f, 2, 2, 3, 4, 1), new(0f, 1, 1, 3, 8, 1), 1024, 4096),
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
        _bounceGroups = p.Groups;
        _bounceReach = p.Reach;
        _bands[0] = p.Near;
        _bands[1] = p.Mid;
        _bands[2] = p.Far;
        _maxRelitPerFrame = p.MaxRelit;
        _maxGroupsPerFrame = p.MaxGroups;
    }

    private bool Matches(in LightingPreset p)
        => _bounceMode == p.Mode && _bounceRays == p.Rays && _bounceGroups == p.Groups
           && _bounceReach == p.Reach && _bands[0].Equals(p.Near) && _bands[1].Equals(p.Mid) && _bands[2].Equals(p.Far)
           && _maxRelitPerFrame == p.MaxRelit && _maxGroupsPerFrame == p.MaxGroups;

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
        "Poses + grid upload", "Gathering lamps", "Marking changes", "Choosing relit bricks", "Bounce touches",
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
        // Logarithmic with two decimals: the useful range is under 1 (ambient is a floor under bounce, see compose_main),
        // where whole levels were too coarse.
        ImGui.SliderFloat("Ambient level", ref _ambientLevel, 0f, 15f, "%.2f", ImGuiSliderFlags.Logarithmic);
        ImGui.SliderFloat("Exposure (lit surfaces)", ref RayLightingSettings.Exposure, 0.5f, 3f, "%.2f");
        ImGui.SliderFloat("Ray AO strength", ref _aoStrength, 0f, 1f, "%.2f");
        int mode = (int)_bounceMode;
        if (ImGui.Combo("Bounce rays", ref mode, "Off\0AO only\0Bounce light + AO\0")) _bounceMode = (BounceMode)mode;
        ImGui.SliderFloat("Bounce albedo", ref _bounceAlbedo, 0f, 0.9f, "%.2f");
        ImGui.SliderFloat("Bounce ray reach (blocks)", ref _bounceReach, 4f, 16f, "%.0f");
        // Directions per voxel and how many groups they're split into; rays per group follows from the two.
        int directions = _bounceRays * _bounceGroups;
        if (ImGui.SliderInt("Directions per voxel", ref directions, 1, 32 * 64))
            _bounceRays = System.Math.Clamp((directions + _bounceGroups / 2) / _bounceGroups, 1, 32);
        int groups = _bounceGroups;
        if (ImGui.SliderInt("Direction groups", ref groups, 1, 64))
        {
            _bounceRays = System.Math.Clamp(_bounceRays * _bounceGroups / groups, 1, 32);
            _bounceGroups = groups;
        }
        ImGui.TextDisabled($"  = {_bounceRays} rays per group, {_bounceRays * _bounceGroups} directions; a hop fires one or more groups");
        ImGui.SliderFloat("Bounce display scale", ref _bounceScale, 0f, 4f, "%.2f");
        ImGui.Text("Bounce by distance (every change alike)");
        ImGui.TextDisabled("  a changed brick gets updates, at most every so many frames; an update is hops back to back,");
        ImGui.TextDisabled("  firing that many groups. Hops in all = updates x hops per update");
        ImGui.TextDisabled("  rays per voxel below all: each voxel fires part of each group, neighbours the rest; smoothing averages them");
        DrawBand("Near", 0, true);
        DrawBand("Middle", 1, true);
        DrawBand("Far", 2, false);
        ImGui.TextDisabled($"  far bricks fire more as the camera nears. Waiting for more: {_coarse.Count:N0}, touched this frame: {_dbgTopUps:N0}");

        ImGui.Separator();
        ImGui.SliderInt("Max bricks relit per frame", ref _maxRelitPerFrame, 64, 16384);
        ImGui.SliderInt("Max brick groups per frame", ref _maxGroupsPerFrame, 64, 32768);
        ImGui.TextDisabled($"Bricks relit this frame: {_lastDirtyTotal:N0} of {_store.LightSlotsInUse:N0} surface bricks, waiting: {_lastRelitWaiting:N0}");
        ImGui.TextDisabled($"Bricks bounced this frame: {_lastBounceTotal:N0}, plus further hops: {_lastStepTotal:N0}, waiting: {_lastBounceWaiting:N0}");
        ImGui.TextDisabled($"  full relight: {(_dbgFullReason == "" ? "no" : _dbgFullReason)}, new bricks: {_dbgNewSlots}");
        ImGui.TextDisabled($"  world chunks changed: {_dbgChangedChunks}, ships moved: {_dbgShipsMoved}, lamp changes: {_dbgLampChanges}");
        ImGui.TextDisabled($"Chunk lists: {_dbgListChunks:N0} chunks, avg {(_dbgListChunks > 0 ? (float)_dbgListGrids / _dbgListChunks : 0f):F1} grids " +
                           $"and {(_dbgListChunks > 0 ? (float)_dbgListLamps / _dbgListChunks : 0f):F1} lamps each (of {_lit.Count} grids, {_lamps.Count} lamps)");
        ImGui.TextDisabled($"Light pool: {_store.LightSlotsInUse:N0} / {_store.LightSlotCapacity:N0} bricks " +
                           $"({(long)_store.LightSlotCapacity * GridStore.SlotBytes / (1024 * 1024)} MB), high water {_store.LightSlotHighWater:N0}");
        ImGui.TextDisabled($"  {(_store.WorldChunkCount > 0 ? (float)_store.LightSlotsInUse / _store.WorldChunkCount : 0f):F1} bricks per " +
                           $"loaded world chunk ({_store.WorldChunkCount:N0} chunks), world budget {_store.WorldLightBudget:N0} bricks");
        ImGui.TextDisabled($"Accumulation: {_store.AccSlotsInUse:N0} / {_store.AccSlotCapacity:N0} bricks updating " +
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

    private void DrawBand(string name, int i, bool hasRadius)
    {
        ref var b = ref _bands[i];
        ImGui.Text(name);
        // Each band starts where the one inside it ends: near goes up to the middle band's radius, the middle band
        // from the near one's out to how far the world loads (the far band is the rest).
        if (hasRadius)
        {
            float min = i == 0 ? 0f : _bands[0].Radius;
            float max = i == 0 ? _bands[1].Radius : System.Math.Max(_store.WorldReach, min);
            b.Radius = System.Math.Clamp(b.Radius, min, max);
            ImGui.SliderFloat($"  within (blocks)##band{i}", ref b.Radius, min, max, "%.0f");
        }
        ImGui.SliderInt($"  hops per update##band{i}", ref b.Hops, 1, 16);
        ImGui.SliderInt($"  groups per update##band{i}", ref b.Groups, 1, 64);
        ImGui.SliderInt($"  updates per change##band{i}", ref b.Updates, 1, 16);
        ImGui.SliderInt($"  update at most every (frames)##band{i}", ref b.Every, 1, 32);
        int spreadIdx = System.Math.Max(0, System.Array.IndexOf(Spreads, b.Spread));
        if (ImGui.Combo($"  rays per voxel##band{i}", ref spreadIdx, SpreadNames, SpreadNames.Length)) b.Spread = Spreads[spreadIdx];
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
        Console.WriteLine($"[probe] voxels={voxels} not-composed={unlit} sun-shadowed={shadowed} lit={lit} tinted={coloured} ao={ao} bounce={bounce} held={_updateQueue.Count}");
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
        foreach (var b in _stepWorks) b?.Dispose();
        _composeWork?.Dispose();
        _finalComposeWork?.Dispose();
        _clearWork?.Dispose();
        _zeroWork?.Dispose();
        _seedWork?.Dispose();
    }
}
