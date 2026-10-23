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
/// Only <see cref="Want">wanted</see> chunks are in it, and every stage they have runs. A stage reading another chunk
/// waits until that one is wanted and done; it never loads it (a coarse chunk makes its own voxels, at its own
/// resolution). A stage's last output stays in use while it runs again, so a chunk shown stays shown through its reruns.
/// </remarks>
public sealed class ChunkPipeline
{
    private readonly List<PipelineGrid> _grids = new();
    private readonly List<Stage> _stages = new();
    private readonly Dictionary<ChunkKey, PipelineChunk> _records = new();
    // Worked out once, from the grids and stages, before the first chunk: per grid, each stage's slot in its chunks
    // (-1 where the grid doesn't have the stage) and the inputs it reads there; per stage T, the stages reading it, on
    // which grid, through which input (to run again when T's output changes).
    private int[][] _slotOf = Array.Empty<int[]>();
    private int[] _slotCount = Array.Empty<int>();
    private IReadOnlyList<Input>[][] _inputs = Array.Empty<IReadOnlyList<Input>[]>();
    private List<(Stage Reader, int Grid, Input Input)>[] _readersOf = Array.Empty<List<(Stage, int, Input)>>();
    private bool _built;
    // Ready stages, one queue per executor (by Executor), so one running out doesn't hold up the others.
    private readonly PriorityQueue<(PipelineChunk Record, Stage Stage), (float, int)>[] _ready =
        Enum.GetValues<Executor>().Select(_ => new PriorityQueue<(PipelineChunk, Stage), (float, int)>()).ToArray();
    private readonly ConcurrentQueue<(PipelineChunk Record, WorkerStage Stage, object Made)> _made = new();
    // External stages asked and not done yet: asked again in turn, as far as each frame's time goes.
    private readonly Queue<(PipelineChunk Record, ExternalStage Stage)> _awaitingExternal = new();
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

    /// <summary>Wants a chunk (every stage it has runs), or no longer (it's dropped once no worker is making it).</summary>
    public void Want(ChunkKey key, bool wanted)
    {
        var record = wanted ? Ensure(key) : Get(key);
        if (record == null || record.Wanted == wanted) return;
        record.Wanted = wanted;
        for (int s = 0; s < _stages.Count; s++)
            if (record.Has(s)) Check(record, s);
        // Inputs that accept a missing chunk see it come or go.
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

        // Everything below is on the main thread, so all of it stops when the frame's time is spent.
        while (MainLeft() && _inFlight < budgets.WorkerSlots && TakeReady(Executor.Worker, out var record, out var stage, out _))
            StartWorker(record, (WorkerStage)stage);

        // Each one waiting is asked once at most, and those not reached are first next frame.
        for (int n = _awaitingExternal.Count; n > 0 && MainLeft(); n--)
        {
            var (record, stage) = _awaitingExternal.Dequeue();
            if (!record.Removed && !Ask(record, stage)) _awaitingExternal.Enqueue((record, stage));
        }

        while (MainLeft() && TakeReady(Executor.External, out var record, out var stage, out _))
        {
            ref var slot = ref record.At(stage.Id);
            slot.Set(SlotFlags.Stale, false);
            slot.Set(SlotFlags.Running, true);
            if (!Ask(record, (ExternalStage)stage)) _awaitingExternal.Enqueue((record, (ExternalStage)stage));
        }

        var waiting = new List<((PipelineChunk, Stage) Item, (float, int) Priority)>();
        while (MainLeft() && TakeReady(Executor.Frame, out var record, out var stage, out var priority))
            if (!RunFrame(record, (FrameStage)stage)) waiting.Add(((record, stage), priority));
        foreach (var (item, priority) in waiting) _ready[(int)Executor.Frame].Enqueue(item, priority);

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

    /// <summary>Worker jobs running now.</summary>
    public int WorkerJobs => _inFlight;

    /// <summary>External stages asked and not done yet.</summary>
    public int AwaitingExternal => _awaitingExternal.Count;

    /// <summary>The chunk and stage that's been ready longest without running, and for how long (seconds), for the
    /// panel: what a chunk that never seems to get made is stuck behind.</summary>
    public (PipelineChunk Record, Stage Stage, double Seconds, float Priority)? LongestReady()
    {
        (PipelineChunk, Stage, long)? oldest = null;
        foreach (var record in _records.Values)
            foreach (var stage in _stages)
                if (record.Has(stage.Id) && record.State(stage) == SlotState.Ready
                    && (oldest == null || record.At(stage.Id).QueuedAt < oldest.Value.Item3))
                    oldest = (record, stage, record.At(stage.Id).QueuedAt);
        if (oldest is not var (r, s, at)) return null;
        return (r, s, Stopwatch.GetElapsedTime(at).TotalSeconds, Priority?.Invoke(r.Key) ?? 0f);
    }

    /// <summary>Chunks in each grid, and of those, how many are being made (any stage ready or running), for the panel.</summary>
    public (int Chunks, int Making)[] CountByGrid()
    {
        var counts = new (int, int)[_grids.Count];
        foreach (var record in _records.Values)
        {
            ref var c = ref counts[record.Key.Grid];
            c.Item1++;
            foreach (var stage in _stages)
                if (record.State(stage) is SlotState.Ready or SlotState.Running) { c.Item2++; break; }
        }
        return counts;
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

    /// <summary>The next stage ready to run on the executor, skipping any no longer ready.</summary>
    private bool TakeReady(Executor executor, out PipelineChunk record, out Stage stage, out (float, int) priority)
    {
        while (_ready[(int)executor].TryDequeue(out var item, out priority))
        {
            (record, stage) = item;
            ref var slot = ref record.At(stage.Id);
            slot.Set(SlotFlags.Queued, false);
            if (record.Wanted && !slot.Is(SlotFlags.Running) && slot.Is(SlotFlags.Stale) && InputsThere(record, stage))
                return true;
        }
        (record, stage) = (null!, null!);
        return false;
    }

    /// <summary>Queues the stage on the chunk if it's wanted, stale and its inputs are there.</summary>
    private void Check(PipelineChunk record, int stage)
    {
        ref var slot = ref record.At(stage);
        if (!record.Wanted || slot.Is(SlotFlags.Queued | SlotFlags.Running) || !slot.Is(SlotFlags.Stale)
            || !InputsThere(record, _stages[stage])) return;
        slot.Set(SlotFlags.Queued, true);
        slot.QueuedAt = Stopwatch.GetTimestamp();
        _ready[(int)_stages[stage].RunsOn].Enqueue((record, _stages[stage]), (Priority?.Invoke(record.Key) ?? 0f, -stage));
    }

    private bool InputsThere(PipelineChunk record, Stage stage)
    {
        foreach (var input in InputsOf(stage, record.Key.Grid))
        {
            if (input.AcceptsMissing) continue;
            foreach (var key in input.Of.Picked(this, record.Key))
                if (Get(key) is not { Wanted: true } source || source.Has(input.Stage.Id) && source.At(input.Stage.Id).Version == 0) return false;
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
                bool seen = source is { Wanted: true } && source.Has(input.Stage.Id);
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

    /// <summary>Runs a frame stage; false if it can't yet (it's left stale, to run again next frame).</summary>
    private bool RunFrame(PipelineChunk record, FrameStage stage)
    {
        var result = stage.Run(record, Gather(record, stage));
        if (!result.Finished) { record.At(stage.Id).Set(SlotFlags.Queued, true); return false; }
        record.At(stage.Id).Set(SlotFlags.Stale, false);
        Finish(record, stage, result.Output, result.Change);
        return true;
    }

    /// <summary>Asks an external stage; true once it answered done (and is finished).</summary>
    private bool Ask(PipelineChunk record, ExternalStage stage)
    {
        var result = stage.Request(record);
        if (result.Finished) Finish(record, stage, result.Output, result.Change);
        return result.Finished;
    }

    private void Finish(PipelineChunk record, Stage stage, object? output, Change change)
    {
        ref var slot = ref record.At(stage.Id);
        slot.Set(SlotFlags.Running, false);
        if (slot.Output is { } before && !ReferenceEquals(before, output)) stage.Release(before);
        slot.Output = output;
        slot.Version++;
        Propagate(record, stage, change, acceptsMissingOnly: false);
        Check(record, stage.Id);  // its inputs changed while it ran: runs again
        _maybeDrop.Add(record);
    }

    /// <summary>Marks what reads <paramref name="stage"/>'s output on the chunk, and cares about the change, to run again.</summary>
    private void Propagate(PipelineChunk record, Stage stage, Change change, bool acceptsMissingOnly)
    {
        if (!record.Wanted && !acceptsMissingOnly) return; // on its way out: nothing reads it
        foreach (var (reader, grid, input) in _readersOf[stage.Id])
        {
            if (acceptsMissingOnly && !input.AcceptsMissing) continue;
            foreach (var key in input.Of.Affected(this, record.Key, change))
                if (key.Grid == grid && Get(key) is { } dependant)
                    MarkStale(dependant, reader.Id);
        }
    }

    private void MarkStale(PipelineChunk record, int stage)
    {
        record.At(stage).Set(SlotFlags.Stale, true);
        Check(record, stage);
    }

    /// <summary>Drops a chunk no longer wanted, once no worker is making anything for it.</summary>
    private void DropIfUnneeded(PipelineChunk record)
    {
        if (record.Removed || record.Wanted) return;
        foreach (var stage in _stages)
            if (stage is WorkerStage && record.Has(stage.Id) && record.At(stage.Id).Is(SlotFlags.Running)) return;
        record.Removed = true;
        _records.Remove(record.Key);
        foreach (var stage in _stages)
        {
            if (!record.Has(stage.Id)) continue;
            ref var slot = ref record.At(stage.Id);
            if (slot.Output is { } output) { slot.Output = null; stage.Release(output); }
        }
        Dropped?.Invoke(record);
    }
}
