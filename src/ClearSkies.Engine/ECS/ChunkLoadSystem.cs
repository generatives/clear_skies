using System.Numerics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Gui;
using ClearSkies.Engine.Rendering;
using ClearSkies.Engine.Voxels;
using ClearSkies.Engine.Generation;
using ClearSkies.Engine.Persistence;
using DefaultEcs;
using ImGuiNET;
using Silk.NET.Maths;
using System.Collections.Concurrent;

namespace ClearSkies.Engine.ECS;

/// <summary>
/// Streams the static world around terrain interests (see <see cref="TerrainInterest"/>), like a typical block game: a
/// queue of the chunk columns within each interest's load radius, closest to any interest first by horizontal distance,
/// rebuilt whenever an interest crosses into another column (or one comes or goes), and loaded a whole column at a time.
/// Every interest is streamed the same way: the local player's view is one with a large draw radius, and a body
/// simulated here away from the view (another player's character on the host, a ship) one with only a collider radius. A rebuild is spread over frames (within <see cref="StreamBudgetMs"/>, for
/// unloading what's no longer wanted and for scanning the wanted columns), so crossing a column doesn't cost one long frame.
/// Only chunks that may hold something are queued: the layers the generator says
/// it may fill (<see cref="IWorldGenerator.ColumnLayers"/>) and edited chunks (builds). An edited chunk is loaded from
/// the store (<see cref="IChunkStore"/>, which asks the Host for it): a column keeps its place in the queue until its
/// edited chunks are there, and the columns after it load meanwhile.
/// The generator's
/// layers are a loose bound, so chunks that turn out to be air are remembered (a bit per column) until their column
/// is no longer wanted, and aren't queued again.
///
/// Full detail is the first of the levels streamed (<see cref="TerrainLevel"/>); the view's distant terrain may be
/// drawn coarse in more (see <see cref="SetLevels"/>). Every level streams the same way, each with its own queue, and
/// differs only in where its chunks come from.
///
/// What's loaded is limited by a budget (<see cref="IChunkBudget"/>: GPU light storage for a drawn world). When the
/// budget is full, the queue stops; and if the next queued column is nearer (to its nearest interest) than the farthest
/// loaded one, the farthest is unloaded to make room. So the loaded world is always what's nearest an interest that fits.
///
/// What's on its way is in the ECS for other systems (the fog, see <see cref="FogSystem"/>): each column queued or
/// loading is an entity with <see cref="TerrainColumnLoading"/> until its chunks are added, and each interest gets
/// <see cref="TerrainScanned"/>, how far around it every wanted column is known (queued, loading or loaded).
///
/// Edits ask it which chunks that aren't loaded are known to hold nothing (<see cref="IsKnownEmpty"/>): those an edit
/// makes. Any other chunk an edit reaches is left to it (a <see cref="TerrainEditedElsewhere"/>): edited, loaded from
/// the store from then on, and if its column was loading, that chunk is loaded again.
/// </summary>
public sealed partial class ChunkLoadSystem : ISystem, IDebugUiSystem, IChunkStreaming
{
    /// <summary>Chunks being made on workers at once (see <see cref="ChunkPipeline"/>). Well above the core count: the
    /// background workers queue the excess, and most of a first visit is sky that's ruled out in microseconds.</summary>
    private const int MaxInFlight = 64;

    /// <summary>Chunks a coarse level asks for and doesn't have yet, at most: they're slow to make (a level-1 chunk is
    /// eight full ones, level 2 sixty-four), and the pipeline makes them nearest first anyway.</summary>
    private const int MaxLevelInFlight = 256;

    /// <summary>Columns queued at once. Far more than load before the centre next moves a column; when the queue runs
    /// out short of the view distance it is rebuilt from there.</summary>
    private const int MaxQueued = 4096;

    /// <summary>Streamed layers below the lowest generated one, for building under the islands. Streaming covers 64
    /// layers (a column's bits); the rest are above.</summary>
    private const int LayersBelow = 8;

    /// <summary>Chunks unloaded at most per frame to make room: enough to keep up with flying, few enough that
    /// unloading doesn't stall a frame.</summary>
    private const int MaxEvictChunksPerFrame = 512;

    /// <summary>Main-thread time eviction may take per frame (ms): unloading a chunk (saving it, disposing its entity,
    /// releasing its storage) isn't free, and a full batch of chunks in one frame took ~14 ms. It stops after the column
    /// that runs over, and carries on next frame; loading waits for the room meanwhile, which the fog covers.</summary>
    private const double MaxEvictMsPerFrame = 2.0;

    private const int S = ChunkData.Size;

    private readonly IChunkStore _chunkStore;

    private readonly EntitySet      _interests;
    private readonly EntitySet      _editedElsewhere;
    private readonly ChunkVolume    _staticVolume;
    private readonly IChunkPreparer? _preparer;
    private readonly ThreadLocal<IWorldGenerator> _generator;
    private readonly ThreadLocal<ChunkData> _scratch = new(() => new ChunkData());
    private readonly int            _minY;      // the lowest streamed layer: bit 0 of a column's bits

    /// <summary>Column offsets from an interest's column, closest first, with their distance squared, by radius in
    /// columns. Computed once per radius; a rebuild just walks them.</summary>
    private readonly Dictionary<int, (short dx, short dz, int d)[]> _offsetsByDistance = new();
    private readonly float _viewDistance; // the farthest anything is drawn in full detail

    /// <summary>An interest as a level streams it: its entity, the chunk it's streamed around (in the level's chunks),
    /// and its radius in chunks: either whole columns within it, or a ball of 2x2x2 groups of chunks (the view's, while
    /// there are coarse levels, see <see cref="InRange"/>), so each chunk of the next level has its eight together.</summary>
    internal readonly record struct Ring(Entity Interest, (int x, int y, int z) Centre, int Radius, bool Ball);

    private readonly HashSet<Entity> _seen = new();
    private readonly World _world;

    /// <summary>Full detail (<see cref="FullDetail"/>), then the coarse levels nearest the view first.</summary>
    private readonly List<TerrainLevel> _levels = new();
    private TerrainLevel FullDetail => _levels[0];

    /// <summary>The entity standing for each full-detail column queued or loading (see <see cref="TerrainColumnLoading"/>).</summary>
    private readonly Dictionary<(int x, int z), Entity> _loading = new();

    /// <summary>Every chunk edited since it was generated, loaded from the store (read from it as streaming starts, then
    /// added to as chunks are edited here or elsewhere). Concurrent: chunks are made on workers from them.</summary>
    private readonly ConcurrentDictionary<ChunkPosition, byte> _edited = new();
    private readonly ConcurrentDictionary<(int x, int z), ulong> _editedLayers = new(); // the same, as layer bits per column
    private bool _editsRead;

    /// <summary>Per column (layer bits): chunks holding a build — edited, with blocks.</summary>
    private readonly ConcurrentDictionary<(int x, int z), ulong> _built = new();

    /// <summary>Main-thread time per frame for the work that comes in bursts, shared: unloading what left view,
    /// adding finished columns, and scanning for columns to queue, in that order. What doesn't fit waits for the next
    /// frame. Each still gets a minimum (<see cref="MinStepMs"/>) so none is starved for long.</summary>
    private const double StreamBudgetMs = 2.5, MinStepMs = 0.3;
    private readonly System.Diagnostics.Stopwatch _budget = new();

    private double BudgetLeft => System.Math.Max(MinStepMs, StreamBudgetMs - _budget.Elapsed.TotalMilliseconds);

    // Each interest's last published scan radius (TerrainScanned) and the column it was around.
    private readonly Dictionary<Entity, (float Radius, (int x, int z) Column)> _published = new();

    /// <param name="budget">What limits what's loaded in full detail.</param>
    /// <param name="viewDistance">The farthest out chunks are streamed in full detail to be drawn, in blocks
    /// (horizontally): an interest's draw radius is capped at it (the GPU store's world index is sized for it).</param>
    /// <param name="minChunkY">Lowest chunk layer the generator fills. Streaming covers 64 layers from
    /// <see cref="LayersBelow"/> under it; the generator's layers must fall inside them. Edits to the static world
    /// outside them are refused (see <see cref="ChunkVolume.EditableLayers"/>).</param>
    /// <param name="chunkStore">Where edited chunks come from (the Host, which keeps them).</param>
    /// <param name="preparer">Work on each loaded chunk's data on the worker that loaded it (packing it for the GPU, for
    /// a drawn world).</param>
    public ChunkLoadSystem(World world, ChunkVolume staticVolume, IChunkBudget budget, Func<IWorldGenerator> generatorFactory,
                           float viewDistance, int minChunkY, IChunkStore chunkStore, IChunkPreparer? preparer = null)
    {
        _chunkStore = chunkStore;
        _world = world;

        _interests    = world.GetEntities().With<Transform>().With<TerrainInterest>().AsSet();
        _editedElsewhere = world.GetEntities().With<TerrainEditedElsewhere>().AsSet();
        _staticVolume = staticVolume;
        _preparer     = preparer;
        _generator    = new ThreadLocal<IWorldGenerator>(generatorFactory);
        _minY         = minChunkY - LayersBelow;
        _staticVolume.EditableLayers = (_minY, _minY + 63); // only what streaming can load back
        _viewDistance = viewDistance;
        StartPipeline();
        _levels.Add(new TerrainLevel(0, _grids[0], staticVolume, budget, _minY, _minY + 63) { Radius = viewDistance });
    }

    /// <summary>Column offsets within <paramref name="radius"/> columns, closest first.</summary>
    private (short dx, short dz, int d)[] OffsetsByDistance(int radius)
    {
        if (_offsetsByDistance.TryGetValue(radius, out var cached)) return cached;
        var offsets = new List<(short dx, short dz, int d)>();
        for (int dz = -radius; dz <= radius; dz++)
        for (int dx = -radius; dx <= radius; dx++)
            if (dx * dx + dz * dz <= radius * radius) offsets.Add(((short)dx, (short)dz, dx * dx + dz * dz));
        offsets.Sort((a, b) => a.d.CompareTo(b.d));
        return _offsetsByDistance[radius] = offsets.ToArray();
    }

    /// <summary>The chunks edited before this machine joined, read once, as streaming starts (the store has them by
    /// then).</summary>
    private void ReadEdits()
    {
        _editsRead = true;
        foreach (var pos in _chunkStore.EditedChunks())
            RecordEdited(pos, hasBlocks: true);
    }

    // ── debug UI ─────────────────────────────────────────────────────────────
    public string DebugName => "Chunk Loading";

    public void DrawDebugUi()
    {
        var full = FullDetail;
        ImGui.Text($"Interests: {full.Rings.Count}   Draw limit: {_viewDistance:F0} blocks ({full.Columns.Count} columns known)");
        ImGui.Text($"Budget: {full.Budget!.Describe(_staticVolume)}" + (full.Full ? " (full)" : ""));
        ImGui.Text($"Loaded: {_staticVolume.LoadedCount:N0} chunks   Queued columns: {full.Queue.Count - full.QueueHead}" +
                   $"{(full.QueueTruncated ? "+" : "")}   Being made: {full.InFlightChunks} chunks");
        ImGui.Text($"Columns evicted: {full.Evictions}");
        ImGui.Text($"Edited chunks: {_edited.Count}   Layers: {_minY}..{_minY + 63}");
        DrawPipelineUi();

        ImGui.Separator();
        _steps.Draw();
    }

    // CPU time of each step, for the debug panel: which one a hitch while streaming came from.
    private const int PipelineStep = 0, UnloadStep = 1, QueueStep = 2, DispatchStep = 3, EvictStep = 4, EvictOrderStep = 5,
                      LevelsStep = 6;
    private readonly StepTimer _steps = new("Making chunks (pipeline)", "Rebuild: unloading what's unwanted",
                                            "Rebuild: queueing columns", "Dispatching jobs", "Evicting far columns",
                                            "Evicting: ordering columns", "Dropping coarse blocks") { Owner = "Chunk Loading" };

    public void Update(float dt)
    {
        _steps.Start();
        _frame++;
        if (!_editsRead) ReadEdits();
        TakeEditsElsewhere();

        _budget.Restart();
        _seen.Clear();
        bool moved = false;
        foreach (var level in _levels)
        {
            GatherRings(level);
            bool idle = level.ScanDone && level.QueueHead == level.Queue.Count && level.InFlightChunks == 0;
            if (!level.Current.SequenceEqual(level.Rings) || (idle && level.QueueTruncated))
            {
                level.Rings.Clear();
                level.Rings.AddRange(level.Current);
                Rebuild(level);
                moved = true;
            }
        }
        if (moved) _pipeline.Reprioritise(); // nearest first from where the view is now
        if (_published.Count > _seen.Count)
            foreach (var gone in _published.Keys.Where(k => !_seen.Contains(k)).ToList()) _published.Remove(gone);
        foreach (var level in _levels) UnloadSome(level, BudgetLeft);
        _steps.Lap(UnloadStep);

        foreach (var level in _levels) ScanSome(level, BudgetLeft);
        _steps.Lap(QueueStep);

        Dispatch();
        _steps.Lap(DispatchStep);

        _pipeline.Update(new Pipeline.FrameBudgets(MaxInFlight, BudgetLeft));
        _steps.Lap(PipelineStep);

        DropSome();
        _steps.Lap(LevelsStep);
    }

    /// <summary>This frame's interests as <paramref name="level"/> streams them (<see cref="TerrainLevel.Current"/>, in
    /// a fixed order so a frame where none moved compares equal to the last rebuild's). Full detail streams every
    /// interest; a coarse level only those that are drawn, as far as the level reaches.</summary>
    private void GatherRings(TerrainLevel level)
    {
        level.Current.Clear();
        foreach (ref readonly Entity e in _interests.GetEntities())
        {
            var interest = e.Get<TerrainInterest>();
            var position = e.Get<Transform>().Position;
            _seen.Add(e);
            int radius;
            bool ball = false;
            if (level.Level == 0)
            {
                float draw = MathF.Min(interest.DrawRadius, _viewDistance);
                if (draw > 0 && FullDetailRadius is { } full)
                {
                    draw = MathF.Min(draw, full);
                    ball = draw >= interest.ColliderRadius;
                }
                radius = (int)MathF.Ceiling(MathF.Max(interest.ColliderRadius, draw) / S);
            }
            else
            {
                if (interest.DrawRadius <= 0) continue;
                radius = (int)MathF.Ceiling(level.Radius / level.Blocks);
                ball = true;
            }
            var centre = InterestChunk(level, e, position);
            if (!ball) centre.y = 0; // whole columns: moving up or down changes nothing
            level.Current.Add(new Ring(e, centre, radius, ball));
        }
        if (level.InterestChunks.Count > _seen.Count)
            foreach (var gone in level.InterestChunks.Keys.Where(k => !_seen.Contains(k)).ToList()) level.InterestChunks.Remove(gone);
        level.Current.Sort((a, b) => (a.Centre, a.Radius, a.Ball, a.Interest.GetHashCode())
                                     .CompareTo((b.Centre, b.Radius, b.Ball, b.Interest.GetHashCode())));
    }

    /// <summary>How many times the full-detail queue has been rebuilt (the view moved, or loading caught up).</summary>
    public int Rebuilds => FullDetail.Rebuilds;

    /// <summary>Full-detail chunks loaded, and columns with chunks still being made.</summary>
    public int LoadedChunks => _staticVolume.LoadedCount;
    public int ColumnsInFlight => FullDetail.InFlight.Count;

    /// <summary>Starts the level over from the interests' new chunks: lists what's no longer wanted to unload, and
    /// restarts the scan for wanted columns with chunks still to fetch, closest first. Both then go on a little each
    /// frame (<see cref="UnloadSome"/>, <see cref="ScanSome"/>).</summary>
    private void Rebuild(TerrainLevel level)
    {
        level.Rebuilds++;
        // Whatever the last rebuild didn't get to unload goes now if it could matter: a chunk that stays loaded out of
        // view could share a cell of the GPU store's world index with one coming into view on the other side (see
        // LightBudget.WorldIndexDim). The index is sized for the view distance, so while the rings load much less than
        // that (full detail only near, see FullDetailRadius) it can't, and the rest goes on under the frame's budget:
        // unloading it all here made a long frame at every column crossed.
        int indexDim = LightBudget.WorldIndexDim(_viewDistance);
        if (level.Level == 0 && level.Rings.Any(r => 2 * (r.Radius + 4) + 3 >= indexDim)) UnloadSome(level, double.PositiveInfinity);

        level.ToUnload.Clear();
        level.UnloadAt = 0;
        // (An edit that put blocks where there were none counts as a build once its chunk unloads. Until then the chunk
        // is loaded, so the queue doesn't need to know.)
        foreach (var p in level.Wanting)
            if (!Wanted(level, p) && !level.Held.ContainsKey(p)) level.ToUnload.Add(p);
        foreach (var (p, _) in level.Volume.All) // made by an edit, not the pipeline
            if (!level.Wanting.Contains(p) && !Wanted(level, p) && !level.Held.ContainsKey(p)) level.ToUnload.Add(p);

        // Forget the columns no longer wanted: their air is re-learned on return.
        foreach (var key in level.Columns.Keys.Where(k => !Wanted(level, k.x, k.z)).ToList()) level.Columns.Remove(key);
        if (level.Level == 0)
            foreach (var key in _loading.Keys.Where(k => !Wanted(level, k.x, k.z)).ToList()) Loaded(key);
        _steps.Lap(UnloadStep);

        level.Queue.Clear();
        level.QueueHead = 0;
        level.QueueTruncated = false;
        level.NothingToEvict = false;
        level.EvictNext = -1;
        // A ball wants whole 2x2 groups, which reach up to two columns past its radius.
        level.ScanOffsets = level.Rings.Select(r => OffsetsByDistance(r.Radius + (r.Ball ? 2 : 0))).ToArray();
        level.ScanAt = new int[level.Rings.Count];
        level.ScanCarry = new float[level.Rings.Count];
        level.ScanDone = false;
        if (level.Level == 0)
        {
            for (int i = 0; i < level.Rings.Count; i++)
            {
                var r = level.Rings[i];
                if (!_published.TryGetValue(r.Interest, out var last)) continue;
                // Everything within the last radius of the old column was loaded or queued, and still is (only columns no
                // longer wanted were dropped above), so within that radius less the move of the new one it's known.
                float known = MathF.Min(last.Radius, r.Radius * S);
                float dx = r.Centre.x - last.Column.x, dz = r.Centre.z - last.Column.z;
                level.ScanCarry[i] = MathF.Max(0f, known - MathF.Sqrt(dx * dx + dz * dz) * S);
            }
            PublishScanned();
        }
        _steps.Lap(QueueStep);
    }

    /// <summary>Unloads the level's chunks no longer wanted at its last rebuild, for up to <paramref name="budgetMs"/>.</summary>
    private void UnloadSome(TerrainLevel level, double budgetMs)
    {
        double start = _steps.SinceLap();
        while (level.UnloadAt < level.ToUnload.Count && _steps.SinceLap() - start < budgetMs)
        {
            var p = level.ToUnload[level.UnloadAt++];
            if (KeepLoaded?.Invoke(level, p) == true && level.Volume.IsLoaded(p)) { level.Held.TryAdd(p, _frame); continue; }
            LetGoOf(level, p); // a no-op if eviction already let it go
        }
        // Held ones let go are unloaded under the same budget (a frame's share, even from Rebuild): many are let go at
        // once, as a ring of coarse chunks finishes fading in.
        LetGo(level, start, System.Math.Min(budgetMs, StreamBudgetMs));
    }

    /// <summary>Whether a level's chunk no longer wanted stays loaded for now (see <see cref="TerrainSwapSystem"/>:
    /// until the coarser level has faded in over it, so it doesn't vanish first). Unset: everything unloads at once.</summary>
    public Func<TerrainLevel, ChunkPosition, bool>? KeepLoaded { get; set; }

    // Chunks are held at most this long, in case what they're held for never comes.
    private const int MaxHoldFrames = 600;
    private int _frame;

    /// <summary>Lets go of the level's held chunks once they needn't stay (or they're wanted again, or held too long),
    /// unloading them until <paramref name="budgetMs"/> after <paramref name="start"/>.</summary>
    private void LetGo(TerrainLevel level, double start, double budgetMs)
    {
        if (level.Held.Count == 0) return;
        var letGo = new List<ChunkPosition>();
        foreach (var (p, since) in level.Held)
        {
            if (Wanted(level, p) || !level.Volume.IsLoaded(p)) { letGo.Add(p); continue; } // wanted again, or gone already
            if (KeepLoaded?.Invoke(level, p) == true && _frame - since <= MaxHoldFrames) continue;
            if (_steps.SinceLap() - start >= budgetMs) break;
            LetGoOf(level, p);
            letGo.Add(p);
        }
        foreach (var p in letGo) level.Held.Remove(p);
    }

    /// <summary>Carries on the level's scan of the wanted columns, closest to an interest first, for up to
    /// <paramref name="budgetMs"/>, queueing each with chunks still to fetch. The rings are walked together: each step
    /// takes the ring whose next column is nearest its centre, and where rings overlap, a column is the nearest ring's.</summary>
    private void ScanSome(TerrainLevel level, double budgetMs)
    {
        if (level.ScanDone) return;
        double start = _steps.SinceLap();
        var offsets = level.ScanOffsets;
        var at = level.ScanAt;
        while (true)
        {
            // Checked every column: one seen for the first time asks the generator for its layers, which isn't cheap.
            if (_steps.SinceLap() - start >= budgetMs) { if (level.Level == 0) PublishScanned(); return; }
            int next = 0;
            for (int i = 1; i < offsets.Length; i++)
                if (at[i] < offsets[i].Length && (at[next] >= offsets[next].Length || offsets[i][at[i]].d < offsets[next][at[next]].d))
                    next = i;
            if (next >= offsets.Length || at[next] >= offsets[next].Length) break;
            var (dx, dz, d) = offsets[next][at[next]++];
            var ring = level.Rings[next];
            int x = ring.Centre.x + dx, z = ring.Centre.z + dz;
            if (!WantedBy(ring, x, z)) continue; // past a ball's 2x2 groups
            if (level.Rings.Count > 1 && !NearestRing(level, next, x, z, d)) continue; // another ring's
            if (HasMissing(level, x, z))
            {
                if (level.Queue.Count == MaxQueued) { level.QueueTruncated = true; at[next]--; break; }
                level.Queue.Add((x, z));
                if (level.Level == 0) Loading((x, z));
            }
        }
        level.ScanDone = true;
        if (level.Level != 0) return;
        PublishScanned();
        Console.WriteLine($"[load] rebuild: queued {level.Queue.Count}{(level.QueueTruncated ? "+" : "")} columns around {level.Rings.Count} " +
                          $"interests, loaded {_staticVolume.LoadedCount} chunks ({level.Budget!.Describe(_staticVolume)}), " +
                          $"unloaded {level.ToUnload.Count}, evicted {level.Evictions} columns so far");
    }

    /// <summary>Blocks an interest must be past its chunk's edge before it's streamed around the next (in full detail;
    /// a coarse level's scales with its chunks), so standing on a chunk boundary doesn't unload and reload the edge of
    /// the view every wobble.</summary>
    private const float ViewHysteresis = 2f;

    /// <summary>The level's chunk an interest is streamed around: the one it's in, once it's clearly left the last one.</summary>
    private static (int x, int y, int z) InterestChunk(TerrainLevel level, Entity e, Vector3D<float> position)
    {
        int size = level.Blocks;
        var chunk = ((int)MathF.Floor(position.X / size), (int)MathF.Floor(position.Y / size), (int)MathF.Floor(position.Z / size));
        if (level.InterestChunks.TryGetValue(e, out var last) && chunk != last)
        {
            float h = ViewHysteresis * (1 << level.Level);
            float x0 = last.x * size, y0 = last.y * size, z0 = last.z * size;
            bool near = position.X >= x0 - h && position.X < x0 + size + h &&
                        position.Y >= y0 - h && position.Y < y0 + size + h &&
                        position.Z >= z0 - h && position.Z < z0 + size + h;
            if (near) chunk = last;
        }
        return level.InterestChunks[e] = chunk;
    }

    /// <summary>Gives each interest <see cref="TerrainScanned"/>: how far around it the full-detail scan has looked,
    /// short of the first column it hasn't (or all of its load radius once it's looked at everything), and at least what
    /// it's known to have from before the last rebuild (<see cref="TerrainLevel.ScanCarry"/>): another interest moving,
    /// or this one moving a column, rebuilds the queue, and dropping to the new scan's progress pulled the fog in and out
    /// every time.</summary>
    private void PublishScanned()
    {
        var level = FullDetail;
        for (int i = 0; i < level.Rings.Count; i++)
        {
            var e = level.Rings[i].Interest;
            if (!e.IsAlive) continue;
            var offsets = level.ScanOffsets[i];
            float radius = level.ScanAt[i] >= offsets.Length ? float.PositiveInfinity
                         : MathF.Max(level.ScanCarry[i], MathF.Max(0f, (MathF.Sqrt(offsets[level.ScanAt[i]].d) - 1f) * S));
            _published[e] = (radius, (level.Rings[i].Centre.x, level.Rings[i].Centre.z));
            if (!e.Has<TerrainScanned>() || e.Get<TerrainScanned>().Radius != radius) e.Set(new TerrainScanned { Radius = radius });
        }
    }

    /// <summary>Full-detail column (x, z) is queued: it has an entity saying so (<see cref="TerrainColumnLoading"/>).</summary>
    private void Loading((int x, int z) col)
    {
        if (_loading.ContainsKey(col)) return;
        var e = _world.CreateEntity();
        e.Set(new TerrainColumnLoading { X = col.x, Z = col.z });
        _loading[col] = e;
    }

    /// <summary>Full-detail column (x, z) has loaded, or is no longer wanted, or had nothing to load: its entity goes.</summary>
    private void Loaded((int x, int z) col)
    {
        if (_loading.Remove(col, out var e) && e.IsAlive) e.Dispose();
    }

    /// <summary>Whether ring <paramref name="ring"/>, <paramref name="distSq"/> from column (x, z), is the nearest ring
    /// that wants it (the first, of rings as near), so it's scanned once.</summary>
    private static bool NearestRing(TerrainLevel level, int ring, int x, int z, long distSq)
    {
        for (int i = 0; i < level.Rings.Count; i++)
        {
            if (i == ring) continue;
            var other = level.Rings[i];
            long d = Sq(x - other.Centre.x) + Sq(z - other.Centre.z);
            if (WantedBy(other, x, z) && (d < distSq || (d == distSq && i < ring))) return false;
        }
        return true;
    }

    /// <summary>Some chunk of the level's column (x, z) is within an interest's load radius.</summary>
    private static bool Wanted(TerrainLevel level, int x, int z)
    {
        foreach (var ring in level.Rings)
            if (WantedBy(ring, x, z)) return true;
        return false;
    }

    /// <summary>The level's chunk <paramref name="p"/> is within an interest's load radius.</summary>
    internal static bool Wanted(TerrainLevel level, ChunkPosition p)
        => p.Y >= level.MinY && p.Y <= level.MaxY && (WantedLayers(level, p.X, p.Z) & level.Bit(p)) != 0;

    /// <summary>The layers of the level's column (x, z) within an interest's load radius, as bits from its lowest
    /// streamed layer.</summary>
    private static ulong WantedLayers(TerrainLevel level, int x, int z)
    {
        ulong bits = 0;
        foreach (var ring in level.Rings) bits |= LayersBy(level, ring, x, z);
        return bits;
    }

    /// <summary>The layers of column (x, z) <paramref name="ring"/> wants: all of a column within its radius, or for a
    /// ball, those in range (see <see cref="InRange"/>).</summary>
    private static ulong LayersBy(TerrainLevel level, in Ring ring, int x, int z)
    {
        if (!WantedBy(ring, x, z)) return 0;
        if (!ring.Ball) return level.AllLayers;
        // Groups g whose middle (2g + 1) is within reach of the centre's (y + 0.5), up or down.
        double gx = GroupOffset(x, ring.Centre.x), gz = GroupOffset(z, ring.Centre.z);
        double up = System.Math.Sqrt((double)ring.Radius * ring.Radius - gx * gx - gz * gz);
        int lo = 2 * (int)System.Math.Ceiling((ring.Centre.y - 0.5 - up) / 2) - level.MinY;
        int hi = 2 * (int)System.Math.Floor((ring.Centre.y - 0.5 + up) / 2) + 1 - level.MinY;
        lo = System.Math.Max(lo, 0);
        hi = System.Math.Min(hi, level.MaxY - level.MinY);
        return lo > hi ? 0 : (ulong.MaxValue >> (63 - hi)) & (ulong.MaxValue << lo);
    }

    /// <summary>Some of column (x, z) is within <paramref name="ring"/>'s load radius: the column, or for a ball, the
    /// 2x2 group it's in, at the centre's height.</summary>
    private static bool WantedBy(in Ring ring, int x, int z)
    {
        if (!ring.Ball) return Sq(x - ring.Centre.x) + Sq(z - ring.Centre.z) <= Sq(ring.Radius);
        double gx = GroupOffset(x, ring.Centre.x), gz = GroupOffset(z, ring.Centre.z);
        return gx * gx + gz * gz + 0.25 <= (double)ring.Radius * ring.Radius;
    }

    /// <summary>Whether chunk <paramref name="c"/> is within <paramref name="radius"/> chunks of
    /// <paramref name="centre"/>, by the middle of the 2x2x2 group it's in, so a ball ends in whole chunks of the next
    /// level.</summary>
    internal static bool InRange((int x, int y, int z) c, (int x, int y, int z) centre, int radius)
    {
        double dx = GroupOffset(c.x, centre.x), dy = GroupOffset(c.y, centre.y), dz = GroupOffset(c.z, centre.z);
        return dx * dx + dy * dy + dz * dz <= (double)radius * radius;
    }

    /// <summary>From the middle of chunk <paramref name="centre"/> to the middle of the 2x2x2 group chunk
    /// <paramref name="c"/> is in, along one axis, in chunks.</summary>
    private static double GroupOffset(int c, int centre) => (c >> 1) * 2 + 1 - (centre + 0.5);

    /// <summary>When set, the view's terrain loads in full detail only this far (blocks), in a ball of whole 2x2x2
    /// groups of chunks, so each chunk of the first coarse level (see <see cref="SetLevels"/>) has its eight loaded
    /// together. Null: full detail in whole columns as far as the view.</summary>
    public float? FullDetailRadius => _levels.Count > 1 ? FullDetail.Radius : null;

    private static long Sq(int v) => (long)v * v;

    /// <summary>Distance squared, in the level's columns, from column <paramref name="c"/> to the nearest interest's.</summary>
    private static long ColumnDistSq(TerrainLevel level, (int x, int z) c)
    {
        long best = long.MaxValue;
        foreach (var ring in level.Rings) best = System.Math.Min(best, Sq(c.x - ring.Centre.x) + Sq(c.z - ring.Centre.z));
        return best;
    }

    /// <summary>Chunks of the level's column (x, z) that may hold something: what the generator may fill, less what
    /// turned out to be air, plus builds. A coarse column may hold something anywhere until its job finds out.</summary>
    private ulong MaybeContent(TerrainLevel level, int x, int z)
    {
        if (!level.Columns.TryGetValue((x, z), out var col))
            level.Columns[(x, z)] = col = (level.Level == 0 ? _generator.Value!.ColumnLayers(x, z, _minY) : level.AllLayers, 0UL);
        ulong maybe = col.Generated & ~col.Air;
        return level.Level == 0 ? maybe | _built.GetValueOrDefault((x, z)) : maybe;
    }

    private ulong Bit(ChunkPosition p) => 1UL << (p.Y - _minY);

    /// <summary>Records whether full chunk <paramref name="p"/> holds a build; a chunk that was emptied out is air
    /// again. Coarse chunks over a build may hold something again.</summary>
    private void RecordBuild(ChunkPosition p, bool hasBlocks)
    {
        if (p.Y < _minY || p.Y > _minY + 63) return;
        ulong b = Bit(p);
        ulong built = _built.GetValueOrDefault((p.X, p.Z));
        _built[(p.X, p.Z)] = hasBlocks ? built | b : built & ~b;
        if (FullDetail.Columns.TryGetValue((p.X, p.Z), out var col))
            FullDetail.Columns[(p.X, p.Z)] = (col.Generated, hasBlocks ? col.Air & ~b : col.Air | b);
        if (!hasBlocks) return;
        foreach (var level in _levels.Skip(1))
        {
            var over = new ChunkPosition(p.X >> level.Level, p.Y >> level.Level, p.Z >> level.Level);
            if (level.Columns.TryGetValue((over.X, over.Z), out var c))
                level.Columns[(over.X, over.Z)] = (c.Generated, c.Air & ~level.Bit(over));
        }
    }

    private void RecordEdited(ChunkPosition p, bool hasBlocks)
    {
        _edited.TryAdd(p, 0);
        if (p.Y >= _minY && p.Y <= _minY + 63) _editedLayers[(p.X, p.Z)] = _editedLayers.GetValueOrDefault((p.X, p.Z)) | Bit(p);
        RecordBuild(p, hasBlocks);
    }

    /// <summary>Asks the pipeline for queued columns' chunks: full detail's, until its budget is full, then each coarse
    /// level's, up to <see cref="MaxLevelInFlight"/> chunks. A column whose edited chunks haven't arrived stays where it
    /// is in its queue (they're asked for), and the columns after it go meanwhile, up to MaxInFlight of them looked past.</summary>
    private void Dispatch()
    {
        var full = FullDetail;
        full.Full = false;
        int i = full.QueueHead, waiting = 0;
        double until = _steps.SinceLap() + BudgetLeft; // a share of the frame, at least MinStepMs
        while (i < full.Queue.Count && waiting < MaxInFlight && _steps.SinceLap() < until)
            if (!DispatchAt(full, ref i, ref waiting)) break;
        foreach (var level in _levels.Skip(1))
        {
            i = level.QueueHead;
            waiting = 0;
            while (level.InFlightChunks < MaxLevelInFlight && i < level.Queue.Count && waiting < MaxInFlight)
                DispatchAt(level, ref i, ref waiting);
        }
    }

    /// <summary>Asks for the level's queued column at <paramref name="i"/>'s chunks, or passes over it: asked for
    /// already, or waiting for its edited chunks. False when the level's budget has no room (it stops; when the column is
    /// nearer than the farthest loaded one, that's let go to make room).</summary>
    private bool DispatchAt(TerrainLevel level, ref int i, ref int waiting)
    {
        var col = level.Queue[i];
        var work = Missing(level, col.x, col.z);
        if (work.Count == 0)
        {
            Dequeue(level, ref i);
            if (level.Level == 0 && !level.InFlight.ContainsKey(col)) Loaded(col);
            return true;
        }
        if (!Arrived(work, level)) { i++; waiting++; return true; }

        if (level.Budget is { } budget)
        {
            int adding = level.InFlightChunks + work.Count;
            if (!budget.HasRoomFor(level.Volume, adding))
            {
                level.Full = true;
                var shortfall = budget.ShortfallFor(level.Volume, adding);
                if (!shortfall.IsNone)
                {
                    _steps.Lap(DispatchStep);
                    EvictFartherThan(level, ColumnDistSq(level, col), shortfall.Chunks + work.Count, shortfall.Units);
                    _steps.Lap(EvictStep);
                }
                return false;
            }
        }

        Dequeue(level, ref i);
        foreach (var (pos, _) in work) Want(level, pos);
        return true;
    }

    /// <summary>Takes the level's queued column at <paramref name="i"/> out (the head, unless columns before it are
    /// waiting), leaving <paramref name="i"/> at the next.</summary>
    private static void Dequeue(TerrainLevel level, ref int i)
    {
        if (i == level.QueueHead) i = ++level.QueueHead;
        else level.Queue.RemoveAt(i);
    }

    /// <summary>Whether the edited full chunks that making <paramref name="work"/> reads are all here to load; asks the
    /// store for any that aren't.</summary>
    private bool Arrived(List<(ChunkPosition Pos, bool Edited)> work, TerrainLevel level)
    {
        bool all = true;
        foreach (var pos in EditedUnder(level, work))
        {
            if (_chunkStore.IsReady(pos)) continue;
            _chunkStore.Request(pos);
            all = _chunkStore.IsReady(pos) && all; // a store on this machine may have it at once
        }
        return all;
    }

    /// <summary>Unloads the level's farthest loaded columns (not being loaded) that are more than a column farther than
    /// <paramref name="distSq"/> (so two columns at about the same distance don't keep swapping), until
    /// <paramref name="chunks"/> chunks and <paramref name="units"/> of the budget are freed, or
    /// <see cref="MaxEvictChunksPerFrame"/> chunks, or <see cref="MaxEvictMsPerFrame"/> of time.</summary>
    private void EvictFartherThan(TerrainLevel level, long distSq, int chunks, int units)
    {
        if (level.NothingToEvict) return;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (level.EvictNext < 0) BuildEvictOrder(level);
        _steps.Lap(EvictOrderStep);

        float margin = MathF.Sqrt(distSq) + 1f;
        int freedChunks = 0, freedUnits = 0;
        while (freedChunks < MaxEvictChunksPerFrame && (freedChunks < chunks || freedUnits < units))
        {
            if (level.EvictNext >= level.EvictOrder.Count || ColumnDistSq(level, level.EvictOrder[level.EvictNext]) <= margin * margin)
            {
                level.NothingToEvict = true;
                return;
            }
            var far = level.EvictOrder[level.EvictNext];
            if (level.InFlight.ContainsKey(far)) { level.EvictNext++; continue; } // still being made
            // The time budget is checked per chunk: a column cut short stays next, and its unloaded chunks are
            // skipped when it comes up again.
            int unloaded = 0;
            for (int y = level.MinY; y <= level.MaxY; y++)
            {
                var p = new ChunkPosition(far.x, y, far.z);
                if (!level.Volume.IsLoaded(p)) continue;
                if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds >= MaxEvictMsPerFrame)
                {
                    if (unloaded > 0) level.Evictions++;
                    return;
                }
                freedUnits += level.Budget!.CostOf(level.Volume, p);
                LetGoOf(level, p);
                unloaded++;
                freedChunks++;
            }
            level.EvictNext++;
            if (unloaded > 0) level.Evictions++;
        }
    }

    /// <summary>The level's loaded columns, farthest first, for eviction until its next rebuild: columns loaded since
    /// are nearer (the queue is closest first), so the order holds.</summary>
    private static void BuildEvictOrder(TerrainLevel level)
    {
        var columns = new HashSet<(int x, int z)>();
        foreach (var (p, _) in level.Volume.All) columns.Add((p.X, p.Z));
        var order = level.EvictOrder;
        order.Clear();
        order.AddRange(columns);
        // Each column's distance once, farthest first (the comparison sort would compute it twice per comparison).
        var keys = new long[order.Count];
        for (int i = 0; i < order.Count; i++) keys[i] = -ColumnDistSq(level, order[i]);
        keys.AsSpan().Sort(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(order));
        level.EvictNext = 0;
    }

    /// <summary>Whether the level's column (x, z) has wanted chunks that may hold something and aren't loaded yet.</summary>
    private bool HasMissing(TerrainLevel level, int x, int z)
    {
        for (ulong bits = MaybeContent(level, x, z) & WantedLayers(level, x, z); bits != 0; bits &= bits - 1)
            if (!level.Volume.IsLoaded(new ChunkPosition(x, level.MinY + BitOperations.TrailingZeroCount(bits), z))) return true;
        return false;
    }

    /// <summary>The level's column (x, z)'s wanted chunks that may hold something and aren't loaded or asked for yet,
    /// with whether each is edited (full detail's only: a coarse chunk is made from the full chunks under it).</summary>
    private List<(ChunkPosition Pos, bool Edited)> Missing(TerrainLevel level, int x, int z)
    {
        var missing = new List<(ChunkPosition, bool)>();
        for (ulong bits = MaybeContent(level, x, z) & WantedLayers(level, x, z); bits != 0; bits &= bits - 1)
        {
            var p = new ChunkPosition(x, level.MinY + BitOperations.TrailingZeroCount(bits), z);
            if (!level.Volume.IsLoaded(p) && !level.Wanting.Contains(p)) missing.Add((p, level.Level == 0 && _edited.ContainsKey(p)));
        }
        return missing;
    }

    /// <summary>Whether full chunk <paramref name="pos"/> (not loaded) is known to hold nothing: its column is wanted
    /// and not loading, and it's neither something the generator may fill nor a build. Where streaming doesn't reach, or
    /// hasn't looked yet, nothing is known.</summary>
    public bool IsKnownEmpty(ChunkPosition pos)
    {
        if (pos.Y < _minY || pos.Y > _minY + 63) return true; // never loaded: nothing there
        var full = FullDetail;
        if (!Wanted(full, pos.X, pos.Z) || full.InFlight.ContainsKey((pos.X, pos.Z))) return false;
        return (MaybeContent(full, pos.X, pos.Z) & Bit(pos)) == 0;
    }

    /// <summary>Chunks edits couldn't change here since the last frame (see <see cref="TerrainEditedElsewhere"/>).</summary>
    private void TakeEditsElsewhere()
    {
        if (_editedElsewhere.Count == 0) return;
        foreach (var e in _editedElsewhere.GetEntities().ToArray())
        {
            EditedElsewhere(e.Get<TerrainEditedElsewhere>().Position);
            e.Dispose();
        }
    }

    /// <summary>An edit changed chunk <paramref name="pos"/> while it wasn't here: it's edited from now on (loaded
    /// from the store, not generated), whatever the store had of it is stale, and if the pipeline is making it (or made
    /// it air), it's made again.</summary>
    private void EditedElsewhere(ChunkPosition pos)
    {
        if (pos.Y < _minY || pos.Y > _minY + 63) return;
        RecordEdited(pos, hasBlocks: true);
        _chunkStore.Outdated(pos);
        if (FullDetail.Wanting.Contains(pos) && !_staticVolume.IsLoaded(pos)) _pipeline.Invalidate(FullDetail.Key(pos), _voxels);
    }

    /// <summary>Whether the terrain within <paramref name="radius"/> (horizontally) of <paramref name="centre"/> has
    /// loaded in full detail: every column there is wanted, with nothing still to load or loading.</summary>
    public bool IsTerrainLoaded(Vector3D<float> centre, float radius)
    {
        var full = FullDetail;
        int cx = (int)MathF.Floor(centre.X / S), cz = (int)MathF.Floor(centre.Z / S), r = (int)MathF.Ceiling(radius / S);
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            int x = cx + dx, z = cz + dz;
            if (!Wanted(full, x, z) || full.InFlight.ContainsKey((x, z)) || HasMissing(full, x, z)) return false;
        }
        return true;
    }

    private void Unload(TerrainLevel level, ChunkPosition pos)
    {
        var entry = level.Volume.GetEntry(pos);
        // Edited while loaded: it loads from the store next time (the Host has every edit), so an edit off the island's
        // terrain (a bridge, a tower) comes back, and one that cleared a chunk out stops costing budget.
        if (level.Level == 0 && entry is not null && entry.Data.IsDirty) RecordEdited(pos, entry.Data.HasAnyNonAir());
        level.Volume.RemoveChunk(pos);
        level.ToDrop.Remove(pos);
        if (entry is { DataDropped: false }) entry.Data.Release(); // its arrays go to the next chunk to load
    }
}
