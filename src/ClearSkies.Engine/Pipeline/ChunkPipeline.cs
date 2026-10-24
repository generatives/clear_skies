using System.Collections.Concurrent;
using System.Diagnostics;
using ClearSkies.Engine.Core;
using ClearSkies.Engine.Voxels;

namespace ClearSkies.Engine.Pipeline;

/// <summary>What one <see cref="ChunkPipeline.Update"/> may spend: worker jobs in flight at once on each worker, and
/// main-thread time for applying worker results and running frame stages.</summary>
public readonly record struct FrameBudgets(int WorkerSlots, double MainMs);

/// <summary>
/// Makes chunks: runs each <see cref="Stage"/> on each chunk once its inputs are there, and again when one changes,
/// nearest first under each executor's budget. It's the one owner of where every chunk is, so nothing else keeps its
/// own copy of "is this ready".
/// </summary>
/// <remarks>
/// Only <see cref="Want">wanted</see> chunks are in it, and every stage they need runs: those for any chunk, and those
/// serving what it's wanted for (<see cref="WantFor"/>). A stage reading another chunk
/// waits until that one is wanted and done; it never loads it (a coarse chunk makes its own voxels, at its own
/// resolution). A stage's last output stays in use while it runs again, so a chunk shown stays shown through its reruns.
/// </remarks>
public sealed class ChunkPipeline
{
    private const int MaxSkipped = 1024; // ready chunks looked past a frame, for their worker being full
    // The most of a frame's main-thread time spent asking external stages: thousands of chunks wait on their first
    // light while the world loads, each asked every frame.
    private const double ExternalShare = 0.25;
    private readonly List<PipelineGrid> _grids = new();
    private readonly Dictionary<ChunkVolume, PipelineGrid> _byVolume = new();
    private readonly List<Stage> _stages = new();
    private readonly Dictionary<ChunkKey, PipelineChunk> _records = new();
    // Worked out from the grids and stages, before the first chunk (and for a grid added after, as it's added): each
    // grid's stage slots and inputs (on the grid), and per stage T, the stages reading it, on which grid, through which
    // input (to run again when T's output changes).
    private List<(Stage Reader, int Grid, Input Input)>[] _readersOf = Array.Empty<List<(Stage, int, Input)>>();
    private bool _built;
    // Ready stages, one queue per executor (by Executor), so one running out doesn't hold up the others.
    private readonly PriorityQueue<(PipelineChunk Record, Stage Stage), (float, int)>[] _ready =
        Enum.GetValues<Executor>().Select(_ => new PriorityQueue<(PipelineChunk, Stage), (float, int)>()).ToArray();
    // Finished worker results; urgent ones (run on no particular worker, -1) taken first.
    private readonly ConcurrentQueue<(PipelineChunk Record, WorkerStage Stage, int Worker, object Made)> _made = new(), _madeUrgent = new();
    // External stages asked and not done yet: asked again in turn, as far as each frame's time goes.
    private readonly Queue<(PipelineChunk Record, ExternalStage Stage)> _awaitingExternal = new();
    private readonly HashSet<PipelineChunk> _maybeDrop = new();
    private readonly Action<int, Action> _runOnWorker;
    private readonly Action<Action> _runUrgent;
    private readonly int[] _inFlight; // per worker
    private readonly List<((PipelineChunk, Stage) Item, (float, int) Priority)> _skipped = new();

    /// <summary>On the background workers (see <see cref="BackgroundWork"/>), each chunk on the one for its ground (see
    /// <see cref="WorkerOf"/>).</summary>
    public ChunkPipeline() : this(BackgroundWork.Queue, BackgroundWork.Workers, BackgroundWork.Soon) { }

    /// <param name="runOnWorker">Runs worker stages, all on one worker (tests run them in place).</param>
    public ChunkPipeline(Action<Action> runOnWorker) : this((_, work) => runOnWorker(work), 1) { }

    /// <param name="runOnWorker">Runs a worker stage on the given worker, of <paramref name="workers"/>.</param>
    /// <param name="runUrgent">Runs urgent work (see <see cref="Change.Urgent"/>) on whichever worker is free first,
    /// before anything already queued; by default, on worker 0.</param>
    public ChunkPipeline(Action<int, Action> runOnWorker, int workers, Action<Action>? runUrgent = null)
    {
        _runOnWorker = runOnWorker;
        _runUrgent = runUrgent ?? (work => runOnWorker(0, work));
        _inFlight = new int[workers];
    }

    /// <summary>Chunks over the same column of this level's chunks (16 of the finest grid's columns) go to the same worker.</summary>
    private const int WorkerColumnLevel = 2;

    /// <summary>Which worker makes a chunk: the one for the column of level-<see cref="WorkerColumnLevel"/> chunks it's
    /// in, so every grid's chunks over the same ground are made on the same worker, and share what its caches keep of
    /// that ground (a generator's column profiles).</summary>
    private int WorkerOf(ChunkKey key)
    {
        int shift = WorkerColumnLevel - _grids[key.Grid].Level;
        int x = shift >= 0 ? key.Pos.X >> shift : key.Pos.X << -shift, z = shift >= 0 ? key.Pos.Z >> shift : key.Pos.Z << -shift;
        return (int)((uint)(x * 73856093 ^ z * 19349663) % (uint)_inFlight.Length);
    }

    /// <summary>Lower runs first, per chunk and stage: by default, all the same.</summary>
    public Func<ChunkKey, Stage, float>? Priority { get; set; }

    /// <summary>A chunk the pipeline dropped (its outputs released already).</summary>
    public event Action<PipelineChunk>? Dropped;

    public IReadOnlyList<Stage> Stages => _stages;
    public int ChunkCount => _records.Count;
    public IEnumerable<PipelineChunk> Records => _records.Values;

    /// <summary>Adds a grid, at any time.</summary>
    public PipelineGrid AddGrid(string name, PipelineGrid? finer = null) => Add(new PipelineGrid(_grids.Count, name, finer, null));

    /// <summary>The grid of a volume (a ship's): the one it has, or a new one.</summary>
    public PipelineGrid GridFor(ChunkVolume volume, string name)
        => _byVolume.TryGetValue(volume, out var grid) ? grid : _byVolume[volume] = Add(new PipelineGrid(_grids.Count, name, null, volume));

    /// <summary>The grid <see cref="GridFor"/> gave the volume, if it's still here.</summary>
    public PipelineGrid? GridOf(ChunkVolume volume) => _byVolume.GetValueOrDefault(volume);

    private PipelineGrid Add(PipelineGrid grid)
    {
        _grids.Add(grid);
        if (_built) BuildGrid(grid);
        return grid;
    }

    /// <summary>Takes a grid out (a ship that's gone): its chunks are let go of, and dropped as usual.</summary>
    public void RemoveGrid(PipelineGrid grid)
    {
        if (grid.Removed) return;
        foreach (var record in _records.Values.Where(r => r.Grid == grid).ToList()) Want(record.Key, false);
        grid.Removed = true;
        if (grid.Volume != null && _byVolume.GetValueOrDefault(grid.Volume) == grid) _byVolume.Remove(grid.Volume);
        foreach (var readers in _readersOf) readers.RemoveAll(r => r.Grid == grid.Id);
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
        _readersOf = _stages.Select(_ => new List<(Stage, int, Input)>()).ToArray();
        foreach (var grid in _grids) BuildGrid(grid);
    }

    private void BuildGrid(PipelineGrid grid)
    {
        grid.SlotOf = new int[_stages.Count];
        grid.Inputs = new IReadOnlyList<Input>[_stages.Count];
        foreach (var stage in _stages)
        {
            bool has = stage.AppliesTo(grid);
            grid.SlotOf[stage.Id] = has ? grid.SlotCount++ : -1;
            var inputs = grid.Inputs[stage.Id] = has ? stage.InputsFor(grid) : Array.Empty<Input>();
            foreach (var input in inputs) _readersOf[input.Stage.Id].Add((stage, grid.Id, input));
        }
    }

    public PipelineChunk? Get(ChunkKey key) => _records.GetValueOrDefault(key);

    /// <summary>The chunk in its grid's volume that the pipeline made (or took) for <paramref name="key"/>, if it's there.</summary>
    internal ChunkEntry? EntryOf(ChunkKey key)
        => key.Grid < _grids.Count && _grids[key.Grid].Volume?.GetEntry(key.Pos) is { } entry
           && entry.Entity.Has<InPipeline>() && entry.Entity.Get<InPipeline>().Key == key ? entry : null;

    /// <summary>Asks for a chunk (every stage it needs runs, see <see cref="WantFor"/>), or no longer: it's dropped once
    /// it isn't held either (see <see cref="Hold"/>) and no worker is making it.</summary>
    public void Want(ChunkKey key, bool wanted)
    {
        var record = wanted ? Ensure(key) : Get(key);
        if (record == null || record.Asked == wanted) return;
        bool was = record.Wanted;
        record.Asked = wanted;
        Changed(record, was);
    }

    /// <summary>Holds a wanted chunk, or lets go of a hold: it stays in the pipeline while anything holds it, asked for
    /// or not. A chunk not in the pipeline isn't held (a hold never makes one); false then.</summary>
    public bool Hold(ChunkKey key, bool held)
    {
        if (Get(key) is not { Wanted: true } record || !held && record.Holds == 0) return false;
        bool was = record.Wanted;
        record.Holds += held ? 1 : -1;
        if (record.Wanted != was) Changed(record, was);
        return true;
    }

    /// <summary>A chunk was asked for or let go of, or its last hold went.</summary>
    private void Changed(PipelineChunk record, bool wasWanted)
    {
        if (record.Wanted != wasWanted)
            for (int s = 0; s < _stages.Count; s++)
                if (record.Has(s)) Check(record, s);
        // Inputs that accept a missing chunk see it come or go (or stay only held).
        for (int s = 0; s < _stages.Count; s++)
            if (record.Has(s) && record.At(s).Version > 0) Propagate(record, _stages[s], Change.All, acceptsMissingOnly: true);
        if (!record.Wanted) _maybeDrop.Add(record);
    }

    /// <summary>Wants a wanted chunk for <paramref name="purpose"/> too, or no longer: the stages serving it run on the
    /// chunk while it is, and let go of what they made once it isn't (what reads that, on other chunks, sees it missing).
    /// Nothing happens for a chunk not in the pipeline.</summary>
    public void WantFor(ChunkKey key, Purpose purpose, bool wanted)
    {
        if (Get(key) is not { } record) return;
        var before = record.Purposes;
        record.Purposes = wanted ? before | purpose : before & ~purpose;
        var changed = before ^ record.Purposes;
        if (changed == Purpose.None) return;
        foreach (var stage in _stages)
        {
            if (!record.Has(stage.Id) || (stage.Serves & changed) == Purpose.None) continue;
            if (record.Needs(stage)) Check(record, stage.Id);
            else Unmake(record, stage);
        }
    }

    /// <summary>Lets go of what a stage made on a chunk that no longer needs it: it's as if never run (one running
    /// finishes, and what it makes is let go of unless the chunk needs it again by then).</summary>
    private void Unmake(PipelineChunk record, Stage stage)
    {
        ref var slot = ref record.At(stage.Id);
        if (slot.Output is { } output) { slot.Output = null; stage.Release(output); }
        bool made = slot.Version > 0;
        slot.Version = 0;
        slot.Flags = SlotFlags.Stale | (slot.Flags & SlotFlags.Running);
        if (made) Propagate(record, stage, Change.All, acceptsMissingOnly: true);
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

    /// <summary>Ranks everything waiting its turn again by <see cref="Priority"/> as it is now (it's ranked when queued):
    /// for when what's nearest changes, as the view moves.</summary>
    public void Reprioritise()
    {
        foreach (var queue in _ready)
        {
            _reranked.Clear();
            foreach (var (item, _) in queue.UnorderedItems)
                if (!item.Record.Removed) _reranked.Add((item, RankOf(item.Record, item.Stage)));
            queue.Clear();
            queue.EnqueueRange(_reranked);
        }
    }

    // Reprioritise's items, kept: the queues hold hundreds of thousands while the view moves, re-ranked each time it
    // moves a chunk, and a new list each time was most of what moving allocated.
    private readonly List<((PipelineChunk Record, Stage Stage), (float, int))> _reranked = new();

    /// <summary>Takes finished worker results and runs ready stages, nearest first, within <paramref name="budgets"/>.</summary>
    public void Update(FrameBudgets budgets)
    {
        long start = Stopwatch.GetTimestamp();
        bool MainLeft() => Stopwatch.GetElapsedTime(start).TotalMilliseconds < budgets.MainMs;
        if (FrameMs.Length != _stages.Count + 1) FrameMs = new double[_stages.Count + 1];
        Array.Clear(FrameMs);

        while (MainLeft() && (_madeUrgent.TryDequeue(out var made) || _made.TryDequeue(out made)))
        {
            using var timed = Timed(made.Stage);
            if (made.Worker >= 0) _inFlight[made.Worker]--;
            if (made.Made is Exception e) throw new InvalidOperationException($"{made.Stage} failed on {made.Record}", e);
            if (made.Record.Removed) { made.Stage.Release(made.Made); continue; }
            if (!made.Record.Needs(made.Stage)) // no longer wanted for what it serves
            {
                made.Record.At(made.Stage.Id).Set(SlotFlags.Running, false);
                made.Stage.Release(made.Made);
                continue;
            }
            var change = made.Stage.AppliedAny(made.Record, made.Made, made.Record.At(made.Stage.Id).Output);
            Finish(made.Record, made.Stage, made.Made, change);
        }

        // Everything below is on the main thread, so all of it stops when the frame's time is spent.
        // Nearest first, each to its worker; one whose worker is full waits its turn (looked past a bounded number of
        // times a frame, so one busy worker doesn't cost the frame).
        // Urgent work first, whether or not its worker is full: it goes before what's queued on the workers already.
        while (MainLeft() && _ready[(int)Executor.Worker].TryPeek(out _, out var first) && first.Item1 == Urgent
               && TakeReady(Executor.Worker, out var record, out var stage, out var priority))
        {
            if (priority.Item1 != Urgent)
            {
                record.At(stage.Id).Set(SlotFlags.Queued, true);
                _ready[(int)Executor.Worker].Enqueue((record, stage), priority);
                break;
            }
            StartWorker(record, (WorkerStage)stage, -1);
        }
        int full = _inFlight.Count(n => n >= budgets.WorkerSlots);
        while (full < _inFlight.Length && _skipped.Count < MaxSkipped && MainLeft()
               && TakeReady(Executor.Worker, out var record, out var stage, out var priority))
        {
            int worker = WorkerOf(record.Key);
            if (_inFlight[worker] >= budgets.WorkerSlots)
            {
                record.At(stage.Id).Set(SlotFlags.Queued, true);
                _skipped.Add(((record, stage), priority));
                continue;
            }
            StartWorker(record, (WorkerStage)stage, worker);
            if (_inFlight[worker] == budgets.WorkerSlots) full++;
        }
        foreach (var (item, priority) in _skipped) _ready[(int)Executor.Worker].Enqueue(item, priority);
        _skipped.Clear();

        // Asking takes a share of the time at most, so a crowd of chunks waiting on other systems leaves the frame stages
        // their turn. Those ready are asked first, nearest first (often done at once); then each one waiting is asked
        // again once at most, and those not reached are first next frame.
        long asking = Stopwatch.GetTimestamp();
        bool AskingLeft() => MainLeft() && Stopwatch.GetElapsedTime(asking).TotalMilliseconds < budgets.MainMs * ExternalShare;
        double now = Clock();
        while (_askAgain.TryPeek(out var again, out double at) && at <= now)
        {
            _askAgain.Dequeue();
            if (!again.Record.Removed) MarkStale(again.Record, again.Stage.Id);
        }
        int waitingBefore = _awaitingExternal.Count;
        while (AskingLeft() && TakeReady(Executor.External, out var record, out var stage, out _))
        {
            ref var slot = ref record.At(stage.Id);
            slot.Set(SlotFlags.Stale, false);
            slot.Set(SlotFlags.Running, true);
            if (!Ask(record, (ExternalStage)stage)) _awaitingExternal.Enqueue((record, (ExternalStage)stage));
        }
        for (int n = waitingBefore; n > 0 && AskingLeft(); n--)
        {
            var (record, stage) = _awaitingExternal.Dequeue();
            if (record.Removed) continue;
            if (!record.Needs(stage)) { record.At(stage.Id).Set(SlotFlags.Running, false); continue; }
            if (!Ask(record, stage)) _awaitingExternal.Enqueue((record, stage));
        }

        while (MainLeft() && TakeReady(Executor.Frame, out var record, out var stage, out var priority))
            if (!RunFrame(record, (FrameStage)stage)) _skipped.Add(((record, stage), priority));
        foreach (var (item, priority) in _skipped) _ready[(int)Executor.Frame].Enqueue(item, priority);
        _skipped.Clear();

        // Dropping lets go of what a chunk made, which can let go of holds on others: dropped too, in turn. Within the
        // frame's time, but some each frame whatever, so they don't pile up while the stages take it all.
        long dropping = Stopwatch.GetTimestamp();
        for (int dropped = 0; ; dropped++)
        {
            foreach (var record in _maybeDrop) _dropping.Enqueue(record);
            _maybeDrop.Clear();
            if (_dropping.Count == 0 || dropped >= MinDropsPerFrame && !MainLeft()) break;
            DropIfUnneeded(_dropping.Dequeue()); // wanted again meanwhile: kept
        }
        FrameMs[^1] = Stopwatch.GetElapsedTime(dropping).TotalMilliseconds;
    }

    /// <summary>Main-thread time (ms) the last <see cref="Update"/> spent on each stage, by id (taking its finished
    /// work, starting it, asking it or running it), then on dropping chunks: so a long frame can be traced to a stage.</summary>
    public double[] FrameMs { get; private set; } = Array.Empty<double>();

    private StageTime Timed(Stage stage) => new(FrameMs, stage.Id, Stopwatch.GetTimestamp());

    private readonly struct StageTime(double[] ms, int stage, long start) : IDisposable
    {
        public void Dispose() => ms[stage] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private readonly Queue<PipelineChunk> _dropping = new();
    private const int MinDropsPerFrame = 64;

    /// <summary>How many chunks have the stage in each state, for the panel.</summary>
    public (int Waiting, int Ready, int Running, int Done) Count(Stage stage)
    {
        int w = 0, r = 0, run = 0, d = 0;
        foreach (var record in _records.Values)
        {
            if (!record.Needs(stage)) continue;
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
    public int WorkerJobs => _inFlight.Sum();

    /// <summary>External stages asked and not done yet.</summary>
    public int AwaitingExternal => _awaitingExternal.Count;

    /// <summary>The chunk and stage that's been ready longest without running, and for how long (seconds), for the
    /// panel: what a chunk that never seems to get made is stuck behind.</summary>
    public (PipelineChunk Record, Stage Stage, double Seconds)? LongestReady()
    {
        (PipelineChunk, Stage, long)? oldest = null;
        foreach (var record in _records.Values)
            foreach (var stage in _stages)
                if (record.Has(stage.Id) && record.State(stage) == SlotState.Ready
                    && (oldest == null || record.At(stage.Id).QueuedAt < oldest.Value.Item3))
                    oldest = (record, stage, record.At(stage.Id).QueuedAt);
        if (oldest is not var (r, s, at)) return null;
        return (r, s, Stopwatch.GetElapsedTime(at).TotalSeconds);
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

    private IReadOnlyList<Input> InputsOf(Stage stage, int grid) => _grids[grid].Inputs[stage.Id];

    private PipelineChunk Ensure(ChunkKey key)
    {
        if (_records.TryGetValue(key, out var record)) return record;
        if (!_built) Build();
        var grid = _grids[key.Grid];
        if (grid.Removed) throw new InvalidOperationException($"{grid} was removed.");
        record = new PipelineChunk(key, grid, grid.SlotOf, grid.SlotCount);
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
            if (record.Wanted && record.Needs(stage) && !slot.Is(SlotFlags.Running) && slot.Is(SlotFlags.Stale)
                && InputsThere(record, stage))
                return true;
        }
        (record, stage) = (null!, null!);
        return false;
    }

    /// <summary>Queues the stage on the chunk if it's wanted and needs it, it's stale and its inputs are there.</summary>
    private void Check(PipelineChunk record, int stage)
    {
        ref var slot = ref record.At(stage);
        if (!record.Wanted || !record.Needs(_stages[stage]) || slot.Is(SlotFlags.Queued | SlotFlags.Running) || !slot.Is(SlotFlags.Stale)
            || !InputsThere(record, _stages[stage])) return;
        slot.Set(SlotFlags.Queued, true);
        slot.QueuedAt = Stopwatch.GetTimestamp();
        _ready[(int)_stages[stage].RunsOn].Enqueue((record, _stages[stage]), RankOf(record, _stages[stage]));
    }

    /// <summary>Rank in the ready queues for an urgent change's work: before everything else.</summary>
    private const float Urgent = float.NegativeInfinity;

    private (float, int) RankOf(PipelineChunk record, Stage stage)
        => (record.At(stage.Id).Is(SlotFlags.Urgent) ? Urgent : Priority?.Invoke(record.Key, stage) ?? 0f, -stage.Id);

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

    private void StartWorker(PipelineChunk record, WorkerStage stage, int worker)
    {
        using var timed = Timed(stage);
        var inputs = Gather(record, stage);
        inputs.Prepared = stage.Prepare(record);
        ref var slot = ref record.At(stage.Id);
        slot.Set(SlotFlags.Stale, false);
        slot.Set(SlotFlags.Running, true);
        var key = record.Key;
        Action work = () =>
        {
            object made;
            try { made = stage.MakeAny(key, inputs); }
            catch (Exception e) { made = e; }
            (worker < 0 ? _madeUrgent : _made).Enqueue((record, stage, worker, made));
        };
        if (worker < 0) { _runUrgent(work); return; }
        _inFlight[worker]++;
        _runOnWorker(worker, work);
    }

    /// <summary>Runs a frame stage; false if it can't yet (it's left stale, to run again next frame).</summary>
    private bool RunFrame(PipelineChunk record, FrameStage stage)
    {
        using var timed = Timed(stage);
        var result = stage.Run(record, Gather(record, stage));
        if (!result.Finished) { record.At(stage.Id).Set(SlotFlags.Queued, true); return false; }
        record.At(stage.Id).Set(SlotFlags.Stale, false);
        Finish(record, stage, result.Output, result.Change, result.Changed);
        return true;
    }

    /// <summary>Asks an external stage; false if it's to be asked again next frame.</summary>
    private bool Ask(PipelineChunk record, ExternalStage stage)
    {
        using var timed = Timed(stage);
        var result = stage.Request(record);
        if (result.Finished) Finish(record, stage, result.Output, result.Change, result.Changed);
        if (result.Again == 0) return result.Finished;
        if (!result.Finished)
        {
            record.At(stage.Id).Set(SlotFlags.Running, false);
            Check(record, stage.Id); // an input changed while it was asked
        }
        if (double.IsFinite(result.Again))
            _askAgain.Enqueue((record, stage), Clock() + result.Again);
        return true;
    }

    // External stages to ask again, by when (see StageResult.Again).
    private readonly PriorityQueue<(PipelineChunk Record, Stage Stage), double> _askAgain = new();

    /// <summary>The time (seconds) that stages' <see cref="StageResult.Again"/> counts in: the game's, where they wait
    /// on its clock (fades).</summary>
    public Func<double> Clock { get; set; } = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void Finish(PipelineChunk record, Stage stage, object? output, Change change, bool changed = true)
    {
        ref var slot = ref record.At(stage.Id);
        slot.Set(SlotFlags.Running, false);
        if (slot.Output is { } before && !ReferenceEquals(before, output)) stage.Release(before);
        slot.Output = output;
        slot.Version++;
        if (slot.Is(SlotFlags.Urgent))
        {
            change = change with { Urgent = true };
            if (!slot.Is(SlotFlags.Stale)) slot.Set(SlotFlags.Urgent, false); // else it runs again, still urgent
        }
        if (changed) Propagate(record, stage, change, acceptsMissingOnly: false);
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
                    MarkStale(dependant, reader.Id, change.Urgent);
        }
    }

    private void MarkStale(PipelineChunk record, int stage, bool urgent = false)
    {
        ref var slot = ref record.At(stage);
        slot.Set(SlotFlags.Stale, true);
        if (urgent && !slot.Is(SlotFlags.Urgent))
        {
            slot.Set(SlotFlags.Urgent, true);
            slot.Set(SlotFlags.Queued, false); // queued again, ahead (the place it had is skipped once it's run)
        }
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
