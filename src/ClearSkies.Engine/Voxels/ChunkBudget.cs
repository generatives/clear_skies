namespace ClearSkies.Engine.Voxels;

/// <summary>
/// What limits how much of a volume is loaded at once (see ChunkLoadSystem): the loader asks whether chunks it's about
/// to load fit, and when they don't, how much to free by unloading the farthest. What a chunk costs is the budget's
/// own business: GPU light storage for a drawn world (<see cref="LightBudget"/>).
/// </summary>
public interface IChunkBudget
{
    /// <summary>Whether <paramref name="adding"/> more chunks (loading, their cost not known yet) fit alongside what
    /// <paramref name="volume"/> has loaded.</summary>
    bool HasRoomFor(ChunkVolume volume, int adding);

    /// <summary>When <paramref name="adding"/> more chunks don't fit: how much to free by unloading, or
    /// <see cref="ChunkCost.None"/> if waiting will make room (estimates settling, say).</summary>
    ChunkCost ShortfallFor(ChunkVolume volume, int adding);

    /// <summary>What unloading loaded chunk <paramref name="p"/> frees, in the budget's units.</summary>
    int CostOf(ChunkVolume volume, ChunkPosition p);

    /// <summary>A line for the streaming debug panel and log: how much of the budget is used.</summary>
    string Describe(ChunkVolume volume);
}

/// <summary>How much of a budget to free: a number of chunks, and an amount in the budget's own units.</summary>
public readonly record struct ChunkCost(int Chunks, int Units)
{
    public static readonly ChunkCost None = new(0, -1);
    public bool IsNone => Chunks <= 0 && Units < 0;
}

/// <summary>
/// Work on a chunk's data once it has loaded, on the worker thread that loaded it, rather than in the frame the chunk
/// is added: packing it for the GPU, say (<see cref="LightPacking"/>). What it returns goes to the chunk as it's added.
/// </summary>
public interface IChunkPreparer
{
    ChunkPreparation Prepare(ChunkData data);
}

/// <summary>What an <see cref="IChunkPreparer"/> made of a chunk's data, handed to the chunk as it's added.</summary>
public abstract class ChunkPreparation
{
    internal abstract void ApplyTo(ChunkEntry entry);
}
