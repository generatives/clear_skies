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

/// <summary>Everything the pipeline knows about one chunk: whether it's wanted, and each of its stages' progress and
/// output. It lives from when the chunk is first wanted (or needed by another's stage) until nothing needs it.</summary>
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
    /// <summary>Wanted for itself (in range), not only as another chunk's input: every stage it has runs.</summary>
    public bool Wanted { get; internal set; }
    /// <summary>Dropped by the pipeline: nothing needs it any more.</summary>
    public bool Removed { get; internal set; }

    // Only the stages its grid has get a slot: SlotOf maps a stage's id to its slot (-1: none), shared by the grid.
    internal readonly int[] SlotOf;
    internal readonly Slot[] Slots;

    internal bool Has(int stage) => SlotOf[stage] >= 0;
    internal ref Slot At(int stage) => ref Slots[SlotOf[stage]];

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
    /// <summary>It's keeping its required sources loaded (demanding them) while it needs to run.</summary>
    Holding = 8,
}

/// <summary>One stage's progress on one chunk. Only the pipeline writes it.</summary>
internal struct Slot
{
    public object? Output;
    /// <summary>Outputs taken so far; 0 until the first.</summary>
    public int Version;
    /// <summary>What needs it run: 1 if its chunk is wanted, plus each stage elsewhere waiting on it.</summary>
    public int Demand;
    public SlotFlags Flags;

    public readonly bool Is(SlotFlags flag) => (Flags & flag) != 0;
    public void Set(SlotFlags flag, bool on) => Flags = on ? Flags | flag : Flags & ~flag;
    /// <summary>Something needs it run on inputs it hasn't run on yet.</summary>
    public readonly bool NeedsRun => Demand > 0 && Is(SlotFlags.Stale);
}
