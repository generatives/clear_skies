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
    // Gradual bounce (see GpuLightSystem's settings): the evaluations a world brick has been granted since it last
    // changed (255: none yet), and the bricks granted fewer than a full hold, topped up as the camera nears them.
    private byte[] _given = Array.Empty<byte>();
    private bool[] _inCoarse = Array.Empty<bool>();
    private readonly List<int> _coarse = new();
    private int _coarseAt, _fullHold, _dbgTopUps;
    private Vector3D<float> _tierCam;
    private bool _tiersOn;
    // Marked bricks waiting for direct light, and held bricks waiting for bounce, nearest the camera first. Stale
    // entries (freed slots, holds run out) are dropped as they come up.
    private NearestQueue? _dirtyQueueField, _heldQueueField;
    private NearestQueue _dirtyQueue => _dirtyQueueField ??= new NearestQueue(SlotCentre);
    private NearestQueue _heldQueue => _heldQueueField ??= new NearestQueue(SlotCentre);
    private readonly List<(int slot, int bucket)> _putBack = new();
    private Func<int, bool>? _keepDirty, _keepHeld;
    private int _frame;
    private int _worldIndex = -1;
    private GridLightState? _worldState;

    // Every buffer written once per frame: the frame's dispatches go in one submit, after all the writes.
    private GpuBuffer? _lightWork, _bounceWork;
    private GpuBuffer?[] _nearWorks = new GpuBuffer?[4];
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

    private const float SweepPad  = 1.5f; // pad on the swept box, for the sun rays' sample jitter
    private const int S = ChunkData.Size;

    private void RayTracedDispatch()
    {
        _frame++;
        _dbgChangedChunks = _dbgShipsMoved = 0;
        EnsureSlotArrays(_store.LightSlotCapacity);
        DropGoneGridStates();
        foreach (var lg in _lit)
            if (!_gridStates.ContainsKey(lg.Handle)) _gridStates[lg.Handle] = new GridLightState();
        // The static world, for the per-slot fast paths (-1: not lit this frame).
        _worldIndex = _gridStates.TryGetValue(_staticVolume.Gpu, out _worldState) ? _staticVolume.Gpu.Index : -1;

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
            _given[slot] = byte.MaxValue;
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
            _marks.Flush(_markSlot);
        }
        _phaseTimer.Lap(2);

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
        _phaseTimer.Lap(3);
        _fullHold = hold;
        _tiersOn = haveCam;
        _tierCam = cam.Position;
        HoldBounce(hold, _bounceRechangeN, bounceReset);
        _phaseTimer.Lap(4);
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
            if (haveCam) TopUpNearing();
            float nearR = haveCam && _bounceNearRepeats > 1 ? _bounceNearRadius : -1f;
            nb = BuildBounceWork(cam.Position, nearR);
            _lastBounceTotal = nb;
            if (nb > 0) AddCompose(_scratch.AsSpan(0, 2 * nb), 2);
        }

        _phaseTimer.Lap(5);
        BuildLists(_composeList.AsSpan(0, _composeCount), sunDir);
        _phaseTimer.Lap(6);

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
                                                 _bounceCycle, _nearWorks[r]!, nr);
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
            _finalComposeWork = UploadWords(_finalComposeWork, _composeList.AsSpan(0, _composeCount));
            _rayLight.DispatchCompose(_store, _bounceEnabled ? _bounceScale : 0f, _finalComposeWork, _composeCount);
            _lampTimer.Stop();
        }
        _rayLight.Submit();

        _phaseTimer.Lap(7);
        _phaseTimer.Stop();
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
            _hold[slot] = (byte)Grant(slot, holdEvals);
            if (!_inHeld[slot]) { _inHeld[slot] = true; _heldQueue.Add(slot); }
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
        _marks.AddBox(world, bx0, by0, bz0, bx1, by1, bz1); // flushed into MarkSlot once marking is done
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
    /// box for some t >= 0, i.e. the box swept along the sun's travel direction, until it leaves the loaded world.
    /// When the change <paramref name="darkens"/> (a placed block may now shade something), the bounce along the
    /// sweep is cleared too.</summary>
    private void MarkSunShadow(Vector3D<float> mn, Vector3D<float> mx, Vector3D<float> sunDir, bool darkens)
    {
        // Ships: one test each against the whole sweep (the box moved along the sun direction, padded generously, as
        // MarkRegion's ship test is).
        var half = (mx - mn) * 0.5f + new Vector3D<float>(4.5f);
        var centre = (mn + mx) * 0.5f;
        foreach (var lg in _lit)
        {
            if (lg.Handle.IsWorld || !lg.Handle.HasSolid) continue;
            var st = _gridStates[lg.Handle];
            if (!st.LightAll && RayHitsBox(centre, sunDir, st.CurWorldMin - half, st.CurWorldMax + half)) st.LightAll = true;
        }

        var world = _staticVolume.Gpu;
        if (!world.HasBox || !_gridStates.TryGetValue(world, out var worldState)) return;
        if (worldState.LightAll && !darkens) return;
        var wMin = world.BoxMin.WorldOrigin;
        var wMax = world.BoxMax.WorldOrigin + new Vector3D<float>(S);

        // The world, one brick layer at a time along the sun's steepest axis: in each layer, the exact extent of the
        // (padded) box swept through it, so each layer is one thin box one brick deep instead of a run of
        // overlapping copies. Stops once the sweep has left the loaded world.
        var pad = new Vector3D<float>(SweepPad);
        Span<float> lo = stackalloc float[3], hi = stackalloc float[3], d = stackalloc float[3];
        Span<float> wLo = stackalloc float[3], wHi = stackalloc float[3];
        Span<int> b0 = stackalloc int[3], b1 = stackalloc int[3];
        Put(lo, mn - pad); Put(hi, mx + pad); Put(d, sunDir); Put(wLo, wMin); Put(wHi, wMax);
        int a = MathF.Abs(d[0]) >= MathF.Abs(d[1]) ? (MathF.Abs(d[0]) >= MathF.Abs(d[2]) ? 0 : 2)
                                                  : (MathF.Abs(d[1]) >= MathF.Abs(d[2]) ? 1 : 2);
        float da = d[a];
        int step = da > 0 ? 1 : -1;
        int j = (int)MathF.Floor((da > 0 ? lo[a] : hi[a]) / 8f);
        int jEnd = (int)MathF.Floor((da > 0 ? wHi[a] : wLo[a]) / 8f);
        for (; (jEnd - j) * step >= 0; j += step)
        {
            // Sweep distances at which the box overlaps this layer.
            float t0 = da > 0 ? (8f * j - hi[a]) / da : (8f * j + 8f - lo[a]) / da;
            float t1 = da > 0 ? (8f * j + 8f - lo[a]) / da : (8f * j - hi[a]) / da;
            t0 = MathF.Max(t0, 0f);
            if (t1 < t0) continue;
            bool gone = false;
            for (int i = 0; i < 3; i++)
            {
                if (i == a) { b0[i] = b1[i] = j; continue; }
                float l = lo[i] + MathF.Min(t0 * d[i], t1 * d[i]);
                float h = hi[i] + MathF.Max(t0 * d[i], t1 * d[i]);
                if ((d[i] >= 0 && l > wHi[i]) || (d[i] <= 0 && h < wLo[i])) gone = true; // past the world, moving away
                b0[i] = (int)MathF.Floor(l / 8f);
                b1[i] = (int)MathF.Floor(h / 8f);
            }
            if (gone) break;
            if (!worldState.LightAll) _marks.AddBox(world, b0[0], b0[1], b0[2], b1[0], b1[1], b1[2]);
            if (darkens)
                ClearBounceAround(new Vector3D<float>(b0[0] * 8f, b0[1] * 8f, b0[2] * 8f),
                                  new Vector3D<float>(b1[0] * 8f + 8f, b1[1] * 8f + 8f, b1[2] * 8f + 8f));
        }
    }

    private static void Put(Span<float> dst, Vector3D<float> v) { dst[0] = v.X; dst[1] = v.Y; dst[2] = v.Z; }

    // World bricks marked this frame, per chunk; and the delegates they are flushed into (made once).
    private readonly BrickMarks _marks = new(), _holdMarks = new();
    private Action<int>? _markSlotDelegate, _holdSlotDelegate;
    private Action<int> _markSlot => _markSlotDelegate ??= MarkSlot;
    private int _holdFramesNow, _rechangeNNow;

    private void MarkSlot(int slot)
    {
        if (_dirty[slot]) return;
        _dirty[slot] = true;
        _dirtyQueue.Add(slot);
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

        // The world bricks within bounce reach of every relit one, gathered per chunk so the overlapping reaches of
        // neighbouring bricks are visited once.
        const int m = BounceMarginBricks;
        var world = _staticVolume.Gpu;
        foreach (int slot in _relitList)
        {
            int gi = _store.SlotGrid[slot];
            if (_store.GridAt(gi) != world) continue;
            var c = _store.SlotChunk[slot];
            int b = _store.SlotBrick[slot];
            int bx = c.X * 4 + (b & 3), by = c.Y * 4 + ((b >> 2) & 3), bz = c.Z * 4 + (b >> 4);
            _holdMarks.AddBox(world, bx - m, by - m, bz - m, bx + m, by + m, bz + m);
        }
        if (_holdMarks.IsEmpty) return;
        _holdFramesNow = holdFrames;
        _rechangeNNow = rechangeN;
        _holdMarks.Flush(_holdSlotDelegate ??= s => HoldSlot(s, _holdFramesNow, _rechangeNNow));
    }

    private void HoldSlot(int slot, int holdFrames, int rechangeN)
    {
        if (_holdStamp[slot] == _frame) return;
        _holdStamp[slot] = _frame;
        _n[slot] = _hold[slot] == 0 ? (byte)0 : (byte)System.Math.Min(_n[slot], rechangeN);
        _hold[slot] = (byte)Grant(slot, holdFrames);
        if (!_inHeld[slot]) { _inHeld[slot] = true; _heldQueue.Add(slot); }
    }

    // ── Gradual bounce ─────────────────────────────────────────────────────────

    /// <summary>
    /// The evaluations a changed brick gets now: a full hold near the camera, fewer farther out (world bricks only;
    /// ships always get the full hold). A brick granted less is remembered, and <see cref="TopUpNearing"/> gives it
    /// the rest as the camera comes closer, continuing its running average (the same ray set, the next slices of it)
    /// rather than restarting it, so its bounce and AO sharpen in a few small steps instead of being relit.
    /// </summary>
    private int Grant(int slot, int full)
    {
        if (!_tiersOn || _store.SlotGrid[slot] != _worldIndex) return full;
        int grant = TierEvals(slot, full);
        _given[slot] = (byte)grant;
        if (grant < full && !_inCoarse[slot]) { _inCoarse[slot] = true; _coarse.Add(slot); }
        return grant;
    }

    /// <summary>The evaluations a world brick should have had at its distance from the camera, out of
    /// <paramref name="full"/>.</summary>
    private int TierEvals(int slot, int full)
    {
        float fullR = System.Math.Max(_bounceFullRadius, _bounceNearRadius);
        float midR = System.Math.Max(_bounceMidRadius, fullR);
        float d2 = Vector3D.DistanceSquared(BrickCentre(slot), _tierCam);
        int evals = d2 <= fullR * fullR ? full : d2 <= midR * midR ? _bounceMidEvals : _bounceFarEvals;
        return System.Math.Clamp(evals, 1, full);
    }

    /// <summary>
    /// Walks part of the list of bricks granted fewer than a full hold (all of it every 16 frames or so) and tops up
    /// those the camera has come closer to: their hold grows by the evaluations they're now owed, and their
    /// evaluation count carries on. Bricks that have had a full hold, were freed, or went to a ship leave the list.
    /// </summary>
    private void TopUpNearing()
    {
        _dbgTopUps = 0;
        if (_worldIndex < 0 || _coarse.Count == 0) return;
        int full = _fullHold;
        int budget = System.Math.Min(_coarse.Count, System.Math.Clamp(_coarse.Count / 16, 2048, 32768));
        for (int k = 0; k < budget && _coarse.Count > 0; k++)
        {
            if (_coarseAt >= _coarse.Count) _coarseAt = 0;
            int slot = _coarse[_coarseAt];
            if (_store.SlotGrid[slot] == _worldIndex && _given[slot] < full)
            {
                int want = TierEvals(slot, full);
                if (want > _given[slot])
                {
                    _hold[slot] = (byte)System.Math.Min(255, _hold[slot] + want - _given[slot]);
                    _given[slot] = (byte)want;
                    if (!_inHeld[slot]) { _inHeld[slot] = true; _heldQueue.Add(slot); }
                    _dbgTopUps++;
                }
                if (_given[slot] < full) { _coarseAt++; continue; }
            }
            _inCoarse[slot] = false;
            _coarse[_coarseAt] = _coarse[^1];
            _coarse.RemoveAt(_coarse.Count - 1);
        }
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

        // Marked bricks, nearest first up to the cap; the rest wait for later frames. Marks that no longer need work
        // (freed slots, bricks of a ship already listed whole) are dropped as they come up.
        _relitList.Clear();
        _dirtyQueue.Recentre(camPos, _keepDirty ??= slot => _dirty[slot]);
        while (_relitList.Count < _maxRelitPerFrame && _dirtyQueue.TryTake(out int slot))
        {
            if (!_dirty[slot]) continue;
            _dirty[slot] = false;
            var g = _store.SlotGrid[slot] >= 0 ? _store.GridAt(_store.SlotGrid[slot]) : null;
            if (g == null || !_gridStates.TryGetValue(g, out var st) || st.AllThisFrame) continue;
            _relitList.Add(slot);
            Push(ref n, (uint)slot);
        }
        _lastRelitWaiting = _dirtyQueue.Count;

        if (n > 0) Upload(ref _lightWork, n);
        return n;
    }

    private readonly List<int> _relitList = new(), _bounceChosen = new();
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

        // Held bricks of the other grids, nearest first up to the cap; the rest keep their hold for later frames.
        // Bounce cleared this frame is re-accumulated in full now, wherever it is: its display already dropped the old
        // bounce, so waiting would show as a dip.
        _bounceChosen.Clear();
        foreach (int slot in _clearSlots)
            if (IsHeldAlone(slot)) _bounceChosen.Add(slot);
        int forced = _bounceChosen.Count;
        _putBack.Clear();
        _heldQueue.Recentre(camPos, _keepHeld ??= IsHeld);
        while (_bounceChosen.Count - forced < _maxBouncedPerFrame && _heldQueue.TryTake(out int slot, out int bucket))
        {
            if (!IsHeld(slot)) continue;
            // Evaluated with its whole grid, or already chosen above: still held, so it goes back in the queue.
            // Otherwise chosen, and still put back: dropped when it next comes up if this frame uses up its hold.
            _putBack.Add((slot, bucket));
            if (IsHeldAlone(slot) && _clearStamp[slot] != _frame) _bounceChosen.Add(slot);
        }
        foreach (var (slot, bucket) in _putBack) _heldQueue.PutBack(slot, bucket);
        _lastBounceWaiting = _heldQueue.Count;
        // The hold counts evaluations, not frames: a near-camera brick's extra same-frame evaluations use it up too, so
        // with as many near evaluations per frame as the hold, a change near the camera is finished within its frame.
        foreach (int slot in _bounceChosen)
        {
            _hold[slot]--;
            Push(ref n, (uint)slot);
            Push(ref n, _n[slot]);
            int more = 0;
            if (_clearStamp[slot] == _frame || (nearRadius >= 0f && Vector3D.DistanceSquared(SlotCentre(slot), camPos) <= nearR2))
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

    /// <summary>Whether a held slot still needs bounce (its grid is still there and its hold isn't used up); if not,
    /// it is released.</summary>
    private bool IsHeld(int slot)
    {
        int gi = _store.SlotGrid[slot];
        if (gi >= 0 && gi == _worldIndex && _hold[slot] > 0) return true; // the common case, without the lookups
        var g = gi >= 0 ? _store.GridAt(gi) : null;
        if (g != null && _hold[slot] > 0 && _gridStates.ContainsKey(g)) return true;
        _inHeld[slot] = false;
        _hold[slot] = 0;
        return false;
    }

    /// <summary>Whether a slot is held and not evaluated with its whole grid this frame.</summary>
    private bool IsHeldAlone(int slot)
    {
        if (!_inHeld[slot] || _hold[slot] == 0) return false;
        int gi = _store.SlotGrid[slot];
        if (gi >= 0 && gi == _worldIndex) return !_worldState!.BounceAllThisFrame;
        var g = gi >= 0 ? _store.GridAt(gi) : null;
        return g != null && _gridStates.TryGetValue(g, out var st) && !st.BounceAllThisFrame;
    }

    /// <summary>World-space centre of a light slot's brick, for the queues.</summary>
    private Vector3D<float> SlotCentre(int slot)
    {
        int gi = _store.SlotGrid[slot];
        var g = gi >= 0 ? _store.GridAt(gi) : null;
        return g == null || g.IsWorld ? BrickCentre(slot) : BrickCentre(slot, g.VoxelToWorld);
    }

    private bool IsNear(int slot, in Mat4 voxelToWorld, Vector3D<float> camPos, float r2)
        => Vector3D.DistanceSquared(BrickCentre(slot, voxelToWorld), camPos) <= r2;

    /// <summary>Grid-space centre of a light slot's brick (world space for the static world).</summary>
    private Vector3D<float> BrickCentre(int slot)
    {
        var c = _store.SlotChunk[slot];
        int b = _store.SlotBrick[slot];
        return new Vector3D<float>(c.X * S + (b & 3) * 8 + 4, c.Y * S + ((b >> 2) & 3) * 8 + 4, c.Z * S + (b >> 4) * 8 + 4);
    }

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
    /// extra evaluation number <paramref name="repeat"/> (a buffer per repeat), and returns how many.</summary>
    private int UploadNearRepeat(int repeat)
    {
        int n = 0;
        for (int k = 0; k < _nearCount; k++)
        {
            if (_nearExtra[k] < repeat) continue;
            Push(ref n, _nearScratch[2 * k]);
            Push(ref n, System.Math.Min(_nearScratch[2 * k + 1] + (uint)repeat, 255u));
        }
        if (repeat >= _nearWorks.Length) Array.Resize(ref _nearWorks, repeat + 1);
        if (n > 0) Upload(ref _nearWorks[repeat], n);
        return n / 2;
    }

    // This frame's compose list: every slot the sun or bounce pass touched, once (stamped by frame).
    private uint[] _composeList = new uint[4096];
    private int _composeCount;
    private int[] _composeStamp = Array.Empty<int>();
    private GpuBuffer? _composeWork, _finalComposeWork;

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
        Array.Resize(ref _given, capacity);
        Array.Resize(ref _inCoarse, capacity);
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
