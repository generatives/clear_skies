using ClearSkies.Engine.Math;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Rendering.WebGpu;
using ClearSkies.Engine.Voxels;
using Silk.NET.Maths;
using System.Runtime.InteropServices;

namespace ClearSkies.Engine.ECS;

// Ray-traced lighting: change tracking and dispatch. Each frame only the surface bricks (light slots) whose lighting
// can have changed are re-traced; every other voxel keeps its stored light.
//
// What makes a brick dirty:
//  - everything, on the first frame, when the sun direction changes, or on the debug panel's relight button;
//  - being newly given light storage (a chunk loaded or edited next to it);
//  - a changed occluder (a world chunk with solid in it that was loaded, edited or unloaded; a ship that moved or
//    was edited, at its old and new pose): the bricks right next to it, every brick whose sun ray passes through it
//    (its bounds swept along the sun direction), and the full radius of every lamp whose reach overlaps it;
//  - a lamp that appeared, disappeared or moved: its full radius, at old and new positions.
// A ship is all-or-nothing (it is small); the static world is tracked per brick.
// Bounce (and the ray AO it measures) follows the direct-light marks, held for a number of evaluations around
// every dirty brick, grown by the bounce ray length.
public sealed partial class GpuLightSystem
{
    /// <summary>Per-grid tracking.</summary>
    private sealed class GridLightState
    {
        public bool LightAll = true;          // relight every surface brick of the grid this frame
        public int BounceAllFrames, BounceAllN;
        public bool AllThisFrame;

        // Ships: last frame's pose / opacity version, and world-space bounds of their solid bricks.
        public bool HavePrev;
        public Vector3D<float> PrevPos, PrevWorldMin, PrevWorldMax, CurWorldMin, CurWorldMax;
        public Quaternion<float> PrevRot;
        public int PrevVersion = -1;
    }

    private readonly Dictionary<GridHandle, GridLightState> _gridStates = new();

    // Per light slot. Hold is how many more bounce evaluations the brick gets; N how many it has had since it went
    // from idle to changed (it picks the ray slice, N mod cycle, and the shader's blend weight, max(1/cycle,
    // 1/(N+1))). A brick changed again while still held has N capped at the re-change setting rather than reset,
    // so an area that changes every frame (a moving ship's shadow) keeps some smoothing instead of flickering.
    private bool[] _dirty = Array.Empty<bool>();
    private byte[] _hold = Array.Empty<byte>();
    private byte[] _n = Array.Empty<byte>();
    private bool[] _inHeld = Array.Empty<bool>();
    private int[] _holdStamp = Array.Empty<int>();
    private readonly List<int> _dirtyList = new();
    private readonly List<int> _heldList = new();
    private int _frame;

    private GpuBuffer? _lightWork, _bounceWork, _nearWork;
    private uint[] _scratch = new uint[4096];
    private uint[] _nearScratch = new uint[1024];
    private int _nearCount;

    private int _lastDirtyTotal, _lastBounceTotal, _lastNearTotal;
    private const int BounceMarginBricks = 2;  // bounce rays reach 16 voxels = 2 bricks

    private float _prevBounceAlbedo = -1f, _prevSunLevel = -1f;
    private bool _prevBounceEnabled;
    private int _prevBounceRays = -1, _prevBounceCycle = -1;

    private bool _rayWasActive, _relightRequested;
    private Vector3D<float> _prevSunDir;
    private readonly List<WorldLamp> _prevLamps = new();
    private HashSet<(int, int, int, int)> _prevLampKeys = new(), _curLampKeys = new();
    private readonly List<(Vector3D<float> min, Vector3D<float> max)> _occluderChanges = new();
    private readonly List<(Vector3D<float> min, Vector3D<float> max)> _directChanges = new();

    // Debug-panel breakdown of why bricks were relit this frame.
    private string _dbgFullReason = "";
    private int _dbgNewSlots, _dbgChangedChunks, _dbgShipsMoved, _dbgLampChanges;

    private const float SweepStep = 4f;   // voxels between successive sun-shadow sweep copies
    private const float SweepPad  = 3f;   // pad each copy by > SweepStep/2 plus sample jitter, so copies overlap
    private const int S = ChunkData.Size;

    private void RayTracedDispatch()
    {
        _frame++;
        _dbgChangedChunks = _dbgShipsMoved = 0;
        EnsureSlotArrays(_store.LightSlotCapacity);
        DropGoneGridStates();
        foreach (var lg in _lit)
            if (!_gridStates.ContainsKey(lg.Handle)) _gridStates[lg.Handle] = new GridLightState();

        var sunDir = SunLight.Direction;
        bool relightAll = !_rayWasActive || sunDir != _prevSunDir || _relightRequested;
        _dbgFullReason = !_rayWasActive ? "first frame" : sunDir != _prevSunDir ? "sun moved" : _relightRequested ? "requested" : "";
        _rayWasActive = true;
        _relightRequested = false;
        _prevSunDir = sunDir;

        // Freshly allocated light storage holds nothing yet: clear any state left by the slot's previous owner.
        _dbgNewSlots = _store.NewSlots.Count;
        foreach (int slot in _store.NewSlots)
        {
            _hold[slot] = 0;
            _n[slot] = 0;
            MarkSlot(slot);
        }
        _store.NewSlots.Clear();

        _occluderChanges.Clear();
        _directChanges.Clear();
        CollectOccluderChanges(relightAll);
        CollectLampChanges();
        _dbgLampChanges = _directChanges.Count;

        if (relightAll)
        {
            foreach (var st in _gridStates.Values) st.LightAll = true;
        }
        else
        {
            var pad = new Vector3D<float>(1.5f);
            foreach (var (mn, mx) in _occluderChanges)
            {
                MarkRegion(mn - pad, mx + pad);   // surface voxels right at the change
                MarkSunShadow(mn, mx, sunDir);    // voxels whose sun ray passes through it
                MarkLampsTouching(mn, mx);        // lamps whose rays may pass through it
            }
            foreach (var (mn, mx) in _directChanges) MarkRegion(mn, mx);
        }

        // Evaluations a changed area gets, rounded up to whole cycles so it always stops having covered each voxel's
        // complete ray set equally.
        int hold = System.Math.Min(64, (_bounceHoldFrames + _bounceCycle - 1) / _bounceCycle * _bounceCycle);

        // Bounce inputs that change what every surface receives (or which rays it fires): re-evaluate everything.
        bool bounceOn = _bounceEnabled;
        bool bounceReset = bounceOn && (!_prevBounceEnabled || _bounceAlbedo != _prevBounceAlbedo || SunLight.Level != _prevSunLevel
                                        || _bounceRays != _prevBounceRays || _bounceCycle != _prevBounceCycle);
        _prevBounceEnabled = bounceOn;
        _prevBounceAlbedo = _bounceAlbedo;
        _prevSunLevel = SunLight.Level;
        _prevBounceRays = _bounceRays;
        _prevBounceCycle = _bounceCycle;

        HoldBounce(hold, _bounceRechangeN, bounceReset);

        _sunTimer.Reset();
        _lampTimer.Reset();
        _bounceTimer.Reset();
        _lastBounceTotal = 0;
        _lastNearTotal = 0;
        int gridCount = _store.GridCount;

        int n = BuildLightWork();
        _lastDirtyTotal = n;        if (n > 0)
        {
            _sunTimer.Start();
            _rayLight.DispatchSun(_store, gridCount, sunDir, _lightWork!, n);
            _sunTimer.Stop();

            _lampTimer.Start();
            _rayLight.DispatchLamps(_store, gridCount, CollectionsMarshal.AsSpan(_lampVecs), _lightWork!, n);
            _lampTimer.Stop();
        }

        // Bounce reads every grid's direct light, so it runs after all of it is written.
        if (bounceOn)
        {
            bool haveCam = CameraUtil.TryGetActive(_cameras, out var cam);
            float nearR = haveCam && _bounceNearRepeats > 1 ? _bounceNearRadius : -1f;
            int nb = BuildBounceWork(cam.Position, nearR);
            _lastBounceTotal = nb;
            if (nb > 0)
            {
                _bounceTimer.Start();
                _rayLight.DispatchBounce(_store, gridCount, sunDir, SunLight.Strength, _bounceAlbedo, _bounceRays,
                                         _bounceCycle, _bounceWork!, nb);

                // Extra evaluations of the held bricks near the camera, in the same frame. Each reads the previous
                // one's result, so each adds a hop and more samples to the running average.
                for (int r = 1; r < _bounceNearRepeats && _nearCount > 0; r++)
                {
                    UploadNearRepeat(r);
                    _rayLight.DispatchBounce(_store, gridCount, sunDir, SunLight.Strength, _bounceAlbedo, _bounceRays,
                                             _bounceCycle, _nearWork!, _nearCount);
                    _lastNearTotal += _nearCount;
                }
                _bounceTimer.Stop();
            }
        }

        _rtSunMsEma    = Ema(_rtSunMsEma, _sunTimer.Elapsed.TotalMilliseconds);
        _rtLampMsEma   = Ema(_rtLampMsEma, _lampTimer.Elapsed.TotalMilliseconds);
        _rtBounceMsEma = Ema(_rtBounceMsEma, _bounceTimer.Elapsed.TotalMilliseconds);
    }

    // ── What changed ───────────────────────────────────────────────────────────

    private void CollectOccluderChanges(bool relightAll)
    {
        // World chunks: loads, edits and unloads. One with no solid before or after can't have shadowed anything;
        // the bricks it gained or lost storage for are already in NewSlots.
        foreach (var (grid, pos, solid) in _store.ChangedChunks)
        {
            if (!grid.IsWorld) continue;
            _dbgChangedChunks++;
            if (solid && !relightAll)
                _occluderChanges.Add((pos.WorldOrigin, pos.WorldOrigin + new Vector3D<float>(S)));
        }
        _store.ChangedChunks.Clear();

        foreach (var lg in _lit)
        {
            if (lg.Handle.IsWorld) continue;
            var st = _gridStates[lg.Handle];
            var h = lg.Handle;
            WorldBounds(h, lg.VoxelToWorld, out st.CurWorldMin, out st.CurWorldMax);
            bool moved = !st.HavePrev || lg.Pos != st.PrevPos || lg.Rot != st.PrevRot || h.Version != st.PrevVersion;
            if (moved)
            {
                _dbgShipsMoved++;
                st.LightAll = true;
                if (st.HavePrev) _occluderChanges.Add((st.PrevWorldMin, st.PrevWorldMax));
                if (h.HasSolid) _occluderChanges.Add((st.CurWorldMin, st.CurWorldMax));
            }
            st.HavePrev     = h.HasSolid;
            st.PrevPos      = lg.Pos;
            st.PrevRot      = lg.Rot;
            st.PrevVersion  = h.Version;
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
    /// whole grid for a ship whose solid bounds it touches.</summary>
    private void MarkRegion(Vector3D<float> mn, Vector3D<float> mx)
    {
        foreach (var lg in _lit)
        {
            var st = _gridStates[lg.Handle];
            if (st.LightAll) continue;
            if (lg.Handle.IsWorld)
            {
                MarkWorldBox(lg.Handle, mn, mx);
            }
            else if (lg.Handle.HasSolid)
            {
                var pad = new Vector3D<float>(1.5f);
                if (Overlaps(mn, mx, st.CurWorldMin - pad, st.CurWorldMax + pad)) st.LightAll = true;
            }
        }
    }

    /// <summary>Marks the static world's surface bricks overlapping a box (world space = its voxel space).</summary>
    private void MarkWorldBox(GridHandle world, Vector3D<float> mn, Vector3D<float> mx)
    {
        int bx0 = (int)MathF.Floor(mn.X / 8f), bx1 = (int)MathF.Floor(mx.X / 8f);
        int by0 = (int)MathF.Floor(mn.Y / 8f), by1 = (int)MathF.Floor(mx.Y / 8f);
        int bz0 = (int)MathF.Floor(mn.Z / 8f), bz1 = (int)MathF.Floor(mx.Z / 8f);
        ForEachSlotInBrickBox(world, bx0, by0, bz0, bx1, by1, bz1, MarkSlot);
    }

    /// <summary>Calls <paramref name="action"/> for every light slot in the inclusive brick-coordinate box.</summary>
    private static void ForEachSlotInBrickBox(GridHandle g, int bx0, int by0, int bz0, int bx1, int by1, int bz1, Action<int> action)
    {
        if (bx0 > bx1 || by0 > by1 || bz0 > bz1) return;
        // Clip to the grid's chunk box so a long sun sweep doesn't walk empty sky.
        if (!g.HasBox) return;
        int cx0 = System.Math.Max(bx0 >> 2, g.BoxMin.X), cx1 = System.Math.Min(bx1 >> 2, g.BoxMax.X);
        int cy0 = System.Math.Max(by0 >> 2, g.BoxMin.Y), cy1 = System.Math.Min(by1 >> 2, g.BoxMax.Y);
        int cz0 = System.Math.Max(bz0 >> 2, g.BoxMin.Z), cz1 = System.Math.Min(bz1 >> 2, g.BoxMax.Z);
        for (int cz = cz0; cz <= cz1; cz++)
        for (int cy = cy0; cy <= cy1; cy++)
        for (int cx = cx0; cx <= cx1; cx++)
        {
            if (!g.Chunks.TryGetValue(new ChunkPosition(cx, cy, cz), out var rec) || rec.BrickSlots == null) continue;
            int lx0 = System.Math.Max(bx0 - cx * 4, 0), lx1 = System.Math.Min(bx1 - cx * 4, 3);
            int ly0 = System.Math.Max(by0 - cy * 4, 0), ly1 = System.Math.Min(by1 - cy * 4, 3);
            int lz0 = System.Math.Max(bz0 - cz * 4, 0), lz1 = System.Math.Min(bz1 - cz * 4, 3);
            for (int z = lz0; z <= lz1; z++)
            for (int y = ly0; y <= ly1; y++)
            for (int x = lx0; x <= lx1; x++)
            {
                int slot = rec.BrickSlots[x + 4 * (y + 4 * z)];
                if (slot >= 0) action(slot);
            }
        }
    }

    /// <summary>Marks the voxels whose sun ray passes through the box: every point p with p - t*sunDir in the
    /// box for some t >= 0, i.e. the box swept along the sun's travel direction. Swept in overlapping padded
    /// copies until it leaves the loaded world.</summary>
    private void MarkSunShadow(Vector3D<float> mn, Vector3D<float> mx, Vector3D<float> sunDir)
    {
        var world = _staticWorld.Gpu;
        if (!world.HasBox) return;
        var wMin = world.BoxMin.WorldOrigin;
        var wMax = world.BoxMax.WorldOrigin + new Vector3D<float>(S);
        var pad  = new Vector3D<float>(SweepPad);

        float maxT = Vector3D.Distance(wMin, wMax) + Vector3D.Distance(mn, mx);
        for (float t = 0f; t <= maxT; t += SweepStep)
        {
            var a = mn + sunDir * t - pad;
            var b = mx + sunDir * t + pad;
            if ((sunDir.X > 0 && a.X > wMax.X) || (sunDir.X < 0 && b.X < wMin.X) ||
                (sunDir.Y > 0 && a.Y > wMax.Y) || (sunDir.Y < 0 && b.Y < wMin.Y) ||
                (sunDir.Z > 0 && a.Z > wMax.Z) || (sunDir.Z < 0 && b.Z < wMin.Z))
                break; // moving away from the world and already past it
            MarkRegion(a, b);
        }
    }

    private void MarkSlot(int slot)
    {
        if (_dirty[slot]) return;
        _dirty[slot] = true;
        _dirtyList.Add(slot);
    }

    private static bool Overlaps(Vector3D<float> aMin, Vector3D<float> aMax, Vector3D<float> bMin, Vector3D<float> bMax)
        => aMin.X <= bMax.X && aMax.X >= bMin.X && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;

    /// <summary>World-space bounds of a ship's solid bricks, under its current voxel-to-world transform.</summary>
    private static void WorldBounds(GridHandle h, in Mat4 voxelToWorld, out Vector3D<float> mn, out Vector3D<float> mx)
    {
        mn = new Vector3D<float>(float.MaxValue);
        mx = new Vector3D<float>(float.MinValue);
        if (!h.HasSolid) { mn = mx = default; return; }
        for (int c = 0; c < 8; c++)
        {
            var local = new Vector3D<float>((c & 1) != 0 ? h.SolidMax.X : h.SolidMin.X,
                                            (c & 2) != 0 ? h.SolidMax.Y : h.SolidMin.Y,
                                            (c & 4) != 0 ? h.SolidMax.Z : h.SolidMin.Z);
            var w = voxelToWorld.TransformPoint(local);
            mn = Vector3D.Min(mn, w);
            mx = Vector3D.Max(mx, w);
        }
    }

    // ── Bounce holds ───────────────────────────────────────────────────────────

    /// <summary>Before the light work list consumes this frame's marks: hold whole grids that are fully dirty, and
    /// every dirty brick of the others plus the bricks within bounce range of it.</summary>
    private void HoldBounce(int holdFrames, int rechangeN, bool resetAll)
    {
        foreach (var lg in _lit)
        {
            var st = _gridStates[lg.Handle];
            if (resetAll) { st.BounceAllFrames = holdFrames; st.BounceAllN = 0; }
            else if (st.LightAll)
            {
                // Idle -> changed restarts the running average; changed again while held only caps it.
                st.BounceAllN = st.BounceAllFrames == 0 ? 0 : System.Math.Min(st.BounceAllN, rechangeN);
                st.BounceAllFrames = holdFrames;
            }
        }

        const int m = BounceMarginBricks;
        foreach (int slot in _dirtyList)
        {
            int gi = _store.SlotGrid[slot];
            var g = _store.GridAt(gi);
            if (g == null || !_gridStates.TryGetValue(g, out var st) || st.LightAll) continue;
            var c = _store.SlotChunk[slot];
            int b = _store.SlotBrick[slot];
            int bx = c.X * 4 + (b & 3), by = c.Y * 4 + ((b >> 2) & 3), bz = c.Z * 4 + (b >> 4);
            ForEachSlotInBrickBox(g, bx - m, by - m, bz - m, bx + m, by + m, bz + m,
                                  s => HoldSlot(s, holdFrames, rechangeN));
        }
    }

    private void HoldSlot(int slot, int holdFrames, int rechangeN)
    {
        if (_holdStamp[slot] == _frame) return;
        _holdStamp[slot] = _frame;
        _n[slot] = _hold[slot] == 0 ? (byte)0 : (byte)System.Math.Min(_n[slot], rechangeN);
        _hold[slot] = (byte)holdFrames;
        if (!_inHeld[slot]) { _inHeld[slot] = true; _heldList.Add(slot); }
    }

    // ── Work lists ─────────────────────────────────────────────────────────────

    /// <summary>This frame's direct-light bricks (every brick of a fully dirty grid, plus the marked ones) into the
    /// light work buffer; clears the marks. Returns the count.</summary>
    private int BuildLightWork()
    {
        int n = 0;
        foreach (var lg in _lit)
        {
            var st = _gridStates[lg.Handle];
            st.AllThisFrame = st.LightAll;
            if (!st.LightAll) continue;
            foreach (int slot in lg.Handle.Slots) Push(ref n, (uint)slot);
            st.LightAll = false;
        }
        foreach (int slot in _dirtyList)
        {
            _dirty[slot] = false;
            if (_store.SlotGrid[slot] < 0) continue;
            var g = _store.GridAt(_store.SlotGrid[slot]);
            if (g != null && _gridStates.TryGetValue(g, out var st) && st.AllThisFrame) continue; // already listed
            Push(ref n, (uint)slot);
        }
        _dirtyList.Clear();

        if (n > 0) Upload(ref _lightWork, n);
        return n;
    }

    /// <summary>This frame's bounce bricks as (light slot, evaluations since it changed) pairs: every brick of a grid
    /// whose whole-grid hold lasts, plus the held bricks of the others. Counts holds down and evaluations up.
    /// Bricks whose centre is within <paramref name="nearRadius"/> of <paramref name="camPos"/> (a negative radius
    /// disables it) are also collected for extra same-frame evaluations, their N advanced by the repeat count so
    /// the blend weight keeps falling. Returns the number of bricks.</summary>
    private int BuildBounceWork(Vector3D<float> camPos, float nearRadius)
    {
        int n = 0;
        _nearCount = 0;
        float nearR2 = nearRadius * nearRadius;
        int extra = System.Math.Max(0, _bounceNearRepeats - 1);

        foreach (var lg in _lit)
        {
            var st = _gridStates[lg.Handle];
            st.AllThisFrame = st.BounceAllFrames > 0;
            if (!st.AllThisFrame) continue;
            st.BounceAllFrames--;
            uint evals = (uint)System.Math.Min(st.BounceAllN, 255);
            st.BounceAllN++;
            foreach (int slot in lg.Handle.Slots)
            {
                Push(ref n, (uint)slot);
                Push(ref n, evals);
                if (nearRadius >= 0f && IsNear(slot, lg.VoxelToWorld, camPos, nearR2)) AddNear((uint)slot, evals);
            }
        }

        int keep = 0;
        for (int k = 0; k < _heldList.Count; k++)
        {
            int slot = _heldList[k];
            int gi = _store.SlotGrid[slot];
            var g = gi >= 0 ? _store.GridAt(gi) : null;
            if (g == null || _hold[slot] == 0 || !_gridStates.TryGetValue(g, out var st))
            {
                _inHeld[slot] = false;
                _hold[slot] = 0;
                continue;
            }
            _heldList[keep++] = slot;
            if (st.AllThisFrame) continue; // evaluated with its whole grid

            _hold[slot]--;
            Push(ref n, (uint)slot);
            Push(ref n, _n[slot]);
            if (nearRadius >= 0f && IsNear(slot, g.VoxelToWorld, camPos, nearR2))
            {
                AddNear((uint)slot, _n[slot]);
                _n[slot] = (byte)System.Math.Min(_n[slot] + extra, 254);
            }
            if (_n[slot] < 255) _n[slot]++;
        }
        _heldList.RemoveRange(keep, _heldList.Count - keep);

        n /= 2;
        if (n > 0) Upload(ref _bounceWork, 2 * n);
        return n;
    }

    private bool IsNear(int slot, in Mat4 voxelToWorld, Vector3D<float> camPos, float r2)
    {
        var c = _store.SlotChunk[slot];
        int b = _store.SlotBrick[slot];
        var centre = new Vector3D<float>(c.X * S + (b & 3) * 8 + 4, c.Y * S + ((b >> 2) & 3) * 8 + 4, c.Z * S + (b >> 4) * 8 + 4);
        return Vector3D.DistanceSquared(voxelToWorld.TransformPoint(centre), camPos) <= r2;
    }

    private void AddNear(uint slot, uint evals)
    {
        if (2 * _nearCount + 2 > _nearScratch.Length) Array.Resize(ref _nearScratch, _nearScratch.Length * 2);
        _nearScratch[2 * _nearCount] = slot;
        _nearScratch[2 * _nearCount + 1] = evals;
        _nearCount++;
    }

    /// <summary>Uploads the near-camera bricks as (slot, N + repeat) pairs for extra evaluation number
    /// <paramref name="repeat"/>. The queue orders this write after the previous repeat's dispatch.</summary>
    private void UploadNearRepeat(int repeat)
    {
        int n = 0;
        for (int k = 0; k < _nearCount; k++)
        {
            Push(ref n, _nearScratch[2 * k]);
            Push(ref n, System.Math.Min(_nearScratch[2 * k + 1] + (uint)repeat, 255u));
        }
        Upload(ref _nearWork, n);
    }

    private void Push(ref int n, uint v)
    {
        if (n == _scratch.Length) Array.Resize(ref _scratch, _scratch.Length * 2);
        _scratch[n++] = v;
    }

    /// <summary>Writes the first <paramref name="n"/> scratch words to <paramref name="buf"/>, growing it first.</summary>
    private void Upload(ref GpuBuffer? buf, int n)
    {
        ulong bytes = (ulong)n * sizeof(uint);
        if (buf == null || buf.SizeBytes < bytes)
        {
            buf?.Dispose();
            buf = GpuBuffer.CreateStorage(_ctx, System.Math.Max(bytes * 2, 8192UL));
        }
        buf.Write<uint>(0, _scratch.AsSpan(0, n));
    }

    // ── Bookkeeping ────────────────────────────────────────────────────────────

    private void EnsureSlotArrays(int capacity)
    {
        if (_dirty.Length >= capacity) return;
        Array.Resize(ref _dirty, capacity);
        Array.Resize(ref _hold, capacity);
        Array.Resize(ref _n, capacity);
        Array.Resize(ref _inHeld, capacity);
        Array.Resize(ref _holdStamp, capacity);
    }

    /// <summary>Forgets tracking state for grids that were unregistered (a despawned ship).</summary>
    private void DropGoneGridStates()
    {
        if (_gridStates.Count == 0) return;
        List<GridHandle>? gone = null;
        foreach (var h in _gridStates.Keys)
            if (h.Index < 0) (gone ??= new()).Add(h);
        if (gone != null) foreach (var h in gone) _gridStates.Remove(h);
    }
}
