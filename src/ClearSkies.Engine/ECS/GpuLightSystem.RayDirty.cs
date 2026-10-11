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
// What makes a brick dirty (its direct light is redone):
//  - everything, on the first frame, when the sun direction changes, or on the debug panel's relight button;
//  - being newly given light storage (a chunk loaded or edited next to it);
//  - a changed occluder (a world chunk with solid in it that was loaded, edited or unloaded; a ship that moved or
//    was edited, at its old and new pose): the bricks right next to it, every brick whose sun ray passes through it
//    (its bounds swept along the sun direction), and the full radius of every lamp whose reach overlaps it; these are
//    relit the frame they change, past the cap, so a moving ship's shadow keeps up with it;
//  - a lamp that appeared, disappeared or moved: its full radius, at old and new positions.
// A ship is all-or-nothing (it is small) and always processed; the static world is tracked per brick, and its marked
// bricks wait in a queue that is worked through nearest the camera first, up to a per-frame cap, so a rush of chunk
// loads or a sun change spreads over frames instead of landing on one.
//
// Bounce (and the ray AO it measures) is the same for every kind of change; only the brick's distance from the camera
// matters. Every brick relit, and every brick within bounce reach of one, is touched: it gets the band's updates per
// change. An update is the band's hops run back to back in one frame (one dispatch each, each reading the last, so
// each adds a hop), together firing the band's groups of the voxel's directions, always the same ones. A brick updates
// on its turn, once every so many frames (the band's setting), staggered by brick (a ship's bricks together); each
// update reads the light the last one left, so a change gets updates x hops hops. A brick whose own voxels changed
// (or that had settled, its accumulation freed) restarts: an AO pass, then an update that replaces what was there,
// starting from the light it was showing (a settled brick's freed accumulation is seeded from its display). Any
// other brick carries on: its update replaces, or blends in at 1/2 if the brick was touched again and updated on its
// previous turn too (it keeps changing, as around a moving ship), so a fast-changing light fades between updates
// instead of stepping; once it stops changing, one more update replaces, so none of the old light is left. Only where
// a lamp was removed is bounce cleared, since there it would otherwise sustain itself.
public sealed partial class GpuLightSystem
{
    /// <summary>Per-grid tracking.</summary>
    private sealed class GridLightState
    {
        public bool LightAll = true;          // relight every surface brick of the grid this frame
        public bool AllThisFrame;             // relit whole this frame

        // Ships: last frame's pose / opacity version, and world-space bounds of their solid bricks.
        public bool HavePrev;
        public Vector3D<float> PrevPos, PrevWorldMin, PrevWorldMax, CurWorldMin, CurWorldMax;
        public Quaternion<float> PrevRot;
        public int PrevVersion = -1;

        // This frame: moved, and whether its blocks changed (or it appeared); for AO only, whether its rays can reach
        // another grid now or could last frame (only then does its AO change with its pose).
        public bool Moved, Edited, NearOther, WasNearOther;
    }

    private readonly Dictionary<GridHandle, GridLightState> _gridStates = new();

    // Per light slot. Pending: touched since its last update. Restart: its own voxels changed since then. Tail: its
    // last update blended in at 1/2, so it gets one more (replacing) even if nothing touches it again. Left: further
    // updates its last change is owed (updates per change). Last update: the frame of its last update, for whether it
    // updated on its previous turn.
    private bool[] _dirty = Array.Empty<bool>();
    private bool[] _pending = Array.Empty<bool>();
    private bool[] _restart = Array.Empty<bool>();
    private bool[] _tail = Array.Empty<bool>();
    private byte[] _left = Array.Empty<byte>();
    private int[] _lastUpdate = Array.Empty<int>();
    private bool[] _inHeld = Array.Empty<bool>();
    // Distance bands: the groups a world brick last fired (255: none yet), and the bricks that fired fewer than the
    // most any band fires, touched again as the camera nears them (see TopUpNearing).
    private byte[] _given = Array.Empty<byte>();
    private bool[] _inCoarse = Array.Empty<bool>();
    private readonly List<int> _coarse = new();
    private int _coarseAt, _dbgTopUps;
    private Vector3D<float> _tierCam;
    private bool _tiersOn;
    // Marked bricks waiting for direct light, and touched bricks waiting for an update, nearest the camera first. Stale
    // entries (freed slots, updates done) are dropped as they come up.
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
    private GpuBuffer?[] _stepWorks = new GpuBuffer?[4];
    private uint[] _scratch = new uint[4096];

    private int _lastDirtyTotal, _lastBounceTotal, _lastStepTotal;
    // The bricks within bounce ray reach of a brick, each way.
    private int BounceMarginBricks => (int)MathF.Ceiling(_bounceReach / 8f);
    // The stored bounce shown: none with AO only (the rays store none).
    private float ShownBounceScale => _bounceMode == BounceMode.Full ? _bounceScale : 0f;

    private float _prevBounceAlbedo = -1f, _prevSunLevel = -1f;
    private BounceMode _prevBounceMode = BounceMode.Off;
    private float _prevBounceReach = -1f;
    private int _prevBounceRays = -1, _prevBounceGroups = -1;

    private bool _rayWasActive, _relightRequested;
    private (float scale, float ambient, float ao) _prevShown = (-1f, -1f, -1f);
    private Vector3D<float> _prevSunDir;
    private readonly List<WorldLamp> _prevLamps = new();
    private HashSet<(int, int, int, int, int)> _prevLampKeys = new(), _curLampKeys = new();
    private readonly List<(Vector3D<float> min, Vector3D<float> max)> _bounceClears = new();
    private readonly List<int> _clearSlots = new();
    private GpuBuffer? _clearWork;
    // shape: the change altered the blocks right at it (a world chunk loaded, edited or unloaded), so the surface
    // bricks there restart; a ship's move alters no terrain.
    private readonly List<(Vector3D<float> min, Vector3D<float> max, bool shape)> _occluderChanges = new();
    private readonly List<(Vector3D<float> min, Vector3D<float> max)> _directChanges = new();
    // Moving ships: the world bounds of each at its old and new pose (everything within bounce reach of them is
    // touched: the hull blocks and reflects their rays).
    private readonly List<(Vector3D<float> min, Vector3D<float> max)> _shipMoves = new();

    // Bricks an occluder change marked this frame (stamped by frame): relit past the cap.
    private int[] _urgentStamp = Array.Empty<int>();
    private readonly List<int> _urgentMarked = new();

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
        ReleaseSettledAccs();
        DropGoneGridStates();
        foreach (var lg in _lit)
            if (!_gridStates.ContainsKey(lg.Handle)) _gridStates[lg.Handle] = new GridLightState();
        // The static world, for the per-slot fast paths (-1: not lit this frame).
        _worldIndex = _gridStates.TryGetValue(_staticVolume.Gpu, out _worldState) ? _staticVolume.Gpu.Index : -1;

        var sunDir = SunLight.Direction;
        // The bounce display scale, ambient and AO strength are baked into the composed display, so changing any of them
        // (or bounce on/off) recomposes.
        var shown = (ShownBounceScale, RayLightingSettings.Ambient, RayLightingSettings.AoStrength);
        bool scaleChanged = shown != _prevShown;
        _prevShown = shown;
        bool relightAll = !_rayWasActive || sunDir != _prevSunDir || _relightRequested || scaleChanged;
        _dbgFullReason = !_rayWasActive ? "first frame" : sunDir != _prevSunDir ? "sun moved" : _relightRequested ? "requested"
                       : scaleChanged ? "display settings changed" : "";
        _rayWasActive = true;
        _relightRequested = false;
        _prevSunDir = sunDir;

        // Freshly allocated light storage holds nothing yet: clear any state left by the slot's previous owner.
        _dbgNewSlots = _store.NewSlots.Count;
        _newSlots.Clear();
        _newSlots.AddRange(_store.NewSlots);
        foreach (int slot in _store.NewSlots)
        {
            _pending[slot] = false;
            _restart[slot] = true;
            _tail[slot] = false;
            _left[slot] = 0;
            _lastUpdate[slot] = int.MinValue / 2;
            _given[slot] = byte.MaxValue;
            MarkSlot(slot);
        }
        _store.NewSlots.Clear();

        _occluderChanges.Clear();
        _shipMoves.Clear();
        _urgentMarked.Clear();
        _directChanges.Clear();
        _bounceClears.Clear();
        _shapeChanges.Clear();
        _shipEdits.Clear();
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
            // One set of marks at a time (see BrickMarks): the surface voxels right at a changed block, whose shape
            // changed, so they restart; then everything else an occluder change touches (right at a moving ship,
            // voxels whose sun ray passes through the change, the radius of lamps whose rays may), relit this frame;
            // then the lamp changes, queued.
            var pad = new Vector3D<float>(1.5f);
            foreach (var (mn, mx, shape) in _occluderChanges)
                if (shape) MarkRegion(mn - pad, mx + pad, _restartMarks);
            if (!_restartMarks.IsEmpty) _restartMarks.Flush(_restartSlotDelegate ??= slot => { _restart[slot] = true; MarkUrgent(slot); });
            foreach (var (mn, mx, shape) in _occluderChanges)
            {
                if (!shape) MarkRegion(mn - pad, mx + pad, _urgentMarks);
                MarkSunShadow(mn, mx, sunDir, _urgentMarks);
                foreach (var lamp in _lamps) { var (a, b) = LampBox(lamp); if (Overlaps(a, b, mn, mx)) MarkRegion(a, b, _urgentMarks); }
                foreach (var lamp in _prevLamps) { var (a, b) = LampBox(lamp); if (Overlaps(a, b, mn, mx)) MarkRegion(a, b, _urgentMarks); }
            }
            if (!_urgentMarks.IsEmpty) _urgentMarks.Flush(_urgentSlotDelegate ??= MarkUrgent);
            foreach (var (mn, mx) in _directChanges) MarkRegion(mn, mx);
            if (!_marks.IsEmpty) _marks.Flush(_markSlot);
        }
        _phaseTimer.Lap(2);

        // Bounce inputs that change what every surface receives (or which rays it fires): update everything. The
        // displayed light bounce rays read includes the AO-darkened ambient, so that counts too.
        // With AO only, nothing about direct light feeds the rays: only a change of what they measure counts.
        bool bounceOn = _bounceEnabled;
        bool bounceReset = bounceOn && (_bounceMode != _prevBounceMode || _bounceReach != _prevBounceReach
                                        || _bounceRays != _prevBounceRays || _bounceGroups != _prevBounceGroups
                                        || !_aoOnly && (_bounceAlbedo != _prevBounceAlbedo || SunLight.Level != _prevSunLevel || scaleChanged));
        _prevBounceMode = _bounceMode;
        _prevBounceReach = _bounceReach;
        _prevBounceAlbedo = _bounceAlbedo;
        _prevSunLevel = SunLight.Level;
        _prevBounceRays = _bounceRays;
        _prevBounceGroups = _bounceGroups;

        _sunTimer.Reset();
        _lampTimer.Reset();
        _bounceTimer.Reset();
        _lastBounceTotal = 0;
        _lastStepTotal = 0;
        bool haveCam = CameraUtil.TryGetActive(_cameras, out var cam);

        // All of this frame's work is chosen first (it depends only on CPU state), so the per-chunk lists can be built
        // once for every chunk any pass touches. Then the order is: sun (display sun bits) -> clear bounce where light
        // was removed -> compose what changed (so the bounce pass reads current direct light, not last frame's)
        // -> bounce updates (accumulation; reads the display at its hits) -> compose again (display colour and AO, from
        // lamp rays plus the new bounce). Every brick whose sun or bounce changed is recomposed.
        _composeCount = 0;
        _tiersOn = haveCam;
        _tierCam = cam.Position;
        CollectBounceClears(); // first, so the cleared bricks' direct light is redone this frame too
        int n = BuildLightWork(cam.Position);
        _lastDirtyTotal = n;
        _phaseTimer.Lap(3);
        TouchBounce(bounceReset);
        TouchBounceClears();
        _phaseTimer.Lap(4);
        if (n > 0) AddCompose(_scratch.AsSpan(0, n), 1);

        int nClear = _clearSlots.Count;
        if (nClear > 0)
        {
            var words = new uint[nClear];
            for (int k = 0; k < words.Length; k++) words[k] = (uint)_clearSlots[k];
            _clearWork = UploadWords(_clearWork, words);
            AddCompose(words, 1);
        }
        int preCompose = _composeCount;

        int steps = 0;
        if (bounceOn)
        {
            if (haveCam) TopUpNearing();
            steps = BuildBounceWork(cam.Position);
            _lastBounceTotal = steps > 0 ? _stepLen[0] / 2 : 0;
            if (steps > 0) AddCompose(_stepWords[0].AsSpan(0, _stepLen[0]), 2);
        }

        _phaseTimer.Lap(5);
        BuildLists(_composeList.AsSpan(0, _composeCount), sunDir);
        _phaseTimer.Lap(6);

        // Accumulation handed out this frame holds its last brick's data: zeroed before anything reads it.
        _store.FlushAccMap();
        if (_store.NewAccs.Count > 0)
        {
            var fresh = new uint[_store.NewAccs.Count];
            for (int k = 0; k < fresh.Length; k++) fresh[k] = (uint)_store.NewAccs[k];
            _zeroWork = UploadWords(_zeroWork, fresh);
            _rayLight.DispatchZeroAcc(_store, _zeroWork, fresh.Length);
            _store.NewAccs.Clear();
        }
        if (_seedSlots.Count > 0)
        {
            _seedWork = UploadWords(_seedWork, CollectionsMarshal.AsSpan(_seedSlots));
            _rayLight.DispatchSeedAcc(_store, ShownBounceScale, _seedWork, _seedSlots.Count);
            _seedSlots.Clear();
        }
        // With bounce on, a brick is composed only once it has accumulation (its bounce is updating): until
        // then it keeps its display (its sun, written by the sun pass, is current already).
        if (bounceOn)
        {
            int kept0 = 0;
            for (int k = 0; k < preCompose; k++)
                if (_store.AccOf[(int)_composeList[k]] >= 0) _preCompose[kept0++] = _composeList[k];
            preCompose = kept0;
        }

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
            _composeWork = UploadWords(_composeWork, _preCompose.AsSpan(0, preCompose));
            _rayLight.DispatchCompose(_store, ShownBounceScale, RayLightingSettings.Ambient,
                                     RayLightingSettings.AoStrength, _composeWork, preCompose, smooth: false);
            _lampTimer.Stop();
        }

        if (bounceOn && steps > 0)
        {
            // The updates, one dispatch per step: every brick's first step (its AO pass, or its update's first hop),
            // then each brick's next, and so on. Each step reads what the one before wrote, so each adds a hop; a hit
            // reads the stored bounce directly, so no compose is needed between them, except after an AO pass: its AO
            // has to reach the display before the update reads those walls.
            _bounceTimer.Start();
            for (int r = 0; r < steps; r++)
            {
                if (r == 1 && _aoPassCount > 0)
                {
                    _lampTimer.Start();
                    _aoPassWork = UploadWords(_aoPassWork, _aoPassed.AsSpan(0, _aoPassCount));
                    _rayLight.DispatchCompose(_store, ShownBounceScale, RayLightingSettings.Ambient,
                                             RayLightingSettings.AoStrength, _aoPassWork, _aoPassCount, smooth: false);
                    _lampTimer.Stop();
                }
                var buf = r == 0 ? _bounceWork : _stepWorks[r];
                _rayLight.DispatchBounce(_store, sunDir, SunLight.Strength, _bounceAlbedo, _bounceRays,
                                         _bounceGroups, _bounceReach, _aoOnly, buf!, _stepLen[r] / 2);
                if (r > 0) _lastStepTotal += _stepLen[r] / 2;
            }
            _bounceTimer.Stop();
        }

        // Everything the sun or bounce pass touched.
        int kept = 0;
        for (int k = 0; k < _composeCount; k++)
        {
            int slot = (int)_composeList[k];
            if (!bounceOn || _store.AccOf[slot] >= 0) _composeList[kept++] = _composeList[k];
        }
        _composeCount = kept;
        if (_composeCount > 0)
        {
            _lampTimer.Start();
            _finalComposeWork = UploadWords(_finalComposeWork, _composeList.AsSpan(0, _composeCount));
            _rayLight.DispatchCompose(_store, ShownBounceScale, RayLightingSettings.Ambient,
                                     RayLightingSettings.AoStrength, _finalComposeWork, _composeCount, smooth: true);
            _lampTimer.Stop();
        }
        _rayLight.Submit();

        _phaseTimer.Lap(7);
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
        foreach (var (grid, mn, mx, solid, _) in _store.ChangedChunks)
        {
            if (!grid.IsWorld) { _shipEdits.Add((grid, mn, mx)); continue; }
            _dbgChangedChunks++;
            if (solid && !relightAll) _occluderChanges.Add((mn, mx, true));
            if (solid) _shapeChanges.Add((mn, mx, true));
        }
        _store.ChangedChunks.Clear();

        foreach (var lg in _lit)
        {
            if (lg.Handle.IsWorld) continue;
            var st = _gridStates[lg.Handle];
            var h = lg.Handle;
            WorldBounds(h, lg.VoxelToWorld, out st.CurWorldMin, out st.CurWorldMax);
            // Compared with the last pose that counted as a move, so float noise on a still (or locked) ship doesn't
            // relight and restart it every frame, while slow drift still adds up to a move.
            bool moved = !st.HavePrev || h.Version != st.PrevVersion || PoseChanged(lg.Pos, lg.Rot, st.PrevPos, st.PrevRot);
            st.Moved = moved;
            st.Edited = !st.HavePrev || h.Version != st.PrevVersion;
            if (moved)
            {
                // What other grids' AO sees of it changed at both poses; restarted where its blocks changed.
                if (st.HavePrev) _shapeChanges.Add((st.PrevWorldMin, st.PrevWorldMax, st.Edited));
                if (h.HasSolid) _shapeChanges.Add((st.CurWorldMin, st.CurWorldMax, st.Edited));
                _dbgShipsMoved++;
                st.LightAll = true;
                // Like a placed block, the ship may now shade what it didn't: the bricks around both poses and along
                // both sun shadows are relit this frame, and everything within bounce reach of the hull is touched.
                if (st.HavePrev) { _occluderChanges.Add((st.PrevWorldMin, st.PrevWorldMax, false)); _shipMoves.Add((st.PrevWorldMin, st.PrevWorldMax)); }
                if (h.HasSolid) { _occluderChanges.Add((st.CurWorldMin, st.CurWorldMax, false)); _shipMoves.Add((st.CurWorldMin, st.CurWorldMax)); }
            }
            st.HavePrev     = h.HasSolid;
            st.PrevVersion  = h.Version;
            if (!moved) continue;
            st.PrevPos      = lg.Pos;
            st.PrevRot      = lg.Rot;
            st.PrevWorldMin = st.CurWorldMin;
            st.PrevWorldMax = st.CurWorldMax;
        }
    }

    /// <summary>A ship moves for lighting once it has gone this far (in blocks) from its last counted pose.</summary>
    private const float MovePosEpsilon = 1e-3f;
    /// <summary>... or turned this far: the vector part of the relative rotation, about half the angle (~2e-4 rad).</summary>
    private const float MoveRotEpsilon = 1e-4f;

    private static bool PoseChanged(Vector3D<float> pos, Quaternion<float> rot, Vector3D<float> prevPos, Quaternion<float> prevRot)
    {
        var d = pos - prevPos;
        if (Vector3D.Dot(d, d) > MovePosEpsilon * MovePosEpsilon) return true;
        var rel = Quaternion<float>.Concatenate(Quaternion<float>.Conjugate(prevRot), rot);
        return rel.X * rel.X + rel.Y * rel.Y + rel.Z * rel.Z > MoveRotEpsilon * MoveRotEpsilon;
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

    /// <summary>The slots inside this frame's bounce-clear boxes (per brick in the world, a whole ship it touches);
    /// see <see cref="TouchBounceClears"/>.</summary>
    private void CollectBounceClears()
    {
        _clearSlots.Clear();
        if (_bounceClears.Count == 0 || _aoOnly) return; // AO only stores no bounce to clear
        foreach (var (mn, mx) in _bounceClears)
        {
            foreach (var lg in _lit)
            {
                if (lg.Handle.IsWorld)
                    ForEachSlotInBrickBox(lg.Handle, (int)MathF.Floor(mn.X / 8f), (int)MathF.Floor(mn.Y / 8f), (int)MathF.Floor(mn.Z / 8f),
                                          (int)MathF.Floor(mx.X / 8f), (int)MathF.Floor(mx.Y / 8f), (int)MathF.Floor(mx.Z / 8f), _addClear);
                else
                {
                    var st = _gridStates[lg.Handle];
                    if (lg.Handle.HasSolid && Overlaps(mn, mx, st.CurWorldMin, st.CurWorldMax))
                        foreach (int slot in lg.Handle.Slots) AddClear(slot);
                }
            }
        }
    }

    /// <summary>The cleared slots restart, and update this frame wherever they are (see <see cref="BuildBounceWork"/>):
    /// their display already dropped the old bounce, so waiting would show as a dip.</summary>
    private void TouchBounceClears()
    {
        foreach (int slot in _clearSlots) Touch(slot, restart: true);
    }

    private void AddClear(int slot)
    {
        if (_clearStamp[slot] == _frame) return;
        _clearStamp[slot] = _frame;
        _clearSlots.Add(slot);
    }

    private Action<int>? _addClearDelegate;
    private Action<int> _addClear => _addClearDelegate ??= AddClear;
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

    // ── Marking ────────────────────────────────────────────────────────────────

    /// <summary>Marks every surface brick overlapping the world-space box: per brick in the static world, the
    /// whole grid for a ship whose solid bounds it touches. World bricks go into <paramref name="marks"/> (default
    /// <see cref="_marks"/>).</summary>
    private void MarkRegion(Vector3D<float> mn, Vector3D<float> mx, BrickMarks? marks = null)
    {
        foreach (var lg in _lit)
        {
            var st = _gridStates[lg.Handle];
            if (st.LightAll) continue;
            if (lg.Handle.IsWorld)
            {
                MarkWorldBox(lg.Handle, mn, mx, marks ?? _marks);
            }
            else if (lg.Handle.HasSolid)
            {
                var pad = new Vector3D<float>(1.5f);
                if (Overlaps(mn, mx, st.CurWorldMin - pad, st.CurWorldMax + pad)) st.LightAll = true;
            }
        }
    }

    /// <summary>Marks the static world's surface bricks overlapping a box (world space = its voxel space).</summary>
    private void MarkWorldBox(GridHandle world, Vector3D<float> mn, Vector3D<float> mx, BrickMarks marks)
    {
        int bx0 = (int)MathF.Floor(mn.X / 8f), bx1 = (int)MathF.Floor(mx.X / 8f);
        int by0 = (int)MathF.Floor(mn.Y / 8f), by1 = (int)MathF.Floor(mx.Y / 8f);
        int bz0 = (int)MathF.Floor(mn.Z / 8f), bz1 = (int)MathF.Floor(mx.Z / 8f);
        marks.AddBox(world, bx0, by0, bz0, bx1, by1, bz1); // flushed into MarkSlot once marking is done
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
    /// World bricks go into <paramref name="marks"/>.</summary>
    private void MarkSunShadow(Vector3D<float> mn, Vector3D<float> mx, Vector3D<float> sunDir, BrickMarks marks)
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
        if (worldState.LightAll) return;
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
            marks.AddBox(world, b0[0], b0[1], b0[2], b1[0], b1[1], b1[2]);
        }
    }

    private static void Put(Span<float> dst, Vector3D<float> v) { dst[0] = v.X; dst[1] = v.Y; dst[2] = v.Z; }

    // World bricks marked this frame, per chunk: queued for direct light (lamps), relit this frame past the cap
    // (occluder changes), and those also restarting (the surface right at a changed block); and the bounce touches.
    // Only one may be filling at a time (see BrickMarks).
    private readonly BrickMarks _marks = new(), _urgentMarks = new(), _restartMarks = new(), _touchMarks = new();
    private Action<int>? _markSlotDelegate, _urgentSlotDelegate, _restartSlotDelegate, _touchSlotDelegate, _touchRestartDelegate;
    private Action<int> _markSlot => _markSlotDelegate ??= MarkSlot;

    private void MarkSlot(int slot)
    {
        if (_dirty[slot]) return;
        _dirty[slot] = true;
        _dirtyQueue.Add(slot);
    }

    private void MarkUrgent(int slot)
    {
        MarkSlot(slot);
        if (_urgentStamp[slot] == _frame) return;
        _urgentStamp[slot] = _frame;
        _urgentMarked.Add(slot);
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

    // ── Bounce touches ─────────────────────────────────────────────────────────

    /// <summary>After this frame's direct-light bricks are chosen: touch the ships that were relit (all their bricks),
    /// and every world brick relit this frame plus the bricks within bounce range of it, and of a moving ship.
    /// <paramref name="resetAll"/> (a bounce setting changed) restarts everything.</summary>
    private void TouchBounce(bool resetAll)
    {
        foreach (var lg in _lit)
        {
            var st = _gridStates[lg.Handle];
            if (lg.Handle.IsWorld)
            {
                if (resetAll) foreach (int slot in lg.Handle.Slots) Touch(slot, restart: true);
                continue;
            }
            if (resetAll) { TouchGrid(lg.Handle, restart: true); continue; }
            if (_aoOnly)
            {
                // A ship's own AO changes with its blocks, and with its pose only near another grid its rays reach.
                st.WasNearOther = st.NearOther;
                st.NearOther = st.Moved && NearOtherGrid(lg, st);
                if (st.Edited) TouchEditedShip(lg.Handle);
                else if (st.Moved && (st.NearOther || st.WasNearOther)) TouchGrid(lg.Handle, restart: false);
            }
            else if (st.Edited) TouchEditedShip(lg.Handle);
            else if (st.AllThisFrame) TouchGrid(lg.Handle, restart: false);
        }

        if (_aoOnly) { TouchShapeChanges(); return; }

        // The world bricks within bounce reach of every relit one, gathered per chunk so the overlapping reaches of
        // neighbouring bricks are visited once, and everything within bounce reach of a moving ship (its hull blocks
        // and reflects their rays).
        int m = BounceMarginBricks;
        var world = _staticVolume.Gpu;
        foreach (int slot in _relitList)
        {
            if (_store.SlotGrid[slot] != _worldIndex) continue;
            var c = _store.SlotChunk[slot];
            int b = _store.SlotBrick[slot];
            int bx = c.X * 4 + (b & 3), by = c.Y * 4 + ((b >> 2) & 3), bz = c.Z * 4 + (b >> 4);
            _touchMarks.AddBox(world, bx - m, by - m, bz - m, bx + m, by + m, bz + m);
        }
        if (_worldIndex >= 0) foreach (var (mn, mx) in _shipMoves) AddBrickBox(_touchMarks, world, mn, mx, m);
        if (!_touchMarks.IsEmpty) _touchMarks.Flush(_touchSlotDelegate ??= s => Touch(s, restart: false));
    }

    private void TouchGrid(GridHandle g, bool restart)
    {
        foreach (int slot in g.Slots) Touch(slot, restart);
    }

    /// <summary>A ship whose blocks changed: only the bricks at the change (the edited blocks' bounds, a brick
    /// around) restart; the rest of the ship carries on, so its light doesn't drop back to a few hops' worth.</summary>
    private void TouchEditedShip(GridHandle g)
    {
        TouchGrid(g, restart: false);
        foreach (var (grid, mn, mx) in _shipEdits)
            if (grid == g)
                ForEachSlotInBrickBox(g, (int)MathF.Floor(mn.X / 8f) - 1, (int)MathF.Floor(mn.Y / 8f) - 1, (int)MathF.Floor(mn.Z / 8f) - 1,
                                      (int)MathF.Floor(mx.X / 8f) + 1, (int)MathF.Floor(mx.Y / 8f) + 1, (int)MathF.Floor(mx.Z / 8f) + 1,
                                      _restartSlot);
    }

    // Ships' changed blocks this frame, in grid space.
    private readonly List<(GridHandle grid, Vector3D<float> min, Vector3D<float> max)> _shipEdits = new();
    private Action<int>? _restartSlotField;
    private Action<int> _restartSlot => _restartSlotField ??= slot => _restart[slot] = true;

    /// <summary>
    /// AO only: touches the world bricks within ray reach of this frame's changed geometry (chunks loaded, edited or
    /// unloaded, ships that moved, and bricks newly given storage), whatever happened to direct light. Where blocks
    /// changed they restart; where a ship merely moved past, they carry on.
    /// </summary>
    private void TouchShapeChanges()
    {
        var world = _staticVolume.Gpu;
        if (_worldIndex < 0) return;
        int m = BounceMarginBricks;
        foreach (int slot in _newSlots)
        {
            if (_store.SlotGrid[slot] != _worldIndex) continue;
            var c = _store.SlotChunk[slot];
            int b = _store.SlotBrick[slot];
            int bx = c.X * 4 + (b & 3), by = c.Y * 4 + ((b >> 2) & 3), bz = c.Z * 4 + (b >> 4);
            _touchMarks.AddBox(world, bx - m, by - m, bz - m, bx + m, by + m, bz + m);
        }
        foreach (var (mn, mx, restart) in _shapeChanges)
            if (restart) AddBrickBox(_touchMarks, world, mn, mx, m);
        if (!_touchMarks.IsEmpty) _touchMarks.Flush(_touchRestartDelegate ??= s => Touch(s, restart: true));
        foreach (var (mn, mx, restart) in _shapeChanges)
            if (!restart) AddBrickBox(_touchMarks, world, mn, mx, m);
        if (!_touchMarks.IsEmpty) _touchMarks.Flush(_touchSlotDelegate ??= s => Touch(s, restart: false));
    }

    private static void AddBrickBox(BrickMarks marks, GridHandle world, Vector3D<float> mn, Vector3D<float> mx, int m)
        => marks.AddBox(world, (int)MathF.Floor(mn.X / 8f) - m, (int)MathF.Floor(mn.Y / 8f) - m, (int)MathF.Floor(mn.Z / 8f) - m,
                        (int)MathF.Floor(mx.X / 8f) + m, (int)MathF.Floor(mx.Y / 8f) + m, (int)MathF.Floor(mx.Z / 8f) + m);

    /// <summary>A brick needs updates; <paramref name="restart"/> when its own voxels changed. World bricks wait in the
    /// queue; a ship's are looked at with their ship every frame.</summary>
    private void Touch(int slot, bool restart)
    {
        if (restart) _restart[slot] = true;
        _pending[slot] = true;
        if (!_inHeld[slot] && _store.SlotGrid[slot] == _worldIndex) { _inHeld[slot] = true; _heldQueue.Add(slot); }
    }

    /// <summary>Whether a brick has an update to come: touched since its last, owed more for its last change, or owed
    /// the replacing one after a blend.</summary>
    private bool NeedsUpdate(int slot) => _pending[slot] || _left[slot] > 0 || _tail[slot];

    /// <summary>AO only: whether a ship's rays can reach another grid, at its pose this frame.</summary>
    private bool NearOtherGrid(in LitGrid ship, GridLightState st)
    {
        var pad = new Vector3D<float>(_bounceReach + 1f);
        Vector3D<float> mn = st.CurWorldMin - pad, mx = st.CurWorldMax + pad;
        foreach (var lg in _lit)
        {
            if (lg.Handle == ship.Handle) continue;
            if (lg.Handle.IsWorld)
            {
                bool any = false;
                ForEachSlotInBrickBox(lg.Handle, (int)MathF.Floor(mn.X / 8f), (int)MathF.Floor(mn.Y / 8f), (int)MathF.Floor(mn.Z / 8f),
                                      (int)MathF.Floor(mx.X / 8f), (int)MathF.Floor(mx.Y / 8f), (int)MathF.Floor(mx.Z / 8f), _ => any = true);
                if (any) return true;
            }
            else if (lg.Handle.HasSolid && _gridStates.TryGetValue(lg.Handle, out var other)
                     && Overlaps(mn, mx, other.CurWorldMin, other.CurWorldMax)) return true;
        }
        return false;
    }

    private readonly List<int> _newSlots = new();
    // AO only: this frame's changed geometry (world space), and whether its blocks changed (restart) or it moved.
    private readonly List<(Vector3D<float> min, Vector3D<float> max, bool restart)> _shapeChanges = new();

    // ── Distance bands ─────────────────────────────────────────────────────────

    /// <summary>The band of a point this far (squared) from the camera: 0 near, 1 middle, 2 far.</summary>
    private int BandOf(float d2)
    {
        float nearR = _bands[0].Radius, midR = System.Math.Max(_bands[1].Radius, nearR);
        return d2 <= nearR * nearR ? 0 : d2 <= midR * midR ? 1 : 2;
    }

    private int WorldBandOf(int slot) => _tiersOn ? BandOf(Vector3D.DistanceSquared(BrickCentre(slot), _tierCam)) : 0;

    /// <summary>The groups an update in this band fires (at least one, at most all of them).</summary>
    private int BandGroups(int band) => System.Math.Clamp(_bands[band].Groups, 1, System.Math.Max(_bounceGroups, 1));

    private int MostGroups() => System.Math.Max(BandGroups(0), System.Math.Max(BandGroups(1), BandGroups(2)));

    /// <summary>Whether it is this brick's turn (or ship's, by grid index) to update: once every band's frames,
    /// staggered by <paramref name="key"/> so the bricks share the frames between them.</summary>
    private bool Due(int key, int band)
    {
        int every = System.Math.Max(1, _bands[band].Every);
        return (int)((uint)(_frame + key) % (uint)every) == 0;
    }

    /// <summary>
    /// Walks part of the list of world bricks that last fired fewer groups than the most any band fires (all of it
    /// every 16 frames or so) and touches those the camera has come close enough to for more, so distant terrain
    /// costs a fraction of the bounce work and sharpens as it's approached. Bricks that have fired the most, were
    /// freed, or went to a ship leave the list.
    /// </summary>
    private void TopUpNearing()
    {
        _dbgTopUps = 0;
        if (_worldIndex < 0 || _coarse.Count == 0) return;
        int most = MostGroups();
        int budget = System.Math.Min(_coarse.Count, System.Math.Clamp(_coarse.Count / 16, 2048, 32768));
        for (int k = 0; k < budget && _coarse.Count > 0; k++)
        {
            if (_coarseAt >= _coarse.Count) _coarseAt = 0;
            int slot = _coarse[_coarseAt];
            if (_store.SlotGrid[slot] == _worldIndex && _given[slot] < most)
            {
                if (!_pending[slot] && BandGroups(WorldBandOf(slot)) > _given[slot])
                {
                    Touch(slot, restart: false);
                    _dbgTopUps++;
                }
                _coarseAt++;
                continue;
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
    /// <see cref="_relitList"/> for the bounce touches. Returns the count.
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
        // Bricks whose bounce is cleared this frame (a lamp removed) are relit now, past the cap: their bounce is
        // recomputed this frame and should see the new direct light. So are the bricks an occluder change marked: a
        // ship's sun shadow moves with it.
        _relitList.Clear();
        foreach (int slot in _clearSlots)
            if (_dirty[slot]) TakeDirty(slot, ref n);
        foreach (int slot in _urgentMarked)
            if (_dirty[slot]) TakeDirty(slot, ref n);
        int urgent = _relitList.Count;
        _dirtyQueue.Recentre(camPos, _keepDirty ??= slot => _dirty[slot]);
        while (_relitList.Count - urgent < _maxRelitPerFrame && _dirtyQueue.TryTake(out int slot))
            if (_dirty[slot]) TakeDirty(slot, ref n);
        _lastRelitWaiting = _dirtyQueue.Count;

        if (n > 0) Upload(ref _lightWork, n);
        return n;
    }

    private void TakeDirty(int slot, ref int n)
    {
        _dirty[slot] = false;
        var g = _store.SlotGrid[slot] >= 0 ? _store.GridAt(_store.SlotGrid[slot]) : null;
        if (g == null || !_gridStates.TryGetValue(g, out var st) || st.AllThisFrame) return;
        _relitList.Add(slot);
        Push(ref n, (uint)slot);
    }

    private readonly List<int> _relitList = new();
    private int _lastRelitWaiting, _lastBounceWaiting;

    /// <summary>
    /// This frame's updates, as step lists of (light slot, hop word) pairs: step 0 holds every updating brick's
    /// first hop (or AO pass), step 1 each one's next, and so on. Ships' bricks update on their ship's turn, every
    /// brick that needs one, past the cap (ships are small). World bricks: those cleared this frame update now, past
    /// the cap; the rest on their turn, nearest the camera first, until the frame's groups run out (the cap counts
    /// the groups updates fire, per brick, so it bounds the rays per group whatever the bands are). Returns the
    /// number of steps.
    /// </summary>
    private int BuildBounceWork(Vector3D<float> camPos)
    {
        for (int r = 0; r < _stepLen.Length; r++) _stepLen[r] = 0;
        _aoPassCount = 0;

        foreach (var lg in _lit)
        {
            if (lg.Handle.IsWorld) continue;
            var st = _gridStates[lg.Handle];
            int band = _tiersOn ? BandOf(BoxDistanceSquared(camPos, st.CurWorldMin, st.CurWorldMax)) : 0;
            bool due = Due(lg.Handle.Index, band);
            foreach (int slot in lg.Handle.Slots)
                if (NeedsUpdate(slot) && (due || _clearStamp[slot] == _frame)) UpdateBrick(slot, band);
        }

        foreach (int slot in _clearSlots)
            if (_store.SlotGrid[slot] == _worldIndex && NeedsUpdate(slot)) UpdateBrick(slot, WorldBandOf(slot));

        int spent = 0;
        _putBack.Clear();
        _heldQueue.Recentre(camPos, _keepHeld ??= IsHeld);
        while (spent < _maxGroupsPerFrame && _heldQueue.TryTake(out int slot, out int bucket))
        {
            if (!IsHeld(slot)) continue;
            _putBack.Add((slot, bucket)); // dropped when it next comes up if this update was its last
            if (_lastUpdate[slot] == _frame) continue;
            int band = WorldBandOf(slot);
            if (Due(slot, band)) spent += UpdateBrick(slot, band);
        }
        foreach (var (slot, bucket) in _putBack) _heldQueue.PutBack(slot, bucket);
        _lastBounceWaiting = _heldQueue.Count;

        int steps = 0;
        while (steps < _stepLen.Length && _stepLen[steps] > 0) steps++;
        for (int r = 0; r < steps; r++)
        {
            var words = _stepWords[r].AsSpan(0, _stepLen[r]);
            if (r == 0) _bounceWork = UploadWords(_bounceWork, words);
            else
            {
                if (r >= _stepWorks.Length) Array.Resize(ref _stepWorks, r + 1);
                _stepWorks[r] = UploadWords(_stepWorks[r], words);
            }
        }
        return steps;
    }

    /// <summary>
    /// Queues one update of a brick in this band, and returns the groups it costs (an AO pass counts as one). The
    /// band's hops each fire an equal share of its groups, always the same ones (from the first), so a partial set
    /// stays a fixed, coarser one rather than leaning toward whichever group ran last. The running average restarts
    /// with each update: hop i weighs 1/(i+1), so the update replaces what was there with its mean; blending in at
    /// 1/2 instead, hop i weighs 1/(2H - i), so each of the H counts 1/(2H) and the old light keeps half. An update
    /// for a fresh touch owes the band's further updates per change; each of those replaces, reading the light the
    /// last one left, so it adds the band's hops again.
    /// </summary>
    private int UpdateBrick(int slot, int band)
    {
        var b = _bands[band];
        int groups = BandGroups(band);
        int hops = System.Math.Clamp(b.Hops, 1, groups);
        int per = groups / hops;
        bool fresh = GiveAcc(slot);
        bool restart = fresh || _restart[slot];
        // With AO only there is no bounce for an AO pass to protect. Unchecking "AO pass for settled bricks" runs one
        // only where the brick's own voxels changed: a settled brick's first hop then reads walls not yet showing their
        // AO (as ships always did before the bands), which is brighter.
        bool aoPass = (_aoPassSettled ? restart : _restart[slot]) && !_aoOnly;
        // A brick given fresh accumulation (it had settled) starts from the light it was showing, whether or not its
        // own voxels changed: from nothing, the bricks around a placed block read only dark neighbours and settle a
        // few hops dim, and the rest of a settled ship reads them. Light a filled hole let in fades by the albedo each
        // hop (a few percent is left after one update); only a removed lamp clears bounce (clear_main). A brick new
        // to light storage shows nothing yet, so it starts from nothing.
        bool seed = fresh && !_aoOnly;
        if (seed) _seedSlots.Add((uint)slot);
        int every = System.Math.Max(1, b.Every);
        bool blend = !restart && _pending[slot] && every > 1 && _frame - _lastUpdate[slot] <= every;
        int step = 0;
        if (aoPass)
        {
            // The brick keeps its bounce (carried on, or seeded) through the AO pass: zeroing it would dim everything
            // whose hops read it, a whole small ship when one of its blocks changed.
            AddStep(step++, (uint)slot, AoPassWord(b.Spread, keepBounce: !fresh || seed));
            if (_aoPassCount == _aoPassed.Length) Array.Resize(ref _aoPassed, _aoPassed.Length * 2);
            _aoPassed[_aoPassCount++] = (uint)slot;
        }
        for (int i = 0; i < hops; i++)
            AddStep(step++, (uint)slot, HopWord(i * per, per, blend ? 2 * hops - i : i + 1, b.Spread));
        _tail[slot] = blend;
        // A fresh touch owes the band's further updates; a follow-up uses one up.
        int owed = _pending[slot] ? System.Math.Clamp(b.Updates, 1, 255) - 1 : _left[slot] - 1;
        _left[slot] = (byte)System.Math.Max(owed, 0);
        _pending[slot] = false;
        _restart[slot] = false;
        _lastUpdate[slot] = _frame;
        if (_store.SlotGrid[slot] == _worldIndex)
        {
            _given[slot] = (byte)(per * hops);
            if (per * hops < MostGroups() && !_inCoarse[slot]) { _inCoarse[slot] = true; _coarse.Add(slot); }
        }
        return per * hops + (aoPass ? 1 : 0);
    }

    /// <summary>A hop for the shader: fire <paramref name="count"/> groups from <paramref name="first"/> with rays per
    /// voxel 1/<paramref name="spread"/>, and blend the result in with weight 1/<paramref name="weightDenominator"/>
    /// (see GpuRayLightPass bounce_main).</summary>
    private static uint HopWord(int first, int count, int weightDenominator, int spread)
        => (uint)(first & 0xFF) | ((uint)(count & 0xFF) << 8) | ((uint)(weightDenominator & 0xFF) << 16) | SpreadBits(spread);

    /// <summary>An AO pass (see GpuRayLightPass bounceVoxel) with rays per voxel 1/<paramref name="spread"/>;
    /// <paramref name="keepBounce"/>: replace only AO, keeping the stored bounce.</summary>
    private static uint AoPassWord(int spread, bool keepBounce) => 0x80000000u | (keepBounce ? 0x40000000u : 0u) | SpreadBits(spread);

    // Bits 24-25: log2 of the spread (1, 2 or 4: all, half or a quarter of the rays per voxel).
    private static uint SpreadBits(int spread) => (uint)(spread >= 4 ? 2 : spread >= 2 ? 1 : 0) << 24;

    private void AddStep(int step, uint slot, uint eval)
    {
        if (step >= _stepLen.Length)
        {
            Array.Resize(ref _stepLen, step + 1);
            Array.Resize(ref _stepWords, step + 1);
        }
        _stepWords[step] ??= new uint[1024];
        if (_stepLen[step] + 2 > _stepWords[step].Length) Array.Resize(ref _stepWords[step], _stepWords[step].Length * 2);
        _stepWords[step][_stepLen[step]++] = slot;
        _stepWords[step][_stepLen[step]++] = eval;
    }

    private int[] _stepLen = new int[4];
    private uint[][] _stepWords = new uint[4][];
    // Bricks given an AO pass this frame, recomposed before their updates' hops (see RayTracedDispatch).
    private uint[] _aoPassed = new uint[1024];
    private int _aoPassCount;
    private GpuBuffer? _aoPassWork;

    /// <summary>Whether a queued world slot still needs an update (its grid is still there); if not, it leaves the queue.</summary>
    private bool IsHeld(int slot)
    {
        if (_store.SlotGrid[slot] == _worldIndex && _worldIndex >= 0 && NeedsUpdate(slot)) return true;
        _inHeld[slot] = false;
        return false;
    }

    /// <summary>World-space centre of a light slot's brick, for the queues.</summary>
    private Vector3D<float> SlotCentre(int slot)
    {
        int gi = _store.SlotGrid[slot];
        var g = gi >= 0 ? _store.GridAt(gi) : null;
        return g == null || g.IsWorld ? BrickCentre(slot) : BrickCentre(slot, g.VoxelToWorld);
    }

    private static float BoxDistanceSquared(Vector3D<float> p, Vector3D<float> mn, Vector3D<float> mx)
        => Vector3D.DistanceSquared(p, Vector3D.Clamp(p, mn, mx));

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

    // This frame's compose list: every slot the sun or bounce pass touched, once (stamped by frame).
    private uint[] _composeList = new uint[4096];
    private int _composeCount;
    private int[] _composeStamp = Array.Empty<int>();
    private GpuBuffer? _composeWork, _finalComposeWork;

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
        Array.Resize(ref _pending, capacity);
        Array.Resize(ref _restart, capacity);
        Array.Resize(ref _tail, capacity);
        Array.Resize(ref _left, capacity);
        Array.Resize(ref _lastUpdate, capacity);
        Array.Resize(ref _inHeld, capacity);
        Array.Resize(ref _given, capacity);
        Array.Resize(ref _inCoarse, capacity);
        Array.Resize(ref _composeStamp, capacity);
        Array.Resize(ref _clearStamp, capacity);
        Array.Resize(ref _urgentStamp, capacity);
        Array.Resize(ref _inAcc, capacity);
        if (_preCompose.Length < capacity) Array.Resize(ref _preCompose, capacity);
    }

    // ── Accumulation ───────────────────────────────────────────────────────────

    // Slots holding accumulation (GridStore.AccOf), so the settled ones can be found and freed.
    private readonly List<int> _accSlots = new();
    private bool[] _inAcc = Array.Empty<bool>();
    private uint[] _preCompose = Array.Empty<uint>();
    private GpuBuffer? _zeroWork, _seedWork;
    // Settled bricks updating again this frame, whose fresh accumulation starts from their display (see UpdateBrick).
    private readonly List<uint> _seedSlots = new();

    /// <summary>Gives a brick about to update its accumulation; true if it had none (it restarts).</summary>
    private bool GiveAcc(int slot)
    {
        if (!_store.AllocAcc(slot)) return false;
        if (!_inAcc[slot]) { _inAcc[slot] = true; _accSlots.Add(slot); }
        return true;
    }

    /// <summary>Frees the accumulation of bricks with no update to come (last frame's compose has read it): a settled
    /// brick keeps only its display.</summary>
    private void ReleaseSettledAccs()
    {
        for (int k = _accSlots.Count - 1; k >= 0; k--)
        {
            int slot = _accSlots[k];
            if (_store.AccOf[slot] >= 0 && NeedsUpdate(slot)) continue;
            _store.FreeAcc(slot);
            _inAcc[slot] = false;
            _accSlots[k] = _accSlots[^1];
            _accSlots.RemoveAt(_accSlots.Count - 1);
        }
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
