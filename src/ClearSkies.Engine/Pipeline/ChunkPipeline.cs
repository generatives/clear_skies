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
    private List<(Stage Reader, int Grid, Input Input)>[] _readersOf = Array.Empty<List<(Stage, int, Input)>>();
    private readonly Dictionary<(int Stage, int Grid), IReadOnlyList<Input>> _inputs = new();
    private readonly PriorityQueue<(PipelineChunk Record, Stage Stage), (float, int)> _ready = new();
    private readonly ConcurrentQueue<(PipelineChunk Record, WorkerStage Stage, object Made)> _made = new();
    private readonly HashSet<(PipelineChunk Record, Stage Stage)> _asking = new();
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
        var grid = new PipelineGrid(_grids.Count, name, finer);
        _grids.Add(grid);
        return grid;
    }

    public PipelineGrid Grid(int id) => _grids[id];

    /// <summary>Adds a stage. All stages are added before the first chunk.</summary>
    public T Add<T>(T stage) where T : Stage
    {
        if (_records.Count > 0) throw new InvalidOperationException("Stages are added before any chunk.");
        stage.Id = _stages.Count;
        _stages.Add(stage);
        _readersOf = _stages.Select(_ => new List<(Stage, int, Input)>()).ToArray();
        _inputs.Clear();
        foreach (var reader in _stages)
            foreach (var grid in _grids)
                if (reader.AppliesTo(grid))
                    foreach (var input in InputsOf(reader, grid.Id))
                        _readersOf[input.Stage.Id].Add((reader, grid.Id, input));
        return stage;
    }

    public PipelineChunk? Get(ChunkKey key) => _records.GetValueOrDefault(key);

    /// <summary>Wants a chunk for itself (every stage it has runs), or no longer (it's dropped once nothing else needs it).</summary>
    public void Want(ChunkKey key, bool wanted)
    {
        var record = wanted ? Ensure(key) : Get(key);
        if (record == null || record.Wanted == wanted) return;
        record.Wanted = wanted;
        for (int s = 0; s < _stages.Count; s++)
            if (record.Slots[s].Applies) Demand(record, s, wanted ? 1 : -1);
        // Inputs that accept a missing chunk only see wanted ones: to them it just came or went.
        for (int s = 0; s < _stages.Count; s++)
            if (record.Slots[s].Version > 0) Propagate(record, _stages[s], Change.All, acceptsMissingOnly: true);
        if (!wanted) _maybeDrop.Add(record);
    }

    /// <summary>The stage's output on the chunk changed in place (an edit wrote new blocks into its voxels): what reads
    /// it runs again, as far as <paramref name="change"/> concerns it.</summary>
    public void OutputChanged(ChunkKey key, Stage stage, Change change)
    {
        if (Get(key) is not { } record || record.Slots[stage.Id].Version == 0) return;
        record.Slots[stage.Id].Version++;
        Propagate(record, stage, change, acceptsMissingOnly: false);
    }

    /// <summary>Something outside the pipeline that the stage depends on changed: it runs again on the chunk.</summary>
    public void Invalidate(ChunkKey key, Stage stage)
    {
        if (Get(key) is not { } record) return;
        MarkStale(record, stage.Id);
    }

    /// <summary>Finishes a stage that was left running: an external stage's work, or a frame stage's GPU work.</summary>
    public void Report(ChunkKey key, Stage stage, object? output = null, Change? change = null)
    {
        if (Get(key) is not { } record || !record.Slots[stage.Id].Running) return;
        _asking.Remove((record, stage));
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
            var slot = made.Record.Slots[made.Stage.Id];
            if (made.Record.Removed) { made.Stage.Release(made.Made); continue; }
            var change = made.Stage.AppliedAny(made.Record, made.Made, slot.Output);
            Finish(made.Record, made.Stage, made.Made, change);
        }

        foreach (var (record, stage) in _asking) ((ExternalStage)stage).Request(record);

        var later = new List<((PipelineChunk, Stage) Item, (float, int) Priority)>();
        bool workersFull = _inFlight >= budgets.WorkerSlots, mainFull = !MainLeft();
        while (!(workersFull && mainFull) && _ready.TryDequeue(out var item, out var priority))
        {
            var (record, stage) = item;
            var slot = record.Slots[stage.Id];
            slot.Queued = false;
            if (record.Removed || slot.Running || !NeedsRun(slot) || !InputsThere(record, stage)) continue;
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
                    slot.Stale = false;
                    slot.Running = true;
                    _asking.Add((record, external));
                    external.Request(record);
                    break;
                default:
                    later.Add((item, priority)); // its executor is spent this frame
                    slot.Queued = true;
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
            if (!record.Slots[stage.Id].Applies) continue;
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

    private IReadOnlyList<Input> InputsOf(Stage stage, int grid)
    {
        if (!_inputs.TryGetValue((stage.Id, grid), out var inputs))
            _inputs[(stage.Id, grid)] = inputs = stage.InputsFor(_grids[grid]);
        return inputs;
    }

    private PipelineChunk Ensure(ChunkKey key)
    {
        if (_records.TryGetValue(key, out var record)) return record;
        record = new PipelineChunk(key, _grids[key.Grid], _stages);
        _records.Add(key, record);
        return record;
    }

    private static bool NeedsRun(Slot slot) => slot.Applies && slot.Demand > 0 && slot.Stale;

    /// <summary>Adds (or takes away) a reason for the stage to run on the chunk.</summary>
    private void Demand(PipelineChunk record, int stage, int delta)
    {
        var slot = record.Slots[stage];
        slot.Demand += delta;
        Hold(record, stage);
        Check(record, stage);
        if (slot.Demand == 0) _maybeDrop.Add(record);
    }

    /// <summary>Keeps the stage's required sources loaded (demanded) while it needs to run or is running, and lets
    /// them go once it's done.</summary>
    private void Hold(PipelineChunk record, int stage)
    {
        var slot = record.Slots[stage];
        bool hold = NeedsRun(slot) || slot.Running;
        if (hold && slot.Holds == null)
        {
            slot.Holds = new();
            foreach (var input in InputsOf(_stages[stage], record.Key.Grid))
            {
                if (input.AcceptsMissing) continue;
                foreach (var key in input.Of.Picked(this, record.Key))
                {
                    var source = Ensure(key);
                    if (!source.Slots[input.Stage.Id].Applies) continue;
                    slot.Holds.Add((source, input.Stage.Id));
                    Demand(source, input.Stage.Id, 1);
                }
            }
        }
        else if (!hold && slot.Holds is { } holds)
        {
            slot.Holds = null;
            foreach (var (source, s) in holds) Demand(source, s, -1);
        }
    }

    /// <summary>Queues the stage on the chunk if it needs to run and its inputs are there.</summary>
    private void Check(PipelineChunk record, int stage)
    {
        var slot = record.Slots[stage];
        if (record.Removed || slot.Queued || slot.Running || !NeedsRun(slot) || !InputsThere(record, _stages[stage])) return;
        slot.Queued = true;
        _ready.Enqueue((record, _stages[stage]), (Priority?.Invoke(record.Key) ?? 0f, -stage));
    }

    private bool InputsThere(PipelineChunk record, Stage stage)
    {
        foreach (var input in InputsOf(stage, record.Key.Grid))
        {
            if (input.AcceptsMissing) continue;
            foreach (var key in input.Of.Picked(this, record.Key))
                if (Get(key) is not { } source || source.Slots[input.Stage.Id] is { Applies: true, Version: 0 }) return false;
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
                bool seen = source != null && (!input.AcceptsMissing || source.Wanted);
                read.Add(input.Stage, key, seen ? source!.Slots[input.Stage.Id].Output : null);
            }
        return read;
    }

    private void StartWorker(PipelineChunk record, WorkerStage stage)
    {
        var slot = record.Slots[stage.Id];
        var inputs = Gather(record, stage);
        slot.Stale = false;
        slot.Running = true;
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
        var slot = record.Slots[stage.Id];
        slot.Stale = false;
        slot.Running = true;
        var result = stage.Run(record, Gather(record, stage));
        if (result.Finished) Finish(record, stage, result.Output, result.Change);
    }

    private void Finish(PipelineChunk record, Stage stage, object? output, Change change)
    {
        var slot = record.Slots[stage.Id];
        slot.Running = false;
        if (slot.Output is { } before && !ReferenceEquals(before, output)) stage.Release(before);
        slot.Output = output;
        slot.Version++;
        Propagate(record, stage, change, acceptsMissingOnly: false);
        Hold(record, stage.Id);   // done: lets its sources go, unless it went stale meanwhile
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
                if (key.Grid == grid && Get(key) is { } dependant && dependant.Slots[reader.Id].Applies)
                    MarkStale(dependant, reader.Id);
        }
    }

    private void MarkStale(PipelineChunk record, int stage)
    {
        record.Slots[stage].Stale = true;
        Hold(record, stage);
        Check(record, stage);
    }

    /// <summary>Drops a chunk nothing needs: not wanted, no stage elsewhere waiting on it, no worker making it.</summary>
    private void DropIfUnneeded(PipelineChunk record)
    {
        if (record.Removed || record.Wanted) return;
        for (int s = 0; s < record.Slots.Length; s++)
            if (record.Slots[s].Demand > 0 || record.Slots[s].Running && _stages[s] is WorkerStage) return;
        record.Removed = true;
        _records.Remove(record.Key);
        for (int s = 0; s < record.Slots.Length; s++)
        {
            var slot = record.Slots[s];
            _asking.Remove((record, _stages[s]));
            if (slot.Holds is { } holds) { slot.Holds = null; foreach (var (source, i) in holds) Demand(source, i, -1); }
            if (slot.Output is { } output) { slot.Output = null; _stages[s].Release(output); }
        }
        Dropped?.Invoke(record);
    }
}
