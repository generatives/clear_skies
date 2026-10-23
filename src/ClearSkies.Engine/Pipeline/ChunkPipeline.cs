using System.Collections.Concurrent;
using System.Diagnostics;
using ClearSkies.Engine.Core;

namespace ClearSkies.Engine.Pipeline;

/// <summary>What one <see cref="ChunkPipeline.Update"/> may spend: worker jobs in flight at once, and main-thread time
/// for applying worker results and running frame stages.</summary>
public readonly record struct FrameBudgets(int WorkerSlots, double MainMs);

/// <summary>
/// Makes chunks: runs each <see cref="Stage"/> on each chunk once its inputs are there, and again when one changes,
/// nearest first under each executor's budget. It's the one owner of where every chunk is, so nothing else keeps its
/// own copy of "is this ready".
/// </summary>
/// <remarks>
/// A chunk is <see cref="Want">wanted</see> (all its stages run) or only needed by another chunk's stage that reads it
/// (then only the stages that stage reads, and what they read, run). It's dropped once neither holds. A stage's last
/// output stays in use while it runs again, so a chunk shown stays shown through its reruns.
/// </remarks>
public sealed class ChunkPipeline
{
    private readonly List<PipelineGrid> _grids = new();
    private readonly List<Stage> _stages = new();
    private readonly Dictionary<ChunkKey, PipelineChunk> _records = new();
    // Per stage T: the stages reading it, on which grid, through which input (to run again when T's output changes).
    // Worked out once, from the grids and stages, before the first chunk: per grid, each stage's slot in its chunks
    // (-1 where the grid doesn't have the stage) and the inputs it reads there; per stage T, the stages reading it, on
    // which grid, through which input (to run again when T's output changes).
    private int[][] _slotOf = Array.Empty<int[]>();
    private int[] _slotCount = Array.Empty<int>();
    private IReadOnlyList<Input>[][] _inputs = Array.Empty<IReadOnlyList<Input>[]>();
    private List<(Stage Reader, int Grid, Input Input)>[] _readersOf = Array.Empty<List<(Stage, int, Input)>>();
    private bool _built;
    private readonly PriorityQueue<(PipelineChunk Record, Stage Stage), (float, int)> _ready = new();
    private readonly ConcurrentQueue<(PipelineChunk Record, WorkerStage Stage, object Made)> _made = new();
    // External stages started and not yet reported: each is asked again every frame.
    private readonly HashSet<(PipelineChunk Record, Stage Stage)> _awaitingExternal = new();
    private readonly HashSet<PipelineChunk> _maybeDrop = new();
    private readonly Action<Action> _runOnWorker;
    private int _inFlight;

    /// <param name="runOnWorker">Runs worker stages (tests run them in place); background workers by default.</param>
    public ChunkPipeline(Action<Action>? runOnWorker = null) => _runOnWorker = runOnWorker ?? BackgroundWork.Queue;

    /// <summary>Lower runs first: by default, all the same.</summary>
    public Func<ChunkKey, float>? Priority { get; set; }

    /// <summary>A chunk the pipeline dropped (its outputs released already).</summary>
    public event Action<PipelineChunk>? Dropped;

    public IReadOnlyList<Stage> Stages => _stages;
    public int ChunkCount => _records.Count;
    public IEnumerable<PipelineChunk> Records => _records.Values;

    public PipelineGrid AddGrid(string name, PipelineGrid? finer = null)
    {
        if (_built) throw new InvalidOperationException("Grids are added before any chunk.");
        var grid = new PipelineGrid(_grids.Count, name, finer);
        _grids.Add(grid);
        return grid;
    }

    public PipelineGrid Grid(int id) => _grids[id];

    /// <summary>Adds a stage. All stages are added before the first chunk.</summary>
    public T Add<T>(T stage) where T : Stage
    {
        if (_built) throw new InvalidOperationException("Stages are added before any chunk.");
        stage.Id = _stages.Count;
        _stages.Add(stage);
        return stage;
    }

    /// <summary>Works out which stages each grid has, what they read and what reads them.</summary>
    private void Build()
    {
        _built = true;
        _slotOf = new int[_grids.Count][];
        _slotCount = new int[_grids.Count];
        _inputs = new IReadOnlyList<Input>[_grids.Count][];
        _readersOf = _stages.Select(_ => new List<(Stage, int, Input)>()).ToArray();
        foreach (var grid in _grids)
        {
            var slotOf = _slotOf[grid.Id] = new int[_stages.Count];
            _inputs[grid.Id] = new IReadOnlyList<Input>[_stages.Count];
            foreach (var stage in _stages)
            {
                bool has = stage.AppliesTo(grid);
                slotOf[stage.Id] = has ? _slotCount[grid.Id]++ : -1;
                var inputs = _inputs[grid.Id][stage.Id] = has ? stage.InputsFor(grid) : Array.Empty<Input>();
                foreach (var input in inputs) _readersOf[input.Stage.Id].Add((stage, grid.Id, input));
            }
        }
    }

    public PipelineChunk? Get(ChunkKey key) => _records.GetValueOrDefault(key);

    /// <summary>Wants a chunk for itself (every stage it has runs), or no longer (it's dropped once nothing else needs it).</summary>
    public void Want(ChunkKey key, bool wanted)
    {
        var record = wanted ? Ensure(key) : Get(key);
        if (record == null || record.Wanted == wanted) return;
        record.Wanted = wanted;
        for (int s = 0; s < _stages.Count; s++)
            if (record.Has(s)) AddNeed(record, s, wanted ? 1 : -1);
        // Inputs that accept a missing chunk only see wanted ones: to them it just came or went.
        for (int s = 0; s < _stages.Count; s++)
            if (record.Has(s) && record.At(s).Version > 0) Propagate(record, _stages[s], Change.All, acceptsMissingOnly: true);
        if (!wanted) _maybeDrop.Add(record);
    }

    /// <summary>The stage's output on the chunk changed in place (an edit wrote new blocks into its voxels): what reads
    /// it runs again, as far as <paramref name="change"/> concerns it.</summary>
    public void OutputChanged(ChunkKey key, Stage stage, Change change)
    {
        if (Get(key) is not { } record || !record.EverDone(stage)) return;
        record.At(stage.Id).Version++;
        Propagate(record, stage, change, acceptsMissingOnly: false);
    }

    /// <summary>Something outside the pipeline that the stage depends on changed: it runs again on the chunk.</summary>
    public void Invalidate(ChunkKey key, Stage stage)
    {
        if (Get(key) is not { } record || !record.Has(stage.Id)) return;
        MarkStale(record, stage.Id);
    }

    /// <summary>Finishes a stage that was left running: an external stage's work, or a frame stage's GPU work.</summary>
    public void Report(ChunkKey key, Stage stage, object? output = null, Change? change = null)
    {
        if (Get(key) is not { } record || !record.Has(stage.Id) || !record.At(stage.Id).Is(SlotFlags.Running)) return;
        _awaitingExternal.Remove((record, stage));
        Finish(record, stage, output, change ?? Change.All);
    }

    /// <summary>Takes finished worker results and runs ready stages, nearest first, within <paramref name="budgets"/>.</summary>
    public void Update(FrameBudgets budgets)
    {
        long start = Stopwatch.GetTimestamp();
        bool MainLeft() => Stopwatch.GetElapsedTime(start).TotalMilliseconds < budgets.MainMs;

        while (MainLeft() && _made.TryDequeue(out var made))
        {
            _inFlight--;
            if (made.Made is Exception e) throw new InvalidOperationException($"{made.Stage} failed on {made.Record}", e);
            if (made.Record.Removed) { made.Stage.Release(made.Made); continue; }
            var change = made.Stage.AppliedAny(made.Record, made.Made, made.Record.At(made.Stage.Id).Output);
            Finish(made.Record, made.Stage, made.Made, change);
        }

        foreach (var (record, stage) in _awaitingExternal) ((ExternalStage)stage).Request(record);

        var later = new List<((PipelineChunk, Stage) Item, (float, int) Priority)>();
        bool workersFull = _inFlight >= budgets.WorkerSlots, mainFull = !MainLeft();
        while (!(workersFull && mainFull) && _ready.TryDequeue(out var item, out var priority))
        {
            var (record, stage) = item;
            if (record.Removed) continue;
            ref var slot = ref record.At(stage.Id);
            slot.Set(SlotFlags.Queued, false);
            if (slot.Is(SlotFlags.Running) || !slot.NeedsRun || !InputsThere(record, stage)) continue;
            switch (stage)
            {
                case WorkerStage worker when !workersFull:
                    StartWorker(record, worker);
                    workersFull = _inFlight >= budgets.WorkerSlots;
                    break;
                case FrameStage frame when !mainFull:
                    StartFrame(record, frame);
                    mainFull = !MainLeft();
                    break;
                case ExternalStage external:
                    slot.Set(SlotFlags.Stale, false);
                    slot.Set(SlotFlags.Running, true);
                    _awaitingExternal.Add((record, external));
                    external.Request(record);
                    break;
                default:
                    later.Add((item, priority)); // its executor is spent this frame
                    slot.Set(SlotFlags.Queued, true);
                    break;
            }
        }
        foreach (var (item, priority) in later) _ready.Enqueue(item, priority);

        foreach (var record in _maybeDrop) DropIfUnneeded(record);
        _maybeDrop.Clear();
    }

    /// <summary>How many chunks have the stage in each state, for the panel.</summary>
    public (int Waiting, int Ready, int Running, int Done) Count(Stage stage)
    {
        int w = 0, r = 0, run = 0, d = 0;
        foreach (var record in _records.Values)
        {
            if (!record.Has(stage.Id)) continue;
            switch (record.State(stage))
            {
                case SlotState.Waiting: w++; break;
                case SlotState.Ready: r++; break;
                case SlotState.Running: run++; break;
                default: d++; break;
            }
        }
        return (w, r, run, d);
    }

    private IReadOnlyList<Input> InputsOf(Stage stage, int grid) => _inputs[grid][stage.Id];

    private PipelineChunk Ensure(ChunkKey key)
    {
        if (_records.TryGetValue(key, out var record)) return record;
        if (!_built) Build();
        record = new PipelineChunk(key, _grids[key.Grid], _slotOf[key.Grid], _slotCount[key.Grid]);
        _records.Add(key, record);
        return record;
    }

    /// <summary>Adds (or takes away) a reason for the stage to run on the chunk.</summary>
    private void AddNeed(PipelineChunk record, int stage, int delta)
    {
        int neededBy = record.At(stage).NeededBy += delta;
        KeepSourcesLoaded(record, stage);
        Check(record, stage);
        if (neededBy == 0) _maybeDrop.Add(record);
    }

    /// <summary>Keeps the stage's required sources loaded (adds to what needs them) while it needs to run or is running, and lets
    /// them go once it's done. Relations pick the same sources every time, so letting go needs no list of them.</summary>
    private void KeepSourcesLoaded(PipelineChunk record, int stage)
    {
        ref var slot = ref record.At(stage);
        bool keep = slot.NeedsRun || slot.Is(SlotFlags.Running);
        if (keep == slot.Is(SlotFlags.KeepingSourcesLoaded)) return;
        slot.Set(SlotFlags.KeepingSourcesLoaded, keep);
        AddNeedToSources(record, stage, keep ? 1 : -1);
    }

    private void AddNeedToSources(PipelineChunk record, int stage, int delta)
    {
        foreach (var input in InputsOf(_stages[stage], record.Key.Grid))
        {
            if (input.AcceptsMissing) continue;
            foreach (var key in input.Of.Picked(this, record.Key))
            {
                var source = Ensure(key);
                if (source.Has(input.Stage.Id)) AddNeed(source, input.Stage.Id, delta);
            }
        }
    }

    /// <summary>Queues the stage on the chunk if it needs to run and its inputs are there.</summary>
    private void Check(PipelineChunk record, int stage)
    {
        ref var slot = ref record.At(stage);
        if (record.Removed || slot.Is(SlotFlags.Queued | SlotFlags.Running) || !slot.NeedsRun
            || !InputsThere(record, _stages[stage])) return;
        slot.Set(SlotFlags.Queued, true);
        _ready.Enqueue((record, _stages[stage]), (Priority?.Invoke(record.Key) ?? 0f, -stage));
    }

    private bool InputsThere(PipelineChunk record, Stage stage)
    {
        foreach (var input in InputsOf(stage, record.Key.Grid))
        {
            if (input.AcceptsMissing) continue;
            foreach (var key in input.Of.Picked(this, record.Key))
                if (Get(key) is not { } source || source.Has(input.Stage.Id) && source.At(input.Stage.Id).Version == 0) return false;
        }
        return true;
    }

    private StageInputs Gather(PipelineChunk record, Stage stage)
    {
        var read = new StageInputs();
        foreach (var input in InputsOf(stage, record.Key.Grid))
            foreach (var key in input.Of.Picked(this, record.Key))
            {
                var source = Get(key);
                bool seen = source != null && source.Has(input.Stage.Id) && (!input.AcceptsMissing || source.Wanted);
                read.Add(input.Stage, key, seen ? source!.At(input.Stage.Id).Output : null);
            }
        return read;
    }

    private void StartWorker(PipelineChunk record, WorkerStage stage)
    {
        var inputs = Gather(record, stage);
        ref var slot = ref record.At(stage.Id);
        slot.Set(SlotFlags.Stale, false);
        slot.Set(SlotFlags.Running, true);
        _inFlight++;
        var key = record.Key;
        _runOnWorker(() =>
        {
            object made;
            try { made = stage.MakeAny(key, inputs); }
            catch (Exception e) { made = e; }
            _made.Enqueue((record, stage, made));
        });
    }

    private void StartFrame(PipelineChunk record, FrameStage stage)
    {
        ref var slot = ref record.At(stage.Id);
        slot.Set(SlotFlags.Stale, false);
        slot.Set(SlotFlags.Running, true);
        var result = stage.Run(record, Gather(record, stage));
        if (result.Finished) Finish(record, stage, result.Output, result.Change);
    }

    private void Finish(PipelineChunk record, Stage stage, object? output, Change change)
    {
        ref var slot = ref record.At(stage.Id);
        slot.Set(SlotFlags.Running, false);
        if (slot.Output is { } before && !ReferenceEquals(before, output)) stage.Release(before);
        slot.Output = output;
        slot.Version++;
        Propagate(record, stage, change, acceptsMissingOnly: false);
        KeepSourcesLoaded(record, stage.Id);   // done: lets its sources go, unless it went stale meanwhile
        Check(record, stage.Id);  // it did: runs again
        _maybeDrop.Add(record);
    }

    /// <summary>Marks what reads <paramref name="stage"/>'s output on the chunk, and cares about the change, to run again.</summary>
    private void Propagate(PipelineChunk record, Stage stage, Change change, bool acceptsMissingOnly)
    {
        foreach (var (reader, grid, input) in _readersOf[stage.Id])
        {
            if (acceptsMissingOnly && !input.AcceptsMissing) continue;
            if (input.AcceptsMissing && !acceptsMissingOnly && !record.Wanted) continue; // they don't see it
            foreach (var key in input.Of.Affected(this, record.Key, change))
                if (key.Grid == grid && Get(key) is { } dependant)
                    MarkStale(dependant, reader.Id);
        }
    }

    private void MarkStale(PipelineChunk record, int stage)
    {
        record.At(stage).Set(SlotFlags.Stale, true);
        KeepSourcesLoaded(record, stage);
        Check(record, stage);
    }

    /// <summary>Drops a chunk nothing needs: not wanted, no stage elsewhere waiting on it, no worker making it.</summary>
    private void DropIfUnneeded(PipelineChunk record)
    {
        if (record.Removed || record.Wanted) return;
        foreach (var stage in _stages)
            if (record.Has(stage.Id) && (record.At(stage.Id).NeededBy > 0
                                         || record.At(stage.Id).Is(SlotFlags.Running) && stage is WorkerStage)) return;
        record.Removed = true;
        _records.Remove(record.Key);
        foreach (var stage in _stages)
        {
            if (!record.Has(stage.Id)) continue;
            ref var slot = ref record.At(stage.Id);
            _awaitingExternal.Remove((record, stage));
            if (slot.Is(SlotFlags.KeepingSourcesLoaded)) { slot.Set(SlotFlags.KeepingSourcesLoaded, false); AddNeedToSources(record, stage.Id, -1); }
            if (slot.Output is { } output) { slot.Output = null; stage.Release(output); }
        }
        Dropped?.Invoke(record);
    }
}
