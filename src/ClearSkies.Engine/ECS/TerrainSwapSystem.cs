using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using System.Diagnostics;
using ClearSkies.Engine.Voxels;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;
using TerrainLevel = ClearSkies.Engine.ECS.ChunkLoadSystem.TerrainLevel;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Swaps between the levels of detail the chunk loader streams (see <see cref="ChunkLoadSystem.SetLevels"/>). A coarse
/// chunk is drawn until the eight chunks of the finer level under it are ready (loaded or air, and shown: full detail
/// meshed, a level's lit), then waits for those to show their own light and dissolves out over
/// <see cref="FadeSeconds"/> as they dissolve in, each pixel showing one or the other. Going the other way, the finer
/// chunks are held (the loader keeps them loaded though no longer wanted, see <see cref="Holds"/>) while the coarse
/// chunk dissolves in over them.
///
/// A coarse chunk is never shown before it's lit: until then it's left out, behind the fog, which follows the nearest
/// terrain not ready in any level (<see cref="FarReadyRadius"/>). Terrain waiting to be shown is lit first
/// (<see cref="ShowFirst"/>). While a finer level has a coarse chunk's eight, the coarse chunk is covered in the light
/// store (<see cref="SetCovered"/>): the finer grid's rays pass through it, so each level is lit by its own blocks.
///
/// Every level's chunks are drawn by what this says (<see cref="DrawState"/>, on each level's root as
/// <see cref="ChunkDrawStates"/>), full detail's while there are coarse levels.
/// </summary>
public sealed class TerrainSwapSystem : ISystem
{
    /// <summary>A coarse chunk's place in its swap with the finer level, and in the light store.</summary>
    private sealed class Swap
    {
        public bool Drawn;    // not all eight finer chunks under it are ready, or they are but aren't lit yet
        public bool Settled;  // in the light store and lit since it went in: its light maps follow the store
        public bool Baked;    // drawn lit from the store at least once
        public bool Covered;  // the finer level has it (see SetCovered)
        public bool WasReady; // its finer chunks were ready when last looked at, so they were drawn
        public int Waiting;   // frames spent settling
        public int Leaving;   // frames its finer chunks have been ready while it's drawn (0: they aren't)
        public float FadeAt = float.NegativeInfinity; // when it last swapped in (while drawn) or out
        public bool Quiet;    // done swapping and settled: looked at again only when something changes (see Update)
        public bool Parked;   // waiting only for its mesh or its place in the light store: looked at again when it comes
    }

    private const int S = ChunkData.Size;

    private readonly ChunkLoadSystem _load;
    private readonly EntitySet _interests;

    /// <summary>Each coarse level's swaps, one per loaded chunk; and each level's draw states.</summary>
    private readonly Dictionary<TerrainLevel, Dictionary<ChunkPosition, Swap>> _swaps = new();
    private readonly Dictionary<TerrainLevel, ChunkDrawStateOf> _drawStates = new();
    private static readonly Dictionary<ChunkPosition, Swap> None = new();

    public TerrainSwapSystem(World world, ChunkLoadSystem load)
    {
        _load = load;
        _interests = world.GetEntities().With<Transform>().With<TerrainInterest>().AsSet();
        load.KeepLoaded = Holds;
        // A chunk meshed or put in the light store: a coarse one's swap moves on; a full-detail one may be the last of
        // the eight a level-1 swap waits for.
        world.SubscribeComponentAdded((in Entity e, in ChunkMeshedFlag _) => Woken(e));
        world.SubscribeComponentAdded((in Entity e, in ChunkUploadedFlag _) => Woken(e));
        world.SubscribeComponentAdded((in Entity e, in LightmapsBakedFlag _) => Woken(e));
        // Coarse chunks loading and unloading, followed as they come (see Track).
        world.SubscribeComponentAdded((in Entity _, in Chunk c) =>
        {
            if (c.Entry.Volume.IsLowResolutionCopy) _changes.Add((c.Entry.Volume, c.Entry.Position, true));
        });
        world.SubscribeEntityDisposed((in Entity e) =>
        {
            if (e.Has<Chunk>() && e.Get<Chunk>().Entry is { Volume.IsLowResolutionCopy: true } entry)
                _changes.Add((entry.Volume, entry.Position, false));
        });
    }

    // Chunks meshed or put in the light store since the last frame (see Wake).
    private readonly List<(ChunkVolume Volume, ChunkPosition Pos)> _woken = new();

    private void Woken(in Entity e)
    {
        if (Levels.Count > 1 && e.Has<Chunk>()) _woken.Add((e.Get<Chunk>().Entry.Volume, e.Get<Chunk>().Entry.Position));
    }

    /// <summary>Wakes the swaps whose chunks were meshed or put in the light store, and the level-1 swaps over
    /// full-detail chunks meshed.</summary>
    private void Wake()
    {
        foreach (var (volume, p) in _woken)
        {
            if (volume == FullDetail.Volume && Levels.Count > 1) WakeSwap(Levels[1].Volume, new ChunkPosition(p.X >> 1, p.Y >> 1, p.Z >> 1));
            else if (volume.IsLowResolutionCopy) WakeSwap(volume, p);
        }
        _woken.Clear();
    }

    private void WakeSwap(ChunkVolume volume, ChunkPosition p)
    {
        if (SwapsOfVolume(volume, out var swaps) && swaps.TryGetValue(p, out var c)) c.Quiet = c.Parked = false;
    }

    // Coarse chunks loaded (true) and unloaded since the last frame, in order.
    private readonly List<(ChunkVolume Volume, ChunkPosition Pos, bool Loaded)> _changes = new();

    /// <summary>How long a swap between detail levels takes to dissolve one into the other (seconds; 0: at once).</summary>
    public float FadeSeconds { get; set; } = 2f;
    private float _time;
    private int _frame;

    /// <summary>Whether a chunk of a grid is lit (GpuLightSystem.IsLit); without it, everything counts as lit.</summary>
    public Func<GridHandle, ChunkPosition, bool>? IsLit { get; set; }

    /// <summary>Whether a coarse chunk is drawn only once its light maps are baked (<see cref="LightmapsBakedFlag"/>), so
    /// one not baked yet isn't ready to be seen: true where a renderer draws the terrain.</summary>
    public bool WaitsForBake { get; set; }

    /// <summary>Puts a chunk of a grid ahead of the light queues (GpuLightSystem.ShowFirst), asked every frame for
    /// terrain waiting to be shown: a coarse chunk held back for its first light, and what's swapping in for one.</summary>
    public Action<GridHandle, ChunkPosition>? ShowFirst { get; set; }

    /// <summary>Tells the light store a coarse chunk is covered by the finer level, or not (GridStore.SetCovered).</summary>
    public Action<GridHandle, ChunkPosition, bool>? SetCovered { get; set; }

    // A swap waits for the incoming terrain's light: at least this many frames, since a change is marked for lighting
    // the frame after it's made, and at most the other, in case lighting never settles there.
    private const int MinSwapFrames = 2, MaxSwapFrames = 240;

    // A chunk that's never been shown waits for its light longer: it's hidden (behind the fog) meanwhile, rather than
    // showing open-air light. The limit is for lighting that never comes there.
    private const int MaxFirstLightFrames = 1800;

    // Light checks a frame (each reads up to 64 bricks), and how often a chunk waiting on light looks (staggered).
    private const int LitChecksPerFrame = 2048, LitCheckEvery = 4;
    private int _litChecksLeft;

    private IReadOnlyList<TerrainLevel> Levels => _load.Levels;
    private TerrainLevel FullDetail => _load.Levels[0];

    // Quiet swaps are looked at again where a move can change them once the view has moved this share of a level's
    // chunk, or any level's interests change (see Moved); all of them when the view jumps more than WakeShell of the
    // level's chunks, or the fade changes; and each, staggered, every so many frames (for what's left, such as a chunk
    // that went into the light store again).
    private const float WakeMove = 0.25f, WakeShell = 2f;
    // How far either side of the level's radius its range can end, in its chunks: the ball's centre is the interest's
    // chunk (with hysteresis), and it ends in whole groups of two.
    private const float EdgeShell = 4f;
    private const int QuietCheckEvery = 16;
    private readonly Dictionary<TerrainLevel, Vector3D<float>> _wokeAt = new();
    private readonly List<ChunkLoadSystem.Ring> _ringsSeen = new(), _rings = new();
    private float _fadeSeen = float.NaN;

    // For the panel: each step's time, and this frame's counts (see Counts).
    private readonly StepTimer _steps = new("Following the loader's chunks", "Swaps: waking", "Swaps: range and readiness",
                                            "Swaps: light and covers") { Owner = "Terrain Swaps" };
    private const int TrackStep = 0, WakeStep = 1, ReadyStep = 2, LightStep = 3;
    private long _wakeTicks, _readyTicks, _lightTicks;
    private Counts _counts, _shown;

    /// <summary>A frame's work: swaps looked at (of all of them), readiness checks of the finer chunks under one, light
    /// checks made, chunks asked to be lit first, and covers told to the light store.</summary>
    private struct Counts { public int Swaps, Visited, Readiness, LitChecks, ShowFirst, Covers; }

    private Dictionary<ChunkPosition, Swap> SwapsOf(TerrainLevel level) => _swaps.GetValueOrDefault(level) ?? None;

    /// <summary>How chunk <paramref name="p"/> of <paramref name="level"/> is drawn: for a coarse level, coming in over
    /// the finer level it replaces (drawing the pixels that leaves out), drawn, or going away (leaving out the pixels the
    /// finer level coming back draws); and for any level, hidden the same way by a coarser level over it. A coarse chunk
    /// is lit from the store once it's settled there; until then with the light maps it last baked; one never lit isn't
    /// drawn (it waits behind the fog) unless there's no lighting, when it's lit as open air.</summary>
    public ChunkDrawState DrawState(TerrainLevel level, ChunkPosition p)
    {
        Swap? c = null;
        float dissolve = 0f;
        if (level.Level > 0)
        {
            if (!SwapsOf(level).TryGetValue(p, out c) || !Kept(level, p)) return ChunkDrawState.Hidden;
            float share = c.Drawn ? Faded(c.FadeAt) : 1f - Faded(c.FadeAt);
            if (share <= 0f) return ChunkDrawState.Hidden;
            dissolve = c.Drawn ? (share < 1f ? -share : 0f) : 1f - share;
        }
        if (level.Coarser is { } coarser)
        {
            float over = CoverOf(coarser, p);
            if (over >= 1f) return ChunkDrawState.Hidden;
            if (over != 0f) dissolve = over;
        }
        if (c == null) return dissolve == 0f ? ChunkDrawState.Whole : new ChunkDrawState(dissolve);
        if (Waits(level, c, p)) return ChunkDrawState.Hidden;
        ChunkLight light;
        if (c.Settled && Uploaded(level, p)) { light = ChunkLight.Baked; c.Baked = true; }
        else light = c.Baked ? ChunkLight.Kept : ChunkLight.OpenAir;
        return new ChunkDrawState(dissolve, light);
    }

    /// <summary>How much the finer level's chunk <paramref name="finer"/> is hidden by <paramref name="level"/> over
    /// it: 1 while drawn, rising from 0 to 1 over <see cref="FadeSeconds"/> as it comes in, 0 when it isn't; while it
    /// dissolves away, -t: the finer chunk draws only the share t of pixels it has left. Where this level hides nothing
    /// there, a coarser one may (it's drawn where this one isn't ready).</summary>
    private float CoverOf(TerrainLevel level, ChunkPosition finer)
    {
        var p = new ChunkPosition(finer.X >> 1, finer.Y >> 1, finer.Z >> 1);
        float own = 0f;
        if (SwapsOf(level).TryGetValue(p, out var c) && !Waits(level, c, p))
        {
            float t = Faded(c.FadeAt);
            own = c.Drawn ? t : t >= 1f ? 0f : t <= 0f ? 1f : -t;
        }
        return own == 0f && level.Coarser is { } coarser ? CoverOf(coarser, p) : own;
    }

    /// <summary>Whether the level's chunk <paramref name="p"/>, no longer wanted, stays loaded and drawn for now: until
    /// the coarser level has faded in over it, so it doesn't vanish first (the loader asks, see
    /// <see cref="ChunkLoadSystem.KeepLoaded"/>).</summary>
    private bool Holds(TerrainLevel level, ChunkPosition p)
        => level.Coarser is { } coarser && ChunkLoadSystem.Wanted(coarser, new ChunkPosition(p.X >> 1, p.Y >> 1, p.Z >> 1))
           && CoverOf(coarser, p) < 1f;

    /// <summary>Whether the level's chunk is drawn, as far as range goes: wanted, or held (see <see cref="Holds"/>).</summary>
    private bool Kept(TerrainLevel level, ChunkPosition p) => ChunkLoadSystem.Wanted(level, p) || Holds(level, p);

    private float Faded(float at) => FadeSeconds <= 0f ? 1f : System.Math.Clamp((_time - at) / FadeSeconds, 0f, 1f);

    private static Entity? EntityOf(TerrainLevel level, ChunkPosition p) => level.Volume.GetEntry(p)?.Entity;

    private static bool Uploaded(TerrainLevel level, ChunkPosition p) => EntityOf(level, p) is { } e && e.Has<ChunkUploadedFlag>();

    /// <summary>Whether a coarse chunk can't be shown yet: it isn't meshed, or (with lighting) it has faces and has
    /// never been lit from the store. Drawn meanwhile it would show as open air, bright where it should be shadowed.</summary>
    private bool Waits(TerrainLevel level, Swap c, ChunkPosition p)
    {
        if (EntityOf(level, p) is not { } e || !e.Has<ChunkMeshedFlag>()) return true;
        return IsLit != null && !c.Baked && !c.Settled && e.Has<ChunkRenderData>() && !e.Get<ChunkRenderData>().IsEmpty;
    }

    /// <summary>Whether the level's chunk <paramref name="p"/> can stand in for it in a coarser level's swap: the level
    /// has it (wanted, and loaded or air), and it's shown (full detail: meshed).</summary>
    private bool Ready(TerrainLevel level, ChunkPosition p)
    {
        if (!_load.Has(level, p)) return false;
        if (EntityOf(level, p) is not { } e) return true; // air
        return SwapsOf(level).TryGetValue(p, out var c) ? !Waits(level, c, p) && !Unbaked(level, p) : e.Has<ChunkMeshedFlag>();
    }

    /// <summary>Whether a coarse chunk with faces is waiting for its light maps' first bake (see
    /// <see cref="WaitsForBake"/>): until then it isn't drawn, whatever its swap says.</summary>
    private bool Unbaked(TerrainLevel level, ChunkPosition p)
        => WaitsForBake && EntityOf(level, p) is { } e && e.Has<ChunkRenderData>() && !e.Get<ChunkRenderData>().IsEmpty
           && !e.Has<LightmapsBakedFlag>();

    /// <summary>Whether the eight finer chunks under coarse chunk <paramref name="p"/> are ready to be seen.</summary>
    private bool ChildrenReady(TerrainLevel level, ChunkPosition p)
    {
        for (int j = 0; j < 8; j++)
            if (!Ready(level.Finer!, Child(p, j))) return false;
        return true;
    }

    private static ChunkPosition Child(ChunkPosition p, int j) => new(p.X * 2 + (j & 1), p.Y * 2 + ((j >> 1) & 1), p.Z * 2 + (j >> 2));

    /// <summary>Whether the full-detail chunks under level-1 chunk <paramref name="p"/> are lit by their own blocks (a
    /// coarse level's chunks are lit before they're ready, see Ready).</summary>
    private bool ChildrenLit(TerrainLevel level, ChunkPosition p)
    {
        if (IsLit == null || level.Level > 1) return true;
        for (int j = 0; j < 8; j++)
            if (!Budgeted(FullDetail.Volume.Gpu, Child(p, j))) return false;
        return true;
    }

    /// <summary>Whether a coarse chunk in the store shows its light: it, and the chunks above and below it that its top
    /// and bottom faces read.</summary>
    private bool ChunkLit(TerrainLevel level, ChunkPosition p)
        => IsLit == null || Budgeted(level.Volume.Gpu, p) && Budgeted(level.Volume.Gpu, Above(p, 1)) && Budgeted(level.Volume.Gpu, Above(p, -1));

    private static ChunkPosition Above(ChunkPosition p, int dy) => new(p.X, p.Y + dy, p.Z);

    /// <summary>A light check while the frame's budget lasts; past it, not lit yet (looked at again later).</summary>
    private bool Budgeted(GridHandle grid, ChunkPosition pos)
    {
        if (_litChecksLeft-- <= 0) return false;
        _counts.LitChecks++;
        return IsLit!(grid, pos);
    }

    private bool CheckNow(ChunkPosition p) => ((_frame + p.X * 3 + p.Y * 7 + p.Z * 5) & (LitCheckEvery - 1)) == 0;

    private bool QuietCheckNow(ChunkPosition p) => ((_frame + p.X * 3 + p.Y * 7 + p.Z * 5) & (QuietCheckEvery - 1)) == 0;

    /// <summary>Whether any level's interests changed since the last frame.</summary>
    private bool RingsChanged()
    {
        _rings.Clear();
        foreach (var level in Levels) _rings.AddRange(level.Rings);
        bool changed = !_rings.SequenceEqual(_ringsSeen);
        _ringsSeen.Clear();
        _ringsSeen.AddRange(_rings);
        return changed;
    }

    /// <summary>Whether a move of the view (or of what's wanted) can change a quiet swap: it's swapped out (so may have
    /// to come back), or it's near where the finer level ends (it may swap) or where the level's range ends (it may
    /// leave it). Everything between keeps its swap.</summary>
    private static bool Moved(TerrainLevel level, ChunkPosition p, Swap c, Vector3D<float> view, float near)
    {
        if (!c.Drawn) return true;
        float d = BoxDistance.ToChunk(view, p.X, p.Y, p.Z, level.Blocks), m = WakeShell * level.Blocks;
        return d <= near + m || MathF.Abs(d - level.Radius) <= EdgeShell * level.Blocks + m;
    }

    /// <summary>Moves each coarse chunk's swap on a frame (see the class summary), after the loader's frame.</summary>
    public void Update(float dt)
    {
        _steps.Start();
        _shown = _counts;
        _counts = default;
        _wakeTicks = _readyTicks = _lightTicks = 0;
        _frame++;
        _time += dt;
        _litChecksLeft = LitChecksPerFrame;
        Track();
        Wake();
        _steps.Lap(TrackStep);
        bool hasView = TryView(out var view);
        bool ringsChanged = RingsChanged(), fadeChanged = FadeSeconds != _fadeSeen;
        _fadeSeen = FadeSeconds;
        foreach (var level in Levels.Skip(1))
        {
            var swaps = _swaps[level];
            // Only chunks near where the finer level ends can have it ready under them.
            float near = level.Finer!.Radius + 2 * level.Blocks;
            float moved = _wokeAt.TryGetValue(level, out var at) ? Vector3D.Distance(at, view) : float.PositiveInfinity;
            bool wakeAll = fadeChanged || moved > WakeShell * level.Blocks;
            bool wake = wakeAll || ringsChanged || moved > WakeMove * level.Blocks;
            if (wake) _wokeAt[level] = view;
            bool missing = false;
            _counts.Swaps += swaps.Count;
            long t0 = Stopwatch.GetTimestamp(), t1 = t0;
            foreach (var (p, c) in swaps)
            {
                if ((c.Quiet || c.Parked) && !wakeAll && !QuietCheckNow(p) && !(wake && Moved(level, p, c, view, near))) continue;
                // Timed in turns (a timestamp costs more than some steps): waking is everything up to here.
                long t2 = Stopwatch.GetTimestamp();
                _wakeTicks += t2 - t1;
                _counts.Visited++;
                var e = EntityOf(level, p);
                missing |= e == null;
                bool uploaded = e is { } u && u.Has<ChunkUploadedFlag>();
                if (!Kept(level, p))
                {
                    // Out of range: not drawn, and gone at once (it's at the fog, or under the finer level).
                    c.Drawn = c.WasReady = false;
                    c.Leaving = 0;
                    c.FadeAt = float.NegativeInfinity;
                    SetCover(level, p, c, uploaded, false);
                    c.Quiet = true;
                    t1 = Stopwatch.GetTimestamp();
                    _readyTicks += t1 - t2;
                    continue;
                }
                bool inBand = hasView && BoxDistance.ToChunk(view, p.X, p.Y, p.Z, level.Blocks) <= near;
                if (inBand) _counts.Readiness++;
                bool ready = inBand && ChildrenReady(level, p);
                long t3 = Stopwatch.GetTimestamp();
                _readyTicks += t3 - t2;

                if (!uploaded) { c.Settled = false; c.Waiting = 0; }
                else if (!c.Settled)
                {
                    c.Settled = ++c.Waiting >= MinSwapFrames && CheckNow(p)
                                && (c.Waiting > (c.Baked ? MaxSwapFrames : MaxFirstLightFrames) || ChunkLit(level, p));
                    // One held back for its light swaps in from when it's first shown (the finer level is held till then).
                    if (c.Settled && !c.Baked && c.Drawn && float.IsFinite(c.FadeAt)) c.FadeAt = _time;
                }
                bool waits = Waits(level, c, p);
                // Terrain waiting to be shown is lit ahead of light changes on terrain already shown: this chunk while
                // it's held back for its first light (it holds the fog there), with the chunks above and below it that
                // ChunkLit looks at too.
                if (uploaded && waits && ShowFirst is { } first)
                {
                    _counts.ShowFirst += 3;
                    first(level.Volume.Gpu, p);
                    first(level.Volume.Gpu, Above(p, 1));
                    first(level.Volume.Gpu, Above(p, -1));
                }
                if (!ready)
                {
                    c.Leaving = 0;
                    if (!c.Drawn) c.FadeAt = c.WasReady ? _time : float.NegativeInfinity;
                    c.Drawn = true;
                }
                else if (c.Drawn)
                {
                    // Swapping to full detail: hidden once that is lit by its own blocks (not the coarse ones over it), so
                    // it comes in showing its light.
                    if (level.Level == 1 && ShowFirst is { } first2)
                    {
                        _counts.ShowFirst += 8;
                        for (int j = 0; j < 8; j++) first2(FullDetail.Volume.Gpu, Child(p, j));
                    }
                    if (++c.Leaving >= MinSwapFrames && CheckNow(p) && (c.Leaving > MaxSwapFrames || ChildrenLit(level, p)))
                    {
                        c.Drawn = false;
                        c.Leaving = 0;
                        c.FadeAt = _time;
                    }
                }
                // Covered while the finer level is ready, and while that's still drawn as this swaps in over it: its rays
                // then see only its own blocks there. (Uncovered at once, the finer level fading out was shadowed by the
                // coarse blocks in it, and darkened.)
                SetCover(level, p, c, uploaded, ready || c.Drawn && (waits || Faded(c.FadeAt) < 1f));
                c.WasReady = ready;
                // Done: settled, shown, and swapped in or out with its fade over. Nothing changes for it now till the
                // view moves or what's wanted does.
                // Parked: as done, but for its mesh or its place in the store (it's lit already if it has that), which
                // wake it when they come.
                bool still = c.Leaving == 0 && !(ready && c.Drawn) && Faded(c.FadeAt) >= 1f;
                bool unbaked = Unbaked(level, p);
                c.Quiet = still && uploaded && c.Settled && !waits && !unbaked;
                c.Parked = still && !c.Quiet && (!uploaded || c.Settled && (!e!.Value.Has<ChunkMeshedFlag>() || unbaked));
                t1 = Stopwatch.GetTimestamp();
                _lightTicks += t1 - t3;
            }
            _wakeTicks += Stopwatch.GetTimestamp() - t1;
            if (missing) _resync.Add(level);
        }
        _steps.Add(WakeStep, Ms(_wakeTicks));
        _steps.Add(ReadyStep, Ms(_readyTicks));
        _steps.Add(LightStep, Ms(_lightTicks));
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    // Levels with a swap for a chunk that's gone: their swaps are matched to the chunks loaded again next frame.
    private readonly HashSet<TerrainLevel> _resync = new();

    /// <summary>Follows the loader's levels and chunks: a swap for each coarse chunk loaded (and none for one gone), and
    /// each level's draw states on its root, full detail's while there are coarse levels.</summary>
    private void Track()
    {
        foreach (var gone in _swaps.Keys.Where(l => !Levels.Contains(l)).ToList())
        {
            _swaps.Remove(gone);
            _drawStates.Remove(gone);
            _wokeAt.Remove(gone);
        }
        if (Levels.Count == 1)
        {
            if (FullDetail.Root.Has<ChunkDrawStates>()) FullDetail.Root.Remove<ChunkDrawStates>();
            _drawStates.Remove(FullDetail);
            return;
        }
        foreach (var level in Levels)
        {
            if (!_drawStates.ContainsKey(level))
            {
                ChunkDrawStateOf of = p => DrawState(level, p);
                _drawStates[level] = of;
                level.Root.Set(new ChunkDrawStates { Of = of });
            }
            if (level.Level == 0) continue;
            // A level new here, or one with a swap seen without its chunk (which shouldn't happen): all its chunks.
            bool all = !_swaps.TryGetValue(level, out var swaps);
            if (all) _swaps[level] = swaps = new();
            if (!all && !_resync.Remove(level)) continue;
            foreach (var p in swaps!.Keys.Where(p => !level.Volume.IsLoaded(p)).ToList()) swaps.Remove(p);
            foreach (var (p, _) in level.Volume.All) swaps.TryAdd(p, new Swap());
        }
        // Otherwise just the chunks that came and went: a chunk loaded starts a new swap (one loaded again where it
        // was unloaded too).
        foreach (var (volume, p, loaded) in _changes)
        {
            if (!SwapsOfVolume(volume, out var swaps)) continue;
            if (loaded) swaps[p] = new Swap();
            else if (!volume.IsLoaded(p)) swaps.Remove(p);
        }
        _changes.Clear();
    }

    private bool SwapsOfVolume(ChunkVolume volume, out Dictionary<ChunkPosition, Swap> swaps)
    {
        foreach (var (level, s) in _swaps)
            if (level.Volume == volume) { swaps = s; return true; }
        swaps = None;
        return false;
    }

    private void SetCover(TerrainLevel level, ChunkPosition p, Swap c, bool uploaded, bool covered)
    {
        _counts.Covers++;
        // Told every frame (the store ignores no change), so a chunk that goes into the store again is told too.
        c.Covered = covered;
        if (uploaded && SetCovered is { } set) set(level.Volume.Gpu, p, covered);
    }

    /// <summary>Where the view is: the first interest that's drawn.</summary>
    private bool TryView(out Vector3D<float> centre)
    {
        foreach (ref readonly Entity e in _interests.GetEntities())
        {
            if (e.Get<TerrainInterest>().DrawRadius <= 0) continue;
            centre = e.Get<Transform>().Position;
            return true;
        }
        centre = default;
        return false;
    }

    /// <summary>What <see cref="FarReadyRadius"/> last found holding the fog in (for the panel).</summary>
    internal string FogHeldBy { get; private set; } = "nothing";

    /// <summary>For the fog while coarse levels are on (null otherwise): the nearest terrain not ready to be seen in any
    /// coarse level, out to the farthest level's radius. That's a column being made or queued (by its distance across, as
    /// which of its chunks are in range isn't known yet), or a chunk in range held back for its light, where nothing else
    /// shows instead (the finer level's ready, or the coarser one drawn). Full detail not ready is covered by level 1.</summary>
    public float? FarReadyRadius(Vector3D<float> centre)
    {
        if (Levels.Count == 1) return null;
        var last = Levels[^1];
        // Chunks in the outermost band don't count: the view moving on a chunk brings a shell of new ones in there at
        // once, and the fog pulled in for each and went out again (they come in behind nearly total fog anyway).
        float best = last.Radius, ignoreFrom = last.Radius - 2 * last.Blocks;
        FogHeldBy = "nothing (out to the full range)";
        foreach (var level in Levels.Skip(1))
        {
            var coarser = level.Coarser;
            int coarserY = (int)MathF.Floor(centre.Y / level.Blocks) >> 1;
            foreach (var col in level.InFlight.Keys)
                if (!HiddenColumn(col) && BoxDistance.ToColumn(centre, col.x, col.z, level.Blocks) is var d && d < best && d < ignoreFrom)
                    (best, FogHeldBy) = (d, $"level {level.Level} column being made, {d:F0} blocks out");
            for (int i = level.QueueHead; i < level.Queue.Count; i++)
                if (!HiddenColumn(level.Queue[i]))
                {
                    if (BoxDistance.ToColumn(centre, level.Queue[i].x, level.Queue[i].z, level.Blocks) is var d && d < best && d < ignoreFrom)
                        (best, FogHeldBy) = (d, $"level {level.Level} column queued, {d:F0} blocks out");
                    break;
                }
            foreach (var (p, c) in SwapsOf(level))
            {
                if (c.Quiet || c.WasReady || !Kept(level, p)) continue; // a quiet one doesn't wait
                if (!Waits(level, c, p) && !(c.Drawn && Unbaked(level, p))) continue;
                if (EntityOf(level, p) is { } e && e.Has<ChunkRenderData>() && e.Get<ChunkRenderData>().IsEmpty) continue;
                if (coarser != null && Shows(coarser, new ChunkPosition(p.X >> 1, p.Y >> 1, p.Z >> 1))) continue;
                float d = BoxDistance.ToChunk(centre, p.X, p.Y, p.Z, level.Blocks);
                if (d < best && d < ignoreFrom)
                    (best, FogHeldBy) = (d, $"level {level.Level} chunk waiting for {WaitsFor(level, c, p)}, {d:F0} blocks out");
            }

            bool HiddenColumn((int x, int z) k)
                => coarser != null && Shows(coarser, new ChunkPosition(k.x >> 1, coarserY, k.z >> 1));
        }
        return best;
    }

    /// <summary>What a chunk held back (see <see cref="Waits"/>) waits for: its mesh, its place in the light store, or
    /// its light there. A chunk lit but not yet meshed waits on the mesher however long it was lit for.</summary>
    private static string WaitsFor(TerrainLevel level, Swap c, ChunkPosition p)
    {
        if (EntityOf(level, p) is not { } e || !e.Has<ChunkMeshedFlag>())
            return c.Settled ? "its mesh (lit already)" : "its mesh";
        if (!e.Has<ChunkUploadedFlag>()) return "the light store";
        if (!c.Settled) return $"its light ({c.Waiting} frames)";
        return "its light maps' first bake";
    }

    /// <summary>Whether the level draws chunk <paramref name="p"/> now, lit: what's under it doesn't show.</summary>
    private bool Shows(TerrainLevel level, ChunkPosition p)
        => SwapsOf(level).TryGetValue(p, out var c) && c.Drawn && !Waits(level, c, p) && !Unbaked(level, p);

    /// <summary>The Distant Terrain panel's part: the fade, what the fog waits on, and each level's swaps.</summary>
    internal void DrawUi()
    {
        float fade = FadeSeconds;
        if (ImGui.SliderFloat("Swap fade (seconds)", ref fade, 0f, 4f, "%.2f")) FadeSeconds = fade;
        ImGui.Text($"Fog held by: {FogHeldBy}");
        ImGui.Text($"Last frame: {_shown.Visited:N0} of {_shown.Swaps:N0} swaps looked at, {_shown.Readiness:N0} readiness checks, " +
                   $"{_shown.LitChecks:N0} light checks, {_shown.ShowFirst:N0} asked to light first, {_shown.Covers:N0} covers told");
        _steps.Draw();
        ImGui.Text($"Full-detail chunks loaded: {FullDetail.Volume.LoadedCount:N0} ({FullDetail.Held.Count:N0} held while coarse terrain fades in)");
        foreach (var level in Levels.Skip(1))
        {
            int drawn = 0, waiting = 0, covered = 0;
            foreach (var (p, c) in SwapsOf(level))
            {
                if (c.Drawn) drawn++;
                if (c.Covered) covered++;
                if (Waits(level, c, p)) waiting++;
            }
            ImGui.Text($"Level {level.Level}: {drawn:N0} drawn, {covered:N0} covered, {waiting:N0} waiting to be shown, {level.Held.Count:N0} held");
        }
    }
}
