using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;
using System.Runtime.InteropServices;

namespace ClearSkies.Engine.ECS;

// Ray-traced prototype: surface-brick work lists and dirty tracking. Each frame only the surface bricks whose
// lighting can have changed are re-traced; every other voxel keeps last frame's SunVis/LightA values.
//
// What makes a brick dirty:
//  - everything, when the toggle turns on, the sun direction or ambient changes, or a volume is (re)allocated;
//  - a changed occluder (a static chunk edited or loaded, a ship that moved or was edited — at its old and new
//    pose): the bricks right next to it, every brick whose sun ray passes through it (its bounds swept along the
//    sun direction), and the full radius of every lamp whose reach overlaps it;
//  - a lamp that appeared, disappeared or moved: its full radius, at old and new positions.
// A ship's own volume is all-or-nothing (it is small); the static world is tracked per brick.
// Bounce (and the ray AO it measures) follows the direct-light marks, held for a number of frames and grown by
// the bounce ray length; see BounceWork.
public sealed partial class GpuLightSystem
{
    /// <summary>The sun + lamp passes' dirty bricks and their GPU work buffer.</summary>
    private sealed class BrickWork : IDisposable
    {
        public bool All = true;
        public bool AnyMarked;
        public bool[] Dirty = Array.Empty<bool>();
        public GpuBuffer? Work;
        public int WorkCapacity;

        public void Dispose() => Work?.Dispose();
    }

    /// <summary>
    /// Bounce scheduling. Bounce converges over repeated evaluations (and gains a hop per evaluation), so a brick
    /// keeps being evaluated for the hold-frames setting after its direct light changed, and so do the bricks
    /// within bounce range of it. Hold is a per-brick countdown and N a per-brick count of evaluations since it
    /// went from idle to changed (static world only; it picks the ray slice, N mod cycle, and the shader's blend
    /// weight, max(1/cycle, 1/(N+1))).
    /// AllFrames/AllN do the same for the whole volume. A brick marked again while still held has its N capped at
    /// the re-change setting rather than reset, so an area that changes every frame (a moving ship's shadow)
    /// keeps some smoothing instead of restarting at weight 1 each frame and flickering.
    /// Held bricks near the camera are also evaluated extra times per frame (Near*), each adding a hop.
    /// </summary>
    private sealed class BounceWork : IDisposable
    {
        public int AllFrames = DefaultBounceHoldFrames;
        public int AllN;
        public byte[] Hold = Array.Empty<byte>();
        public byte[] N = Array.Empty<byte>();
        public int AnyFrames;
        public GpuBuffer? Work;
        public int WorkCapacity;

        // This frame's held bricks near the camera: packed brick and its N, re-sent once per extra repeat.
        public uint[] NearScratch = new uint[1024];
        public int NearCount;
        public GpuBuffer? NearWork;
        public int NearCapacity;

        public void Dispose() { Work?.Dispose(); NearWork?.Dispose(); }
    }

    private sealed class RayVolumeState : IDisposable
    {
        // Surface-brick list (CPU), packed x | y<<10 | z<<20 in this volume's brick coordinates.
        public uint[] Surface = new uint[4096];
        public int SurfaceCount;
        public int BuiltGeneration = -1, BuiltVersion = -1, BuiltLoaded = -1;

        // Bounds of this volume's solid bricks in its own voxel space (used for ships as an occluder/receiver).
        public bool HasSolid;
        public Vector3D<float> SolidMin, SolidMax;

        // Dirty state for the sun + lamp passes: whole volume, or per brick over a BW x BH x BD brick grid
        // (static world only).
        public readonly BrickWork Light = new();
        public readonly BounceWork Bounce = new();
        public int BW, BH, BD;

        // Ships: last frame's pose / opacity version, and this frame's world-space bounds of their solid bricks.
        public bool HavePrev;
        public Vector3D<float> PrevPos, PrevWorldMin, PrevWorldMax;
        public Quaternion<float> PrevRot;
        public int PrevVersion = -1;
        public Vector3D<float> CurWorldMin, CurWorldMax;

        // Scratch for building a work list (the dirty subset of Surface) before upload.
        public uint[] WorkScratch = new uint[4096];

        public void Dispose() { Light.Dispose(); Bounce.Dispose(); }
    }

    private readonly Dictionary<ChunkVolume, RayVolumeState> _rayStates = new();
    private readonly RayVolumeState?[] _slotState = new RayVolumeState?[GpuRayLightPass.MaxRayVolumes];
    private int _lastBrickTotal, _lastDirtyTotal, _lastBounceTotal, _lastNearTotal;

    private const int DefaultBounceHoldFrames = 4;
    private const int BounceMarginBricks = 2;  // bounce rays reach 16 voxels = 2 bricks

    private readonly GpuBuffer[] _slotLight = new GpuBuffer[GpuRayLightPass.MaxRayVolumes];
    private readonly GpuBuffer[] _slotSunVis = new GpuBuffer[GpuRayLightPass.MaxRayVolumes];
    private float _prevBounceAlbedo = -1f, _prevSunLevel = -1f;
    private bool _prevBounceEnabled;
    private int _prevBounceRays = -1, _prevBounceCycle = -1;

    private bool _rayWasActive;
    private Vector3D<float> _prevSunDir;
    private int _prevAmbient = -1;
    private readonly List<WorldLamp> _prevLamps = new();
    private HashSet<(int, int, int, int)> _prevLampKeys = new(), _curLampKeys = new();
    private readonly List<(Vector3D<float> min, Vector3D<float> max)> _occluderChanges = new();
    private readonly List<(Vector3D<float> min, Vector3D<float> max)> _directChanges = new();

    // Debug-panel breakdown of why bricks were relit this frame.
    private string _dbgFullReason = "";
    private int _dbgReallocs, _dbgChangedChunks, _dbgShipsMoved, _dbgLampChanges;

    private const float SweepStep = 4f;   // voxels between successive sun-shadow sweep copies
    private const float SweepPad  = 3f;   // pad each copy by > SweepStep/2 plus sample jitter, so copies overlap

    /// <summary>Per-frame ray-traced lighting work after the slots and lamps are gathered: work out what
    /// changed, mark the affected bricks, and dispatch the sun and lamp passes over just those.</summary>
    private void RayTracedDispatch(int volumeCount)
    {
        _dbgReallocs = _dbgChangedChunks = _dbgShipsMoved = 0;
        DropStaleRayStates(volumeCount);
        for (int i = 0; i < volumeCount; i++) _slotState[i] = RayStateFor(_slotVol[i]!, _slotGpu[i]!);

        var sunDir = SunLight.Direction;
        int ambient = (int)_ambientLevel;
        bool toggledOn = !_rayWasActive;
        bool relightAll = toggledOn || sunDir != _prevSunDir || ambient != _prevAmbient;
        _dbgFullReason = !_rayWasActive ? "toggled on" : sunDir != _prevSunDir ? "sun moved" : ambient != _prevAmbient ? "ambient changed" : "";
        _rayWasActive = true;
        _prevSunDir = sunDir;
        _prevAmbient = ambient;

        _occluderChanges.Clear();
        _directChanges.Clear();
        CollectOccluderChanges(volumeCount, relightAll);
        CollectLampChanges();
        _dbgLampChanges = _directChanges.Count;

        if (relightAll)
        {
            for (int i = 0; i < volumeCount; i++) _slotState[i]!.Light.All = true;
        }
        else
        {
            var pad = new Vector3D<float>(1.5f);
            foreach (var (mn, mx) in _occluderChanges)
            {
                MarkRegion(volumeCount, mn - pad, mx + pad);   // surface voxels right at the change
                MarkSunShadow(volumeCount, mn, mx, sunDir);    // voxels whose sun ray passes through it
                MarkLampsTouching(mn, mx);                     // lamps whose rays may pass through it
            }
            foreach (var (mn, mx) in _directChanges) MarkRegion(volumeCount, mn, mx);
        }

        // Evaluations a changed area gets, rounded up to whole cycles so it always stops having covered each voxel's
        // complete ray set equally.
        int hold = System.Math.Min(64, (_bounceHoldFrames + _bounceCycle - 1) / _bounceCycle * _bounceCycle);

        // Bounce inputs that change what every surface receives (or which rays it fires): re-evaluate everything.
        bool bounceOn = _bounceEnabled && _rayLight.BounceSupported;
        if (bounceOn && (!_prevBounceEnabled || _bounceAlbedo != _prevBounceAlbedo || SunLight.Level != _prevSunLevel
                         || _bounceRays != _prevBounceRays || _bounceCycle != _prevBounceCycle))
            for (int i = 0; i < volumeCount; i++)
            {
                var b = _slotState[i]!.Bounce;
                b.AllFrames = hold;
                b.AllN = 0;
            }
        _prevBounceEnabled = bounceOn;
        _prevBounceAlbedo = _bounceAlbedo;
        _prevSunLevel = SunLight.Level;
        _prevBounceRays = _bounceRays;
        _prevBounceCycle = _bounceCycle;

        _sunTimer.Reset();
        _lampTimer.Reset();
        _bounceTimer.Reset();
        _lastBrickTotal = 0;
        _lastDirtyTotal = 0;
        _lastBounceTotal = 0;
        _lastNearTotal = 0;
        for (int i = 0; i < volumeCount; i++)
        {
            var gpu = _slotGpu[i]!;
            var st  = _slotState[i]!;
            _lastBrickTotal += st.SurfaceCount;
            HoldBounce(st, hold, _bounceRechangeN);

            int n = BuildWorkList(st, st.Light);
            _lastDirtyTotal += n;
            if (n == 0) continue;

            _sunTimer.Start();
            _rayLight.DispatchSun(_slots, volumeCount, i, gpu.SunVis, sunDir, st.Light.Work!, n);
            _sunTimer.Stop();

            // Lamps that can plausibly reach this volume at all (LampAabb as a boolean prefilter). No
            // same-volume skip: a volume's own lamps go through the identical world-space test as any other's.
            _volumeLampScratch.Clear();
            foreach (var lamp in _lamps)
            {
                if (_volumeLampScratch.Count >= GpuRayLightPass.MaxLampsPerDispatch) break;
                if (!LampAabb(_slotVol[i]!, gpu, _slotPos[i], _slotRot[i], _slotCom[i], lamp,
                              out _, out _, out _, out _, out _, out _))
                    continue;
                _volumeLampScratch.Add(new Vector4D<float>(lamp.World.X, lamp.World.Y, lamp.World.Z, lamp.Level));
            }

            _lampTimer.Start();
            _rayLight.DispatchLamps(_slots, volumeCount, i, gpu.LightA, CollectionsMarshal.AsSpan(_volumeLampScratch),
                                    ambient, st.Light.Work!, n);
            _lampTimer.Stop();
        }

        // Bounce reads every volume's direct light, so it runs after all of them are written.
        if (bounceOn)
        {
            for (int i = 0; i < volumeCount; i++)
            {
                _slotLight[i]  = _slotGpu[i]!.LightA;
                _slotSunVis[i] = _slotGpu[i]!.SunVis;
            }
            bool haveCam = CameraUtil.TryGetActive(_cameras, out var cam);
            float nearR = haveCam && _bounceNearRepeats > 1 ? _bounceNearRadius : -1f;
            for (int i = 0; i < volumeCount; i++)
            {
                var st = _slotState[i]!;
                int nb = BuildBounceList(st, _slots[i].VoxelToWorld, cam.Position, nearR);
                _lastBounceTotal += nb;
                if (nb == 0) continue;
                _bounceTimer.Start();
                _rayLight.DispatchBounce(_slots, _slotLight, _slotSunVis, volumeCount, i, sunDir, SunLight.Strength,
                                         _bounceAlbedo, _bounceRays, _bounceCycle, st.Bounce.Work!, nb);

                // Extra evaluations of the held bricks near the camera, in the same frame. Each reads the previous
                // one's result, so each adds a hop and more samples to the running average.
                var b = st.Bounce;
                for (int r = 1; r < _bounceNearRepeats && b.NearCount > 0; r++)
                {
                    UploadNearRepeat(st, r);
                    _rayLight.DispatchBounce(_slots, _slotLight, _slotSunVis, volumeCount, i, sunDir, SunLight.Strength,
                                             _bounceAlbedo, _bounceRays, _bounceCycle, b.NearWork!, b.NearCount);
                    _lastNearTotal += b.NearCount;
                }
                _bounceTimer.Stop();
            }
        }

        _rtSunMsEma    = Ema(_rtSunMsEma, _sunTimer.Elapsed.TotalMilliseconds);
        _rtLampMsEma   = Ema(_rtLampMsEma, _lampTimer.Elapsed.TotalMilliseconds);
        _rtBounceMsEma = Ema(_rtBounceMsEma, _bounceTimer.Elapsed.TotalMilliseconds);
    }

    private static bool IsNear(uint e, in Mat4 voxelToWorld, Vector3D<float> camPos, float r2)
    {
        var c = new Vector3D<float>((e & 1023u) * 8 + 4, ((e >> 10) & 1023u) * 8 + 4, (e >> 20) * 8 + 4);
        return Vector3D.DistanceSquared(voxelToWorld.TransformPoint(c), camPos) <= r2;
    }

    private static void AddNear(BounceWork b, uint e, uint evals)
    {
        if (2 * b.NearCount + 2 > b.NearScratch.Length) Array.Resize(ref b.NearScratch, b.NearScratch.Length * 2);
        b.NearScratch[2 * b.NearCount] = e;
        b.NearScratch[2 * b.NearCount + 1] = evals;
        b.NearCount++;
    }

    /// <summary>Uploads the near-camera bricks as (brick, N + repeat) pairs for extra evaluation number
    /// <paramref name="repeat"/>. The queue orders this write after the previous repeat's dispatch.</summary>
    private void UploadNearRepeat(RayVolumeState st, int repeat)
    {
        var b = st.Bounce;
        int n = b.NearCount;
        EnsureWorkScratch(st, 2 * n);
        for (int k = 0; k < n; k++)
        {
            st.WorkScratch[2 * k] = b.NearScratch[2 * k];
            st.WorkScratch[2 * k + 1] = System.Math.Min(b.NearScratch[2 * k + 1] + (uint)repeat, 255u);
        }
        if (2 * n > b.NearCapacity)
        {
            b.NearWork?.Dispose();
            b.NearCapacity = System.Math.Max(2 * n, System.Math.Max(2048, b.NearCapacity * 2));
            b.NearWork = GpuBuffer.CreateStorage(_ctx, (ulong)b.NearCapacity * sizeof(uint));
        }
        b.NearWork!.Write<uint>(0, st.WorkScratch.AsSpan(0, 2 * n));
    }

    /// <summary>Before the light work list consumes this frame's dirty marks: hold the whole volume for bounce if
    /// it is fully dirty, otherwise every dirty brick and the bricks within bounce range of it.</summary>
    private static void HoldBounce(RayVolumeState st, int holdFrames, int rechangeN)
    {
        var b = st.Bounce;
        if (st.Light.All)
        {
            // Idle -> changed restarts the running average; changed again while held only caps it.
            b.AllN = b.AllFrames == 0 ? 0 : System.Math.Min(b.AllN, rechangeN);
            b.AllFrames = holdFrames;
            return;
        }
        if (!st.Light.AnyMarked || b.Hold.Length == 0) return;

        var dirty = st.Light.Dirty;
        const int m = BounceMarginBricks;
        for (int z = 0; z < st.BD; z++)
        for (int y = 0; y < st.BH; y++)
        {
            int row = st.BW * (y + st.BH * z);
            for (int x = 0; x < st.BW; x++)
            {
                if (!dirty[row + x]) continue;
                int x0 = System.Math.Max(0, x - m), x1 = System.Math.Min(st.BW - 1, x + m);
                int y0 = System.Math.Max(0, y - m), y1 = System.Math.Min(st.BH - 1, y + m);
                int z0 = System.Math.Max(0, z - m), z1 = System.Math.Min(st.BD - 1, z + m);
                for (int zz = z0; zz <= z1; zz++)
                for (int yy = y0; yy <= y1; yy++)
                {
                    int r = st.BW * (yy + st.BH * zz);
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        // Held bricks are set to holdFrames + 1 below; skip ones already handled this frame.
                        if (b.Hold[r + xx] == holdFrames + 1) continue;
                        b.N[r + xx] = b.Hold[r + xx] == 0 ? (byte)0 : (byte)System.Math.Min(b.N[r + xx], rechangeN);
                        b.Hold[r + xx] = (byte)(holdFrames + 1);
                    }
                }
            }
        }
        b.AnyFrames = holdFrames + 1;
    }

    /// <summary>This frame's bounce bricks (whole volume while AllFrames lasts, otherwise the held bricks) as
    /// (packed brick, evaluations since it changed) pairs, counting holds down and evaluations up, uploaded to
    /// the bounce work buffer. Returns the number of bricks.</summary>
    /// Bricks whose centre is within <paramref name="nearRadius"/> of <paramref name="camPos"/> (world space; a
    /// negative radius disables it) are also collected into NearScratch for extra same-frame evaluations, and their
    /// N is advanced by the repeat count so the blend weight keeps falling.
    private int BuildBounceList(RayVolumeState st, in Mat4 voxelToWorld, Vector3D<float> camPos, float nearRadius)
    {
        var b = st.Bounce;
        int n = 0;
        b.NearCount = 0;
        float nearR2 = nearRadius * nearRadius;
        int extra = System.Math.Max(0, _bounceNearRepeats - 1);
        if (b.AllFrames > 0)
        {
            b.AllFrames--;
            uint evals = (uint)System.Math.Min(b.AllN, 255);
            b.AllN++;
            EnsureWorkScratch(st, st.SurfaceCount * 2);
            for (int k = 0; k < st.SurfaceCount; k++)
            {
                uint e = st.Surface[k];
                st.WorkScratch[2 * k] = e;
                st.WorkScratch[2 * k + 1] = evals;
                if (nearRadius >= 0f && IsNear(e, voxelToWorld, camPos, nearR2)) AddNear(b, e, evals);
            }
            n = st.SurfaceCount;
        }
        else if (b.AnyFrames > 0)
        {
            for (int k = 0; k < st.SurfaceCount; k++)
            {
                uint e = st.Surface[k];
                int idx = (int)(e & 1023u) + st.BW * ((int)((e >> 10) & 1023u) + st.BH * (int)(e >> 20));
                if (b.Hold[idx] == 0) continue;
                b.Hold[idx]--;
                EnsureWorkScratch(st, 2 * n + 2);
                st.WorkScratch[2 * n] = e;
                st.WorkScratch[2 * n + 1] = b.N[idx];
                if (nearRadius >= 0f && IsNear(e, voxelToWorld, camPos, nearR2))
                {
                    AddNear(b, e, b.N[idx]);
                    b.N[idx] = (byte)System.Math.Min(b.N[idx] + extra, 254);
                }
                if (b.N[idx] < 255) b.N[idx]++;
                n++;
            }
        }
        if (b.AnyFrames > 0 && --b.AnyFrames == 0) Array.Clear(b.Hold);
        if (n == 0) return 0;

        if (2 * n > b.WorkCapacity)
        {
            b.Work?.Dispose();
            b.WorkCapacity = System.Math.Max(2 * n, System.Math.Max(2048, b.WorkCapacity * 2));
            b.Work = GpuBuffer.CreateStorage(_ctx, (ulong)b.WorkCapacity * sizeof(uint));
        }
        b.Work!.Write<uint>(0, st.WorkScratch.AsSpan(0, 2 * n));
        return n;
    }

    /// <summary>Called every frame the ray-traced path is off: forget change history so turning it back on
    /// relights everything, and keep the per-volume changed-chunk queues from growing.</summary>
    private void ResetRayTracedTracking()
    {
        _rayWasActive = false;
        _staticWorld.VolumeGpu?.ChangedChunks.Clear();
        foreach (ref readonly var e in _grids.GetEntities())
            e.Get<DynamicGridComponent>().Grid.VolumeGpu?.ChangedChunks.Clear();
    }

    // ── What changed ───────────────────────────────────────────────────────────

    private void CollectOccluderChanges(int volumeCount, bool relightAll)
    {
        for (int i = 0; i < volumeCount; i++)
        {
            var st  = _slotState[i]!;
            var gpu = _slotGpu[i]!;

            if (ReferenceEquals(_slotVol[i], _staticWorld))
            {
                _dbgChangedChunks += gpu.ChangedChunks.Count;
                if (!relightAll && !st.Light.All)
                    foreach (var pos in gpu.ChangedChunks)
                        _occluderChanges.Add((pos.WorldOrigin, pos.WorldOrigin + new Vector3D<float>(ChunkData.Size)));
                gpu.ChangedChunks.Clear();
                continue;
            }

            gpu.ChangedChunks.Clear();
            WorldBounds(st, _slots[i].VoxelToWorld, out st.CurWorldMin, out st.CurWorldMax);
            bool moved = !st.HavePrev || _slotPos[i] != st.PrevPos || _slotRot[i] != st.PrevRot
                                      || gpu.OpacityVersion != st.PrevVersion;
            if (moved)
            {
                _dbgShipsMoved++;
                st.Light.All = true;
                if (st.HavePrev) _occluderChanges.Add((st.PrevWorldMin, st.PrevWorldMax));
                if (st.HasSolid) _occluderChanges.Add((st.CurWorldMin, st.CurWorldMax));
            }
            st.HavePrev     = st.HasSolid;
            st.PrevPos      = _slotPos[i];
            st.PrevRot      = _slotRot[i];
            st.PrevVersion  = gpu.OpacityVersion;
            st.PrevWorldMin = st.CurWorldMin;
            st.PrevWorldMax = st.CurWorldMax;
        }
    }

    /// <summary>Lamps that appeared, disappeared or moved since last frame: their full radius is dirty at the
    /// old and new position. Lamps are keyed by quantised world position + level.</summary>
    private void CollectLampChanges()
    {
        _curLampKeys.Clear();
        foreach (var lamp in _lamps)
        {
            var key = LampKey(lamp);
            _curLampKeys.Add(key);
            if (!_prevLampKeys.Contains(key)) _directChanges.Add(LampBox(lamp));
        }
        foreach (var lamp in _prevLamps)
            if (!_curLampKeys.Contains(LampKey(lamp))) _directChanges.Add(LampBox(lamp));

        (_prevLampKeys, _curLampKeys) = (_curLampKeys, _prevLampKeys);
        _prevLamps.Clear();
        _prevLamps.AddRange(_lamps);
    }

    private static (int, int, int, int) LampKey(WorldLamp l)
        => ((int)MathF.Round(l.World.X * 16f), (int)MathF.Round(l.World.Y * 16f), (int)MathF.Round(l.World.Z * 16f), l.Level);

    private static (Vector3D<float>, Vector3D<float>) LampBox(WorldLamp l)
    {
        var r = new Vector3D<float>(l.Level + 1.5f);
        return (l.World - r, l.World + r);
    }

    /// <summary>A changed occluder can change what any lamp in reach of it sees, anywhere in that lamp's radius.</summary>
    private void MarkLampsTouching(Vector3D<float> mn, Vector3D<float> mx)
    {
        foreach (var lamp in _lamps)     { var (a, b) = LampBox(lamp); if (Overlaps(a, b, mn, mx)) _directChanges.Add((a, b)); }
        foreach (var lamp in _prevLamps) { var (a, b) = LampBox(lamp); if (Overlaps(a, b, mn, mx)) _directChanges.Add((a, b)); }
    }

    // ── Marking ────────────────────────────────────────────────────────────────

    /// <summary>Marks every surface brick overlapping the world-space box: per brick in the static world, the
    /// whole volume for a ship whose solid bounds it touches.</summary>
    private void MarkRegion(int volumeCount, Vector3D<float> mn, Vector3D<float> mx)
    {
        for (int i = 0; i < volumeCount; i++)
        {
            var st = _slotState[i]!;
            if (st.Light.All) continue;
            if (ReferenceEquals(_slotVol[i], _staticWorld))
            {
                var o = Min32(_slotGpu[i]!);
                MarkBricks(st, st.Light, mn - o, mx - o);
            }
            else if (st.HasSolid)
            {
                var pad = new Vector3D<float>(1.5f);
                if (Overlaps(mn, mx, st.CurWorldMin - pad, st.CurWorldMax + pad)) st.Light.All = true;
            }
        }
    }

    /// <summary>Marks the voxels whose sun ray passes through the box: every point p with p - t*sunDir in the
    /// box for some t >= 0, i.e. the box swept along the sun's travel direction. Swept in overlapping padded
    /// copies until it leaves the static world's volume.</summary>
    private void MarkSunShadow(int volumeCount, Vector3D<float> mn, Vector3D<float> mx, Vector3D<float> sunDir)
    {
        var world = _staticWorld.VolumeGpu;
        if (world == null) return;
        var wMin = Min32(world);
        var wMax = wMin + new Vector3D<float>(world.VW, world.VH, world.VD);
        var pad  = new Vector3D<float>(SweepPad);

        float maxT = Vector3D.Distance(wMin, wMax) + Vector3D.Distance(mn, mx);
        for (float t = 0f; t <= maxT; t += SweepStep)
        {
            var a = mn + sunDir * t - pad;
            var b = mx + sunDir * t + pad;
            if ((sunDir.X > 0 && a.X > wMax.X) || (sunDir.X < 0 && b.X < wMin.X) ||
                (sunDir.Y > 0 && a.Y > wMax.Y) || (sunDir.Y < 0 && b.Y < wMin.Y) ||
                (sunDir.Z > 0 && a.Z > wMax.Z) || (sunDir.Z < 0 && b.Z < wMin.Z))
                break; // moving away from the volume and already past it
            MarkRegion(volumeCount, a, b);
        }
    }

    private static void MarkBricks(RayVolumeState st, BrickWork set, Vector3D<float> vmin, Vector3D<float> vmax)
    {
        if (set.Dirty.Length == 0) return;   // ships have no per-brick grid
        int x0 = System.Math.Max(0, (int)MathF.Floor(vmin.X / 8f)), x1 = System.Math.Min(st.BW - 1, (int)MathF.Floor(vmax.X / 8f));
        int y0 = System.Math.Max(0, (int)MathF.Floor(vmin.Y / 8f)), y1 = System.Math.Min(st.BH - 1, (int)MathF.Floor(vmax.Y / 8f));
        int z0 = System.Math.Max(0, (int)MathF.Floor(vmin.Z / 8f)), z1 = System.Math.Min(st.BD - 1, (int)MathF.Floor(vmax.Z / 8f));
        if (x0 > x1 || y0 > y1 || z0 > z1) return;
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        {
            int row = st.BW * (y + st.BH * z);
            for (int x = x0; x <= x1; x++) set.Dirty[row + x] = true;
        }
        set.AnyMarked = true;
    }

    private static bool Overlaps(Vector3D<float> aMin, Vector3D<float> aMax, Vector3D<float> bMin, Vector3D<float> bMax)
        => aMin.X <= bMax.X && aMax.X >= bMin.X && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;

    /// <summary>World-space bounds of the volume's solid bricks, under its current voxel-to-world transform.</summary>
    private static void WorldBounds(RayVolumeState st, in Mat4 voxelToWorld, out Vector3D<float> mn, out Vector3D<float> mx)
    {
        mn = new Vector3D<float>(float.MaxValue);
        mx = new Vector3D<float>(float.MinValue);
        if (!st.HasSolid) { mn = mx = default; return; }
        for (int c = 0; c < 8; c++)
        {
            var local = new Vector3D<float>((c & 1) != 0 ? st.SolidMax.X : st.SolidMin.X,
                                            (c & 2) != 0 ? st.SolidMax.Y : st.SolidMin.Y,
                                            (c & 4) != 0 ? st.SolidMax.Z : st.SolidMin.Z);
            var w = voxelToWorld.TransformPoint(local);
            mn = Vector3D.Min(mn, w);
            mx = Vector3D.Max(mx, w);
        }
    }

    // ── Work lists ─────────────────────────────────────────────────────────────

    /// <summary>Copies this frame's dirty surface bricks of one set into its GPU work buffer, clears that
    /// set's dirty state, and returns how many there are.</summary>
    private int BuildWorkList(RayVolumeState st, BrickWork set)
    {
        int n = 0;
        if (set.All)
        {
            EnsureWorkScratch(st, st.SurfaceCount);
            Array.Copy(st.Surface, st.WorkScratch, st.SurfaceCount);
            n = st.SurfaceCount;
        }
        else if (set.AnyMarked)
        {
            for (int k = 0; k < st.SurfaceCount; k++)
            {
                uint e = st.Surface[k];
                int idx = (int)(e & 1023u) + st.BW * ((int)((e >> 10) & 1023u) + st.BH * (int)(e >> 20));
                if (!set.Dirty[idx]) continue;
                EnsureWorkScratch(st, n + 1);
                st.WorkScratch[n++] = e;
            }
        }

        set.All = false;
        if (set.AnyMarked) { Array.Clear(set.Dirty); set.AnyMarked = false; }
        if (n == 0) return 0;

        if (n > set.WorkCapacity)
        {
            set.Work?.Dispose();
            set.WorkCapacity = System.Math.Max(n, System.Math.Max(1024, set.WorkCapacity * 2));
            set.Work = GpuBuffer.CreateStorage(_ctx, (ulong)set.WorkCapacity * sizeof(uint));
        }
        set.Work!.Write<uint>(0, st.WorkScratch.AsSpan(0, n));
        return n;
    }

    private static void EnsureWorkScratch(RayVolumeState st, int n)
    {
        if (st.WorkScratch.Length < n) Array.Resize(ref st.WorkScratch, System.Math.Max(n, st.WorkScratch.Length * 2));
    }

    // ── Surface-brick lists ────────────────────────────────────────────────────

    // Brick bit layout within a chunk: bit = bx + 4*(by + 4*bz), bricks 8³, 4 per axis.
    private const ulong BxLo = 0x1111111111111111UL, BxHi = 0x8888888888888888UL; // bricks with bx == 0 / 3
    private const ulong ByLo = 0x000F000F000F000FUL, ByHi = 0xF000F000F000F000UL; // by == 0 / 3
    private const ulong BzLo = 0x000000000000FFFFUL, BzHi = 0xFFFF000000000000UL; // bz == 0 / 3

    /// <summary>
    /// The volume's tracking state, with its surface-brick list rebuilt when its opacity, allocation or
    /// loaded-chunk count changed. A brick is listed if it contains air and there is solid in it or in a
    /// face-adjacent brick (including across chunk boundaries) — a conservative superset of the bricks holding
    /// a surface air voxel, since a voxel's six neighbours are all in its own brick or a face-adjacent one.
    /// Chunks whose opacity hasn't been uploaded yet are skipped; their upload bumps OpacityVersion, which
    /// triggers the rebuild that adds them. A new allocation means fresh GPU buffers, so everything is dirty.
    /// </summary>
    private RayVolumeState RayStateFor(ChunkVolume vol, VolumeGpuResources gpu)
    {
        if (!_rayStates.TryGetValue(vol, out var st)) { st = new RayVolumeState(); _rayStates[vol] = st; }
        if (st.BuiltGeneration == gpu.Generation && st.BuiltVersion == gpu.OpacityVersion && st.BuiltLoaded == vol.LoadedCount)
            return st;

        if (st.BuiltGeneration != gpu.Generation)
        {
            _dbgReallocs++;
            st.Light.All = true;
            if (ReferenceEquals(vol, _staticWorld))
            {
                st.BW = gpu.VW / 8; st.BH = gpu.VH / 8; st.BD = gpu.VD / 8;
                st.Light.Dirty = new bool[st.BW * st.BH * st.BD];
                st.Bounce.Hold = new byte[st.BW * st.BH * st.BD];
                st.Bounce.N    = new byte[st.BW * st.BH * st.BD];
                st.Light.AnyMarked = false;
                st.Bounce.AnyFrames = 0;
            }
        }

        int n = 0;
        var sMin = new Vector3D<float>(float.MaxValue);
        var sMax = new Vector3D<float>(float.MinValue);
        foreach (var (pos, e) in vol.All)
        {
            if (e.PackedOpacityWords == null || !gpu.Contains(pos)) continue;
            var (vx, vy, vz) = gpu.ChunkVoxelBase(pos);

            ulong solid = e.BrickSolidMask;
            while (solid != 0)
            {
                int b = System.Numerics.BitOperations.TrailingZeroCount(solid);
                solid &= solid - 1;
                var lo = new Vector3D<float>(vx + (b & 3) * 8, vy + ((b >> 2) & 3) * 8, vz + (b >> 4) * 8);
                sMin = Vector3D.Min(sMin, lo);
                sMax = Vector3D.Max(sMax, lo + new Vector3D<float>(8f));
            }

            AppendBricks(st, ref n, vx, vy, vz, ActiveBricks(vol, pos, e));

            // Air next to this chunk's solid bricks in a chunk that doesn't exist (a ship only has chunks with
            // blocks in them, so the air under a hull sitting at the bottom of its chunk is in no chunk at all).
            // It is still inside the volume's margin and gets rendered, so it has to be lit.
            ulong s = e.BrickSolidMask;
            if (s == 0) continue;
            AddMissingNeighbour(vol, gpu, pos,  1, 0, 0, (s & BxHi) >> 3);
            AddMissingNeighbour(vol, gpu, pos, -1, 0, 0, (s & BxLo) << 3);
            AddMissingNeighbour(vol, gpu, pos, 0,  1, 0, (s & ByHi) >> 12);
            AddMissingNeighbour(vol, gpu, pos, 0, -1, 0, (s & ByLo) << 12);
            AddMissingNeighbour(vol, gpu, pos, 0, 0,  1, (s & BzHi) >> 48);
            AddMissingNeighbour(vol, gpu, pos, 0, 0, -1, (s & BzLo) << 48);
        }
        foreach (var (mpos, mask) in _missingNeighbours)
        {
            var (mx, my, mz) = gpu.ChunkVoxelBase(mpos);
            AppendBricks(st, ref n, mx, my, mz, mask);
        }
        _missingNeighbours.Clear();

        st.SurfaceCount = n;
        st.HasSolid = sMin.X <= sMax.X;
        st.SolidMin = sMin;
        st.SolidMax = sMax;
        st.BuiltGeneration = gpu.Generation;
        st.BuiltVersion    = gpu.OpacityVersion;
        st.BuiltLoaded     = vol.LoadedCount;
        return st;
    }

    // Missing chunks bordering solid bricks, with the bricks in them that border solid (merged, so a missing
    // chunk touched from two sides lists each brick once).
    private readonly Dictionary<ChunkPosition, ulong> _missingNeighbours = new();

    private void AddMissingNeighbour(ChunkVolume vol, VolumeGpuResources gpu, ChunkPosition pos, int dx, int dy, int dz, ulong mask)
    {
        if (mask == 0) return;
        var npos = new ChunkPosition(pos.X + dx, pos.Y + dy, pos.Z + dz);
        if (vol.GetEntry(npos) != null || !gpu.Contains(npos)) return;
        _missingNeighbours[npos] = _missingNeighbours.GetValueOrDefault(npos) | mask;
    }

    private static void AppendBricks(RayVolumeState st, ref int n, int vx, int vy, int vz, ulong mask)
    {
        uint bx0 = (uint)vx >> 3, by0 = (uint)vy >> 3, bz0 = (uint)vz >> 3;
        while (mask != 0)
        {
            int b = System.Numerics.BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            if (n == st.Surface.Length) Array.Resize(ref st.Surface, n * 2);
            st.Surface[n++] = (bx0 + (uint)(b & 3)) | ((by0 + (uint)((b >> 2) & 3)) << 10) | ((bz0 + (uint)(b >> 4)) << 20);
        }
    }

    private static ulong ActiveBricks(ChunkVolume vol, ChunkPosition pos, ChunkEntry e)
    {
        ulong s = e.BrickSolidMask;
        ulong near = s
            | ((s >> 1) & ~BxHi) | ((s << 1) & ~BxLo)   // solid at bx+1 / bx-1 within the chunk
            | ((s >> 4) & ~ByHi) | ((s << 4) & ~ByLo)   // by±1
            | (s >> 16) | (s << 16);                    // bz±1 (out-of-chunk bits shift out on their own)
        near |= (NeighbourSolid(vol, pos,  1, 0, 0) & BxLo) << 3;   // neighbour's bx=0 borders our bx=3
        near |= (NeighbourSolid(vol, pos, -1, 0, 0) & BxHi) >> 3;
        near |= (NeighbourSolid(vol, pos, 0,  1, 0) & ByLo) << 12;
        near |= (NeighbourSolid(vol, pos, 0, -1, 0) & ByHi) >> 12;
        near |= (NeighbourSolid(vol, pos, 0, 0,  1) & BzLo) << 48;
        near |= (NeighbourSolid(vol, pos, 0, 0, -1) & BzHi) >> 48;
        return e.BrickAirMask & near;
    }

    private static ulong NeighbourSolid(ChunkVolume vol, ChunkPosition pos, int dx, int dy, int dz)
    {
        var n = vol.GetEntry(new ChunkPosition(pos.X + dx, pos.Y + dy, pos.Z + dz));
        return n?.PackedOpacityWords != null ? n.BrickSolidMask : 0UL;
    }

    /// <summary>Disposes tracking state for volumes no longer being lit (e.g. a despawned ship).</summary>
    private void DropStaleRayStates(int volumeCount)
    {
        if (_rayStates.Count <= volumeCount) return;
        foreach (var vol in _rayStates.Keys.ToList())
        {
            if (Array.IndexOf(_slotVol, vol, 0, volumeCount) >= 0) continue;
            _rayStates[vol].Dispose();
            _rayStates.Remove(vol);
        }
    }
}
