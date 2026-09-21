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
// A ship is all-or-nothing (it is small) and always processed; the static world is tracked per brick, and its marked
// bricks wait in a queue that is worked through nearest the camera first, up to a per-frame cap, so a burst of chunk
// loads or a sun change spreads over frames instead of landing on one.
// Bounce (and the ray AO it measures) follows the bricks actually relit, held for a number of evaluations around
// each, grown by the bounce ray length; held bricks are also evaluated nearest-first under their own cap.
public sealed partial class GpuLightSystem
{
    /// <summary>Per-grid tracking.</summary>
    private sealed class GridLightState
    {
        public bool LightAll = true;          // relight every surface brick of the grid this frame
        public int BounceAllFrames, BounceAllN;
        public bool AllThisFrame;             // relit whole this frame
        public bool BounceAllThisFrame;       // bounced whole this frame

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
    private float _prevShownScale = -1f;
    private Vector3D<float> _prevSunDir;
    private readonly List<WorldLamp> _prevLamps = new();
    private HashSet<(int, int, int, int, int)> _prevLampKeys = new(), _curLampKeys = new();
    private readonly List<(Vector3D<float> min, Vector3D<float> max)> _bounceClears = new();
    private readonly List<int> _clearSlots = new();
    private GpuBuffer? _clearWork;
    // darkens: a placed block, which can take light away (bounce there is then cleared rather than left to decay).
    private readonly List<(Vector3D<float> min, Vector3D<float> max, bool darkens)> _occluderChanges = new();
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
        // The bounce display scale is baked into the composed display, so changing it (or bounce on/off) recomposes.
        float shownScale = _bounceEnabled ? _bounceScale : 0f;
        bool scaleChanged = shownScale != _prevShownScale;
        _prevShownScale = shownScale;
        bool relightAll = !_rayWasActive || sunDir != _prevSunDir || _relightRequested || scaleChanged;
        _dbgFullReason = !_rayWasActive ? "first frame" : sunDir != _prevSunDir ? "sun moved" : _relightRequested ? "requested"
                       : scaleChanged ? "bounce display changed" : "";
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
        _bounceClears.Clear();
        CollectOccluderChanges(relightAll);
        CollectLampChanges();
        _dbgLampChanges = _directChanges.Count;

        if (relightAll)
        {
            // Ships relight whole this frame; the world's bricks all join the queue and go nearest-first.
            foreach (var lg in _lit)
            {
                if (lg.Handle.IsWorld) foreach (int slot in lg.Handle.Slots) MarkSlot(slot);
                else _gridStates[lg.Handle].LightAll = true;
            }
        }
        else
        {
            var pad = new Vector3D<float>(1.5f);
            foreach (var (mn, mx, darkens) in _occluderChanges)
            {
                MarkRegion(mn - pad, mx + pad);             // surface voxels right at the change
                MarkSunShadow(mn, mx, sunDir, darkens);     // voxels whose sun ray passes through it
                MarkLampsTouching(mn, mx, darkens);         // lamps whose rays may pass through it
                if (darkens) ClearBounceAround(mn - pad, mx + pad);
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

        _sunTimer.Reset();
        _lampTimer.Reset();
        _bounceTimer.Reset();
        _lastBounceTotal = 0;
        _lastNearTotal = 0;
        bool haveCam = CameraUtil.TryGetActive(_cameras, out var cam);

        // All of this frame's work is chosen first (it depends only on CPU state), so the per-chunk lists can be built
        // once for every chunk any pass touches. Then the order is: sun (display sun bits) -> clear bounce where light
        // may have dropped -> compose what changed (so the bounce pass reads current direct light, not last frame's)
        // -> bounce (accumulation; reads the display at its hits) -> compose again (display colour and AO, from lamp
        // rays plus the new bounce). Every brick whose sun or bounce changed is recomposed.
        _composeCount = 0;
        int n = BuildLightWork(cam.Position);
        _lastDirtyTotal = n;
        HoldBounce(hold, _bounceRechangeN, bounceReset);
        if (n > 0) AddCompose(_scratch.AsSpan(0, n), 1);

        CollectBounceClears(hold);
        int nClear = _clearSlots.Count;
        if (nClear > 0)
        {
            var words = new uint[nClear];
            for (int k = 0; k < words.Length; k++) words[k] = (uint)_clearSlots[k];
            _clearWork = UploadWords(_clearWork, words);
            AddCompose(words, 1);
        }
        int preCompose = _composeCount;

        int nb = 0;
        if (bounceOn)
        {
            float nearR = haveCam && _bounceNearRepeats > 1 ? _bounceNearRadius : -1f;
            nb = BuildBounceWork(cam.Position, nearR);
            _lastBounceTotal = nb;
            if (nb > 0) AddCompose(_scratch.AsSpan(0, 2 * nb), 2);
        }

        BuildLists(_composeList.AsSpan(0, _composeCount), sunDir);

        if (n > 0)
        {
            _sunTimer.Start();
            _rayLight.DispatchSun(_store, sunDir, _lightWork!, n);
            _sunTimer.Stop();
        }
        if (nClear > 0) _rayLight.DispatchClearBounce(_store, _clearWork!, nClear);
        if (preCompose > 0 && bounceOn)
        {
            _lampTimer.Start();
            _composeWork = UploadWords(_composeWork, _composeList.AsSpan(0, preCompose));
            _rayLight.DispatchCompose(_store, _bounceEnabled ? _bounceScale : 0f, _composeWork, preCompose);
            _lampTimer.Stop();
        }

        if (bounceOn)
        {
            if (nb > 0)
            {
                _bounceTimer.Start();
                _rayLight.DispatchBounce(_store, sunDir, SunLight.Strength, _bounceAlbedo, _bounceRays,
                                         _bounceCycle, _bounceWork!, nb);

                // Extra evaluations of the held bricks near the camera, in the same frame. Each reads the previous
                // one's result, so each adds a hop and more samples to the running average. Bounce rays read the
                // display at their hits, so the near bricks are recomposed before every repeat: otherwise a hit's
                // stale display (last frame's max of lamp and bounce) would hide a drop in its bounce until next
                // frame, and darkening would only spread one hop per frame.
                if (_bounceNearRepeats > 1 && _nearCount > 0)
                {
                    for (int k = 0; k < _nearCount; k++)
                    {
                        _nearSlots[k] = _nearScratch[2 * k];
                        _nearStamp[(int)_nearSlots[k]] = _frame;
                    }
                    _nearComposeWork = UploadWords(_nearComposeWork, _nearSlots.AsSpan(0, _nearCount));
                    for (int r = 1; r < _bounceNearRepeats; r++)
                    {
                        int nr = UploadNearRepeat(r);
                        if (nr == 0) break;
                        ComposeNear();
                        _rayLight.DispatchBounce(_store, sunDir, SunLight.Strength, _bounceAlbedo, _bounceRays,
                                                 _bounceCycle, _nearWork!, nr);
                        _lastNearTotal += nr;
                    }
                    ComposeNear();
                }
                _bounceTimer.Stop();
            }
        }

        // Everything else the sun or bounce pass touched (the near bricks were composed above).
        int kept = 0;
        for (int k = 0; k < _composeCount; k++)
            if (_nearStamp[(int)_composeList[k]] != _frame) _composeList[kept++] = _composeList[k];
        _composeCount = kept;
        if (_composeCount > 0)
        {
            _lampTimer.Start();
            _composeWork = UploadWords(_composeWork, _composeList.AsSpan(0, _composeCount));
            _rayLight.DispatchCompose(_store, _bounceEnabled ? _bounceScale : 0f, _composeWork, _composeCount);
            _lampTimer.Stop();
        }

        _rtSunMsEma    = Ema(_rtSunMsEma, _sunTimer.Elapsed.TotalMilliseconds);
        _rtLampMsEma   = Ema(_rtLampMsEma, _lampTimer.Elapsed.TotalMilliseconds);
        _rtBounceMsEma = Ema(_rtBounceMsEma, _bounceTimer.Elapsed.TotalMilliseconds);
    }

    // ── What changed ───────────────────────────────────────────────────────────

    private void CollectOccluderChanges(bool relightAll)
    {
        // World chunks: loads and unloads (the whole chunk) and edits (just the edited blocks' bounds). A chunk with no
        // solid before or after can't have shadowed anything; the bricks it gained or lost storage for are already in
        // NewSlots.
        foreach (var (grid, mn, mx, solid, darkens) in _store.ChangedChunks)
        {
            if (!grid.IsWorld) continue;
            _dbgChangedChunks++;
            if (solid && !relightAll) _occluderChanges.Add((mn, mx, darkens));
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
                if (st.HavePrev) _occluderChanges.Add((st.PrevWorldMin, st.PrevWorldMax, false));
                if (h.HasSolid) _occluderChanges.Add((st.CurWorldMin, st.CurWorldMax, false));
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
        {
            if (_curLampKeys.Contains(LampKey(lamp))) continue;
            var (a, b) = LampBox(lamp);
            _directChanges.Add((a, b));
            // Its light is gone, but bounce there sustains itself from neighbours still holding it: clear the bounce
            // within its reach plus the bounce ray length so it re-accumulates from what is left.
            var m = new Vector3D<float>(BounceMarginBricks * 8f);
            _bounceClears.Add((a - m, b + m));
        }

        (_prevLampKeys, _curLampKeys) = (_curLampKeys, _prevLampKeys);
        _prevLamps.Clear();
        _prevLamps.AddRange(_lamps);
    }

    /// <summary>The slots inside this frame's bounce-clear boxes (per brick in the world, a whole ship it touches):
    /// their bounce restarts from zero, held for a full set of evaluations.</summary>
    private void CollectBounceClears(int holdEvals)
    {
        _clearSlots.Clear();
        if (_bounceClears.Count == 0) return;
        foreach (var (mn, mx) in _bounceClears)
            foreach (var lg in _lit)
            {
                if (lg.Handle.IsWorld)
                    ForEachSlotInBrickBox(lg.Handle, (int)MathF.Floor(mn.X / 8f), (int)MathF.Floor(mn.Y / 8f), (int)MathF.Floor(mn.Z / 8f),
                                          (int)MathF.Floor(mx.X / 8f), (int)MathF.Floor(mx.Y / 8f), (int)MathF.Floor(mx.Z / 8f), AddClear);
                else
                {
                    var st = _gridStates[lg.Handle];
                    if (lg.Handle.HasSolid && Overlaps(mn, mx, st.CurWorldMin, st.CurWorldMax))
                        foreach (int slot in lg.Handle.Slots) AddClear(slot);
                }
            }
        foreach (int slot in _clearSlots)
        {
            // Restart the running average outright, even if the brick was already held this frame.
            _holdStamp[slot] = _frame;
            _n[slot] = 0;
            _hold[slot] = (byte)holdEvals;
            if (!_inHeld[slot]) { _inHeld[slot] = true; _heldList.Add(slot); }
        }
    }

    private void AddClear(int slot)
    {
        if (_clearStamp[slot] == _frame) return;
        _clearStamp[slot] = _frame;
        _clearSlots.Add(slot);
    }

    private int[] _clearStamp = Array.Empty<int>();

    // A lamp is its block: grid and grid-space voxel, plus level and colour (so replacing a lamp in place with a
    // different one counts as a removal and an addition). A lamp riding a moving ship keeps its key; the ship's own
    // movement handling relights around it.
    private static (int, int, int, int, int) LampKey(WorldLamp l)
        => (l.Grid, l.Local.X, l.Local.Y, l.Local.Z,
            l.Level | ((int)(l.Color.X * 255f) << 8) | ((int)(l.Color.Y * 255f) << 16) | ((int)(l.Color.Z * 127f) << 24));

    private static (Vector3D<float>, Vector3D<float>) LampBox(WorldLamp l)
    {
        var r = new Vector3D<float>(l.Level + 1.5f);
        return (l.World - r, l.World + r);
    }

    /// <summary>A changed occluder can change what any lamp in reach of it sees, anywhere in that lamp's radius. When
    /// it <paramref name="darkens"/> (a placed block may now block the lamp), the bounce there is cleared too.</summary>
    private void MarkLampsTouching(Vector3D<float> mn, Vector3D<float> mx, bool darkens)
    {
        foreach (var lamp in _lamps)
        {
            var (a, b) = LampBox(lamp);
            if (!Overlaps(a, b, mn, mx)) continue;
            _directChanges.Add((a, b));
            if (darkens) ClearBounceAround(a, b);
        }
        foreach (var lamp in _prevLamps) { var (a, b) = LampBox(lamp); if (Overlaps(a, b, mn, mx)) _directChanges.Add((a, b)); }
    }

    /// <summary>Clears bounce in a region whose light may have dropped, grown by the bounce ray length (everything
    /// whose bounce rays can reach into it). Bounce there sustains itself from neighbours still holding the old light,
    /// so left alone it only decays by the albedo per evaluation and freezes as a ghost when evaluation stops.</summary>
    private void ClearBounceAround(Vector3D<float> mn, Vector3D<float> mx)
    {
        var m = new Vector3D<float>(BounceMarginBricks * 8f);
        _bounceClears.Add((mn - m, mx + m));
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
    /// copies until it leaves the loaded world. When the change <paramref name="darkens"/> (a placed block may now
    /// shade something), the bounce along the sweep is cleared too.</summary>
    private void MarkSunShadow(Vector3D<float> mn, Vector3D<float> mx, Vector3D<float> sunDir, bool darkens)
    {
        // Ships: one test each against the whole sweep (the box moved along the sun direction, padded like the world
        // copies below and like MarkRegion's ship test), so the cost doesn't multiply by the number of sweep steps.
        var half = (mx - mn) * 0.5f + new Vector3D<float>(SweepPad + 1.5f);
        var centre = (mn + mx) * 0.5f;
        foreach (var lg in _lit)
        {
            if (lg.Handle.IsWorld || !lg.Handle.HasSolid) continue;
            var st = _gridStates[lg.Handle];
            if (!st.LightAll && RayHitsBox(centre, sunDir, st.CurWorldMin - half, st.CurWorldMax + half)) st.LightAll = true;
        }

        var world = _staticWorld.Gpu;
        if (!world.HasBox || !_gridStates.TryGetValue(world, out var worldState)) return;
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
            if (!worldState.LightAll) MarkWorldBox(world, a, b);
            if (darkens) ClearBounceAround(a, b);
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

    /// <summary>After this frame's direct-light bricks are chosen: hold whole ships that were relit, and every world
    /// brick relit this frame plus the bricks within bounce range of it. <paramref name="resetAll"/> (a bounce setting
    /// changed) restarts everything's running average.</summary>
    private void HoldBounce(int holdFrames, int rechangeN, bool resetAll)
    {
        foreach (var lg in _lit)
        {
            var st = _gridStates[lg.Handle];
            if (lg.Handle.IsWorld)
            {
                if (!resetAll) continue;
                foreach (int slot in lg.Handle.Slots) { _hold[slot] = 0; HoldSlot(slot, holdFrames, rechangeN); }
            }
            else if (resetAll) { st.BounceAllFrames = holdFrames; st.BounceAllN = 0; }
            else if (st.AllThisFrame)
            {
                // Idle -> changed restarts the running average; changed again while held only caps it.
                st.BounceAllN = st.BounceAllFrames == 0 ? 0 : System.Math.Min(st.BounceAllN, rechangeN);
                st.BounceAllFrames = holdFrames;
            }
        }

        const int m = BounceMarginBricks;
        foreach (int slot in _relitList)
        {
            int gi = _store.SlotGrid[slot];
            var g = _store.GridAt(gi);
            if (g == null || !g.IsWorld) continue;
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

    /// <summary>
    /// This frame's direct-light bricks into the light work buffer. Every brick of a ship marked whole goes in (ships
    /// are small and move every frame). Marked bricks go in nearest the camera first, up to the per-frame cap; the
    /// rest stay marked for later frames — deferred, never dropped. The marked bricks taken this frame are left in
    /// <see cref="_relitList"/> for the bounce holds. Returns the count.
    /// </summary>
    private int BuildLightWork(Vector3D<float> camPos)
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

        // Drop marks that no longer need work: freed slots, and bricks of a ship already listed whole.
        int keep = 0;
        for (int k = 0; k < _dirtyList.Count; k++)
        {
            int slot = _dirtyList[k];
            var g = _store.SlotGrid[slot] >= 0 ? _store.GridAt(_store.SlotGrid[slot]) : null;
            if (g == null || (_gridStates.TryGetValue(g, out var st) && st.AllThisFrame) || !_gridStates.ContainsKey(g))
            {
                _dirty[slot] = false;
                continue;
            }
            _dirtyList[keep++] = slot;
        }
        _dirtyList.RemoveRange(keep, _dirtyList.Count - keep);

        int take = SelectNearest(_dirtyList, _maxRelitPerFrame, camPos, _relitList);
        foreach (int slot in _relitList) { _dirty[slot] = false; Push(ref n, (uint)slot); }
        if (take < _dirtyList.Count)
        {
            // Keep the rest queued, in their original order.
            keep = 0;
            for (int k = 0; k < _dirtyList.Count; k++) if (_dirty[_dirtyList[k]]) _dirtyList[keep++] = _dirtyList[k];
            _dirtyList.RemoveRange(keep, _dirtyList.Count - keep);
        }
        else _dirtyList.Clear();
        _lastRelitWaiting = _dirtyList.Count;

        if (n > 0) Upload(ref _lightWork, n);
        return n;
    }

    /// <summary>Copies into <paramref name="chosen"/> the (at most <paramref name="cap"/>) slots of
    /// <paramref name="candidates"/> whose brick centres are nearest <paramref name="camPos"/>. Returns the count.</summary>
    private int SelectNearest(List<int> candidates, int cap, Vector3D<float> camPos, List<int> chosen)
    {
        chosen.Clear();
        int count = candidates.Count;
        if (count <= cap)
        {
            chosen.AddRange(candidates);
            return count;
        }
        if (_sortKeys.Length < count) { _sortKeys = new float[count * 2]; _sortSlots = new int[count * 2]; }
        for (int i = 0; i < count; i++)
        {
            int slot = candidates[i];
            var g = _store.GridAt(_store.SlotGrid[slot])!;
            _sortKeys[i] = Vector3D.DistanceSquared(BrickCentre(slot, g.VoxelToWorld), camPos);
            _sortSlots[i] = slot;
        }
        Array.Sort(_sortKeys, _sortSlots, 0, count);
        for (int i = 0; i < cap; i++) chosen.Add(_sortSlots[i]);
        return cap;
    }

    private float[] _sortKeys = Array.Empty<float>();
    private int[] _sortSlots = Array.Empty<int>();
    private readonly List<int> _relitList = new(), _bounceChosen = new(), _bounceCandidates = new(), _bounceForced = new();
    private int _lastRelitWaiting, _lastBounceWaiting;

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
            st.BounceAllThisFrame = st.BounceAllFrames > 0;
            if (!st.BounceAllThisFrame) continue;
            st.BounceAllFrames--;
            uint evals = (uint)System.Math.Min(st.BounceAllN, 255);
            st.BounceAllN++;
            foreach (int slot in lg.Handle.Slots)
            {
                Push(ref n, (uint)slot);
                Push(ref n, evals);
                if (nearRadius >= 0f && IsNear(slot, lg.VoxelToWorld, camPos, nearR2)) AddNear((uint)slot, evals, extra);
            }
        }

        // Held bricks of the other grids, nearest first up to the cap. The rest keep their hold for later frames.
        _bounceCandidates.Clear();
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
            if (st.BounceAllThisFrame) continue; // evaluated with its whole grid
            // Bounce cleared this frame is re-accumulated in full now, wherever it is: its display already dropped the
            // old bounce, so waiting would show as a dip.
            if (_clearStamp[slot] == _frame) _bounceForced.Add(slot);
            else _bounceCandidates.Add(slot);
        }
        _heldList.RemoveRange(keep, _heldList.Count - keep);

        SelectNearest(_bounceCandidates, _maxBouncedPerFrame, camPos, _bounceChosen);
        _lastBounceWaiting = _bounceCandidates.Count - _bounceChosen.Count;
        _bounceChosen.AddRange(_bounceForced);
        _bounceForced.Clear();
        // The hold counts evaluations, not frames: a near-camera brick's extra same-frame evaluations use it up too, so
        // with as many near evaluations per frame as the hold, a change near the camera is finished within its frame.
        foreach (int slot in _bounceChosen)
        {
            _hold[slot]--;
            Push(ref n, (uint)slot);
            Push(ref n, _n[slot]);
            var g = _store.GridAt(_store.SlotGrid[slot])!;
            int more = 0;
            if (_clearStamp[slot] == _frame || (nearRadius >= 0f && IsNear(slot, g.VoxelToWorld, camPos, nearR2)))
            {
                more = System.Math.Min(extra, (int)_hold[slot]);
                if (more > 0) AddNear((uint)slot, _n[slot], more);
                _hold[slot] -= (byte)more;
            }
            _n[slot] = (byte)System.Math.Min(_n[slot] + 1 + more, 255);
        }

        n /= 2;
        if (n > 0) Upload(ref _bounceWork, 2 * n);
        return n;
    }

    private bool IsNear(int slot, in Mat4 voxelToWorld, Vector3D<float> camPos, float r2)
        => Vector3D.DistanceSquared(BrickCentre(slot, voxelToWorld), camPos) <= r2;

    /// <summary>World-space centre of a light slot's brick.</summary>
    private Vector3D<float> BrickCentre(int slot, in Mat4 voxelToWorld)
    {
        var c = _store.SlotChunk[slot];
        int b = _store.SlotBrick[slot];
        var centre = new Vector3D<float>(c.X * S + (b & 3) * 8 + 4, c.Y * S + ((b >> 2) & 3) * 8 + 4, c.Z * S + (b >> 4) * 8 + 4);
        return voxelToWorld.TransformPoint(centre);
    }

    /// <summary>Queues a near-camera brick for <paramref name="extraEvals"/> more evaluations this frame, starting
    /// after evaluation number <paramref name="evals"/>.</summary>
    private void AddNear(uint slot, uint evals, int extraEvals)
    {
        if (2 * _nearCount + 2 > _nearScratch.Length) Array.Resize(ref _nearScratch, _nearScratch.Length * 2);
        if (_nearCount + 1 > _nearSlots.Length)
        {
            Array.Resize(ref _nearSlots, _nearSlots.Length * 2);
            Array.Resize(ref _nearExtra, _nearSlots.Length);
        }
        _nearScratch[2 * _nearCount] = slot;
        _nearScratch[2 * _nearCount + 1] = evals;
        _nearExtra[_nearCount] = extraEvals;
        _nearCount++;
    }

    private int[] _nearExtra = new int[1024];

    /// <summary>Uploads, as (slot, N + repeat) pairs, the near-camera bricks that still have an evaluation left for
    /// extra evaluation number <paramref name="repeat"/>, and returns how many. The queue orders this write after
    /// the previous repeat's dispatch.</summary>
    private int UploadNearRepeat(int repeat)
    {
        int n = 0;
        for (int k = 0; k < _nearCount; k++)
        {
            if (_nearExtra[k] < repeat) continue;
            Push(ref n, _nearScratch[2 * k]);
            Push(ref n, System.Math.Min(_nearScratch[2 * k + 1] + (uint)repeat, 255u));
        }
        if (n > 0) Upload(ref _nearWork, n);
        return n / 2;
    }

    // This frame's compose list: every slot the sun or bounce pass touched, once (stamped by frame).
    private uint[] _composeList = new uint[4096];
    private int _composeCount;
    private int[] _composeStamp = Array.Empty<int>();
    private GpuBuffer? _composeWork;

    // The near-camera bricks, recomposed after every bounce repeat (stamped so the final compose skips them).
    private uint[] _nearSlots = new uint[1024];
    private int[] _nearStamp = Array.Empty<int>();
    private GpuBuffer? _nearComposeWork;

    private void ComposeNear()
    {
        _lampTimer.Start();
        _rayLight.DispatchCompose(_store, _bounceEnabled ? _bounceScale : 0f, _nearComposeWork!, _nearCount);
        _lampTimer.Stop();
    }

    /// <summary>Adds the slots of a work list (every <paramref name="stride"/>th word) to the compose list.</summary>
    private void AddCompose(ReadOnlySpan<uint> work, int stride)
    {
        for (int k = 0; k < work.Length; k += stride)
        {
            int slot = (int)work[k];
            if (_composeStamp[slot] == _frame) continue;
            _composeStamp[slot] = _frame;
            if (_composeCount == _composeList.Length) Array.Resize(ref _composeList, _composeCount * 2);
            _composeList[_composeCount++] = (uint)slot;
        }
    }

    private GpuBuffer UploadWords(GpuBuffer? buf, ReadOnlySpan<uint> words)
    {
        ulong bytes = (ulong)words.Length * sizeof(uint);
        if (buf == null || buf.SizeBytes < bytes)
        {
            buf?.Dispose();
            buf = GpuBuffer.CreateStorage(_ctx, System.Math.Max(bytes * 2, 8192UL));
        }
        buf.Write<uint>(0, words);
        return buf;
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
        Array.Resize(ref _composeStamp, capacity);
        Array.Resize(ref _nearStamp, capacity);
        Array.Resize(ref _clearStamp, capacity);
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
