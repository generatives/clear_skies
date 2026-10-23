namespace ClearSkies.Engine.Pipeline;

/// <summary>Where a stage is on one chunk.</summary>
public enum SlotState
{
    /// <summary>Not run since its inputs last changed (or ever), and not ready: an input isn't there.</summary>
    Waiting,
    /// <summary>Its inputs are there; waiting its turn.</summary>
    Ready,
    Running,
    /// <summary>Run on its inputs as they are now.</summary>
    Done,
}

/// <summary>Everything the pipeline knows about one chunk: whether it's wanted, and each of its stages' progress and
/// output. It lives from when the chunk is wanted until it isn't (and no worker is making anything for it).</summary>
public sealed class PipelineChunk
{
    internal PipelineChunk(ChunkKey key, PipelineGrid grid, int[] slotOf, int slots)
    {
        Key = key;
        Grid = grid;
        SlotOf = slotOf;
        Slots = new Slot[slots];
        for (int i = 0; i < slots; i++) Slots[i].Flags = SlotFlags.Stale;
    }

    public ChunkKey Key { get; }
    public PipelineGrid Grid { get; }
    /// <summary>In the pipeline: asked for, or held. Every stage it has runs. Once not, it's only waiting to be dropped,
    /// and nothing reads it.</summary>
    public bool Wanted => Asked || Holds > 0;
    /// <summary>Wanted by what asked for it (see <see cref="ChunkPipeline.Want"/>).</summary>
    public bool Asked { get; internal set; }
    /// <summary>Holds on it (see <see cref="ChunkPipeline.Hold"/>): what else keeps it while it's asked for or not.</summary>
    internal int Holds;
    /// <summary>What it's wanted for, beyond its blocks (see <see cref="ChunkPipeline.WantFor"/>).</summary>
    public Purpose Purposes { get; internal set; }
    /// <summary>Dropped by the pipeline: nothing needs it any more.</summary>
    public bool Removed { get; internal set; }

    // Only the stages its grid has get a slot: SlotOf maps a stage's id to its slot (-1: none), shared by the grid.
    internal readonly int[] SlotOf;
    internal readonly Slot[] Slots;

    internal bool Has(int stage) => SlotOf[stage] >= 0;
    internal ref Slot At(int stage) => ref Slots[SlotOf[stage]];

    /// <summary>Whether it has the stage and is wanted for what the stage serves (wanted at all or not).</summary>
    public bool Needs(Stage stage) => Has(stage.Id) && (stage.Serves == Purpose.None || (Purposes & stage.Serves) != 0);

    /// <summary>The stage's last output, which stays in use while a rerun is under way.</summary>
    public T? Output<T>(Stage stage) where T : class => Has(stage.Id) ? (T?)At(stage.Id).Output : null;

    /// <summary>Whether the stage has ever finished on this chunk: what showing it depends on, so a rerun never hides it.</summary>
    public bool EverDone(Stage stage) => Has(stage.Id) && At(stage.Id).Version > 0;

    public SlotState State(Stage stage)
    {
        if (!Has(stage.Id)) return SlotState.Done;
        ref var slot = ref At(stage.Id);
        if (slot.Is(SlotFlags.Running)) return SlotState.Running;
        if (slot.Is(SlotFlags.Queued)) return SlotState.Ready;
        return !slot.Is(SlotFlags.Stale) && slot.Version > 0 ? SlotState.Done : SlotState.Waiting;
    }

    public override string ToString() => Key.ToString();
}

[Flags]
internal enum SlotFlags : byte
{
    /// <summary>Its inputs changed since its last run started (or it's never run).</summary>
    Stale = 1,
    Queued = 2,
    Running = 4,
    /// <summary>Stale for an urgent change (see <see cref="Change.Urgent"/>): goes first, and passes it on.</summary>
    Urgent = 8,
}

/// <summary>One stage's progress on one chunk. Only the pipeline writes it.</summary>
internal struct Slot
{
    public object? Output;
    /// <summary>Outputs taken so far; 0 until the first.</summary>
    public int Version;
    public SlotFlags Flags;
    /// <summary>When it was last queued (Stopwatch ticks), for the panel: how long it's been waiting its turn.</summary>
    public long QueuedAt;

    public readonly bool Is(SlotFlags flag) => (Flags & flag) != 0;
    public void Set(SlotFlags flag, bool on) => Flags = on ? Flags | flag : Flags & ~flag;
}
