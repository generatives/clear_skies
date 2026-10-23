namespace ClearSkies.Engine.Pipeline;

/// <summary>Where a stage is on one chunk.</summary>
public enum SlotState
{
    /// <summary>Not run since its inputs last changed (or ever), and not ready: an input isn't there, or nothing
    /// needs it.</summary>
    Waiting,
    /// <summary>Its inputs are there; waiting its turn.</summary>
    Ready,
    Running,
    /// <summary>Run on its inputs as they are now.</summary>
    Done,
}

/// <summary>Everything the pipeline knows about one chunk: whether it's wanted, and each stage's progress and output.
/// It lives from when the chunk is first wanted (or needed by another's stage) until nothing needs it.</summary>
public sealed class PipelineChunk
{
    internal PipelineChunk(ChunkKey key, PipelineGrid grid, IReadOnlyList<Stage> stages)
    {
        Key = key;
        Grid = grid;
        Slots = new Slot[stages.Count];
        for (int i = 0; i < Slots.Length; i++) Slots[i] = new Slot { Applies = stages[i].AppliesTo(grid) };
    }

    public ChunkKey Key { get; }
    public PipelineGrid Grid { get; }
    /// <summary>Wanted for itself (in range), not only as another chunk's input: every stage it has runs.</summary>
    public bool Wanted { get; internal set; }
    /// <summary>Dropped by the pipeline: nothing needs it any more.</summary>
    public bool Removed { get; internal set; }

    internal readonly Slot[] Slots;

    /// <summary>The stage's last output, which stays in use while a rerun is under way.</summary>
    public T? Output<T>(Stage stage) where T : class => (T?)Slots[stage.Id].Output;

    /// <summary>Whether the stage has ever finished on this chunk: what showing it depends on, so a rerun never hides it.</summary>
    public bool EverDone(Stage stage) => Slots[stage.Id].Version > 0;

    public SlotState State(Stage stage) => Slots[stage.Id] switch
    {
        { Running: true } => SlotState.Running,
        { Queued: true } => SlotState.Ready,
        { Stale: false, Version: > 0 } => SlotState.Done,
        _ => SlotState.Waiting,
    };

    public override string ToString() => Key.ToString();
}

/// <summary>One stage's progress on one chunk. Only the pipeline writes it.</summary>
internal sealed class Slot
{
    public bool Applies;
    /// <summary>Outputs taken so far; 0 until the first.</summary>
    public int Version;
    /// <summary>Its inputs changed since its last run started (or it's never run).</summary>
    public bool Stale = true;
    public bool Queued, Running;
    /// <summary>What needs it run: 1 if its chunk is wanted, plus each stage elsewhere waiting on it.</summary>
    public int Demand;
    /// <summary>The sources it's keeping loaded (demanding) while it needs to run.</summary>
    public List<(PipelineChunk Record, int Stage)>? Holds;
    public object? Output;
}
