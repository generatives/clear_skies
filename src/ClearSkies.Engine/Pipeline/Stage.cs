namespace ClearSkies.Engine.Pipeline;

/// <summary>One input of a stage: stage <paramref name="Stage"/>'s output on the chunks <paramref name="Of"/> picks
/// around the one being made. Only wanted chunks are read (see <see cref="ChunkPipeline.Want"/>), and none is loaded
/// for it. The stage runs only once every such output is there, unless <paramref name="AcceptsMissing"/>: then it runs
/// without the ones that aren't (reading null) and runs again when they come, go or change.</summary>
public readonly record struct Input(Stage Stage, Relation Of, bool AcceptsMissing = false);

/// <summary>What a wanted chunk is wanted for, beyond its blocks (see <see cref="ChunkPipeline.WantFor"/>). A stage that
/// serves one (<see cref="Stage.Serves"/>) runs on a chunk only while it's wanted for it.</summary>
[Flags]
public enum Purpose : byte
{
    None = 0,
    /// <summary>Drawn here (on a host, other players' chunks aren't).</summary>
    Draw = 1,
}

/// <summary>Where a stage's work is done.</summary>
public enum Executor
{
    /// <summary>On a worker thread, from its inputs alone; the result is applied on the main thread.</summary>
    Worker,
    /// <summary>On the main thread, under the frame's budget.</summary>
    Frame,
    /// <summary>By another system: the pipeline asks every frame (<see cref="ExternalStage.Request"/>) until it
    /// answers done.</summary>
    External,
}

/// <summary>
/// A step in making a chunk. It runs once its inputs are there, and again whenever one of them changes; until a rerun
/// finishes, its last output stays in use. Its output is kept on the chunk's record (see
/// <see cref="PipelineChunk.Output{T}"/>) for later stages to read.
/// </summary>
public abstract class Stage
{
    /// <summary>Its place in its pipeline's stage list, set when it's added.</summary>
    public int Id { get; internal set; } = -1;
    public virtual string Name => GetType().Name;
    public abstract Executor RunsOn { get; }

    /// <summary>Whether the grid's chunks have this stage at all (a ship's have no shrink).</summary>
    public virtual bool AppliesTo(PipelineGrid grid) => true;

    /// <summary>What it's for: <see cref="Purpose.None"/> (the default) for every wanted chunk; otherwise it runs on a
    /// chunk only while the chunk's wanted for one of these, and lets go of what it made once it isn't.</summary>
    public virtual Purpose Serves => Purpose.None;

    /// <summary>What it reads to make a chunk of <paramref name="grid"/> (asked once per grid).</summary>
    public abstract IReadOnlyList<Input> InputsFor(PipelineGrid grid);

    /// <summary>Lets go of an output that's been replaced, or whose chunk is gone (pooled buffers, GPU meshes).</summary>
    public virtual void Release(object output) { }

    public override string ToString() => Name;
}

/// <summary>A stage done on a worker (see <see cref="WorkerStage{TOut}"/>).</summary>
public abstract class WorkerStage : Stage
{
    public sealed override Executor RunsOn => Executor.Worker;

    /// <summary>On the main thread, as the chunk's job starts: whatever <see cref="WorkerStage{TOut}.Make"/> needs from
    /// outside the pipeline (blocks held in a volume), handed to it as <see cref="StageInputs.Prepared"/>. Nothing, by
    /// default.</summary>
    public virtual object? Prepare(PipelineChunk record) => null;

    internal abstract object MakeAny(ChunkKey key, StageInputs inputs);
    internal abstract Change AppliedAny(PipelineChunk record, object output, object? before);
}

/// <summary>A stage done on a worker: <see cref="Make"/> sees only its inputs, so it can run beside the main thread.</summary>
public abstract class WorkerStage<TOut> : WorkerStage where TOut : class
{
    /// <summary>Makes the chunk's output from its inputs. On a worker thread: no ECS, no volumes.</summary>
    public abstract TOut Make(ChunkKey key, StageInputs inputs);

    /// <summary>On the main thread, as the output is taken: what changed since the last one (all of it, by default).</summary>
    public virtual Change Applied(PipelineChunk record, TOut output, TOut? before) => Change.All;

    internal sealed override object MakeAny(ChunkKey key, StageInputs inputs) => Make(key, inputs);
    internal sealed override Change AppliedAny(PipelineChunk record, object output, object? before)
        => Applied(record, (TOut)output, (TOut?)before);
}

/// <summary>What a frame or external stage answers: done, with its output and what changed, or not yet (asked again
/// next frame); a frame stage's done may also be for now (<see cref="Again"/>).</summary>
public readonly record struct StageResult(bool Finished, object? Output, Change Change, bool RunsAgain = false)
{
    public static StageResult Done(object? output = null) => new(true, output, Change.All);
    public static StageResult Done(object? output, Change change) => new(true, output, change);
    public static readonly StageResult Waiting = new(false, null, Change.All);
    /// <summary>Done for now, and run again next frame: for a frame stage whose output goes on changing with time (a
    /// fade).</summary>
    public static StageResult Again(object? output) => new(true, output, Change.All, RunsAgain: true);
}

/// <summary>A stage done on the main thread, under the frame's budget.</summary>
public abstract class FrameStage : Stage
{
    public sealed override Executor RunsOn => Executor.Frame;
    /// <summary>Runs it; <see cref="StageResult.Waiting"/> if it can't yet, to run again next frame.</summary>
    public abstract StageResult Run(PipelineChunk record, StageInputs inputs);
}

/// <summary>A stage another system does in its own time (first light): the pipeline asks for it every frame until it
/// answers done.</summary>
public abstract class ExternalStage : Stage
{
    public sealed override Executor RunsOn => Executor.External;
    /// <summary>Asks for it (again): <see cref="StageResult.Waiting"/> until the system has done it.</summary>
    public abstract StageResult Request(PipelineChunk record);
}

/// <summary>The outputs a stage reads, as they were when it started: per input, each chunk picked and its output
/// (null where it accepts one missing and there's none).</summary>
public sealed class StageInputs
{
    private readonly List<(Stage Stage, ChunkKey Key, object? Output)> _read = new();

    internal void Add(Stage stage, ChunkKey key, object? output) => _read.Add((stage, key, output));

    /// <summary>What the stage's <see cref="WorkerStage.Prepare"/> took for this job, if anything.</summary>
    public object? Prepared { get; internal set; }

    /// <summary>The output of <paramref name="stage"/> on chunk <paramref name="key"/>, if it was read.</summary>
    public T? Get<T>(Stage stage, ChunkKey key) where T : class
    {
        foreach (var r in _read)
            if (r.Stage == stage && r.Key == key) return (T?)r.Output;
        return null;
    }

    /// <summary>Every chunk read of <paramref name="stage"/>, with its output (null if missing).</summary>
    public IEnumerable<(ChunkKey Key, T? Output)> All<T>(Stage stage) where T : class
    {
        foreach (var r in _read)
            if (r.Stage == stage) yield return (r.Key, (T?)r.Output);
    }
}
